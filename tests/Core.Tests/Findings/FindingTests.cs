using Chistovik.Core.Findings;
using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.Core.Tests.Findings;

public sealed class AdviceCatalogTests
{
    static readonly AdviceCatalog Cat = AdviceCatalog.Load();

    const string EmptyStartup = "[]";
    const string EmptyObjects = """{"services":[],"tasks":[]}""";

    static string OneService(string name, string verdict = "Remove", string risk = "Low", string reasons = "[]") => $$"""
        {"services":[{"name":"{{name}}","verdict":"{{verdict}}","risk":"{{risk}}","what":"a","why":"b","ifRemoved":"c","reasons":{{reasons}}}],"tasks":[]}
        """;

    static string OneTask(string name, string verdict = "Remove") => $$"""
        {"services":[],"tasks":[{"name":"{{name}}","verdict":"{{verdict}}","risk":"Low","what":"a","why":"b","ifRemoved":"c","reasons":[]}]}
        """;

    public static IEnumerable<object[]> ServiceNames() => Cat.Services.Select(s => new object[] { s.Name });

    [Fact]
    public void Catalogs_load_and_are_not_empty()
    {
        Assert.NotEmpty(Cat.Startup);
        Assert.NotEmpty(Cat.Services);
        Assert.NotEmpty(Cat.Tasks);
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Catalog_service_is_not_hardlisted_and_not_forbidden_by_spec(string name)
    {
        Assert.False(ServicePolicy.IsHardListed(name), $"{name} входит в запретный список политики");
        Assert.DoesNotContain(name, AdviceCatalog.NeverAdvise);
    }

    [Fact]
    public void Catalog_has_no_wildcard_services_and_keeps_wu_defense_out()
    {
        Assert.DoesNotContain(Cat.Services, s => s.Name.Contains('*'));
        foreach (var banned in new[] { "wuauserv", "UsoSvc", "WaaSMedicSvc", "WinDefend", "wlidsvc", "LicenseManager", "ClipSVC",
                     "StateRepository", "AppXSvc", "TokenBroker", "TextInputManagementService", "Dnscache" })
            Assert.Null(Cat.FindService(banned));
    }

    [Fact]
    public void Catalog_spec_services_are_present()
    {
        foreach (var name in new[] { "RetailDemo", "WMPNetworkSvc", "RemoteRegistry", "Fax", "DiagTrack", "dmwappushservice", "WerSvc", "WSearch", "SysMain" })
            Assert.NotNull(Cat.FindService(name));
        Assert.Equal("Remove", Cat.FindService("DiagTrack")!.Verdict);
        Assert.Equal("Keep", Cat.FindService("WSearch")!.Verdict);
    }

    [Fact]
    public void Task_lookup_prefers_exact_and_matches_prefix()
    {
        Assert.Equal("Remove", Cat.FindTask(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser")!.Verdict);
        Assert.Equal("Remove", Cat.FindTask(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload")!.Verdict);
        Assert.Equal("Protected", Cat.FindTask(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")!.Verdict);
        Assert.Null(Cat.FindTask(@"\Vendor\SomeTask"));
    }

    [Fact]
    public void Startup_lookup_matches_by_name_prefix_and_file()
    {
        Assert.Equal("steam", Cat.FindStartup("Steam", null)!.Id);
        Assert.Equal("dropbox", Cat.FindStartup("DropboxUpdate", null)!.Id);
        Assert.Equal("onedrive", Cat.FindStartup("Whatever", "OneDrive.exe")!.Id);
        Assert.Null(Cat.FindStartup("Mystery", "mystery.exe"));
    }

    [Fact]
    public void Parse_rejects_hardlisted_and_never_advise_services()
    {
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("WinDefend")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("BITS")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("wlidsvc")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("CDPUserSvc_1a2b3")));
    }

    [Fact]
    public void Parse_rejects_bad_shape()
    {
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("Foo", verdict: "Protected")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("Foo", verdict: "Delete")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("Foo", risk: "Huge")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("Foo", reasons: "[\"made-up\"]")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneService("Fo*o")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneTask("NoSlash")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, OneTask(@"\A\*\B")));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse("{}", EmptyObjects));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, "not json"));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, """{"services":[],"tasks":[],"extra":1}"""));
    }

    [Fact]
    public void Parse_rejects_duplicates_and_bad_startup()
    {
        const string dup = """
            {"services":[
             {"name":"Foo","verdict":"Keep","risk":"Low","what":"a","why":"b","ifRemoved":"c","reasons":[]},
             {"name":"foo","verdict":"Keep","risk":"Low","what":"a","why":"b","ifRemoved":"c","reasons":[]}],"tasks":[]}
            """;
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(EmptyStartup, dup));

        const string badRole = """[{"id":"x","title":"X","names":["X"],"files":[],"role":"role-nonsense","what":"a","why":"b","ifRemoved":"c"}]""";
        const string noKeys = """[{"id":"x","title":"X","names":[],"files":[],"role":"role-tray","what":"a","why":"b","ifRemoved":"c"}]""";
        const string emptyText = """[{"id":"x","title":"X","names":["X"],"files":[],"role":"role-tray","what":"","why":"b","ifRemoved":"c"}]""";
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(badRole, EmptyObjects));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(noKeys, EmptyObjects));
        Assert.Throws<InvalidDataException>(() => AdviceCatalog.Parse(emptyText, EmptyObjects));
    }

    [Fact]
    public void Reason_codes_all_have_text_and_unknown_code_throws()
    {
        foreach (var code in ReasonCodes.All)
        {
            Assert.True(ReasonCodes.IsKnown(code));
            Assert.False(string.IsNullOrWhiteSpace(ReasonCodes.TextOf(code)), code);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ReasonCodes.Make("made-up"));
        Assert.False(ReasonCodes.IsKnown("impact-ms:12"));
    }
}

public sealed class FindingClassifierTests
{
    static readonly FindingClassifier C = FindingClassifier.Shared;
    static readonly AdviceCatalog Cat = AdviceCatalog.Shared;

    static StartupSubject Run(string name, string command, ChangeClass cls = ChangeClass.Modifiable,
        string? exe = "unset", bool? missing = false, bool? sig = true, bool? ms = false)
        => new("run:hku:" + name, name, command, "Реестр", cls, cls == ChangeClass.Modifiable ? null : "причина политики",
            exe == "unset" ? Exe(command) : exe, missing, sig, ms);

    static string? Exe(string command)
    {
        var n = FindingClassifier.ExeName(command);
        return n.Length == 0 ? null : @"C:\Program Files\X\" + n;
    }

    static void AssertComplete(Finding f)
    {
        Assert.False(string.IsNullOrWhiteSpace(f.Title));
        Assert.False(string.IsNullOrWhiteSpace(f.Verdict.Headline));
        Assert.False(string.IsNullOrWhiteSpace(f.Explanation.WhatIsIt), "что это");
        Assert.False(string.IsNullOrWhiteSpace(f.Explanation.WhyNeeded), "зачем нужно");
        Assert.False(string.IsNullOrWhiteSpace(f.Explanation.IfRemoved), "что будет без него");
        Assert.False(string.IsNullOrWhiteSpace(f.Explanation.HowToUndo), "как вернуть");
        Assert.NotEmpty(f.Verdict.Reasons);
        foreach (var r in f.Verdict.Reasons)
        {
            Assert.True(ReasonCodes.IsKnown(r.Code), $"неизвестный код {r.Code}");
            Assert.Equal(ReasonCodes.TextOf(r.Code), r.Text);
        }
    }

    // ----- автозагрузка -----

    [Fact]
    public void Startup_forbidden_is_protected_and_never_changes_class()
    {
        var f = C.Startup(Run("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", ChangeClass.Forbidden));
        Assert.Equal(VerdictKind.Protected, f.Verdict.Kind);
        Assert.Contains(f.Verdict.Reasons, r => r.Code == ReasonCodes.ClassForbidden);
        Assert.Equal(ChangeClass.Forbidden, f.Class);
        Assert.Equal(Reversibility.NotAvailable, f.Verdict.Reversibility);
        AssertComplete(f);
    }

    [Fact]
    public void Startup_readonly_and_unresolved_are_unknown()
    {
        var ro = C.Startup(Run("SetupHelper", @"C:\Setup\helper.exe", ChangeClass.ReadOnly));
        Assert.Equal(VerdictKind.Unknown, ro.Verdict.Kind);
        Assert.Contains(ro.Verdict.Reasons, r => r.Code == ReasonCodes.ClassReadOnly);

        var un = C.Startup(Run("Odd", "weird command", exe: null, missing: null, sig: null));
        Assert.Equal(VerdictKind.Unknown, un.Verdict.Kind);
        Assert.Contains(un.Verdict.Reasons, r => r.Code == ReasonCodes.CommandUnresolved);
        Assert.Equal(Confidence.L4, un.Verdict.Confidence);
        AssertComplete(ro);
        AssertComplete(un);
    }

    [Fact]
    public void Startup_missing_file_is_remove_with_evidence()
    {
        var f = C.Startup(Run("OldApp", @"C:\Program Files\OldApp\old.exe", missing: true, sig: null));
        Assert.Equal(VerdictKind.Remove, f.Verdict.Kind);
        var r = Assert.Single(f.Verdict.Reasons, x => x.Code == ReasonCodes.FileMissing);
        Assert.False(string.IsNullOrEmpty(r.Evidence));
        Assert.Equal(Confidence.L2, f.Verdict.Confidence);
        Assert.Equal(Reversibility.Undoable, f.Verdict.Reversibility);
        AssertComplete(f);
    }

    [Fact]
    public void Startup_updater_is_remove_only_with_valid_signature()
    {
        const string cmd = @"""C:\Program Files\Google\Update\GoogleUpdate.exe"" /c";
        var signed = C.Startup(Run("Google Update", cmd, sig: true));
        var unknownSig = C.Startup(Run("Google Update", cmd, sig: null));
        var badSig = C.Startup(Run("Google Update", cmd, sig: false));

        Assert.Equal(VerdictKind.Remove, signed.Verdict.Kind);
        Assert.Contains(signed.Verdict.Reasons, r => r.Code == ReasonCodes.RoleUpdater);
        Assert.Equal(VerdictKind.Optional, unknownSig.Verdict.Kind);
        Assert.Equal(VerdictKind.Optional, badSig.Verdict.Kind);
        Assert.Contains(badSig.Verdict.Reasons, r => r.Code == ReasonCodes.Unsigned);
        AssertComplete(signed);
        AssertComplete(unknownSig);
        AssertComplete(badSig);
    }

    [Fact]
    public void Startup_security_and_device_helpers_are_keep()
    {
        var sec = C.Startup(Run("Avast Antivirus", @"C:\Program Files\Avast\AvastUI.exe"));
        var dev = C.Startup(Run("RtkAudUService", @"C:\Windows\System32\RtkAudUService64.exe"));
        Assert.Equal(VerdictKind.Keep, sec.Verdict.Kind);
        Assert.Contains(sec.Verdict.Reasons, r => r.Code == ReasonCodes.RoleSecurity);
        Assert.Equal(Risk.High, sec.Verdict.Risk);
        Assert.Equal(VerdictKind.Keep, dev.Verdict.Kind);
        Assert.Contains(dev.Verdict.Reasons, r => r.Code == ReasonCodes.RoleDeviceHelper);
        AssertComplete(sec);
        AssertComplete(dev);
    }

    [Fact]
    public void Startup_known_tray_app_is_optional_with_catalog_text()
    {
        var f = C.Startup(Run("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent"));
        Assert.Equal(VerdictKind.Optional, f.Verdict.Kind);
        Assert.Equal("Steam", f.Title);
        Assert.Contains(f.Verdict.Reasons, r => r.Code == ReasonCodes.RoleGameLauncher);
        Assert.Contains(f.Verdict.Reasons, r => r.Code == ReasonCodes.PublisherKnown);
        Assert.Equal(Confidence.L1, f.Verdict.Confidence);
        AssertComplete(f);
    }

    [Fact]
    public void Startup_temp_and_unsigned_appdata_raise_risk()
    {
        var temp = C.Startup(Run("Tmp", @"C:\Users\u\AppData\Local\Temp\x\run.exe", exe: @"C:\Users\u\AppData\Local\Temp\x\run.exe", sig: true));
        var unsignedApp = C.Startup(Run("Odd", @"C:\Users\u\AppData\Roaming\Odd\odd.exe", exe: @"C:\Users\u\AppData\Roaming\Odd\odd.exe", sig: false));
        var signedApp = C.Startup(Run("Fine", @"C:\Users\u\AppData\Roaming\Fine\fine.exe", exe: @"C:\Users\u\AppData\Roaming\Fine\fine.exe", sig: true));

        Assert.Equal((VerdictKind.Optional, Risk.Medium), (temp.Verdict.Kind, temp.Verdict.Risk));
        Assert.Contains(temp.Verdict.Reasons, r => r.Code == ReasonCodes.PathTemp);
        Assert.Equal((VerdictKind.Optional, Risk.Medium), (unsignedApp.Verdict.Kind, unsignedApp.Verdict.Risk));
        Assert.Contains(unsignedApp.Verdict.Reasons, r => r.Code == ReasonCodes.Unsigned);
        Assert.Equal((VerdictKind.Optional, Risk.Low), (signedApp.Verdict.Kind, signedApp.Verdict.Risk));
        Assert.Contains(signedApp.Verdict.Reasons, r => r.Code == ReasonCodes.ImpactUnknown);
        AssertComplete(temp);
        AssertComplete(unsignedApp);
        AssertComplete(signedApp);
    }

    [Fact]
    public void Startup_remove_never_appears_without_file_missing_or_signed_updater()
    {
        var names = new[] { "Steam", "Google Update", "OneDrive", "SecurityHealth", "Mystery", "Avast UI" };
        var classes = Enum.GetValues<ChangeClass>();
        foreach (var name in names)
        foreach (var cls in classes)
        foreach (var missing in new bool?[] { null, true, false })
        foreach (var sig in new bool?[] { null, true, false })
        foreach (var resolved in new[] { true, false })
        {
            var cmd = $@"C:\Program Files\X\{name.Replace(' ', '_')}.exe";
            var s = Run(name, cmd, cls, exe: resolved ? cmd : null, missing: missing, sig: sig);
            var f = C.Startup(s);
            AssertComplete(f);
            Assert.Equal(cls, f.Class);
            if (f.Verdict.Kind != VerdictKind.Remove) continue;

            Assert.Equal(ChangeClass.Modifiable, cls);
            Assert.True(resolved || missing is not null);
            var viaMissing = missing == true && f.Verdict.Reasons.Any(r => r.Code == ReasonCodes.FileMissing);
            var viaUpdater = sig == true && f.Verdict.Reasons.Any(r => r.Code == ReasonCodes.RoleUpdater);
            Assert.True(viaMissing || viaUpdater, $"Remove без оснований: {name} {cls} missing={missing} sig={sig}");
        }
    }

    [Fact]
    public void Startup_runonce_entry_without_id_still_gets_a_stable_finding_id()
    {
        var s = new StartupSubject("", "SetupHelper", @"C:\Setup\h.exe", "Однократный запуск", ChangeClass.ReadOnly, "r");
        var f = C.Startup(s);
        Assert.StartsWith("startup:ro:", f.Id);
        Assert.Equal(f.Id, C.Startup(s).Id);
    }

    // ----- службы -----

    [Fact]
    public void Service_in_catalog_keeps_advice_but_policy_class_is_untouched()
    {
        // DiagTrack работает в общем svchost: политика сегодня даёт Forbidden. Совет остаётся, изменение недоступно.
        var blocked = C.Service(new ServiceSubject("DiagTrack", "Функциональные возможности для подключённых пользователей и телеметрия", @"C:\Windows\System32\svchost.exe -k utcsvc",
            ChangeClass.Forbidden, "Служба в общем процессе svchost"));
        Assert.Equal(VerdictKind.Remove, blocked.Verdict.Kind);
        Assert.Equal(ChangeClass.Forbidden, blocked.Class);
        Assert.Equal(Reversibility.NotAvailable, blocked.Verdict.Reversibility);
        Assert.Contains("изменение пока недоступно", blocked.Verdict.Headline);
        Assert.Contains(blocked.Verdict.Reasons, r => r.Code == ReasonCodes.ClassForbidden);
        Assert.Contains(blocked.Verdict.Reasons, r => r.Code == ReasonCodes.RoleTelemetry);
        Assert.Contains(blocked.Verdict.Reasons, r => r.Code == ReasonCodes.WuMayRevert);
        Assert.Contains(blocked.Verdict.Reasons, r => r.Code == ReasonCodes.CatalogAdvice);
        AssertComplete(blocked);

        var open = C.Service(new ServiceSubject("DiagTrack", "DiagTrack", "", ChangeClass.Modifiable, null));
        Assert.Equal(Reversibility.Undoable, open.Verdict.Reversibility);
        Assert.DoesNotContain("недоступно", open.Verdict.Headline);
        AssertComplete(open);
    }

    [Fact]
    public void Service_hardlisted_is_protected_even_if_class_says_modifiable()
    {
        foreach (var name in new[] { "WinDefend", "wuauserv", "BITS", "Dnscache", "RpcSs" })
        {
            var f = C.Service(new ServiceSubject(name, name, @"C:\Windows\System32\svchost.exe", ChangeClass.Modifiable, null));
            Assert.Equal(VerdictKind.Protected, f.Verdict.Kind);
            Assert.Contains(f.Verdict.Reasons, r => r.Code == ReasonCodes.ClassForbidden);
            AssertComplete(f);
        }
    }

    [Fact]
    public void Service_outside_catalog_never_gets_remove()
    {
        foreach (var cls in Enum.GetValues<ChangeClass>())
        foreach (var name in new[] { "ThirdPartySvc", "OemUpdateService", "SomeVendorAgent" })
        {
            var f = C.Service(new ServiceSubject(name, name, @"C:\Program Files\Vendor\agent.exe", cls, "r"));
            Assert.NotEqual(VerdictKind.Remove, f.Verdict.Kind);
            Assert.Equal(cls, f.Class);
            AssertComplete(f);
        }
        Assert.Equal(VerdictKind.Protected, C.Service(new ServiceSubject("X", "X", "", ChangeClass.Forbidden, "r")).Verdict.Kind);
        Assert.Equal(VerdictKind.Unknown, C.Service(new ServiceSubject("X", "X", "", ChangeClass.ReadOnly, "r")).Verdict.Kind);
        var opt = C.Service(new ServiceSubject("X", "X", @"C:\Program Files\V\x.exe", ChangeClass.Modifiable, null));
        Assert.Equal(VerdictKind.Optional, opt.Verdict.Kind);
        Assert.Contains(opt.Verdict.Reasons, r => r.Code == ReasonCodes.PathProgramFiles);
    }

    public static IEnumerable<object[]> CatalogServiceNames() => Cat.Services.Select(s => new object[] { s.Name });

    [Theory]
    [MemberData(nameof(CatalogServiceNames))]
    public void Every_catalog_service_yields_a_complete_finding(string name)
    {
        foreach (var cls in new[] { ChangeClass.Modifiable, ChangeClass.Forbidden })
        {
            var f = C.Service(new ServiceSubject(name, name, "", cls, cls == ChangeClass.Modifiable ? null : "r"));
            AssertComplete(f);
            Assert.Equal(Cat.FindService(name)!.Verdict, f.Verdict.Kind.ToString());
        }
    }

    // ----- задачи -----

    public static IEnumerable<object[]> CatalogTaskPaths() => Cat.Tasks.Select(t => new object[] { t.Name.TrimEnd('*') + (t.Name.EndsWith('*') ? "Sample" : "") });

    [Theory]
    [MemberData(nameof(CatalogTaskPaths))]
    public void Every_catalog_task_yields_a_complete_finding(string path)
    {
        var name = path[(path.LastIndexOf('\\') + 1)..];
        foreach (var cls in new[] { ChangeClass.Modifiable, ChangeClass.Forbidden })
        {
            var f = C.Task(new TaskSubject(path, name, "", cls, cls == ChangeClass.Modifiable ? null : "r"));
            AssertComplete(f);
            Assert.Equal(cls, f.Class);
            Assert.Equal(Cat.FindTask(path)!.Verdict, f.Verdict.Kind.ToString());
        }
    }

    [Fact]
    public void Task_rules_for_unknown_paths_follow_class()
    {
        var forb = C.Task(new TaskSubject(@"\Microsoft\Windows\Foo\Bar", "Bar", "", ChangeClass.Forbidden, "r"));
        var ro = C.Task(new TaskSubject(@"\Vendor\T", "T", "", ChangeClass.ReadOnly, "r"));
        var mod = C.Task(new TaskSubject(@"\Vendor\T", "T", @"C:\Program Files\V\upd.exe /check", ChangeClass.Modifiable, null));
        Assert.Equal(VerdictKind.Protected, forb.Verdict.Kind);
        Assert.Equal(VerdictKind.Unknown, ro.Verdict.Kind);
        Assert.Equal(VerdictKind.Optional, mod.Verdict.Kind);
        Assert.Contains(mod.Verdict.Reasons, r => r.Code == ReasonCodes.PathProgramFiles);
        AssertComplete(forb);
        AssertComplete(ro);
        AssertComplete(mod);
    }

    [Fact]
    public void Task_update_trees_are_protected_by_catalog()
    {
        foreach (var path in new[]
                 {
                     @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
                     @"\Microsoft\Windows\Windows Defender\Windows Defender Scheduled Scan", @"\Microsoft\Windows\WaaSMedic\PerformRemediation",
                 })
        {
            var f = C.Task(new TaskSubject(path, "T", "", ChangeClass.Forbidden, "r"));
            Assert.Equal(VerdictKind.Protected, f.Verdict.Kind);
            Assert.Equal(Reversibility.NotAvailable, f.Verdict.Reversibility);
            AssertComplete(f);
        }
    }

    // ----- программы -----

    [Fact]
    public void Program_runtimes_are_keep_and_others_optional_reinstall_only()
    {
        var vc = C.Program(new ProgramSubject("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation", "14.38", 0, "Классическая"));
        var net = C.Program(new ProgramSubject("Microsoft Windows Desktop Runtime - 8.0.1", "Microsoft Corporation", "8.0.1", 0, "Классическая"));
        var app = C.Program(new ProgramSubject("7-Zip", "Igor Pavlov", "23.01", 5_000_000, "Классическая"));

        Assert.Equal(VerdictKind.Keep, vc.Verdict.Kind);
        Assert.Equal(VerdictKind.Keep, net.Verdict.Kind);
        Assert.Contains(vc.Verdict.Reasons, r => r.Code == ReasonCodes.RuntimeShared);
        Assert.Equal(VerdictKind.Optional, app.Verdict.Kind);
        Assert.Equal(Reversibility.ReinstallOnly, app.Verdict.Reversibility);
        Assert.NotEqual(VerdictKind.Remove, app.Verdict.Kind);
        Assert.Equal("", app.OpenIn);
        AssertComplete(vc);
        AssertComplete(net);
        AssertComplete(app);
    }

    // ----- вспомогательные -----

    [Theory]
    [InlineData(@"""C:\Program Files\A B\a.exe"" /x", "a.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe -silent", "steam.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Update.exe")]
    [InlineData(@"C:\Users\u\Start Menu\Programs\Startup\Telegram.lnk", "Telegram.lnk")]
    [InlineData("notepad.exe", "notepad.exe")]
    [InlineData("", "")]
    [InlineData(@"""unclosed", "")]
    public void ExeName_extracts_file(string command, string expected)
        => Assert.Equal(expected, FindingClassifier.ExeName(command));

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\x.exe", ReasonCodes.PathTemp)]
    [InlineData(@"C:\Users\u\AppData\Roaming\x.exe", ReasonCodes.PathAppData)]
    [InlineData(@"C:\Program Files (x86)\V\x.exe", ReasonCodes.PathProgramFiles)]
    [InlineData(@"C:\Tools\x.exe", null)]
    [InlineData("", null)]
    public void PathCode_classifies_location(string path, string? expected)
        => Assert.Equal(expected, FindingClassifier.PathCode(path));

    [Fact]
    public void Headlines_are_distinct_per_kind()
    {
        var all = Enum.GetValues<VerdictKind>().Select(k => FindingClassifier.Headline(k, FindingArea.Startup)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
