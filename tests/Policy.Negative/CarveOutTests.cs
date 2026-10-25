using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;
using StartMode = Chistovik.Core.Policy.StartMode;

namespace Chistovik.PolicyNegative;

/// <summary>Именованное исключение владельца (26.10.2026): 11 служб и 10 задач. Всё, что вокруг него, остаётся запрещённым.</summary>
public class CarveOutTests
{
    static readonly ServicePolicy Svc = new(PolicyContext.Fixed);
    static readonly TaskPolicy Tasks = new(PolicyContext.Fixed);

    static ServiceFacts Facts(
        string name = "DiagTrack", ServiceType type = ServiceType.ShareProcess,
        bool ms = true, bool ppl = false, int deps = 0, bool secProduct = false, StartMode start = StartMode.Automatic)
        => new(name, type, ImageFor(name), true, ms, ppl, deps, false, start, secProduct);

    static string ImageFor(string name) => name switch
    {
        "Fax" => @"C:\Windows\System32\fxssvc.exe",
        "WMPNetworkSvc" => @"C:\Windows\System32\wmpnetwk.exe",
        _ => @"C:\Windows\System32\svchost.exe -k utcsvc -p",
    };

    static StepGate Gate()
    {
        var trust = new FakeFileTrust { Default = new SignatureFacts(true, true) };
        return new StepGate(TweakCatalog.Shared, PowerCatalog.Shared, PolicyContext.Fixed, trust);
    }

    static ServiceRaw Raw(string name, uint typeFlags, uint startType = 3, bool delayed = false, string? image = null)
        => new(name, typeFlags, startType, delayed, image ?? ImageFor(name), false, 0, 0, false);

    static TaskRaw Raw(string path)
        => new(path, true, "<Task/>", "NT AUTHORITY\\SYSTEM", "LeastPrivilege", "ServiceAccount", new List<TaskAction>());

    public static IEnumerable<object[]> CarveNames() => ServicePolicy.CarveOut.Select(n => new object[] { n });
    public static IEnumerable<object[]> CarveTasks() => TaskPolicy.CarveOutTasks.Select(n => new object[] { n });

    [Fact]
    public void Sets_have_expected_size()
    {
        Assert.Equal(11, ServicePolicy.CarveOut.Count);
        Assert.Equal(10, TaskPolicy.CarveOutTasks.Count);
    }

    [Fact]
    public void CarveOut_does_not_intersect_hardlist()
    {
        foreach (var n in ServicePolicy.CarveOut) Assert.False(ServicePolicy.IsHardListed(n), n);
    }

    [Fact]
    public void CarveOut_does_not_contain_update_security_or_core_services()
    {
        string[] never =
        [
            "wuauserv", "UsoSvc", "WaaSMedicSvc", "WinDefend", "wlidsvc", "LicenseManager", "ClipSVC", "StateRepository", "AppXSvc",
            "TokenBroker", "TextInputManagementService", "Dnscache", "SharedAccess", "TermService", "Spooler", "BITS",
        ];
        foreach (var n in never) Assert.False(ServicePolicy.CarveOut.Contains(n), n);
    }

    [Theory]
    [MemberData(nameof(CarveTasks))]
    public void CarveOut_tasks_are_outside_protected_subtrees(string path)
    {
        string[] banned = ["UpdateOrchestrator", "WindowsUpdate", "WaaSMedic", "Windows Defender", "Chkdsk", "BitLocker", "TPM", "Shell", "Servicing"];
        Assert.True(TaskPolicy.TryParsePath(path, out var seg));
        foreach (var s in seg) Assert.DoesNotContain(s, banned, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(CarveNames))]
    public void Clean_carveout_service_is_modifiable_and_can_be_disabled(string name)
    {
        var f = Facts(name);
        var (cls, _, rule) = Svc.Classify(f);
        Assert.Equal(ServiceClass.Modifiable, cls);
        Assert.Equal("service-catalog-carveout", rule);
        Assert.True(Svc.CheckSetStart(f, StartMode.Disabled).IsAllowed);
        Assert.True(Svc.CheckSetStart(f, StartMode.Manual).IsAllowed);
    }

    [Fact]
    public void Ppl_driver_and_security_product_stay_forbidden_for_carveout_name()
    {
        Assert.Equal("service-ppl", Svc.Classify(Facts(ppl: true)).Rule);
        Assert.Equal("service-driver", Svc.Classify(Facts(type: ServiceType.KernelDriver)).Rule);
        var sec = Facts(secProduct: true);
        Assert.Equal(ServiceClass.Forbidden, Svc.Classify(sec).Class);
        Assert.True(Svc.CheckSetStart(sec, StartMode.Disabled).IsDenied);
    }

    [Fact]
    public void Dependents_and_foreign_signature_make_it_read_only()
    {
        var d = Facts(deps: 1);
        Assert.Equal(ServiceClass.ReadOnly, Svc.Classify(d).Class);
        Assert.True(Svc.CheckSetStart(d, StartMode.Disabled).IsDenied);
        var u = Facts(ms: false);
        Assert.Equal(ServiceClass.ReadOnly, Svc.Classify(u).Class);
        Assert.True(Svc.CheckSetStart(u, StartMode.Disabled).IsDenied);
    }

    [Theory]
    [InlineData(StartMode.Automatic)]
    [InlineData(StartMode.AutomaticDelayed)]
    [InlineData(StartMode.Boot)]
    [InlineData(StartMode.System)]
    public void Autostart_and_boot_targets_are_denied(StartMode target)
    {
        Assert.True(Svc.CheckSetStart(Facts(start: StartMode.Disabled), target).IsDenied);
        Assert.True(Svc.CheckSetStart(Facts(start: StartMode.Manual), target).IsDenied);
    }

    [Fact]
    public void Direct_elevation_is_denied_but_own_revert_is_allowed()
    {
        var f = Facts(start: StartMode.Disabled);
        Assert.True(Svc.CheckSetStart(f, StartMode.Manual).IsDenied);
        Assert.True(Svc.CheckSetStart(f, StartMode.Manual, isRevertOfOwnStep: true).IsAllowed);
        Assert.True(Svc.CheckSetStart(f, StartMode.Automatic, isRevertOfOwnStep: true).IsAllowed);
    }

    [Theory]
    [InlineData("DiagTrack_1a2b")]
    [InlineData("DiagTrackX")]
    [InlineData(" DiagTrack")]
    [InlineData("DiagTrack ")]
    public void Near_miss_names_are_not_carveout(string name)
    {
        var f = Facts(name);
        Assert.Equal(ServiceClass.Forbidden, Svc.Classify(f).Class);
        Assert.True(Svc.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Fact]
    public void Cyrillic_a_in_name_is_bad_name()
    {
        var f = Facts("DiagTr\u0430ck");
        Assert.Equal("bad-name", Svc.Classify(f).Rule);
        Assert.True(Svc.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Fact]
    public void Non_carveout_share_process_stays_forbidden()
        => Assert.Equal("service-shared", Svc.Classify(Facts("DiagSvc")).Rule);

    [Theory]
    [InlineData(ServiceType.Other)]
    [InlineData(ServiceType.FileSystemDriver)]
    [InlineData(ServiceType.KernelDriver)]
    public void Carveout_with_wrong_type_is_not_modifiable(ServiceType type)
    {
        var f = Facts(type: type);
        Assert.NotEqual(ServiceClass.Modifiable, Svc.Classify(f).Class);
        Assert.True(Svc.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Fact]
    public void Carveout_with_unknown_type_has_dedicated_rule()
        => Assert.Equal("service-carveout-unknown-type", Svc.Classify(Facts(type: ServiceType.Other)).Rule);

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x2u)]
    [InlineData(0x8u)]
    [InlineData(0x0u)]
    public void Gate_denies_carveout_with_driver_or_unknown_type_flags(uint flags)
    {
        var r = Gate().CheckServiceChange(Raw("DiagTrack", flags), Chistovik.Core.Ports.StartMode.Disabled, false, "DiagTrack");
        Assert.False(r.IsAllowed);
    }

    [Fact]
    public void Gate_allows_clean_carveout_disable_and_denies_autostart_without_revert()
    {
        var g = Gate();
        Assert.True(g.CheckServiceChange(Raw("DiagTrack", 0x20), Chistovik.Core.Ports.StartMode.Disabled, false, "DiagTrack").IsAllowed);
        // Auto -> AutoDelayed и обратно без отката: отказ.
        Assert.False(g.CheckServiceChange(Raw("DiagTrack", 0x20, 2, false), Chistovik.Core.Ports.StartMode.AutomaticDelayed, false, "DiagTrack").IsAllowed);
        Assert.False(g.CheckServiceChange(Raw("DiagTrack", 0x20, 2, true), Chistovik.Core.Ports.StartMode.Automatic, false, "DiagTrack").IsAllowed);
    }

    [Fact]
    public void Autostart_swap_is_denied_by_policy_without_revert()
    {
        Assert.True(Svc.CheckSetStart(Facts(start: StartMode.Automatic), StartMode.AutomaticDelayed).IsDenied);
        Assert.True(Svc.CheckSetStart(Facts(start: StartMode.AutomaticDelayed), StartMode.Automatic).IsDenied);
    }

    [Theory]
    [InlineData("DiagTrack", @"C:\Windows\System32\evil.exe")]
    [InlineData("DiagTrack", @"C:\Windows\SysWOW64\svchost.exe -k x")]
    [InlineData("DiagTrack", @"C:\Users\Public\svchost.exe")]
    [InlineData("Fax", @"C:\Windows\System32\svchost.exe -k x")]
    [InlineData("WMPNetworkSvc", @"C:\Windows\System32\svchost.exe -k x")]
    [InlineData("WMPNetworkSvc", @"C:\Program Files\Windows Media Player\evil\wmpnetwk.exe")]
    public void Carveout_with_unexpected_image_is_read_only(string name, string image)
    {
        var f = Facts(name) with { ImagePath = image };
        Assert.Equal("service-carveout-unexpected-image", Svc.Classify(f).Rule);
        Assert.True(Svc.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Theory]
    [InlineData("Fax", @"c:\WINDOWS\system32\FXSSVC.EXE")]
    [InlineData("WMPNetworkSvc", @"C:\Program Files\Windows Media Player\wmpnetwk.exe")]
    [InlineData("WMPNetworkSvc", @"C:\Program Files (x86)\Windows Media Player\wmpnetwk.exe")]
    [InlineData("DiagTrack", @"%SystemRoot%\System32\svchost.exe -k utcsvc -p")]
    public void Carveout_with_expected_image_in_other_form_is_accepted(string name, string image)
        => Assert.Equal("service-catalog-carveout", Svc.Classify(Facts(name) with { ImagePath = image }).Rule);

    [Fact]
    public void Carveout_without_image_is_read_only()
        => Assert.Equal("service-carveout-unexpected-image", Svc.Classify(Facts() with { ImagePath = null }).Rule);

    // ---------- задачи ----------

    [Fact]
    public void Non_ascii_lookalike_in_task_path_is_not_carveout()
    {
        // U+0131 вместо i, U+017F вместо s, знак Кельвина U+212A вместо k: сравнение без регистра не должно их сворачивать.
        string[] paths =
        [
            "\\Microsoft\\Windows\\Application Experience\\" + "Microsoft Compatibility Appraiser".Replace("i", "\u0131"),
            "\\Microsoft\\Windows\\Customer Experience Improvement Program\\" + "Consolidator".Replace("s", "\u017F"),
            "\\Microsoft\\Windows\\Autochk\\" + "Proxy".Replace("o", "\u00F6"),
            "\\Microsoft\\Windows\\Feedback\\Siuf\\" + "DmClient".Replace("i", "\u0131"),
            "\\Microsoft\\Windows\\Customer Experience Improvement Program\\" + "UsbCeip".Replace("s", "\u017F"),
            "\\Microsoft\\Windows\\" + "Autochk\\Proxy".Replace("k", "\u212A"),
        ];
        foreach (var p in paths)
        {
            Assert.False(TaskPolicy.CarveOutTasks.Contains(p), p);
            var t = Raw(p);
            Assert.NotEqual("task-catalog-carveout", Tasks.Classify(t).Rule);
            Assert.Equal(ChangeClass.Forbidden, Tasks.Classify(t).Class);
            Assert.True(Tasks.CheckSetEnabled(t, false).IsDenied, p);
        }
    }

    [Fact]
    public void Task_in_other_case_is_carveout_by_design()
    {
        var t = Raw(@"\MICROSOFT\WINDOWS\maps\mapsupdatetask");
        Assert.Equal("task-catalog-carveout", Tasks.Classify(t).Rule);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Maps\MapsUpdateTask ")]
    [InlineData(@"\Microsoft\Windows\Maps\MapsUpdateTask\")]
    [InlineData(@"\Microsoft\Windows\Maps\MapsUpdateTask.")]
    [InlineData("/Microsoft/Windows/Maps/MapsUpdateTask")]
    [InlineData(@"\Microsoft\Windows\Maps/MapsUpdateTask")]
    [InlineData(@"\\Microsoft\Windows\Maps\MapsUpdateTask")]
    public void Malformed_carveout_task_paths_are_denied(string path)
    {
        var t = Raw(path);
        Assert.Equal("task-bad-path", Tasks.Classify(t).Rule);
        Assert.True(Tasks.CheckSetEnabled(t, false).IsDenied);
        Assert.True(Tasks.CheckSetEnabled(t, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Fact]
    public void Carveout_task_with_system_principal_is_revert_only()
    {
        var t = Raw(@"\Microsoft\Windows\Maps\MapsUpdateTask");   // Raw: NT AUTHORITY\SYSTEM
        Assert.Equal(ChangeClass.RevertOnly, Tasks.Classify(t).Class);
        Assert.True(Tasks.CheckSetEnabled(t, true).IsDenied);
    }

    [Theory]
    [MemberData(nameof(CarveTasks))]
    public void Carveout_task_can_be_disabled_but_enabled_only_by_revert(string path)
    {
        var t = Raw(path);
        var c = Tasks.Classify(t);
        Assert.Equal(ChangeClass.RevertOnly, c.Class);
        Assert.Equal("task-catalog-carveout", c.Rule);
        Assert.True(Tasks.CheckSetEnabled(t, false).IsAllowed);
        Assert.True(Tasks.CheckSetEnabled(t, true).IsDenied);
        Assert.True(Tasks.CheckSetEnabled(t, true, isRevertOfOwnStep: true).IsAllowed);
    }

    [Theory]
    [MemberData(nameof(CarveTasks))]
    public void Subpaths_of_carveout_task_stay_protected(string path)
    {
        var t = Raw(path + "\\Sub");
        Assert.Equal("task-protected-tree", Tasks.Classify(t).Rule);
        Assert.True(Tasks.CheckSetEnabled(t, false).IsDenied);
        Assert.True(Tasks.CheckSetEnabled(t, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData("\\Microsoft\\Windows\\UpdateOrchestrator\\Schedule Scan")]
    [InlineData("\\Microsoft\\Windows\\Application Experience")]
    [InlineData("\\Microsoft\\Windows\\Application Experience\\StartupAppTask")]
    [InlineData("\\Microsoft\\Windows\\Windows Defender\\Windows Defender Scheduled Scan")]
    public void Neighbours_and_parents_are_denied(string path)
    {
        var t = Raw(path);
        Assert.Equal(ChangeClass.Forbidden, Tasks.Classify(t).Class);
        Assert.True(Tasks.CheckSetEnabled(t, false).IsDenied);
        Assert.True(Tasks.CheckSetEnabled(t, true, isRevertOfOwnStep: true).IsDenied);
    }
}
