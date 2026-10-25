using Chistovik.App;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.App.ViewModels;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

sealed class StateSource(TweakState state = TweakState.Off) : ITweakStateSource
{
    public Task<IReadOnlyDictionary<string, TweakState>> ReadAsync(IReadOnlyList<TweakEntry> tweaks)
        => Task.FromResult<IReadOnlyDictionary<string, TweakState>>(tweaks.ToDictionary(t => t.Id, _ => state));
}

sealed class StartupSrc(params StartupInfo[] items) : IStartupSource
{
    public Task<IReadOnlyList<StartupInfo>> GetStartupAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StartupInfo>>(items);
}

sealed class ServiceSrc(params ServiceInfo[] items) : IServiceSource
{
    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServiceInfo>>(items);
}

sealed class TaskSrc(params TaskInfo[] items) : ITaskSource
{
    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TaskInfo>>(items);
}

sealed class JournalSrc(IReadOnlyList<JournalOperation> ops, IReadOnlyList<JournalStep>? steps = null) : IJournalSource
{
    public Task<IReadOnlyList<JournalOperation>> GetOperationsAsync() => Task.FromResult(ops);
    public Task<IReadOnlyList<JournalStep>> GetStepsAsync(Guid operationId) => Task.FromResult(steps ?? Array.Empty<JournalStep>());
}

sealed class ThrowingJournal : IJournalSource
{
    public Task<IReadOnlyList<JournalOperation>> GetOperationsAsync() => throw new InvalidOperationException("база повреждена");
    public Task<IReadOnlyList<JournalStep>> GetStepsAsync(Guid operationId) => throw new InvalidOperationException();
}

sealed class PowerSrc(PowerSnapshot snap) : IPowerSource
{
    public Task<PowerSnapshot> ReadAsync() => Task.FromResult(snap);
}

public class TweaksViewModelTests
{
    static TweaksViewModel Create(FakeAgentClient agent, FakeDialogs dialogs, FakeEffects? effects = null, FakeNavigator? nav = null,
        TweakState state = TweakState.Off)
        => new("Твики", "t", "tw", TweakCatalog.Shared, new StateSource(state), Make.Changes(agent), dialogs,
            effects ?? new FakeEffects(), nav);

    static IEnumerable<TweakRowViewModel> Rows(TweaksViewModel vm) => vm.Groups.SelectMany(g => g.Items);

    [Fact]
    public async Task Groups_by_category_and_keeps_unverified_mark()
    {
        var vm = Create(new FakeAgentClient(), new FakeDialogs());
        await vm.Loaded;

        Assert.Equal(PageState.Ready, vm.State);
        Assert.NotEmpty(vm.Groups);
        Assert.Equal(Rows(vm).Count(), TweakCatalog.Shared.Tweaks.Count(t => t.Area == "tw"));
        Assert.Equal(TweakCatalog.Shared.Tweaks.Count(t => t.Area == "tw" && !t.Verified), Rows(vm).Count(r => r.IsUnverified));
    }

    [Fact]
    public async Task Apply_sends_only_toggled_rows()
    {
        var agent = new FakeAgentClient();
        var vm = Create(agent, new FakeDialogs());
        await vm.Loaded;
        var safe = Rows(vm).Where(r => !r.IsCareful && !r.IsMachineWide && !r.Entry.RequiresAdmin).Take(2).ToList();
        Assert.Equal(2, safe.Count);
        foreach (var r in safe) r.IsOn = true;

        Assert.Equal(2, vm.PendingCount);
        await vm.ApplyAsync();

        var sent = Assert.IsType<ApplyTweaksRequest>(Assert.Single(agent.Sent));
        Assert.Equal(safe.Select(r => r.Id).OrderBy(x => x), sent.Changes.Select(c => c.TweakId).OrderBy(x => x));
        Assert.All(sent.Changes, c => Assert.True(c.Enabled));
        Assert.Equal(ResultKind.Success, vm.Result);
    }

    [Fact]
    public async Task Careful_tweak_asks_for_confirmation_with_what_changes_and_where_to_undo()
    {
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var vm = Create(agent, dialogs);
        await vm.Loaded;
        var careful = Rows(vm).FirstOrDefault(r => r.IsCareful || r.IsMachineWide || r.Entry.RequiresAdmin);
        if (careful is null) return;
        careful.IsOn = !careful.IsOn;

        await vm.ApplyAsync();

        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Contains("Что изменится", confirm.Message);
        Assert.Contains("Откуда вернуть", confirm.Message);
        Assert.Contains("Журнал и откат", confirm.Message);
        Assert.Empty(agent.Sent); // отказ в подтверждении ничего не отправляет
    }

    [Fact]
    public async Task Nothing_pending_sends_nothing()
    {
        var agent = new FakeAgentClient();
        var vm = Create(agent, new FakeDialogs());
        await vm.Loaded;

        await vm.ApplyAsync();

        Assert.Empty(agent.Sent);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task View_only_disables_apply_and_never_sends()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var vm = Create(agent, new FakeDialogs());
        await vm.Loaded;
        Rows(vm).First().IsOn = true;

        await vm.ApplyAsync();

        Assert.True(vm.ViewOnly);
        Assert.False(vm.CanApply);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.NotEmpty(vm.ApplyHint);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Access_change_updates_apply_state()
    {
        var agent = new FakeAgentClient();
        var vm = Create(agent, new FakeDialogs());
        await vm.Loaded;
        Rows(vm).First().IsOn = true;
        Assert.True(vm.ApplyCommand.CanExecute(null));

        agent.SetAccess(AgentAccess.ViewOnly);

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.RequestRightsCommand.CanExecute(null));
        vm.RequestRightsCommand.Execute(null);
        Assert.Equal(1, agent.RetryCalls);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Partial_result_is_shown_as_warning_with_counts()
    {
        var agent = new FakeAgentClient
        {
            Handler = r => new AgentResponse(r.Id, false, ResponseCodes.Partial, null, Guid.NewGuid(), new[]
            {
                new StepResult(1, 0, StepKind.RegistryValue, StepState.Done, null),
                new StepResult(2, 1, StepKind.RegistryValue, StepState.Failed, null),
            }),
        };
        var vm = Create(agent, new FakeDialogs());
        await vm.Loaded;
        foreach (var r in Rows(vm).Where(r => !r.IsCareful && !r.IsMachineWide && !r.Entry.RequiresAdmin).Take(2)) r.IsOn = true;

        await vm.ApplyAsync();

        Assert.Equal(ResultKind.Warning, vm.Result);
        Assert.Contains("1 из 2", vm.ResultText);
        Assert.True(vm.HasChange);
    }

    [Fact]
    public async Task Rollback_after_apply_leads_to_journal()
    {
        var nav = new FakeNavigator();
        var vm = Create(new FakeAgentClient(), new FakeDialogs(), nav: nav);
        await vm.Loaded;

        vm.OpenJournalCommand.Execute(null);

        Assert.Equal(new[] { "Журнал и откат" }, nav.Opened);
    }

    [Fact]
    public async Task Explorer_restart_is_a_separate_button()
    {
        var effects = new FakeEffects();
        var vm = Create(new FakeAgentClient(), new FakeDialogs(), effects);
        await vm.Loaded;

        vm.RestartExplorerCommand.Execute(null);

        Assert.Equal(1, effects.Restarts);
    }

    [Fact]
    public void Describe_names_registry_target_and_journal()
    {
        var entry = TweakCatalog.Shared.Tweaks.First();
        var row = new TweakRowViewModel(entry, TweakState.Off, () => { }) { IsOn = true };

        var text = TweaksViewModel.Describe(new[] { row });

        Assert.Contains(entry.Title, text);
        Assert.Contains(entry.Key, text);
        Assert.Contains("Откуда вернуть", text);
    }
}

public class StartupViewModelTests
{
    static StartupInfo Entry(string id = "run:hku:Discord", bool enabled = true, bool canToggle = true)
        => new(id, "Discord", "discord.exe", "Реестр, текущий пользователь", enabled, canToggle, canToggle ? null : "Однократный запуск");

    [Fact]
    public async Task Toggle_sends_set_startup_with_entry_id()
    {
        var agent = new FakeAgentClient();
        var vm = new StartupViewModel(new StartupSrc(Entry()), Make.Changes(agent));
        await vm.Loaded;

        vm.Items[0].IsEnabled = false;
        await vm.LastToggle;

        var req = Assert.IsType<SetStartupEntryRequest>(Assert.Single(agent.Sent));
        Assert.Equal("run:hku:Discord", req.EntryId);
        Assert.False(req.Enabled);
        Assert.False(vm.Items[0].IsEnabled);
    }

    [Fact]
    public async Task Denied_change_returns_the_switch()
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, false, ResponseCodes.PolicyDenied) };
        var vm = new StartupViewModel(new StartupSrc(Entry()), Make.Changes(agent));
        await vm.Loaded;

        vm.Items[0].IsEnabled = false;
        await vm.LastToggle;

        Assert.True(vm.Items[0].IsEnabled);
        Assert.Equal(ResultKind.Error, vm.Result);
        Assert.Single(agent.Sent); // возврат переключателя не создаёт второй запрос
    }

    [Fact]
    public async Task Read_only_entry_is_not_sent()
    {
        var agent = new FakeAgentClient();
        var vm = new StartupViewModel(new StartupSrc(Entry("", true, canToggle: false)), Make.Changes(agent));
        await vm.Loaded;
        Assert.False(vm.Items[0].CanChange);

        vm.Items[0].IsEnabled = false;
        await vm.LastToggle;

        Assert.Empty(agent.Sent);
        Assert.True(vm.Items[0].IsEnabled);
    }

    [Fact]
    public async Task View_only_blocks_switches_and_requests()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var vm = new StartupViewModel(new StartupSrc(Entry()), Make.Changes(agent));
        await vm.Loaded;
        Assert.False(vm.Items[0].CanChange);
        Assert.NotEmpty(vm.Items[0].ToggleHint);

        vm.Items[0].IsEnabled = false;
        await vm.LastToggle;

        Assert.Empty(agent.Sent);
        Assert.True(vm.Items[0].IsEnabled);
    }

    [Fact]
    public async Task Empty_list_is_a_state_not_an_error()
    {
        var vm = new StartupViewModel(new StartupSrc(), null);
        await vm.Loaded;

        Assert.Equal(PageState.Empty, vm.State);
        Assert.False(vm.IsError);
        Assert.NotEmpty(vm.StateMessage);
    }
}

public class PowerViewModelTests
{
    static PowerSettingEntry Setting() => PowerCatalog.Shared.Settings.First();

    static PowerSettingRowViewModel Row(string ac, string dc, (uint, uint)? current = null)
    {
        var row = new PowerSettingRowViewModel(Setting(), current, new Chistovik.App.Mvvm.RelayCommand(() => { }));
        row.AcText = ac;
        row.DcText = dc;
        return row;
    }

    [Fact]
    public void Value_out_of_range_is_rejected_with_message()
    {
        var s = Setting();
        var row = Row((s.Max + 1).ToString(), "");

        Assert.False(row.TryRead(out _, out _));
        Assert.True(row.HasError);
    }

    [Fact]
    public void Not_a_number_is_rejected()
    {
        var row = Row("abc", "");
        Assert.False(row.TryRead(out _, out _));
        Assert.True(row.HasError);
    }

    [Fact]
    public void Unchanged_value_is_not_sent()
    {
        var s = Setting();
        var row = Row(s.Min.ToString(), s.Max.ToString(), (s.Min, s.Max));

        Assert.True(row.TryRead(out var ac, out var dc));
        Assert.Null(ac);
        Assert.Null(dc);
    }

    [Fact]
    public void Empty_field_means_do_not_change()
    {
        var s = Setting();
        var row = Row("", s.Max.ToString());

        Assert.True(row.TryRead(out var ac, out var dc));
        Assert.Null(ac);
        Assert.Equal(s.Max, dc);
    }

    [Fact]
    public async Task Apply_setting_sends_changed_side_only()
    {
        var s = Setting();
        var agent = new FakeAgentClient();
        var snap = new PowerSnapshot(null, new Dictionary<string, (uint Ac, uint Dc)> { [s.Id] = (s.Min, s.Min) }, null);
        var vm = new PowerViewModel(PowerCatalog.Shared, new PowerSrc(snap), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;
        var row = vm.Settings.First(x => x.Entry.Id == s.Id);
        row.AcText = s.Max.ToString();

        await vm.ApplySettingAsync(row);

        var req = Assert.IsType<SetPowerSettingRequest>(Assert.Single(agent.Sent));
        Assert.Equal(s.Id, req.SettingId);
        Assert.Equal(s.Max, req.Ac);
        Assert.Null(req.Dc);
    }

    [Fact]
    public async Task Apply_scheme_sends_plan_and_view_only_does_not()
    {
        var agent = new FakeAgentClient();
        var snap = new PowerSnapshot(null, new Dictionary<string, (uint Ac, uint Dc)>(), null);
        var vm = new PowerViewModel(PowerCatalog.Shared, new PowerSrc(snap), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;
        var high = vm.Schemes.First(x => x.Entry.Id == "high");

        await vm.ApplySchemeAsync(high.Entry);
        Assert.Equal(PowerPlan.High, Assert.IsType<SetPowerSchemeRequest>(Assert.Single(agent.Sent)).Plan);

        agent.SetAccess(AgentAccess.ViewOnly);
        await vm.ApplySchemeAsync(high.Entry);
        Assert.Single(agent.Sent);
    }

    [Fact]
    public async Task Schemes_absent_on_modern_standby_device_are_marked_and_cannot_be_chosen()
    {
        var cat = PowerCatalog.Shared;
        var bal = cat.FindScheme("bal")!;
        var agent = new FakeAgentClient();
        var snap = new PowerSnapshot(bal.Guid, new Dictionary<string, (uint Ac, uint Dc)>(), null,
            InstalledSchemes: new[] { bal.Guid }, ModernStandby: true);
        var vm = new PowerViewModel(cat, new PowerSrc(snap), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;

        Assert.True(vm.Schemes.First(x => x.Entry.Id == "bal").IsAvailable);
        foreach (var id in new[] { "saver", "high", "ult" })
        {
            var row = vm.Schemes.First(x => x.Entry.Id == id);
            Assert.True(row.IsMissing, id);
            Assert.False(row.CanChoose, id);
            Assert.False(row.ApplyCommand.CanExecute(null), id);
            await vm.ApplySchemeAsync(row.Entry);
        }
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Unknown_scheme_list_keeps_builtin_schemes_available()
    {
        var snap = new PowerSnapshot(null, new Dictionary<string, (uint Ac, uint Dc)>(), null);
        var vm = new PowerViewModel(PowerCatalog.Shared, new PowerSrc(snap), Make.Changes(new FakeAgentClient()), new FakeDialogs());
        await vm.Loaded;

        Assert.All(vm.Schemes, r => Assert.True(r.IsAvailable));
    }
}

public class ServicesAndTasksViewModelTests
{
    static ServiceInfo Svc(string name, Changeability c) => new(name, name + " display", StartMode.Automatic, c, "причина", @"C:\x.exe");

    [Fact]
    public async Task Only_modifiable_service_is_changed_after_confirmation()
    {
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs();
        var vm = new ServicesViewModel(new ServiceSrc(Svc("Mod", Changeability.Modifiable), Svc("Ro", Changeability.ReadOnly), Svc("Forb", Changeability.Forbidden)),
            Make.Changes(agent), dialogs);
        await vm.Loaded;

        vm.Selected = vm.Items.First(x => x.Name == "Ro");
        Assert.False(vm.CanChangeSelected);
        await vm.SetStartAsync("Disabled");
        vm.Selected = vm.Items.First(x => x.Name == "Forb");
        await vm.SetStartAsync("Disabled");
        Assert.Empty(agent.Sent);

        vm.Selected = vm.Items.First(x => x.Name == "Mod");
        Assert.True(vm.CanChangeSelected);
        await vm.SetStartAsync("Disabled");

        var req = Assert.IsType<SetServiceStartRequest>(Assert.Single(agent.Sent));
        Assert.Equal("Mod", req.ServiceName);
        Assert.Equal(StartMode.Disabled, req.Start);
        Assert.Single(dialogs.Confirms);
        Assert.Contains("Откуда вернуть", dialogs.Confirms[0].Message);
    }

    [Fact]
    public async Task Declined_confirmation_sends_nothing()
    {
        var agent = new FakeAgentClient();
        var vm = new ServicesViewModel(new ServiceSrc(Svc("Mod", Changeability.Modifiable)), Make.Changes(agent), new FakeDialogs { ConfirmAnswer = false });
        await vm.Loaded;
        vm.Selected = vm.Items[0];

        await vm.SetStartAsync("Manual");

        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Garbage_start_key_is_ignored()
    {
        var agent = new FakeAgentClient();
        var vm = new ServicesViewModel(new ServiceSrc(Svc("Mod", Changeability.Modifiable)), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;
        vm.Selected = vm.Items[0];

        await vm.SetStartAsync("Boot");
        await vm.SetStartAsync("99");

        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Service_filter_and_search_work()
    {
        var vm = new ServicesViewModel(new ServiceSrc(Svc("Alpha", Changeability.Modifiable), Svc("Beta", Changeability.ReadOnly)), null, new FakeDialogs());
        await vm.Loaded;

        vm.FilterCommand.Execute("mod");
        Assert.Equal(new[] { "Alpha" }, vm.Items.Select(x => x.Name).ToArray());
        vm.FilterCommand.Execute("all");
        vm.Search = "bet";
        Assert.Equal(new[] { "Beta" }, vm.Items.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Services_view_only_never_sends()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var vm = new ServicesViewModel(new ServiceSrc(Svc("Mod", Changeability.Modifiable)), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;
        vm.Selected = vm.Items[0];

        Assert.False(vm.CanChangeSelected);
        await vm.SetStartAsync("Manual");

        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Read_only_task_cannot_be_changed_but_modifiable_can()
    {
        var agent = new FakeAgentClient();
        var vm = new TasksViewModel(new TaskSrc(
            new TaskInfo(@"\Vendor\A", "A", true, Changeability.ReadOnly, "r", "a.exe"),
            new TaskInfo(@"\Vendor\B", "B", true, Changeability.Modifiable, "r", "b.exe")), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;

        vm.Selected = vm.Items.First(x => x.Name == "A");
        Assert.False(vm.CanChangeSelected);
        await vm.SetEnabledAsync(false);
        Assert.Empty(agent.Sent);

        vm.Selected = vm.Items.First(x => x.Name == "B");
        await vm.SetEnabledAsync(false);

        var req = Assert.IsType<SetTaskEnabledRequest>(Assert.Single(agent.Sent));
        Assert.Equal(@"\Vendor\B", req.TaskPath);
        Assert.False(req.Enabled);
    }

    [Fact]
    public async Task Carveout_service_allows_disable_and_downgrade_only_with_warning_text()
    {
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs();
        var svc = new ServiceInfo("DiagTrack", "DiagTrack", StartMode.Automatic, Changeability.Modifiable, "r", @"C:\x.exe", "service-catalog-carveout");
        var vm = new ServicesViewModel(new ServiceSrc(svc), Make.Changes(agent), dialogs);
        await vm.Loaded;
        vm.Selected = vm.Items[0];

        Assert.False(vm.SetStartCommand.CanExecute("Automatic"));
        Assert.False(vm.SetStartCommand.CanExecute("AutomaticDelayed"));
        Assert.True(vm.SetStartCommand.CanExecute("Manual"));
        Assert.True(vm.SetStartCommand.CanExecute("Disabled"));
        await vm.SetStartAsync("AutomaticDelayed");
        Assert.Empty(agent.Sent);

        await vm.SetStartAsync("Disabled");
        Assert.IsType<SetServiceStartRequest>(Assert.Single(agent.Sent));
        Assert.Contains("не останавливает уже запущенную службу", dialogs.Confirms[0].Message);
        Assert.Contains("Крупное обновление Windows", dialogs.Confirms[0].Message);
    }

    [Fact]
    public async Task Carveout_service_cannot_go_manual_from_disabled()
    {
        var svc = new ServiceInfo("Fax", "Fax", StartMode.Disabled, Changeability.Modifiable, "r", @"C:\x.exe", "service-catalog-carveout");
        var vm = new ServicesViewModel(new ServiceSrc(svc), Make.Changes(new FakeAgentClient()), new FakeDialogs());
        await vm.Loaded;
        vm.Selected = vm.Items[0];

        Assert.False(vm.SetStartCommand.CanExecute("Manual"));
    }

    [Fact]
    public async Task Carveout_task_can_be_disabled_but_never_enabled_directly()
    {
        var agent = new FakeAgentClient();
        var task = new TaskInfo(@"\Microsoft\Windows\Maps\MapsUpdateTask", "MapsUpdateTask", false, Changeability.Modifiable, "r", "x.exe", "task-catalog-carveout");
        var on = task with { Path = @"\Microsoft\Windows\Autochk\Proxy", Name = "Proxy", Enabled = true };
        var vm = new TasksViewModel(new TaskSrc(task, on), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;

        vm.Selected = vm.Items.First(x => x.Name == "MapsUpdateTask");
        Assert.False(vm.ShowEnableButton);
        Assert.False(vm.SetEnabledCommand.CanExecute("on"));
        await vm.SetEnabledAsync(true);
        Assert.Empty(agent.Sent);

        vm.Selected = vm.Items.First(x => x.Name == "Proxy");
        Assert.True(vm.SetEnabledCommand.CanExecute("off"));
        await vm.SetEnabledAsync(false);
        var req = Assert.IsType<SetTaskEnabledRequest>(Assert.Single(agent.Sent));
        Assert.False(req.Enabled);
    }
}

public class JournalAndModesViewModelTests
{
    static JournalOperation Op(Guid id, bool reversible, OperationState state = OperationState.Done)
        => new(id, OperationKind.Tweaks, "Твики", new DateTime(2026, 10, 24, 12, 0, 0), state, "Завершено", reversible, null);

    [Fact]
    public async Task Selecting_operation_loads_steps_and_rollback_uses_undo_flow()
    {
        var id = Guid.NewGuid();
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs();
        var steps = new[] { new JournalStep(1, "Значение реестра", "HKCU\\x", StepState.Done, "Выполнено") };
        var vm = new JournalViewModel(new JournalSrc(new[] { Op(id, true) }, steps), Make.Changes(agent), dialogs);
        await vm.Loaded;
        await vm.LastStepsLoad;

        Assert.Equal(id, vm.Selected!.Model.Id);
        Assert.Single(vm.Steps);

        await vm.RollbackAsync();

        var req = Assert.IsType<UndoRequest>(Assert.Single(agent.Sent));
        Assert.Equal(id, req.OperationId);
        Assert.Null(req.Resolutions);
        Assert.Single(dialogs.Confirms);
    }

    [Fact]
    public async Task Rollback_conflict_asks_user_and_resends_with_resolutions()
    {
        var id = Guid.NewGuid();
        var agent = new FakeAgentClient
        {
            Handler = r => r is UndoRequest { Resolutions: null }
                ? new AgentResponse(r.Id, false, ResponseCodes.Conflict, null, null, null, new[] { new ConflictInfo(5, StepKind.RegistryValue, "0", null) })
                : FakeAgentHost.DefaultReply(r),
        };
        var dialogs = new FakeDialogs
        {
            ConflictAnswer = c => c.Select(x => new UndoResolution { StepId = x.StepId, Action = ConflictAction.Overwrite }).ToList(),
        };
        var vm = new JournalViewModel(new JournalSrc(new[] { Op(id, true) }), Make.Changes(agent), dialogs);
        await vm.Loaded;

        await vm.RollbackAsync();

        Assert.Equal(1, dialogs.ConflictCalls);
        var second = Assert.IsType<UndoRequest>(agent.Sent[1]);
        Assert.Equal(ConflictAction.Overwrite, Assert.Single(second.Resolutions!).Action);
    }

    [Fact]
    public async Task Operation_that_cannot_be_rolled_back_sends_nothing()
    {
        var agent = new FakeAgentClient();
        var vm = new JournalViewModel(new JournalSrc(new[] { Op(Guid.NewGuid(), false, OperationState.Undone) }), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;

        await vm.RollbackAsync();

        Assert.Empty(agent.Sent);
        Assert.False(vm.RollbackCommand.CanExecute(null));
        Assert.NotEmpty(vm.RollbackHint);
    }

    [Fact]
    public async Task View_only_disables_rollback()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var vm = new JournalViewModel(new JournalSrc(new[] { Op(Guid.NewGuid(), true) }), Make.Changes(agent), new FakeDialogs());
        await vm.Loaded;

        await vm.RollbackAsync();

        Assert.Empty(agent.Sent);
        Assert.False(vm.RollbackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Empty_journal_and_error_are_distinct_states()
    {
        var empty = new JournalViewModel(new JournalSrc(Array.Empty<JournalOperation>()), null, new FakeDialogs());
        await empty.Loaded;
        Assert.Equal(PageState.Empty, empty.State);

        var broken = new JournalViewModel(new ThrowingJournal(), null, new FakeDialogs());
        await broken.Loaded;
        Assert.Equal(PageState.Error, broken.State);
        Assert.Contains("база повреждена", broken.StateMessage);
    }

    [Fact]
    public async Task Mode_apply_sends_mode_id_after_confirmation_and_offers_undo()
    {
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs();
        var vm = new ModesViewModel(TweakCatalog.Shared, PowerCatalog.Shared, Make.Changes(agent), dialogs);
        await vm.Loaded;
        var mode = TweakCatalog.Shared.Modes.First();

        await vm.ApplyAsync(mode);

        Assert.Equal(mode.Id, Assert.IsType<ApplyModeRequest>(Assert.Single(agent.Sent)).ModeId);
        Assert.Single(dialogs.Confirms);
        Assert.True(vm.CanUndoLast);

        vm.UndoLastCommand.Execute(null);
        await vm.LastAction;

        Assert.IsType<UndoRequest>(agent.Sent[1]);
        Assert.False(vm.CanUndoLast);
    }

    [Fact]
    public async Task Mode_declined_or_view_only_sends_nothing()
    {
        var agent = new FakeAgentClient();
        var vm = new ModesViewModel(TweakCatalog.Shared, PowerCatalog.Shared, Make.Changes(agent), new FakeDialogs { ConfirmAnswer = false });
        await vm.Loaded;
        var mode = TweakCatalog.Shared.Modes.First();

        await vm.ApplyAsync(mode);
        agent.SetAccess(AgentAccess.ViewOnly);
        await vm.ApplyAsync(mode);

        Assert.Empty(agent.Sent);
    }
}

public class OverviewViewModelTests
{
    [Fact]
    public async Task Overview_shows_real_counts_and_rights_state()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var vm = new OverviewViewModel(
            new StartupSrc(new StartupInfo("run:hku:A", "A", "a.exe", "Реестр", true, true, null)),
            new JournalSrc(Array.Empty<JournalOperation>()), Make.Changes(agent));
        await vm.Loaded;

        Assert.Contains("1 из 1", vm.StartupText);
        Assert.Equal("Режим просмотра", vm.RightsTitle);
        Assert.True(vm.ViewOnly);
        Assert.True(vm.RequestRightsCommand.CanExecute(null));
        Assert.False(vm.ShowViewOnlyBanner);
        Assert.Contains("изменений ещё не было", vm.JournalHint);
        Assert.Equal("не применялся", vm.ModeText);
        Assert.StartsWith("не проверено: ", vm.TweaksHint);
    }

    [Fact]
    public async Task Last_applied_mode_is_taken_from_journal()
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0);
        var ops = new[]
        {
            new JournalOperation(Guid.NewGuid(), OperationKind.Mode, "Режим: Старый", now.AddDays(-2), OperationState.Done, "Завершено", true, null),
            new JournalOperation(Guid.NewGuid(), OperationKind.Tweaks, "Твики: a", now, OperationState.Done, "Завершено", true, null),
            new JournalOperation(Guid.NewGuid(), OperationKind.Mode, "Режим: Работа", now.AddDays(-1), OperationState.Done, "Завершено", true, null),
        };
        var vm = new OverviewViewModel(new StartupSrc(), new JournalSrc(ops), Make.Changes(new FakeAgentClient()));
        await vm.Loaded;

        Assert.Equal("Работа", vm.ModeText);
        Assert.Contains("можно откатить: 3", vm.JournalHint);
    }

    [Fact]
    public void Subtitle_says_demo_only_for_demo_data()
    {
        var empty = new JournalSrc(Array.Empty<JournalOperation>());
        var real = new OverviewViewModel(new StartupSrc(), empty);
        var demo = new OverviewViewModel(new StartupSrc(), empty, isDemo: true);

        Assert.Equal("Демонстрационные данные", demo.Subtitle);
        Assert.StartsWith("Состояние системы по реальным данным", real.Subtitle);
    }
}

public class NavigationTests
{
    static MainViewModel Create() => new(CompositionRoot.CreateDemo(new FakeDialogs()));

    [Fact]
    public void Menu_has_only_release_sections_in_order()
    {
        var vm = Create();

        Assert.Equal(
            new[] { "Обзор", "Чистка", "Инвентарь", "Режимы", "Питание", "Автозагрузка", "Службы", "Задачи", "Твики", "Журнал и откат" },
            vm.NavItems.Select(n => n.Title).ToArray());
        Assert.Equal(MainViewModel.ReleaseSections, vm.NavItems.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void Hidden_sections_stay_in_code_and_group_header_is_shown_once()
    {
        var vm = Create();

        Assert.Contains(vm.AllNavItems, n => n.Title == "Предпросмотр");
        Assert.DoesNotContain(vm.NavItems, n => n.Title == "Предпросмотр");
        Assert.Equal(new[] { "Инвентарь", "Режимы" }, vm.NavItems.Where(n => n.GroupStart).Select(n => n.Title).ToArray());
        vm.Open("Предпросмотр");
        Assert.Equal("Обзор", vm.SelectedNav!.Title);
        vm.Open("Инвентарь");
        Assert.Equal("Инвентарь", vm.SelectedNav!.Title);
        vm.Open("Журнал и откат");
        Assert.Equal("Журнал и откат", vm.SelectedNav!.Title);
    }

    [Fact]
    public async Task Demo_overview_has_six_tiles_data_and_demo_subtitle()
    {
        var vm = Create();
        var overview = Assert.IsType<OverviewViewModel>(vm.Current);
        await overview.Loaded;

        Assert.Equal("Демонстрационные данные", overview.Subtitle);
        foreach (var text in new[] { overview.StartupText, overview.ServicesText, overview.TasksText, overview.TweaksText, overview.ModeText, overview.JournalText })
            Assert.NotEqual("—", text);
    }

    [Fact]
    public async Task Selected_sort_survives_reload()
    {
        var vm = new StartupViewModel(new StartupSrc());
        await vm.Loaded;
        var second = vm.SortOptions[1];

        vm.SelectedSort = second;
        await vm.ReloadAsync();

        Assert.Same(second, vm.SelectedSort);
    }
}
