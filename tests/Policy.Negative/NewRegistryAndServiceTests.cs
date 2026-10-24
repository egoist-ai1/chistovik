using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.PolicyNegative;

public class NewRegistryClientTests
{
    const string Sid = "S-1-5-21-1-2-3-1001";
    const string Other = "S-1-5-21-1-2-3-1002";

    // ---------- правило HKU и SID клиента ----------

    [Fact]
    public void Client_hive_is_allowed_for_ordinary_values()
    {
        Assert.True(RegistryPolicy.CheckSetValue($@"HKU\{Sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", Sid).IsAllowed);
        Assert.True(RegistryPolicy.CheckDeleteValue($@"HKU\{Sid}\Software\Foo", "Enabled", Sid).IsAllowed);
        // машинные ключи проходят обычные правила
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Foo", "Enabled", Sid).IsAllowed);
    }

    [Theory]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1002\Software\Foo")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1002_Classes\Foo")]
    [InlineData(@"HKU\S-1-5-18\Software\Foo")]
    [InlineData(@"HKU\S-1-5-19\Software\Foo")]
    [InlineData(@"HKU\S-1-5-20\Software\Foo")]
    [InlineData(@"HKU\.DEFAULT\Software\Foo")]
    [InlineData(@"HKU\.DEFAULT_Classes\Foo")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-100\Software\Foo")]          // префикс SID клиента
    [InlineData(@"HKU\S-1-5-21-1-2-3-10010\Software\Foo")]        // SID клиента как префикс
    [InlineData(@"HKU")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001")]                       // корень профиля: структурный узел
    public void Foreign_and_system_hives_are_denied_for_the_client(string key)
    {
        Assert.True(RegistryPolicy.CheckSetValue(key, "Enabled", Sid).IsDenied, key);
        Assert.True(RegistryPolicy.CheckDeleteValue(key, "Enabled", Sid).IsDenied, key);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Foo")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Foo")]
    [InlineData(@"HKCR\Foo")]
    [InlineData(@"HKCC\Software\Foo")]
    public void Agent_never_writes_hkcu_hkcr_hkcc(string key)
        => Assert.True(RegistryPolicy.CheckSetValue(key, "Enabled", Sid).IsDenied, key);

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5-18")]
    [InlineData(".DEFAULT")]
    [InlineData("S-1-5-21-1-2-3-1001_Classes")]
    [InlineData("S-1-5-21-1-2-3-1001\n")]
    [InlineData("garbage")]
    public void Bad_client_sid_denies_everything(string sid)
    {
        Assert.True(RegistryPolicy.CheckSetValue(@"HKLM\SOFTWARE\Foo", "Enabled", sid).IsDenied);
        Assert.True(RegistryPolicy.CheckSetValue($@"HKU\{Sid}\Software\Foo", "Enabled", sid).IsDenied);
        Assert.True(RegistryPolicy.CheckDeleteKey($@"HKU\{Sid}\Software\Foo\Bar", sid).IsDenied);
    }

    [Fact]
    public void Segment_with_trailing_newline_is_not_an_ordinary_user()
    {
        var k = new RegKey(RegHive.Users, [Sid + "\n", "Software", "Foo"]);
        Assert.True(RegistryPolicy.CheckSetValue(k, "Enabled").IsDenied);
        Assert.True(RegistryPolicy.CheckDeleteKey(k).IsDenied);
    }

    [Fact]
    public void Delete_key_for_client_uses_the_same_hive_rule()
    {
        Assert.True(RegistryPolicy.CheckDeleteKey($@"HKU\{Sid}\Software\Foo\Bar", Sid).IsAllowed);
        Assert.True(RegistryPolicy.CheckDeleteKey($@"HKU\{Other}\Software\Foo\Bar", Sid).IsDenied);
        Assert.True(RegistryPolicy.CheckDeleteKey(@"HKCU\Software\Foo\Bar", Sid).IsDenied);
    }

    // ---------- CheckDeleteValue не мягче CheckSetValue ----------

    public static IEnumerable<object?[]> Samples() => new (string? Key, string? Value)[]
    {
        (@"HKLM\SOFTWARE\Foo", "Enabled"), (@"HKLM\SOFTWARE\Foo", null), (@"HKLM\SOFTWARE\Foo", "bad\0"),
        (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA"),
        (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell"),
        (@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", "Start"),
        (@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"),
        (@"HKCU\Software\Foo", "x"), (@"HKCU\Environment", "UserInitMprLogonScript"), (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Foo"),
        (@"HKCR\exefile\shell\open\command", ""), (@"HKU\S-1-5-18\Software\Foo", "x"), (@"HKCC\Software", "x"),
        (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"), (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "x"),
        (null, "x"), ("", "x"), (@"HKLM\SOFTWARE\..\SYSTEM", "x"),
    }.Select(t => new object?[] { t.Key, t.Value });

    [Theory]
    [MemberData(nameof(Samples))]
    public void Delete_value_is_never_softer_than_set_value(string? key, string? value)
    {
        var set = RegistryPolicy.CheckSetValue(key, value);
        var del = RegistryPolicy.CheckDeleteValue(key, value);
        Assert.Equal(set.Verdict, del.Verdict);
        Assert.Equal(set.Rule, del.Rule);
        Assert.Equal(set.Target, del.Target);
        var setC = RegistryPolicy.CheckSetValue(key, value, Sid);
        var delC = RegistryPolicy.CheckDeleteValue(key, value, Sid);
        Assert.Equal(setC.Verdict, delC.Verdict);
        // с клиентом решение не мягче, чем без него
        Assert.True(setC.Verdict <= set.Verdict, $"{key}::{value}");
    }

    // ---------- пробел из ревью: запуск кода через реестр ----------

    static readonly string[] ClassesRoots =
    [
        @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\Wow6432Node\Classes", @"HKCU\Software\Classes",
        $@"HKU\{Sid}\Software\Classes", $@"HKU\{Sid}_Classes",
    ];

    public static IEnumerable<object[]> ClassesCodeKeys()
    {
        string[] tails =
        [
            @"exefile\shell\open\command", @".exe", @".exe\shell\open\command", @"lnkfile", @"lnkfile\shell\open\command",
            @"txtfile\shell\open\command", @"*\shell\open\command", @"*\shell\foo\command", @"Directory\shell\foo\command",
            @"CLSID\{00000000-0000-0000-0000-000000000001}\InprocServer32", @"CLSID\{00000000-0000-0000-0000-000000000001}\LocalServer32",
            @"CLSID\{00000000-0000-0000-0000-000000000001}\InprocHandler32", @"CLSID\{00000000-0000-0000-0000-000000000001}\TreatAs",
            @"*\ShellEx\ContextMenuHandlers\Foo", @"Directory\ShellEx\ContextMenuHandlers\Foo", @"Foo.Document\ShellEx\IconHandler",
            @"Foo.Document\shell\open\DropTarget", @"Foo.Document\shell\open\command", @"ms-settings\Shell\Open\command",
            @"Wow6432Node\CLSID\{00000000-0000-0000-0000-000000000001}\InprocServer32", @"Interface\{x}\ProxyStubClsid32",
        ];
        foreach (var root in ClassesRoots)
        foreach (var tail in tails)
        foreach (var value in new[] { "", "DelegateExecute", "ThreadingModel", "Foo" })
            yield return [root + "\\" + tail, value];
    }

    [Theory]
    [MemberData(nameof(ClassesCodeKeys))]
    public void Writing_values_that_redirect_launched_code_is_denied(string key, string value)
    {
        var d = RegistryPolicy.CheckSetValue(key, value);
        Assert.True(d.IsDenied, $"{key}::{value} -> {d.Verdict} ({d.Rule})");
        Assert.True(RegistryPolicy.CheckDeleteValue(key, value).IsDenied, key);
    }

    [Theory]
    [InlineData(@"HKCR\exefile\shell\open\command", "")]
    [InlineData(@"HKCR\.exe", "")]
    [InlineData(@"HKCR\CLSID\{x}\InprocServer32", "")]
    [InlineData(@"HKCR\Foo", "Bar")]
    [InlineData(@"HKEY_CLASSES_ROOT\Foo", "Bar")]
    [InlineData(@"HKCR", "x")]
    public void Hkcr_values_are_never_written(string key, string value)
        => Assert.True(RegistryPolicy.CheckSetValue(key, value).IsDenied);

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes", "x")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Foo.Document", "x")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001_Classes", "x")]
    public void Any_other_classes_value_is_denied_by_default(string key, string value)
        => Assert.True(RegistryPolicy.CheckSetValue(key, value).IsDenied);

    [Theory]
    [InlineData(@"HKCU\Environment", "UserInitMprLogonScript")]
    [InlineData(@"HKCU\Environment", "Path")]
    [InlineData(@"HKCU\Volatile Environment", "X")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Environment", "UserInitMprLogonScript")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Environment", "TEMP")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Volatile Environment", "X")]
    [InlineData(@"HKCU\Software\Foo", "UserInitMprLogonScript")]
    [InlineData(@"HKLM\SOFTWARE\Foo", "userinitmprlogonscript")]
    public void Environment_and_logon_script_values_are_denied(string key, string value)
        => Assert.True(RegistryPolicy.CheckSetValue(key, value).IsDenied);

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnceEx")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunServices")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunServicesOnce")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\runonce")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData(@"HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Run")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run")]
    [InlineData(@"HKCU\Software\Microsoft\Command Processor")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components\{x}")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects\{x}")]
    public void Autorun_keys_are_denied_for_any_value(string key)
    {
        Assert.True(RegistryPolicy.CheckSetValue(key, "Foo").IsDenied, key);
        Assert.True(RegistryPolicy.CheckSetValue(key + @"\Sub", "Foo").IsDenied, key);
        Assert.True(RegistryPolicy.CheckDeleteValue(key, "Foo").IsDenied, key);
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Foo")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "Foo")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "Foo.lnk")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", "a")]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")]
    [InlineData(@"HKCU\Control Panel\Desktop", "MenuShowDelay")]
    public void Neighbouring_legitimate_targets_stay_allowed(string key, string value)
        => Assert.True(RegistryPolicy.CheckSetValue(key, value).IsAllowed, $"{key}::{value}");

    [Fact]
    public void Controlset_and_sid_regexes_do_not_accept_trailing_newline()
    {
        // ControlSet001\n превратилось бы в CurrentControlSet и попало бы под правила SYSTEM; без управляющих символов в ключе это тоже отказ.
        Assert.False(RegistryPolicy.TryParse("HKLM\\SYSTEM\\ControlSet001\n\\Services", out _, out _));
        var k = new RegKey(RegHive.Users, [Sid + "\n"]);
        Assert.True(RegistryPolicy.CheckDeleteKey(k).IsDenied);
    }
}

public class NewServiceStartTests
{
    static readonly ServicePolicy Policy = new(PolicyContext.Fixed);

    static ServiceFacts Facts(
        string name = "FooSvc",
        string? image = "\"C:\\Program Files\\Foo\\foo.exe\" -run",
        StartMode start = StartMode.Manual,
        string? installLocation = "C:\\Program Files\\Foo",
        string? native = null)
        => new(name, ServiceType.OwnProcess, image, true, false, false, 0, true, start, false, installLocation, native);

    // ---------- ValidName: \z вместо $ ----------

    [Theory]
    [InlineData("FooSvc\n")]
    [InlineData("\nFooSvc")]
    [InlineData("Foo\nSvc")]
    [InlineData("FooSvc\r\n")]
    [InlineData("FooSvc ")]
    [InlineData(" FooSvc")]
    [InlineData("Foo\\Svc")]
    [InlineData("Foo\0Svc")]
    [InlineData("")]
    public void Names_with_line_breaks_or_edges_are_rejected(string name)
    {
        var f = Facts(name: name);
        var c = Policy.Classify(f);
        Assert.Equal(ServiceClass.Forbidden, c.Class);
        Assert.Equal("bad-name", c.Rule);
        Assert.True(Policy.CheckSetStart(f, StartMode.Disabled).IsDenied);
        Assert.True(Policy.CheckSetStart(f, StartMode.Automatic, isRevertOfOwnStep: true).IsDenied);
    }

    [Fact]
    public void Name_of_257_chars_is_rejected_and_256_is_accepted()
    {
        Assert.Equal("bad-name", Policy.Classify(Facts(name: new string('a', 257))).Rule);
        Assert.NotEqual("bad-name", Policy.Classify(Facts(name: new string('a', 256))).Rule);
    }

    // ---------- повышение запуска ----------

    [Theory]
    [InlineData(StartMode.Disabled, StartMode.Manual)]
    [InlineData(StartMode.Disabled, StartMode.Automatic)]
    [InlineData(StartMode.Disabled, StartMode.AutomaticDelayed)]
    [InlineData(StartMode.Manual, StartMode.Automatic)]
    [InlineData(StartMode.Manual, StartMode.AutomaticDelayed)]
    public void Elevation_outside_program_files_is_denied_directly_and_allowed_only_as_revert(StartMode from, StartMode to)
    {
        string[] outside =
        [
            "\"C:\\ProgramData\\Foo\\foo.exe\"",
            "\"C:\\Users\\Egoist\\AppData\\Local\\Foo\\foo.exe\"",
            "\"D:\\Tools\\foo.exe\"",
            "\"C:\\Program Files\\..\\ProgramData\\Foo\\foo.exe\"",
        ];
        foreach (var image in outside)
        {
            var f = Facts(image: image, start: from, installLocation: "C:\\ProgramData\\Foo");
            Assert.Equal(ServiceClass.Modifiable, Policy.Classify(f).Class);
            var d = Policy.CheckSetStart(f, to);
            Assert.True(d.IsDenied, image);
            Assert.Equal("service-elevate-outside-program-files", d.Rule);
            Assert.True(Policy.CheckSetStart(f, to, isRevertOfOwnStep: true).IsAllowed, image);
        }
    }

    [Theory]
    [InlineData(StartMode.Disabled, StartMode.Manual)]
    [InlineData(StartMode.Disabled, StartMode.Automatic)]
    [InlineData(StartMode.Manual, StartMode.Automatic)]
    [InlineData(StartMode.Manual, StartMode.AutomaticDelayed)]
    public void Elevation_inside_program_files_is_allowed_directly(StartMode from, StartMode to)
    {
        Assert.True(Policy.CheckSetStart(Facts(start: from), to).IsAllowed);
        Assert.True(Policy.CheckSetStart(Facts(image: "\"C:\\Program Files (x86)\\Foo\\foo.exe\"", start: from), to).IsAllowed);
        Assert.True(Policy.CheckSetStart(Facts(image: null, native: "C:\\Program Files\\Foo\\foo.exe", start: from), to).IsAllowed);
    }

    [Fact]
    public void Elevation_checks_every_known_path_of_the_service_file()
    {
        // сырой путь в Program Files, а раскрытый Native в ProgramData (или наоборот): прямое повышение запрещено
        var a = Facts(image: "\"C:\\Program Files\\Foo\\foo.exe\"", native: "C:\\ProgramData\\Foo\\foo.exe", start: StartMode.Disabled);
        var b = Facts(image: "\"C:\\ProgramData\\Foo\\foo.exe\"", native: "C:\\Program Files\\Foo\\foo.exe", start: StartMode.Disabled);
        Assert.True(Policy.CheckSetStart(a, StartMode.Manual).IsDenied);
        Assert.True(Policy.CheckSetStart(b, StartMode.Manual).IsDenied);
        Assert.True(Policy.CheckSetStart(a, StartMode.Manual, isRevertOfOwnStep: true).IsAllowed);
    }

    [Theory]
    [InlineData(StartMode.Automatic, StartMode.Manual)]
    [InlineData(StartMode.Automatic, StartMode.Disabled)]
    [InlineData(StartMode.AutomaticDelayed, StartMode.Disabled)]
    [InlineData(StartMode.Manual, StartMode.Disabled)]
    [InlineData(StartMode.Manual, StartMode.Manual)]
    [InlineData(StartMode.Automatic, StartMode.AutomaticDelayed)]
    [InlineData(StartMode.AutomaticDelayed, StartMode.Automatic)]
    [InlineData(StartMode.Disabled, StartMode.Disabled)]
    public void Lowering_or_keeping_the_start_type_stays_allowed_everywhere(StartMode from, StartMode to)
    {
        Assert.True(Policy.CheckSetStart(Facts(start: from), to).IsAllowed);
        Assert.True(Policy.CheckSetStart(Facts(image: "\"C:\\ProgramData\\Foo\\foo.exe\"", start: from, installLocation: "C:\\ProgramData\\Foo"), to).IsAllowed);
    }

    [Fact]
    public void Revert_flag_never_relaxes_classification_or_boot_targets()
    {
        var hard = Facts(name: "WinDefend", start: StartMode.Disabled);
        Assert.True(Policy.CheckSetStart(hard, StartMode.Automatic, isRevertOfOwnStep: true).IsDenied);
        var f = Facts(start: StartMode.Disabled);
        Assert.True(Policy.CheckSetStart(f, StartMode.Boot, isRevertOfOwnStep: true).IsDenied);
        Assert.True(Policy.CheckSetStart(f, StartMode.System, isRevertOfOwnStep: true).IsDenied);
        var shared = f with { Type = ServiceType.ShareProcess };
        Assert.True(Policy.CheckSetStart(shared, StartMode.Manual, isRevertOfOwnStep: true).IsDenied);
        var inWindows = Facts(image: "\"C:\\Windows\\System32\\foo.exe\"", start: StartMode.Disabled);
        Assert.True(Policy.CheckSetStart(inWindows, StartMode.Manual, isRevertOfOwnStep: true).IsDenied);
    }
}
