using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.App.ViewModels;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

/// <summary>Экран «Инвентарь»: удаление отмеченных программ через агента (то же ограничение политики, что и в «Чистке»).</summary>
public class ProgramsDeleteViewModelTests
{
    const string KeyA = "arp:machine:x64:{12345678-1234-1234-1234-123456789ABC}";
    const string KeyB = "arp:machine:x64:{22345678-1234-1234-1234-123456789ABC}";

    static ProgramInfo Prog(string name, string id, Changeability cls, string? reason = null)
        => new(name, "Acme", "1.0", 1024 * 1024, null, "Классическая", id, cls, "", reason);

    static (ProgramsViewModel Vm, FakeAgentClient Agent, FakeDialogs Dialogs) Build(params ProgramInfo[] programs)
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid(),
            [new StepResult(1, 0, StepKind.UninstallRun, StepState.Done, null)]) };
        var dialogs = new FakeDialogs();
        var vm = new ProgramsViewModel(new ProgramsSrc(programs), dialogs, Make.Changes(agent));
        return (vm, agent, dialogs);
    }

    static async Task Check(ProgramsViewModel vm, params string[] names)
    {
        await vm.Loaded;
        foreach (var n in names) vm.Items.Single(i => i.Name == n).IsChecked = true;
    }

    [Fact]
    public async Task Command_needs_a_selection()
    {
        var (vm, _, _) = Build(Prog("Тестовая утилита", KeyA, Changeability.Modifiable));
        await vm.Loaded;
        Assert.False(vm.DeleteCommand.CanExecute(null));

        vm.Items.Single().IsChecked = true;

        Assert.True(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirmed_removal_sends_one_request_per_allowed_program()
    {
        var (vm, agent, dialogs) = Build(
            Prog("Первая", KeyA, Changeability.Modifiable), Prog("Вторая", KeyB, Changeability.Modifiable));
        await Check(vm, "Первая", "Вторая");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.True(confirm.Danger);
        Assert.Contains("Первая", confirm.Message);
        Assert.Contains("Вторая", confirm.Message);
        Assert.Equal(new[] { KeyA, KeyB }, agent.Sent.Cast<UninstallRequest>().Select(r => r.FindingId).OrderBy(x => x).ToArray());
        Assert.Equal(ResultKind.Success, vm.Result);
    }

    [Fact]
    public async Task Programs_the_policy_closes_are_skipped_and_reported()
    {
        var (vm, agent, dialogs) = Build(
            Prog("Тестовая утилита", KeyA, Changeability.Modifiable),
            Prog("Закрытая", "arp:machine:x64:Locked", Changeability.ReadOnly, "Нельзя."),
            Prog("Пользовательская", "arp:user:user:Mine", Changeability.ReadOnly, "Нельзя."));
        await Check(vm, "Тестовая утилита", "Закрытая", "Пользовательская");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        var sent = Assert.IsType<UninstallRequest>(Assert.Single(agent.Sent));
        Assert.Equal(KeyA, sent.FindingId);
        Assert.Contains("Пропущено", Assert.Single(dialogs.Confirms).Message);
        Assert.Contains("пропущена", vm.ResultHint);
    }

    [Fact]
    public async Task Only_closed_programs_selected_shows_the_reason_without_a_dialog()
    {
        var (vm, agent, dialogs) = Build(Prog("Закрытая", "arp:machine:x64:Locked", Changeability.ReadOnly, "Это компонент системы."));
        await Check(vm, "Закрытая");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(agent.Sent);
        Assert.Equal(ResultKind.Info, vm.Result);
        Assert.Contains("Это компонент системы.", vm.ResultHint);
    }

    [Fact]
    public async Task Declined_confirmation_sends_nothing()
    {
        var (vm, agent, dialogs) = Build(Prog("Тестовая утилита", KeyA, Changeability.Modifiable));
        dialogs.ConfirmAnswer = false;
        await Check(vm, "Тестовая утилита");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        Assert.Single(dialogs.Confirms);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task One_programs_refusal_does_not_stop_the_batch_and_restart_hint_is_kept()
    {
        var (vm, agent, _) = Build(
            Prog("Первая", KeyA, Changeability.Modifiable), Prog("Вторая", KeyB, Changeability.Modifiable));
        agent.Handler = r => ((UninstallRequest)r).FindingId == KeyA
            ? new AgentResponse(r.Id, false, ResponseCodes.Partial, null, Guid.NewGuid(),
                [new StepResult(1, 0, StepKind.UninstallRun, StepState.Failed, "timeout")])
            : new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid(),
                [new StepResult(1, 0, StepKind.UninstallRun, StepState.Done, "win32:3010")]);
        await Check(vm, "Первая", "Вторая");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        Assert.Equal(2, agent.Sent.Count);
        Assert.Equal(ResultKind.Warning, vm.Result);
        Assert.Contains("Первая", vm.ResultHint);
        Assert.Contains("перезагрузите", vm.ResultHint);
    }

    [Fact]
    public async Task Missing_agent_answer_stops_the_batch_and_names_the_untouched_programs()
    {
        var (vm, agent, _) = Build(
            Prog("Первая", KeyA, Changeability.Modifiable), Prog("Вторая", KeyB, Changeability.Modifiable));
        agent.Handler = r => new AgentResponse(r.Id, false, ClientCodes.Disconnected, null, null, null);
        await Check(vm, "Первая", "Вторая");

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        Assert.Single(agent.Sent);
        Assert.Contains("Вторая: не запускалась", vm.ResultHint);
    }

    [Fact]
    public async Task View_only_mode_sends_nothing()
    {
        var (vm, agent, dialogs) = Build(Prog("Тестовая утилита", KeyA, Changeability.Modifiable));
        await Check(vm, "Тестовая утилита");
        agent.SetAccess(AgentAccess.ViewOnly);

        vm.DeleteCommand.Execute(null);
        await vm.LastDelete;

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(agent.Sent);
        Assert.Equal(ResultKind.Warning, vm.Result);
    }
}

/// <summary>Имя программы в журнале: до удаления агент записывает DisplayName в цель шага; в старых записях его нет.</summary>
public class UninstallJournalNameTests
{
    const string Subject = "arp:machine:x64:{12345678-1234-1234-1234-123456789abc}";

    [Fact]
    public void Journal_title_uses_the_program_name_when_it_is_recorded()
        => Assert.Equal("7-Zip 23.01", SubjectText.OfOperation(OperationKind.Uninstall, Subject, "7-Zip 23.01"));

    [Fact]
    public void Old_records_without_a_name_keep_the_registry_key()
        => Assert.Equal("{12345678-1234-1234-1234-123456789abc}", SubjectText.OfOperation(OperationKind.Uninstall, Subject));

    [Fact]
    public void Blank_name_falls_back_to_the_key()
        => Assert.Equal("{12345678-1234-1234-1234-123456789abc}", SubjectText.OfOperation(OperationKind.Uninstall, Subject, "  "));

    [Fact]
    public void Target_name_survives_the_journal_round_trip_and_old_json_still_reads()
    {
        var json = Chistovik.Core.Execution.StateJson.UninstallTargetJson(
            new UninstallTarget("machine", "x64", "git_is1", "exe", null, "Git"));
        Assert.Equal("Git", Chistovik.Core.Execution.StateJson.ParseUninstallTarget(json).Name);

        var old = "{\"scope\":\"machine\",\"arch\":\"x64\",\"key\":\"git_is1\",\"kind\":\"exe\"}";
        Assert.Null(Chistovik.Core.Execution.StateJson.ParseUninstallTarget(old).Name);
    }
}
