using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Windows;
using Chistovik.App.ViewModels;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace App.Tests;

sealed class EmptyRegistryReader : IRegistryReader
{
    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path) => Array.Empty<string>();
    public IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path) => null;
}

public class ReasonTextsTests
{
    [Fact]
    public void Unknown_rule_says_system_is_not_sure()
    {
        Assert.Equal(ReasonTexts.Unsure, ReasonTexts.Explain("no-such-rule-xyz"));
        Assert.False(ReasonTexts.IsKnown("no-such-rule-xyz"));
    }

    [Fact]
    public void Access_denied_rules_have_russian_text()
    {
        Assert.True(ReasonTexts.IsKnown(ReasonTexts.ServiceNoAccess));
        var text = ReasonTexts.Explain(ReasonTexts.ServiceNoAccess);
        Assert.NotEqual(ReasonTexts.Unsure, text);
        Assert.Matches("[а-яА-Я]", text);
    }
}

public class ScmServiceSourceTests
{
    static ScmServiceSource Make(FakeServiceControl scm)
        => new(scm, new EmptyRegistryReader(), () => Array.Empty<InstalledProgram>(), new FakeFileTrust(), new ServicePolicy(PolicyContext.Fixed));

    [Fact]
    public void Unopenable_service_is_listed_as_no_access_and_does_not_crash()
    {
        var scm = new FakeServiceControl();
        scm.Add(new ServiceRaw("Ok", 0x10, 3, false, @"C:\Program Files\Vendor\ok.exe", false, 0, 0, false));
        // имя есть в списке, но открыть нельзя: подставной SCM в этом случае отдаёт null
        var list = new ThrowingNames(scm, "Ghost");
        var src = new ScmServiceSource(list, new EmptyRegistryReader(), () => Array.Empty<InstalledProgram>(), new FakeFileTrust(), new ServicePolicy(PolicyContext.Fixed));

        var rows = src.Read();

        var ghost = Assert.Single(rows, r => r.Name == "Ghost");
        Assert.True(ghost.NoAccess);
        Assert.Equal(Changeability.ReadOnly, ghost.Class);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Windows_service_is_never_modifiable()
    {
        var scm = new FakeServiceControl();
        scm.Add(new ServiceRaw("WinSvc", 0x20, 2, false, @"C:\Windows\System32\svchost.exe -k netsvcs", false, 3, 0, true));
        var trust = new FakeFileTrust();
        trust.SetSigned(@"C:\Windows\System32\svchost.exe", microsoft: true);
        var src = new ScmServiceSource(scm, new EmptyRegistryReader(), () => Array.Empty<InstalledProgram>(), trust, new ServicePolicy(PolicyContext.Fixed));

        var row = Assert.Single(src.Read());

        Assert.NotEqual(Changeability.Modifiable, row.Class);
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        Assert.False(row.NoAccess);
    }

    [Fact]
    public void Cancelled_token_stops_the_read()
    {
        var scm = new FakeServiceControl();
        scm.Add(new ServiceRaw("A", 0x10, 3, false, @"C:\x\a.exe", false, 0, 0, false));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Make(scm).Read(cts.Token));
    }

    /// <summary>Список имён из подставного SCM плюс одно имя, которое открыть нельзя.</summary>
    sealed class ThrowingNames(FakeServiceControl inner, string ghost) : IServiceControl
    {
        public IReadOnlyList<string> ListNames() => inner.ListNames().Append(ghost).ToList();
        public IServiceHandle? Open(string name, bool write)
            => name == ghost ? throw new UnauthorizedAccessException("нет доступа") : inner.Open(name, write);
    }
}

public class SchedulerTaskSourceTests
{
    static TaskRaw Task(string path, string? user = null, string level = "LeastPrivilege")
        => new(path, true, "<Task/>", user, level, "InteractiveToken", new[] { new TaskAction("Exec", @"C:\Vendor\run.exe", null) });

    [Fact]
    public void Microsoft_tree_is_not_modifiable_and_vendor_task_has_summary()
    {
        var sch = new FakeTaskScheduler();
        sch.Add(Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        sch.Add(Task(@"\Vendor\Updater"));
        var src = new SchedulerTaskSource(sch, new TaskPolicy(PolicyContext.Fixed));

        var rows = src.Read();

        Assert.Equal(2, rows.Count);
        var ms = rows.Single(r => r.Path.StartsWith(@"\Microsoft"));
        Assert.NotEqual(Changeability.Modifiable, ms.Class);
        var vendor = rows.Single(r => r.Path == @"\Vendor\Updater");
        Assert.Contains("run.exe", vendor.Summary);
    }

    [Fact]
    public void Task_that_cannot_be_opened_becomes_no_access_row()
    {
        var sch = new ThrowingScheduler(@"\Vendor\Locked");
        var src = new SchedulerTaskSource(sch, new TaskPolicy(PolicyContext.Fixed));

        var row = Assert.Single(src.Read());

        Assert.True(row.NoAccess);
        Assert.Equal(Changeability.ReadOnly, row.Class);
    }

    sealed class ThrowingScheduler(string path) : ITaskScheduler
    {
        public IReadOnlyList<string> ListPaths() => new[] { path };
        public ITaskHandle? Open(string p) => throw new UnauthorizedAccessException("нет доступа");
    }
}

public class ListPageViewModelTests
{
    static StartupInfo Entry(string name, bool enabled = true, bool canToggle = true, string origin = "Реестр, текущий пользователь")
        => new(canToggle ? "run:hku:" + name : "", name, name.ToLowerInvariant() + ".exe", origin, enabled, canToggle, canToggle ? null : "Однократный запуск",
            canToggle ? Changeability.Modifiable : Changeability.ReadOnly);

    [Fact]
    public async Task Startup_search_filters_by_name_and_command()
    {
        var vm = new StartupViewModel(new StartupSrc(Entry("Discord"), Entry("Steam"), Entry("Spotify")));
        await vm.Loaded;

        vm.Search = "ste";
        await vm.FilterPending;

        Assert.Equal("Steam", Assert.Single(vm.Items).Name);
    }

    [Fact]
    public async Task Startup_filter_read_only_shows_only_read_only_rows()
    {
        var vm = new StartupViewModel(new StartupSrc(Entry("A"), Entry("B", canToggle: false)));
        await vm.Loaded;

        vm.FilterCommand.Execute("ro");
        await vm.FilterPending;

        Assert.Equal("B", Assert.Single(vm.Items).Name);

        vm.FilterCommand.Execute("mod");
        await vm.FilterPending;
        Assert.Equal("A", Assert.Single(vm.Items).Name);
    }

    [Fact]
    public async Task Startup_sort_enabled_first()
    {
        var vm = new StartupViewModel(new StartupSrc(Entry("A", enabled: false), Entry("B", enabled: true)));
        await vm.Loaded;

        vm.SortCommand.Execute("state");
        await vm.FilterPending;

        Assert.Equal("B", vm.Items[0].Name);
    }

    [Fact]
    public async Task Long_list_is_filtered_in_background_and_result_is_correct()
    {
        var rows = Enumerable.Range(0, ListPageViewModel<StartupRowViewModel>.SyncLimit + 500)
            .Select(i => Entry("App" + i)).ToArray();
        var vm = new StartupViewModel(new StartupSrc(rows));
        await vm.Loaded;

        vm.Search = "App2499";
        await vm.FilterPending;

        Assert.Equal("App2499", Assert.Single(vm.Items).Name);
    }

    [Fact]
    public async Task Services_search_and_apply_tooltip_for_read_only_row()
    {
        var vm = new ServicesViewModel(new ServiceSrc(
            new ServiceInfo("Alpha", "Alpha display", Chistovik.Core.Ports.StartMode.Automatic, Changeability.ReadOnly, "защищено", @"C:\a.exe"),
            new ServiceInfo("Beta", "Beta display", Chistovik.Core.Ports.StartMode.Manual, Changeability.Modifiable, "ок", @"C:\b.exe")), null, new FakeDialogs());
        await vm.Loaded;

        vm.Search = "alpha";
        await vm.FilterPending;
        Assert.Equal("Alpha", Assert.Single(vm.Items).Name);

        vm.Selected = vm.Items[0];
        Assert.False(vm.CanChangeSelected);
        Assert.False(string.IsNullOrWhiteSpace(vm.ApplyTooltip));
    }

    [Fact]
    public async Task No_access_service_row_says_so_in_russian()
    {
        var vm = new ServicesViewModel(new ServiceSrc(
            new ServiceInfo("Locked", "Locked", null, Changeability.ReadOnly, ReasonTexts.Explain(ReasonTexts.ServiceNoAccess), "", ReasonTexts.ServiceNoAccess, NoAccess: true)),
            null, new FakeDialogs());
        await vm.Loaded;

        Assert.Equal("Нет доступа", vm.Items[0].ClassText);
    }

    [Fact]
    public async Task Tasks_sort_by_path_and_search_by_summary()
    {
        var vm = new TasksViewModel(new TaskSrc(
            new TaskInfo(@"\B\Task", "Task", true, Changeability.Modifiable, "r", "beta.exe"),
            new TaskInfo(@"\A\Task", "Task", true, Changeability.ReadOnly, "r", "alpha.exe")), null, new FakeDialogs());
        await vm.Loaded;

        vm.SortCommand.Execute("path");
        await vm.FilterPending;
        Assert.Equal(@"\A\Task", vm.Items[0].Path);

        vm.Search = "beta";
        await vm.FilterPending;
        Assert.Equal(@"\B\Task", Assert.Single(vm.Items).Path);
    }

    [Fact]
    public async Task Reload_can_be_cancelled_and_keeps_previous_data()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<StartupInfo>>();
        var src = new GatedStartup(new[] { Entry("First") }, gate);
        var vm = new StartupViewModel(src);
        await vm.Loaded;
        Assert.Single(vm.Items);

        src.Block = true;
        var reload = vm.ReloadAsync();
        vm.CancelLoad();
        gate.TrySetResult(Array.Empty<StartupInfo>());
        await reload;

        Assert.Single(vm.Items);
    }

    sealed class GatedStartup(StartupInfo[] items, TaskCompletionSource<IReadOnlyList<StartupInfo>> gate) : IStartupSource
    {
        public bool Block { get; set; }

        public async Task<IReadOnlyList<StartupInfo>> GetStartupAsync(CancellationToken ct = default)
        {
            if (!Block) return items;
            await using var _ = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        }
    }
}
