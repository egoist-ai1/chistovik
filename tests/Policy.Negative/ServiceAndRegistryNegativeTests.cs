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
        StartMode start = StartMode.Automatic, bool secProduct = false,
        string? installLocation = "C:\\Program Files\\Foo", string? native = null)
        => new(name, type, image, sig, ms, ppl, deps, owned, start, secProduct, installLocation, native);

    [Fact]
    public void Launch_protected_service_is_forbidden()
        => Assert.Equal(ServiceClass.Forbidden, Policy.Classify(Facts(ppl: true)).Class);

    [Fact]
    public void Registered_security_product_is_forbidden()
    {
        var f = Facts(secProduct: true);
        Assert.Equal(ServiceClass.Forbidden, Policy.Classify(f).Class);
        Assert.True(Policy.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Theory]
    [InlineData("edgeupdate")]
    [InlineData("edgeupdatem")]
    [InlineData("EdgeUpdate")]
    [InlineData("MicrosoftEdgeElevationService")]
    public void Edge_services_are_read_only(string name)
    {
        var f = Facts(name: name, image: "\"C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe\" /svc");
        Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(f).Class);
        Assert.True(Policy.CheckSetStart(f, StartMode.Disabled).IsDenied);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\foo.exe")]
    [InlineData("C:\\Program Files\\Windows Defender\\x.exe")]
    [InlineData("\\\\?\\C:\\Windows\\foo.exe")]
    public void Native_resolved_path_in_system_zone_is_forbidden_even_if_raw_path_looks_fine(string native)
        => Assert.Equal(ServiceClass.Forbidden,
            Policy.Classify(Facts(image: "\"C:\\Program Files\\Foo\\foo.exe\"", native: native)).Class);

    [Fact]
    public void Unparsable_native_path_is_read_only()
        => Assert.Equal(ServiceClass.ReadOnly,
            Policy.Classify(Facts(image: "\"C:\\Program Files\\Foo\\foo.exe\"", native: "relative\\x.exe")).Class);

    [Fact]
    public void Native_path_alone_is_enough()
        => Assert.Equal(ServiceClass.Modifiable, Policy.Classify(Facts(image: null, native: "C:\\Program Files\\Foo\\foo.exe")).Class);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\")]
    [InlineData("C:\\Foo")]                       // первый уровень тома
    [InlineData("C:\\Program Files")]             // сам корень Program Files
    [InlineData("C:\\Program Files (x86)")]
    [InlineData("C:\\ProgramData")]
    [InlineData("C:\\Users")]
    [InlineData("C:\\Windows")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("C:\\Windows\\Foo")]
    [InlineData("C:\\Program Files\\Windows Defender")]
    [InlineData("C:\\Program Files\\WindowsApps\\Foo")]
    [InlineData("relative\\Foo")]
    public void Untrusted_install_location_does_not_make_an_owner(string? location)
    {
        Assert.False(Policy.IsTrustedInstallLocation(location));
        // Microsoft-подписанная и неподписанная служба с таким «владельцем» остаются ReadOnly.
        Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(ms: true, installLocation: location)).Class);
        Assert.Equal(ServiceClass.ReadOnly, Policy.Classify(Facts(sig: false, installLocation: location)).Class);
    }

    [Theory]
    [InlineData("C:\\Program Files\\Foo")]
    [InlineData("D:\\Games\\Foo")]
    [InlineData("C:\\ProgramData\\Foo")]
    public void Install_location_two_levels_deep_makes_an_owner(string location)
    {
        Assert.True(Policy.IsTrustedInstallLocation(location));
        Assert.Equal(ServiceClass.Modifiable, Policy.Classify(Facts(ms: true, installLocation: location)).Class);
    }

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

    // ---------- HKLM\SYSTEM: запрет по умолчанию ----------

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "SetupExecute")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs", "kernel32")]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Control\Session Manager", "BootExecute")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\MiniNT", "x")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\EarlyLaunch", "BackupPath")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\WMI\Autologger", "Start")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Srp\GP", "x")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control", "SystemStartOptions")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "TdrLevel")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\SomethingNew", "x")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\..\Environment", "Path")]
    [InlineData(@"HKLM\SYSTEM\Foo", "x")]
    [InlineData(@"HKLM\SYSTEM", "x")]
    public void System_branch_is_denied_unless_in_allowlist(string key, string value)
    {
        var d = RegistryPolicy.CheckSetValue(key, value);
        Assert.True(d.IsDenied, $"{key}::{value} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout")]
    [InlineData(@"HKLM\SYSTEM\ControlSet001\Control", "WaitToKillServiceTimeout")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\abc", "Attributes")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled")]
    public void System_allowlist_entries_stay_allowed(string key, string value)
    {
        var d = RegistryPolicy.CheckSetValue(key, value);
        Assert.True(d.IsAllowed, $"{key}::{value} -> {d.Verdict} ({d.Rule})");
    }

    // ---------- Policies\Microsoft ----------

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceGuard", "EnableVirtualizationBasedSecurity")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "Foo")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CodeIntegrity", "x")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\SrpV2", "x")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DisableWindowsUpdateAccess")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard", "x")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Microsoft Antimalware", "x")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Edge", "x")]
    [InlineData(@"HKLM\SOFTWARE\Wow6432Node\Policies\Microsoft\Windows\DeviceGuard", "x")]
    [InlineData(@"HKCU\Software\Policies\Microsoft\Windows\System", "x")]
    // исключения узкие: только точный ключ и точное имя значения
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\Sub", "AllowTelemetry")]
    [InlineData(@"HKCU\Software\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions")]
    [InlineData(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "NoDriveTypeAutoRun")]
    public void Policies_microsoft_writes_are_denied(string key, string value)
    {
        var d = RegistryPolicy.CheckSetValue(key, value);
        Assert.True(d.IsDenied, $"{key}::{value} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CodeIntegrity")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\SrpV2")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceGuard")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Microsoft Antimalware")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Edge")]
    [InlineData(@"HKCU\Software\Policies\Microsoft\Windows\Explorer")]
    [InlineData(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection")]
    public void Policies_microsoft_key_delete_is_denied(string key)
        => Assert.True(RegistryPolicy.CheckDeleteKey(key).IsDenied);

    [Fact]
    public void Other_vendor_policies_stay_expert_only_for_delete()
        => Assert.Equal(Verdict.ExpertOnly, RegistryPolicy.CheckDeleteKey(@"HKLM\SOFTWARE\Policies\Google\Chrome").Verdict);

    [Fact]
    public void Catalog_policy_exceptions_are_pinned_to_exact_key_and_value()
    {
        // твик «без веб-поиска Bing»
        Assert.True(RegistryPolicy.CheckSetValue(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions").IsAllowed);
        // твик «минимальная телеметрия»
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry").IsAllowed);
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "allowtelemetry").IsAllowed);
    }

    // ---------- новые защищённые узлы Software ----------

    public static IEnumerable<object[]> NewSoftwareDenySeeds()
    {
        string[] seeds =
        [
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\Foo",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections",
            @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Startup\0",
        ];
        foreach (var s in seeds) yield return [s];
    }

    [Theory]
    [MemberData(nameof(NewSoftwareDenySeeds))]
    public void New_protected_software_nodes_are_denied_for_delete_and_write(string seed)
    {
        foreach (var form in HklmForms(seed))
        {
            Assert.True(RegistryPolicy.CheckDeleteKey(form).IsDenied, form);
            Assert.True(RegistryPolicy.CheckSetValue(form, "AppInit_DLLs").IsDenied, form);
        }
    }

    [Theory]
    [InlineData("ProxyEnable")]
    [InlineData("ProxyServer")]
    [InlineData("AutoConfigURL")]
    [InlineData("ProxyOverride")]
    [InlineData("WinHttpSettings")]
    public void Proxy_values_cannot_be_written_in_any_hive(string value)
    {
        Assert.True(RegistryPolicy.CheckSetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", value).IsDenied);
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", value).IsDenied);
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Internet Settings", value).IsDenied);
        Assert.True(RegistryPolicy.CheckSetValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections", value).IsDenied);
        Assert.True(RegistryPolicy.CheckSetValue(@"HKU\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Internet Settings", value).IsDenied);
        Assert.True(RegistryPolicy.CheckDeleteKey(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections").IsDenied);
    }

    [Fact]
    public void Uac_admin_approval_mode_value_is_denied()
        => Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Foo", "TypeOfAdminApprovalMode").IsDenied);

    // ---------- классы ----------

    [Theory]
    [InlineData(@"HKCR\.exe")]
    [InlineData(@"HKCR\.lnk")]
    [InlineData(@"HKCR\.dll")]
    [InlineData(@"HKCR\.sys")]
    [InlineData(@"HKCR\.bat")]
    [InlineData(@"HKCR\.cmd")]
    [InlineData(@"HKCR\.com")]
    [InlineData(@"HKCR\.msi")]
    [InlineData(@"HKCR\.cpl")]
    [InlineData(@"HKCR\.scr")]
    [InlineData(@"HKCR\.inf")]
    [InlineData(@"HKCR\.reg")]
    [InlineData(@"HKCR\.url")]
    [InlineData(@"HKCR\.library-ms")]
    [InlineData(@"HKCR\.settingcontent-ms")]
    [InlineData(@"HKCR\.EXE\shell")]
    [InlineData(@"HKCR\exefile")]
    [InlineData(@"HKCR\exefile\shell\open\command")]
    [InlineData(@"HKCR\lnkfile")]
    [InlineData(@"HKCR\batfile")]
    [InlineData(@"HKCR\cmdfile")]
    [InlineData(@"HKCR\dllfile")]
    [InlineData(@"HKCR\regfile")]
    [InlineData(@"HKCR\inffile")]
    [InlineData(@"HKCR\scrfile")]
    [InlineData(@"HKCR\Unknown")]
    [InlineData(@"HKCR\Folder")]
    [InlineData(@"HKCR\Directory")]
    [InlineData(@"HKCR\Drive")]
    [InlineData(@"HKCR\ms-settings")]
    [InlineData(@"HKCR\http")]
    [InlineData(@"HKCR\https\shell\open\command")]
    [InlineData(@"HKCR\file")]
    [InlineData(@"HKCR\mailto")]
    [InlineData(@"HKCR\ActivatableClasses")]
    [InlineData(@"HKCR\ActivatableClasses\Package\Foo")]
    [InlineData(@"HKCR\PackagedCom")]
    [InlineData(@"HKCR\AppX4hxtad77fbk3jkkeerkrm0ze94wjf3s9")]
    [InlineData(@"HKCR\appxabc\Shell")]
    [InlineData(@"HKCR\Wow6432Node\exefile")]
    [InlineData(@"HKCR\Wow6432Node\.exe")]
    [InlineData(@"HKCU\Software\Classes\.exe")]
    [InlineData(@"HKCU\Software\Classes\exefile\shell")]
    [InlineData(@"HKLM\SOFTWARE\Classes\.lnk")]
    [InlineData(@"HKLM\SOFTWARE\Classes\http\shell")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes\.exe")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes\exefile")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Classes\exefile")]
    public void System_classes_are_never_deletable(string key)
    {
        var d = RegistryPolicy.CheckDeleteKey(key);
        Assert.True(d.IsDenied, $"{key} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKCR\.foo")]
    [InlineData(@"HKCR\.foo\OpenWithProgids")]
    [InlineData(@"HKCU\Software\Classes\.bar")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes\.foo")]
    [InlineData(@"HKCR\Directory\shell\FooTool")]
    [InlineData(@"HKCR\Folder\shellex\ContextMenuHandlers\Foo")]
    [InlineData(@"HKCR\Drive\shell\Foo")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\MuiCache")]
    public void Other_extensions_and_shell_roots_need_expert_confirmation(string key)
    {
        var d = RegistryPolicy.CheckDeleteKey(key);
        Assert.Equal(Verdict.ExpertOnly, d.Verdict);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Classes\Local Settings")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\Foo")]
    [InlineData(@"HKCU\Software\Classes\Local Settings\AppModel\Repository")]
    [InlineData(@"HKCR\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository")]
    public void Local_settings_software_and_appmodel_are_protected(string key)
    {
        var d = RegistryPolicy.CheckDeleteKey(key);
        Assert.True(d.IsDenied, $"{key} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"HKU\.DEFAULT")]
    [InlineData(@"HKU\.DEFAULT\Software\Foo")]
    [InlineData(@"HKU\.DEFAULT\Software\Microsoft\Office")]
    [InlineData(@"HKU\.DEFAULT_Classes\.foo")]
    [InlineData(@"HKU\.DEFAULT_Classes\FooApp.Document")]
    [InlineData(@"HKU\S-1-5-18\Software\Foo")]
    [InlineData(@"HKU\S-1-5-19\Software\Foo")]
    [InlineData(@"HKU\S-1-5-20\Software\Foo")]
    [InlineData(@"HKU\S-1-5-18_Classes\FooApp.Document")]
    [InlineData(@"HKU\Foo\Software\Bar")]
    [InlineData(@"\REGISTRY\USER\.DEFAULT\Software\Foo")]
    public void Non_ordinary_user_hives_are_denied(string key)
    {
        Assert.True(RegistryPolicy.CheckDeleteKey(key).IsDenied, key);
        Assert.True(RegistryPolicy.CheckSetValue(key, "Foo").IsDenied, key);
    }

    [Theory]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Foo")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes\FooApp.Document")]
    [InlineData(@"HKU\S-1-12-1-1-2-3-4\Software\Foo")]
    public void Ordinary_user_hives_stay_deletable(string key)
        => Assert.True(RegistryPolicy.CheckDeleteKey(key).IsAllowed, key);

    // ---------- привязка подтверждения эксперта к объекту ----------

    [Fact]
    public void Registry_decisions_carry_normalized_target()
    {
        var d = RegistryPolicy.CheckDeleteKey(@"hkcu:\Software\Microsoft\Office\16.0\");
        Assert.Equal(Verdict.ExpertOnly, d.Verdict);
        Assert.Equal(@"CurrentUser\Software\Microsoft\Office\16.0", d.Target);
        PolicyViolationException.Enforce(d, new ExpertConfirmation(d.Target));
        Assert.Throws<PolicyViolationException>(() =>
            PolicyViolationException.Enforce(d, new ExpertConfirmation(@"CurrentUser\Software\Microsoft\Office\15.0")));
        var w = RegistryPolicy.CheckSetValue(@"HKCU\Software\Foo", "Bar");
        Assert.Equal(@"CurrentUser\Software\Foo::Bar", w.Target);
    }

    [Fact]
    public void Default_decision_and_verdict_zero_are_deny()
    {
        Assert.True(default(PolicyDecision).IsDenied);
        Assert.Equal(Verdict.Deny, (Verdict)0);
    }

    [Fact]
    public void ControlSet_aliases_resolve_to_current()
    {
        Assert.True(RegistryPolicy.TryParse(@"HKLM\SYSTEM\ControlSet001\Services\Foo", out var k, out _));
        Assert.Equal("CurrentControlSet", k.Segments[1]);
    }
}
