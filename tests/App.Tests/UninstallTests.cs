using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.App.Services.Windows;
using Chistovik.App.ViewModels;
using Chistovik.Core.Inventory;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace App.Tests;

/// <summary>Реестр-подставка с разделом Uninstall: машинные ключи (x64/x86) и пользовательские.</summary>
sealed class UninstallRegistry : IRegistryReader
{
    const string U = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    readonly Dictionary<string, Dictionary<string, object?>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public UninstallRegistry Add(RegistryHiveKind hive, string key, Dictionary<string, object?> values)
    {
        _keys[$"{hive}|{U}\\{key}"] = values;
        return this;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        if (view == RegistryViewKind.Registry32) return [];
        var prefix = $"{hive}|{path}\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..]).ToList();
    }

    public IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path)
        => view == RegistryViewKind.Registry32 ? null : _keys.GetValueOrDefault($"{hive}|{path}");
}

public class InventoryClassificationTests
{
    const string MsiKey = "{12345678-1234-1234-1234-123456789ABC}";

    static Dictionary<string, object?> Msi(string name = "Тестовая утилита") => new()
    {
        ["DisplayName"] = name, ["DisplayVersion"] = "1.0", ["Publisher"] = "Acme", ["WindowsInstaller"] = 1,
        ["UninstallString"] = "MsiExec.exe /X" + MsiKey,
    };

    static InventoryProgramsSource Source(UninstallRegistry reg, bool withPolicy = true)
        => new(reg, withPolicy ? new PackagePolicy(PolicyContext.Fixed, new FakeFileSecurity()) : null);

    [Fact]
    public async Task Machine_msi_is_modifiable_and_carries_the_protocol_id()
    {
        var reg = new UninstallRegistry().Add(RegistryHiveKind.LocalMachine, MsiKey, Msi());

        var p = Assert.Single(await Source(reg).GetProgramsAsync());

        Assert.Equal("arp:machine:x64:" + MsiKey, p.Id);
        Assert.Equal(Changeability.Modifiable, p.Class);
        Assert.Null(p.ReadOnlyReason);
    }

    [Fact]
    public async Task User_scope_program_is_view_only_with_a_reason()
    {
        var reg = new UninstallRegistry().Add(RegistryHiveKind.CurrentUser, "Atlas", new()
        {
            ["DisplayName"] = "Пользовательская программа", ["UninstallString"] = @"C:\Users\x\AppData\Local\App\unins000.exe",
        });

        var p = Assert.Single(await Source(reg).GetProgramsAsync());

        Assert.Equal(Changeability.ReadOnly, p.Class);
        Assert.Equal("hkcu-scope", p.Rule);
        Assert.False(string.IsNullOrWhiteSpace(p.ReadOnlyReason));
    }

    [Fact]
    public async Task Without_a_policy_nothing_is_offered_for_removal()
    {
        var reg = new UninstallRegistry().Add(RegistryHiveKind.LocalMachine, MsiKey, Msi());

        var p = Assert.Single(await Source(reg, withPolicy: false).GetProgramsAsync());

        Assert.Equal(Changeability.ReadOnly, p.Class);
    }

    [Fact]
    public async Task Exe_uninstaller_with_unverified_file_is_never_modifiable()
    {
        var reg = new UninstallRegistry().Add(RegistryHiveKind.LocalMachine, "SomeApp", new()
        {
            ["DisplayName"] = "Некая программа", ["UninstallString"] = @"C:\Program Files\Some\uninst.exe",
        });

        var p = Assert.Single(await Source(reg).GetProgramsAsync());

        Assert.NotEqual(Changeability.Modifiable, p.Class);
        Assert.False(string.IsNullOrWhiteSpace(p.ReadOnlyReason));
    }
}

public class UninstallChangeTests
{
    const string Id = "arp:machine:x64:{12345678-1234-1234-1234-123456789ABC}";

    static AgentResponse Reply(AgentRequest r, string code, bool ok, params (StepState State, string? Code)[] steps)
        => new(r.Id, ok, code, null, Guid.NewGuid(),
            steps.Select((s, i) => new StepResult(i + 1, i, StepKind.UninstallRun, s.State, s.Code)).ToList());

    [Fact]
    public async Task Request_carries_only_the_finding_id()
    {
        var agent = new FakeAgentClient { Handler = r => Reply(r, ResponseCodes.Ok, true, (StepState.Done, null)) };

        var outcome = await Make.Changes(agent).UninstallAsync(Id);

        var sent = Assert.IsType<UninstallRequest>(Assert.Single(agent.Sent));
        Assert.Equal(Id, sent.FindingId);
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
        Assert.Contains("удалена", outcome.Message);
        Assert.Null(outcome.RestartHint);
    }

    [Fact]
    public async Task Reboot_required_code_is_reported_as_a_hint_not_a_failure()
    {
        var agent = new FakeAgentClient { Handler = r => Reply(r, ResponseCodes.Ok, true, (StepState.Done, "win32:3010")) };

        var outcome = await Make.Changes(agent).UninstallAsync(Id);

        Assert.Equal(OutcomeKind.Success, outcome.Kind);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RestartHint));
    }

    [Fact]
    public async Task Failed_step_is_reported_as_not_removed()
    {
        var agent = new FakeAgentClient { Handler = r => Reply(r, ResponseCodes.Partial, false, (StepState.Failed, "in-use")) };

        var outcome = await Make.Changes(agent).UninstallAsync(Id);

        Assert.Equal(OutcomeKind.Unavailable, outcome.Kind);
        Assert.StartsWith("Программа не удалена", outcome.Message);
    }

    [Fact]
    public async Task Already_removed_program_gets_the_no_change_text()
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, true, ResponseCodes.NoChange) };

        var outcome = await Make.Changes(agent).UninstallAsync(Id);

        Assert.Equal(OutcomeKind.NoChange, outcome.Kind);
        Assert.Contains("уже удалена", outcome.Message);
    }

    [Fact]
    public void Not_revertible_answer_is_a_denial()
    {
        var o = ResponseText.ForUninstall(new AgentResponse(Guid.NewGuid(), false, ResponseCodes.NotRevertible));

        Assert.Equal(OutcomeKind.Denied, o.Kind);
    }

    [Fact]
    public async Task Malformed_id_never_reaches_the_agent()
    {
        var agent = new FakeAgentClient();

        var outcome = await Make.Changes(agent).UninstallAsync("arp:user:user:Atlas");

        Assert.Empty(agent.Sent);
        Assert.NotEqual(OutcomeKind.Success, outcome.Kind);
    }

    [Fact]
    public async Task View_only_mode_sends_nothing()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };

        var outcome = await Make.Changes(agent).UninstallAsync(Id);

        Assert.Empty(agent.Sent);
        Assert.Equal(OutcomeKind.ViewOnly, outcome.Kind);
    }
}

public class CleanupUninstallViewModelTests
{
    const string Machine = "arp:machine:x64:{12345678-1234-1234-1234-123456789ABC}";

    static ProgramInfo Prog(string name, string id, Changeability cls, string? reason = null)
        => new(name, "Acme", "1.0", 1024 * 1024, null, "Классическая", id, cls, "", reason);

    static (CleanupViewModel Vm, FakeAgentClient Agent, FakeDialogs Dialogs) Build(params ProgramInfo[] programs)
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid(),
            [new StepResult(1, 0, StepKind.UninstallRun, StepState.Done, null)]) };
        var dialogs = new FakeDialogs();
        var vm = new CleanupViewModel(new CleanupScanner(new ProgramsSrc(programs), new StartupSrc(), new ServiceSrc(), new TaskSrc()),
            null, Make.Changes(agent), dialogs);
        return (vm, agent, dialogs);
    }

    static async Task<FindingRowViewModel> Select(CleanupViewModel vm, string title)
    {
        await vm.Loaded;
        var row = vm.Items.Single(i => i.Finding.Title == title);
        vm.Selected = row;
        return row;
    }

    [Fact]
    public async Task Only_a_modifiable_machine_program_can_be_uninstalled_here()
    {
        var (vm, _, _) = Build(
            Prog("Тестовая утилита", Machine, Changeability.Modifiable),
            Prog("Закрытая программа", "arp:machine:x64:Locked", Changeability.ReadOnly, "Нельзя."),
            Prog("Пользовательская", "arp:user:user:Mine", Changeability.ReadOnly, "Нельзя."));
        await vm.Loaded;

        Assert.True(vm.Items.Single(i => i.Finding.Title == "Тестовая утилита").CanUninstall);
        Assert.False(vm.Items.Single(i => i.Finding.Title == "Закрытая программа").CanUninstall);
        Assert.False(vm.Items.Single(i => i.Finding.Title == "Пользовательская").CanUninstall);
    }

    [Fact]
    public async Task Command_is_available_only_for_the_selected_removable_program()
    {
        var (vm, _, _) = Build(
            Prog("Тестовая утилита", Machine, Changeability.Modifiable),
            Prog("Закрытая программа", "arp:machine:x64:Locked", Changeability.ReadOnly, "Нельзя."));

        await Select(vm, "Закрытая программа");
        Assert.False(vm.UninstallCommand.CanExecute(null));

        await Select(vm, "Тестовая утилита");
        Assert.True(vm.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirmed_removal_sends_the_request_with_the_finding_id()
    {
        var (vm, agent, dialogs) = Build(Prog("Тестовая утилита", Machine, Changeability.Modifiable));
        await Select(vm, "Тестовая утилита");

        vm.UninstallCommand.Execute(null);
        await vm.LastUninstall;

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.True(confirm.Danger);
        Assert.Contains("Тестовая утилита", confirm.Title);
        var sent = Assert.IsType<UninstallRequest>(Assert.Single(agent.Sent));
        Assert.Equal(Machine, sent.FindingId);
    }

    [Fact]
    public async Task Declined_confirmation_sends_nothing()
    {
        var (vm, agent, dialogs) = Build(Prog("Тестовая утилита", Machine, Changeability.Modifiable));
        dialogs.ConfirmAnswer = false;
        await Select(vm, "Тестовая утилита");

        vm.UninstallCommand.Execute(null);
        await vm.LastUninstall;

        Assert.Single(dialogs.Confirms);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task View_only_mode_disables_the_command()
    {
        var (vm, agent, _) = Build(Prog("Тестовая утилита", Machine, Changeability.Modifiable));
        await Select(vm, "Тестовая утилита");
        Assert.True(vm.UninstallCommand.CanExecute(null));

        agent.SetAccess(AgentAccess.ViewOnly);

        Assert.False(vm.UninstallCommand.CanExecute(null));
    }
}
