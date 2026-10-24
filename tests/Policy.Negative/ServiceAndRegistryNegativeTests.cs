using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.PolicyNegative;

public class ServicePolicyNegativeTests
{
    static readonly ServicePolicy Policy = new(PolicyContext.Fixed);

    static ServiceFacts Facts(
        string name = "FooSvc",
        ServiceType type = ServiceType.OwnProcess,
        string? image = "\"C:\\Program Files\\Foo\\foo svc.exe\" -run",
        bool sig = true, bool ms = false, bool ppl = false, int deps = 0, bool owned = true,
        StartMode start = StartMode.Automatic)
        => new(name, type, image, sig, ms, ppl, deps, owned, start);

    [Fact]
    public void Ordinary_program_service_is_modifiable()
    {
        Assert.Equal(ServiceClass.Modifiable, Policy.Classify(Facts()).Class);
        Assert.True(Policy.CheckSetStart(Facts(), StartMode.Manual).IsAllowed);
        Assert.True(Policy.CheckSetStart(Facts(), StartMode.Disabled).IsAllowed);
    }

    [Theory]
    [InlineData(StartMode.Boot)]
    [InlineData(StartMode.System)]
    public void Boot_and_system_start_are_never_assigned(StartMode target)
        => Assert.True(Policy.CheckSetStart(Facts(), target).IsDenied);

    static IEnumerable<string> HardNames() => new[]
    {
        "WinDefend", "mpssvc", "RpcSs", "Dhcp", "Dnscache", "EventLog", "WdNisSvc", "Sense", "SecurityHealthService",
        "wscsvc", "TrustedInstaller", "Schedule", "LSM", "Winmgmt", "BFE", "CryptSvc", "SamSs", "wuauserv",
    };

    public static IEnumerable<object[]> HardNameFacts()
    {
        string?[] images =
        [
            null, "",
            "\"C:\\Program Files\\Foo\\a.exe\"",
            "\"C:\\Windows\\System32\\svchost.exe\" -k netsvcs",
            "C:\\Users\\Egoist\\AppData\\Local\\Foo\\a.exe",
        ];
        foreach (var n in HardNames())
        foreach (var variant in new[] { n, n.ToUpperInvariant(), n.ToLowerInvariant(), n + "_1a2b3" })
        foreach (var img in images)
        foreach (var owned in new[] { true, false })
        foreach (var sig in new[] { true, false })
            yield return [variant, img ?? "<null>", owned, sig];
    }

    [Theory]
    [MemberData(nameof(HardNameFacts))]
    public void No_hardlisted_service_is_ever_modifiable(string name, string image, bool owned, bool sig)
    {
        var f = Facts(name: name, image: image == "<null>" ? null : image, owned: owned, sig: sig);
        Assert.NotEqual(ServiceClass.Modifiable, Policy.Classify(f).Class);
        Assert.True(Policy.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Theory]
    [InlineData(ServiceType.KernelDriver)]
    [InlineData(ServiceType.FileSystemDriver)]
    [InlineData(ServiceType.ShareProcess)]
    public void Drivers_and_shared_process_services_are_forbidden(ServiceType t)
        => Assert.Equal(ServiceClass.Forbidden, Policy.Classify(Facts(type: t)).Class);

    [Fact]
    public void Protected_process_is_forbidden()
        => Assert.Equal(ServiceClass.Forbidden, Policy.Classify(Facts(ppl: true)).Class);

    [Theory]
    [InlineData("\"C:\\Windows\\System32\\foo.exe\"")]
    [InlineData("C:\\Windows\\System32\\foo.exe -x")]
    [InlineData("\\SystemRoot\\System32\\foo.exe")]
    [InlineData("system32\\foo.exe")]
    [InlineData("%SystemRoot%\\System32\\foo.exe")]
    [InlineData("\"C:\\Program Files\\Foo\\..\\..\\Windows\\System32\\foo.exe\"")]
    [InlineData("\"C:/WINDOWS/system32/foo.exe\"")]
    [InlineData("\"\\\\?\\C:\\Windows\\foo.exe\"")]
    [InlineData("\"C:\\Program Files\\Windows Defender\\x.exe\"")]
    [InlineData("\"C:\\ProgramData\\Microsoft\\Windows Defender\\Platform\\4.18\\MsMpEng.exe\"")]
    public void Service_files_in_system_locations_are_forbidden(string image)
        => Assert.Equal(ServiceClass.Forbidden, Policy.Classify(Facts(image: image)).Class);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"unterminated")]
    [InlineData("relative\\foo.exe")]
    public void Unresolvable_image_is_read_only(string? image)
        => Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(image: image)).Class);

    [Fact]
    public void Dependents_make_service_read_only()
        => Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(deps: 1)).Class);

    [Fact]
    public void Unsigned_unowned_is_unknown_and_read_only()
        => Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(sig: false, owned: false)).Class);

    [Fact]
    public void Microsoft_signed_without_owner_program_is_read_only()
        => Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(ms: true, owned: false)).Class);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Foo")]
    [InlineData("Foo ")]
    [InlineData("Foo\\Bar")]
    [InlineData("Foo\0")]
    public void Bad_names_are_forbidden(string name)
        => Assert.Equal(ServiceClass.Forbidden, Policy.Classify(Facts(name: name)).Class);
}

public class RegistryPolicyNegativeTests
{
    // Одни и те же ключи в разных написаниях куста.
    static IEnumerable<string> HklmForms(string sub)
    {
        yield return @"HKLM\" + sub;
        yield return @"hklm\" + sub.ToUpperInvariant();
        yield return @"HKEY_LOCAL_MACHINE\" + sub;
        yield return @"HKLM:\" + sub;
        yield return @"Registry::HKEY_LOCAL_MACHINE\" + sub;
        yield return @"Microsoft.PowerShell.Core\Registry::HKEY_LOCAL_MACHINE\" + sub;
        yield return @"\REGISTRY\MACHINE\" + sub;
        yield return @"HKLM\\" + sub.Replace(@"\", @"\\");
        yield return "HKLM/" + sub.Replace('\\', '/');
        yield return @"HKLM\" + sub + @"\";
    }

    static readonly string[] DeleteDenySeeds =
    [
        @"SOFTWARE",
        @"SOFTWARE\Microsoft",
        @"SOFTWARE\Microsoft\Windows",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\foo.exe",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Microsoft\Windows Defender\Exclusions",
        @"SOFTWARE\Policies\Microsoft\Windows Defender",
        @"SOFTWARE\Policies\Microsoft",
        @"SOFTWARE\Wow6432Node\Microsoft\Windows Defender",
        @"SOFTWARE\Wow6432Node\Microsoft",
        @"SOFTWARE\Classes",
        @"SOFTWARE\Classes\CLSID",
        @"SOFTWARE\Chistovik\Quarantine",
        @"SYSTEM",
        @"SYSTEM\CurrentControlSet",
        @"SYSTEM\CurrentControlSet\Services\WinDefend",
        @"SYSTEM\CurrentControlSet\Services\FooSvc",
        @"SYSTEM\ControlSet001\Services\WinDefend",
        @"SYSTEM\ControlSet002\Control\Lsa",
        @"SAM",
        @"SAM\SAM\Domains",
        @"SECURITY",
        @"BCD00000000",
        @"HARDWARE",
    ];

    public static IEnumerable<object[]> DeleteDenyCases()
    {
        foreach (var seed in DeleteDenySeeds)
        foreach (var form in HklmForms(seed))
            yield return [seed, form];
    }

    [Fact]
    public void Matrix_has_at_least_200_cases()
        => Assert.True(DeleteDenyCases().Count() >= 200);

    [Theory]
    [MemberData(nameof(DeleteDenyCases))]
    public void Protected_key_delete_is_denied(string seed, string key)
    {
        var d = RegistryPolicy.CheckDeleteKey(key);
        Assert.True(d.IsDenied, $"seed={seed} key={key} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HKLM")]
    [InlineData(@"HKLM\")]
    [InlineData(@"HKCU")]
    [InlineData(@"HKCU\Software")]
    [InlineData(@"HKCU\Software\Microsoft")]
    [InlineData(@"HKCU\Software\Classes")]
    [InlineData(@"HKCU\Environment")]
    [InlineData(@"HKCR")]
    [InlineData(@"HKCR\CLSID")]
    [InlineData(@"HKU")]
    [InlineData(@"HKU\S-1-5-21-1")]
    [InlineData(@"HKU\S-1-5-21-1\Software")]
    [InlineData(@"HKCC\Software")]
    [InlineData(@"HKXX\Software\Foo")]
    [InlineData(@"HKLM\SOFTWARE\..\SYSTEM\Foo")]
    [InlineData(@"HKLM\SOFTWARE\.\Foo")]
    [InlineData("HKLM\\SOFTWARE\\Foo\0")]
    [InlineData(@"\REGISTRY")]
    [InlineData(@"\REGISTRY\BOGUS\Foo")]
    public void Structural_and_malformed_keys_are_denied(string? key)
        => Assert.True(RegistryPolicy.CheckDeleteKey(key).IsDenied);

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Foo")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foo.exe")]
    [InlineData(@"HKLM\SOFTWARE\Foo")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Foo\Bar")]
    [InlineData(@"HKCU\Software\Foo")]
    [InlineData(@"HKCR\.foo")]
    [InlineData(@"HKCR\FooApp.Document")]
    [InlineData(@"HKCR\CLSID\{12345678-1234-1234-1234-123456789ABC}")]
    public void Program_entries_are_deletable(string key)
    {
        var d = RegistryPolicy.CheckDeleteKey(key);
        Assert.True(d.IsAllowed, $"{key} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Office")]
    [InlineData(@"HKCU\Software\Microsoft\Office\16.0")]
    [InlineData(@"HKCU\Environment\Foo")]
    public void Ambiguous_branches_need_expert_confirmation(string key)
        => Assert.Equal(Verdict.ExpertOnly, RegistryPolicy.CheckDeleteKey(key).Verdict);

    // ---------- запись значений ----------

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin")]
    [InlineData(@"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Policies\System", "enablelua")]
    [InlineData(@"HKCU\Software\Foo", "EnableLUA")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware")]
    [InlineData(@"HKLM\SOFTWARE\Foo", "DisableRealtimeMonitoring")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL")]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Control\CI\Config", "VulnerableDriverBlocklistEnable")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Kernel", "MitigationOptions")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Start")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\FooSvc", "Start")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum\USB", "x")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\x.exe", "Debugger")]
    [InlineData(@"HKLM\SAM\SAM", "x")]
    [InlineData(@"HKLM\SECURITY", "x")]
    [InlineData(@"HKLM\SOFTWARE\Chistovik", "x")]
    [InlineData(@"HKCC\Software", "x")]
    [InlineData(@"HKLM\SOFTWARE\Foo", null)]
    [InlineData(@"HKLM\SOFTWARE\Foo", "bad\0name")]
    public void Protected_value_writes_are_denied(string key, string? value)
    {
        var d = RegistryPolicy.CheckSetValue(key, value);
        Assert.True(d.IsDenied, $"{key}::{value} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")]
    [InlineData(@"HKLM\SOFTWARE\Foo", "Enabled")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled")]
    public void Ordinary_tweak_targets_are_not_vetoed(string key, string value)
        => Assert.True(RegistryPolicy.CheckSetValue(key, value).IsAllowed);

    [Fact]
    public void ControlSet_aliases_resolve_to_current()
    {
        Assert.True(RegistryPolicy.TryParse(@"HKLM\SYSTEM\ControlSet001\Services\Foo", out var k, out _));
        Assert.Equal("CurrentControlSet", k.Segments[1]);
    }
}
