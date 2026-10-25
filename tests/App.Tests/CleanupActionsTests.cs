using Xunit;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.App.Services.Scanners;
using Chistovik.App.ViewModels;
using Chistovik.Core.Findings;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace App.Tests;

/// <summary>Экран «Чистка»: очистка диска и удаление записей реестра на фейковом агенте.</summary>
public class CleanupActionsTests
{
    sealed class Fixed(params Finding[] findings) : IFindingScanner
    {
        public FindingArea Area => FindingArea.Disk;
        public Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    static Finding F(string id, FindingArea area, string title, VerdictKind kind, ChangeClass cls = ChangeClass.Modifiable,
        string detail = "Около 50 МБ, кэш.")
        => FindingFactory.Make(id, area, title, detail, kind, [ReasonCodes.Make(ReasonCodes.SizeNeedsAgent)], Risk.Low,
            Reversibility.Irreversible, Confidence.L1, new Explanation("a", "b", "c", "d"), cls);

    static Finding[] Sample() =>
    [
        F("cleanup:temp-user", FindingArea.Disk, "Временные файлы пользователя", VerdictKind.Optional),
        F("cleanup:thumbcache", FindingArea.Disk, "Кэш миниатюр и значков", VerdictKind.Optional),
        F("cleanup:minidump", FindingArea.Disk, "Мини-дампы сбоев", VerdictKind.Protected, ChangeClass.ReadOnly),
        F("regkey:hklm:-:x64:a", FindingArea.Registry, "Запись А", VerdictKind.Remove),
        F("regkey:hklm:-:x64:b", FindingArea.Registry, "Запись Б", VerdictKind.Remove),
        F("regkey:hklm:-:x64:c", FindingArea.Registry, "Запись В", VerdictKind.Optional),
    ];

    static async Task<(CleanupViewModel Vm, FakeAgentClient Agent, FakeDialogs Dialogs)> Open(
        Func<AgentRequest, AgentResponse>? handler = null, AgentAccess access = AgentAccess.Connected)
    {
        var agent = new FakeAgentClient { Access = access };
        if (handler is not null) agent.Handler = handler;
        var dialogs = new FakeDialogs();
        var vm = new CleanupViewModel(new CleanupScanner([new Fixed(Sample())]), new FakeNavigator(), Make.Changes(agent), dialogs);
        await vm.Loaded;
        return (vm, agent, dialogs);
    }

    static FindingRowViewModel Row(CleanupViewModel vm, string id) => vm.Items.Single(r => r.Finding.Id == id);

    static FactEntry Measure(string cat, long bytes) => new($"cleanup:user:-:{cat}", "{\"v\":1,\"bytes\":" + bytes + ",\"files\":10}");

    static FactEntry Cleaned(string cat, long freed, int inUse, int fresh)
        => new($"cleanup:user:-:{cat}", "{\"v\":1,\"done\":true,\"deleted\":5,\"freedBytes\":" + freed
            + ",\"skipped\":{\"in-use\":" + inUse + ",\"too-fresh\":" + fresh + "}}");

    [Fact]
    public async Task Protected_and_doubtful_rows_have_no_checkbox()
    {
        var (vm, _, _) = await Open();

        var prot = Row(vm, "cleanup:minidump");
        var optionalKey = Row(vm, "regkey:hklm:-:x64:c");
        Assert.False(prot.CanSelect);
        Assert.False(optionalKey.CanSelect);
        prot.IsChecked = true;
        optionalKey.IsChecked = true;
        Assert.False(prot.IsChecked);
        Assert.False(optionalKey.IsChecked);
        Assert.False(vm.CleanDiskCommand.CanExecute(null));
        Assert.False(vm.ApplyRegistryCommand.CanExecute(null));
        Assert.True(Row(vm, "cleanup:temp-user").CanClean);
        Assert.True(Row(vm, "regkey:hklm:-:x64:a").CanRemoveKey);
    }

    [Fact]
    public async Task Area_chips_include_disk_and_registry()
    {
        var (vm, _, _) = await Open();

        Assert.Contains(vm.AreaChips, c => c.Label == "Диск");
        Assert.Contains(vm.AreaChips, c => c.Label == "Реестр");
        vm.AreaCommand.Execute("Registry");
        Assert.All(vm.Items, r => Assert.Equal("Реестр", r.AreaText));
    }

    [Fact]
    public async Task Row_automation_name_has_title_area_verdict_and_size()
    {
        var (vm, _, _) = await Open();

        var row = Row(vm, "cleanup:temp-user");
        Assert.StartsWith("Временные файлы пользователя, Диск, ", row.AutomationName);
        Assert.EndsWith(", 50 МБ", row.AutomationName);
        Assert.Equal("нет", row.ReversibleText);
        Assert.Equal("да, 90 дней", Row(vm, "regkey:hklm:-:x64:a").ReversibleText);
    }

    [Fact]
    public async Task Clean_measures_first_then_confirms_then_cleans_and_reports()
    {
        var (vm, agent, dialogs) = await Open(r => r is CleanCategoriesRequest { MeasureOnly: true } m
            ? new AgentResponse(r.Id, true, ResponseCodes.Ok, null, null, null, null,
                [Measure("temp-user", 52_428_800), Measure("thumbcache", 10_485_760)])
            : new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid(),
                [new StepResult(1, 0, StepKind.CleanupCategory, StepState.Done, null)], null,
                [Cleaned("temp-user", 41_943_040, 2, 3), Cleaned("thumbcache", 0, 0, 0)]));
        Row(vm, "cleanup:temp-user").IsChecked = true;
        Row(vm, "cleanup:thumbcache").IsChecked = true;
        Assert.Equal(2, vm.CheckedDiskCount);
        Assert.True(vm.CleanDiskCommand.CanExecute(null));

        vm.CleanDiskCommand.Execute(null);
        await vm.LastClean;

        Assert.Equal(2, agent.Sent.Count);
        Assert.True(Assert.IsType<CleanCategoriesRequest>(agent.Sent[0]).MeasureOnly);
        Assert.False(Assert.IsType<CleanCategoriesRequest>(agent.Sent[1]).MeasureOnly);
        var c = Assert.Single(dialogs.Confirms);
        Assert.Equal("Очистить: 2 категории", c.Title);
        Assert.Equal("Очистить", c.ConfirmText);
        Assert.True(c.Danger);
        Assert.Contains("60 МБ", c.Message);
        Assert.Contains("24 часов", c.Message);
        Assert.Contains("Откат невозможен", c.Message);
        Assert.Contains("Проводника", c.Message);
        Assert.Contains("Освобождено 40 МБ", vm.ResultText);
        Assert.Contains("занято 2", vm.ResultText);
        Assert.Contains("свежих 3", vm.ResultText);
        Assert.False(vm.CanUndoResult);
        Assert.Equal(0, vm.CheckedDiskCount);
    }

    [Fact]
    public async Task Clean_cancelled_sends_only_measure()
    {
        var (vm, agent, dialogs) = await Open();
        dialogs.ConfirmAnswer = false;
        Row(vm, "cleanup:temp-user").IsChecked = true;

        vm.CleanDiskCommand.Execute(null);
        await vm.LastClean;

        Assert.Single(agent.Sent);
        Assert.True(Assert.IsType<CleanCategoriesRequest>(agent.Sent[0]).MeasureOnly);
        Assert.Single(dialogs.Confirms);
    }

    [Fact]
    public async Task Clean_partial_result_is_a_warning_with_totals()
    {
        var (vm, _, _) = await Open(r => r is CleanCategoriesRequest { MeasureOnly: true }
            ? new AgentResponse(r.Id, true, ResponseCodes.Ok, null, null, null, null, [Measure("temp-user", 1_048_576)])
            : new AgentResponse(r.Id, true, ResponseCodes.Partial, null, Guid.NewGuid(),
                [new StepResult(1, 0, StepKind.CleanupCategory, StepState.Done, null)], null,
                [Cleaned("temp-user", 1_048_576, 1, 0)]));
        Row(vm, "cleanup:temp-user").IsChecked = true;

        vm.CleanDiskCommand.Execute(null);
        await vm.LastClean;

        Assert.Equal(ResultKind.Warning, vm.Result);
        Assert.Contains("Освобождено", vm.ResultText);
        Assert.Contains("занято 1", vm.ResultText);
        Assert.Contains("Часть категорий", vm.ResultText);
    }

    [Fact]
    public async Task Clean_denied_measure_shows_reason_and_never_cleans()
    {
        var (vm, agent, dialogs) = await Open(r => new AgentResponse(r.Id, false, ResponseCodes.PolicyDenied, "нельзя"));
        Row(vm, "cleanup:temp-user").IsChecked = true;

        vm.CleanDiskCommand.Execute(null);
        await vm.LastClean;

        Assert.Single(agent.Sent);
        Assert.Empty(dialogs.Confirms);
        Assert.True(vm.HasResult);
        Assert.NotEqual(ResultKind.Success, vm.Result);
    }

    [Fact]
    public async Task Registry_dry_run_comes_before_confirmation_then_real_apply_is_undoable()
    {
        var opId = Guid.NewGuid();
        var (vm, agent, dialogs) = await Open(r => r is ApplyFindingsRequest { DryRun: true } d
            ? new AgentResponse(r.Id, true, ResponseCodes.DryRun, null, null, null, null, d.Items.Select((i, n) =>
                new FactEntry("regkey:hklm:-:x64:k" + n,
                    "{\"v\":1,\"display\":\"Ключ " + n + "\",\"subkeys\":" + (n + 2) + ",\"revertible\":true,\"rule\":null}")).ToList())
            : new AgentResponse(r.Id, true, ResponseCodes.Ok, null, opId,
                [new StepResult(1, 0, StepKind.RegistryKeyDelete, StepState.Done, null),
                 new StepResult(2, 1, StepKind.RegistryKeyDelete, StepState.Done, null)]));
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;
        Row(vm, "regkey:hklm:-:x64:b").IsChecked = true;
        Assert.Equal(2, vm.CheckedRegistryCount);

        vm.ApplyRegistryCommand.Execute(null);
        await vm.LastRegistry;

        var first = Assert.IsType<ApplyFindingsRequest>(agent.Sent[0]);
        var second = Assert.IsType<ApplyFindingsRequest>(agent.Sent[1]);
        Assert.True(first.DryRun);
        Assert.False(second.DryRun);
        Assert.Null(second.ExpertConfirmations);
        var c = Assert.Single(dialogs.Confirms);
        Assert.Equal("Удалить из реестра: 2 записи", c.Title);
        Assert.Equal("Удалить", c.ConfirmText);
        Assert.Contains("подключей: 2", c.Message);
        Assert.Contains("90 дней", c.Message);
        Assert.Equal(ResultKind.Success, vm.Result);
        Assert.True(vm.CanUndoResult);
    }

    [Fact]
    public async Task Registry_cancel_never_sends_real_request()
    {
        var (vm, agent, dialogs) = await Open(r => new AgentResponse(r.Id, true, ResponseCodes.DryRun, null, null, null, null,
            [new FactEntry("regkey:hklm:-:x64:k", "{\"v\":1,\"display\":\"Ключ\",\"subkeys\":1,\"revertible\":true,\"rule\":null}")]));
        dialogs.ConfirmAnswer = false;
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;

        vm.ApplyRegistryCommand.Execute(null);
        await vm.LastRegistry;

        Assert.Single(agent.Sent);
        Assert.True(Assert.IsType<ApplyFindingsRequest>(agent.Sent[0]).DryRun);
        Assert.Single(dialogs.Confirms);
    }

    [Fact]
    public async Task Registry_denied_records_are_listed_with_reason_and_all_denied_skips_dialog()
    {
        var (vm, agent, dialogs) = await Open(r => new AgentResponse(r.Id, true, ResponseCodes.DryRun, null, null, null, null,
            [new FactEntry("regkey:hklm:-:x64:k", "{\"v\":1,\"display\":\"Тяжёлый ключ\",\"subkeys\":9,\"revertible\":false,\"rule\":\"reg-tree-too-large\"}")]));
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;

        vm.ApplyRegistryCommand.Execute(null);
        await vm.LastRegistry;

        Assert.Single(agent.Sent);
        Assert.Empty(dialogs.Confirms);
        Assert.Equal(ResultKind.Warning, vm.Result);
        Assert.Contains("Тяжёлый ключ", vm.ResultHint);
        Assert.Contains("слишком много", vm.ResultHint);
    }

    [Fact]
    public async Task Registry_partial_denial_is_shown_in_dialog()
    {
        var (vm, _, dialogs) = await Open(r => r is ApplyFindingsRequest { DryRun: true }
            ? new AgentResponse(r.Id, true, ResponseCodes.DryRun, null, null, null, null,
                [new FactEntry("regkey:hklm:-:x64:k1", "{\"v\":1,\"display\":\"Хороший\",\"subkeys\":0,\"revertible\":true,\"rule\":null}"),
                 new FactEntry("regkey:hklm:-:x64:k2", "{\"v\":1,\"display\":\"Плохой\",\"subkeys\":0,\"revertible\":false,\"rule\":\"reg-tree-has-link\"}")])
            : new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid()));
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;
        Row(vm, "regkey:hklm:-:x64:b").IsChecked = true;

        vm.ApplyRegistryCommand.Execute(null);
        await vm.LastRegistry;

        var c = Assert.Single(dialogs.Confirms);
        Assert.Equal("Удалить из реестра: 1 запись", c.Title);
        Assert.Contains("Не будут тронуты", c.Message);
        Assert.Contains("Плохой", c.Message);
    }

    [Fact]
    public async Task View_only_disables_both_commands()
    {
        var (vm, agent, _) = await Open(access: AgentAccess.ViewOnly);
        Row(vm, "cleanup:temp-user").IsChecked = true;
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;

        Assert.False(vm.CleanDiskCommand.CanExecute(null));
        Assert.False(vm.ApplyRegistryCommand.CanExecute(null));
        Assert.NotEqual("", vm.ActionHint);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Clear_selection_unchecks_everything()
    {
        var (vm, _, _) = await Open();
        Row(vm, "cleanup:temp-user").IsChecked = true;
        Row(vm, "regkey:hklm:-:x64:a").IsChecked = true;
        Assert.True(vm.HasChecked);

        vm.ClearSelectionCommand.Execute(null);

        Assert.False(vm.HasChecked);
        Assert.Equal(0, vm.CheckedDiskCount + vm.CheckedRegistryCount);
    }

    [Fact]
    public void Main_menu_has_no_disk_cleanup_stub()
    {
        var vm = new MainViewModel(CompositionRootDemo());
        Assert.DoesNotContain(vm.AllNavItems, n => n.Title == "Очистка диска");
    }

    static Chistovik.App.AppServices CompositionRootDemo() => Chistovik.App.CompositionRoot.CreateDemo(new FakeDialogs());
}
