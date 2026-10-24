using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>
/// Негативные тесты политики путей. Падение любого из них блокирует релиз:
/// закрытая зона не должна быть пропущена ни при каком написании пути.
/// </summary>
public class PathPolicyNegativeTests
{
    static readonly PathPolicy Policy = new(PolicyContext.Fixed);

    // Всё из этого списка и любой его вариант написания должен получить Deny.
    static readonly string[] ProtectedSeeds =
    [
        @"C:\Windows",
        @"C:\Windows\WinSxS",
        @"C:\Windows\WinSxS\amd64_microsoft-windows-foo_31bf3856ad364e35_10.0.26100.1_none_0123456789abcdef",
        @"C:\Windows\System32\DriverStore\FileRepository\foo.inf_amd64_0123",
        @"C:\Windows\Installer\{12345678-1234-1234-1234-123456789ABC}",
        @"C:\Windows\System32\cmd.exe",
        @"C:\Windows\System32\config\SAM",
        @"C:\Program Files\WindowsApps",
        @"C:\Program Files\WindowsApps\Microsoft.WindowsStore_22401.1400.0.0_x64__8wekyb3d8bbwe",
        @"C:\Program Files\Windows Defender\MpCmdRun.exe",
        @"C:\Program Files (x86)\Common Files\Microsoft Shared\ink",
        @"C:\Program Files\Common Files",
        @"C:\ProgramData\Microsoft\Windows\Start Menu",
        @"C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys",
        @"C:\ProgramData\Microsoft\Windows Defender\Platform",
        @"C:\ProgramData\Microsoft",
        @"C:\ProgramData",
        @"C:\ProgramData\Chistovik\chistovik.db",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users",
        @"C:\Users\Egoist",
        @"C:\Users\Egoist\AppData",
        @"C:\Users\Egoist\AppData\Local",
        @"C:\Users\Egoist\AppData\Roaming",
        @"C:\Users\Egoist\AppData\Roaming\Microsoft",
        @"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
        @"C:\Users\Egoist\AppData\Local\Microsoft\Windows\Explorer",
        @"C:\Users\Egoist\AppData\Roaming\Microsoft\Credentials",
        @"C:\Users\Egoist\NTUSER.DAT",
        @"C:\Users\Egoist\ntuser.dat.LOG1",
        @"C:\Users\Egoist\Documents",
        @"C:\Users\Egoist\Desktop",
        @"C:\Users\Default\AppData",
        @"C:\Users\All Users",
        @"C:\System Volume Information",
        @"C:\System Volume Information\{guid}",
        @"C:\Recovery",
        @"C:\$Recycle.Bin\S-1-5-21",
        @"C:\pagefile.sys",
        @"C:\hiberfil.sys",
        @"C:\Chistovik.Quarantine",
        @"C:\Chistovik.Quarantine\op-1\3\file.txt",
        @"D:\Windows\System32",
        @"D:\System Volume Information",
        @"D:\Chistovik.Quarantine\op-2",
        @"D:\Program Files\WindowsApps\Foo",
    ];

    static IEnumerable<string> Variants(string p)
    {
        yield return p;
        yield return p.ToLowerInvariant();
        yield return p.ToUpperInvariant();
        yield return p.Replace('\\', '/');
        yield return @"\\?\" + p;
        yield return @"\\.\" + p;
        yield return @"\??\" + p;
        yield return (@"\\?\" + p).ToLowerInvariant();
        yield return p.Replace(@"\", @"\\");
        yield return p + @"\";
        yield return p + @"\.";
        yield return p + @"\sub\..";
        yield return p.Replace(@":\", @":\.\");
        yield return p.Replace(@":\", @":\Users\..\");
        yield return p.Replace(@":\", @":\Program Files\..\");
        yield return p.Replace(@":\", @":\Windows\System32\..\..\");
        yield return p + ".";
        yield return p + " ";
        yield return p + @"\x\y\..\..";
        yield return p + @"\..\" + p.Split('\\')[^1];
        // хвостовая точка/пробел на первом компоненте после диска
        var parts = p.Split('\\');
        if (parts.Length > 1)
        {
            parts[1] += ".";
            yield return string.Join('\\', parts);
            parts[1] = parts[1].TrimEnd('.') + " ";
            yield return string.Join('\\', parts);
        }
    }

    public static IEnumerable<object[]> ProtectedVariants()
    {
        foreach (var seed in ProtectedSeeds)
        foreach (var v in Variants(seed))
            yield return [seed, v];
    }

    [Fact]
    public void Matrix_has_at_least_300_cases()
        => Assert.True(ProtectedVariants().Count() >= 300);

    [Theory]
    [MemberData(nameof(ProtectedVariants))]
    public void Protected_zone_is_never_allowed(string seed, string variant)
    {
        var d = Policy.CheckQuarantineMove(variant);
        Assert.True(d.IsDenied, $"seed={seed} variant={variant} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:Windows")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows")]
    [InlineData(@"C:\Windows\WinSxS:stream")]
    [InlineData(@"C:\PROGRA~1\WindowsApps")]
    [InlineData(@"C:\Windows\NUL")]
    [InlineData(@"C:\Windows\con.txt")]
    [InlineData(@"%SystemRoot%\System32")]
    [InlineData(@"C:\Windows\x*")]
    [InlineData(@"C:\Windows\x?")]
    [InlineData("C:\\Windows\\x\0y")]
    [InlineData(@"C:\..\..\Windows")]
    [InlineData(@"C:\...\foo")]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    public void Malformed_or_ambiguous_paths_are_denied(string? path)
        => Assert.True(Policy.CheckQuarantineMove(path).IsDenied);

    [Fact]
    public void Overlong_path_is_denied()
        => Assert.True(Policy.CheckQuarantineMove(@"C:\" + new string('a', 33000)).IsDenied);

    [Theory]
    [InlineData(@"C:\Users\Egoist\AppData\Local\SomeApp")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\SomeApp\cache")]
    [InlineData(@"C:\Users\Egoist\AppData\Local\Temp\foo")]
    [InlineData(@"C:\Users\Egoist\AppData\Local\Microsoft\Edge\User Data\Foo")]
    [InlineData(@"C:\Program Files\SomeApp")]
    [InlineData(@"C:\Program Files (x86)\SomeApp\bin")]
    [InlineData(@"C:\Program Files\Common Files\SomeVendor")]
    [InlineData(@"C:\ProgramData\SomeVendor")]
    [InlineData(@"D:\Games\Foo")]
    [InlineData(@"D:\Program Files\SomeApp")]
    public void Ordinary_program_leftovers_are_allowed(string path)
    {
        var d = Policy.CheckQuarantineMove(path);
        Assert.True(d.IsAllowed, $"{path} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"C:\Users\Egoist\Documents\file.txt")]
    [InlineData(@"C:\Users\Egoist\Downloads\setup.exe")]
    [InlineData(@"C:\SomeRootFolder")]
    [InlineData(@"C:\ProgramData\Package Cache\{abc}")]
    [InlineData(@"C:\Users\Egoist\AppData\Local\Packages\Foo_8wekyb3d8bbwe\LocalState")]
    public void User_data_and_cache_need_expert_confirmation(string path)
    {
        var d = Policy.CheckQuarantineMove(path);
        Assert.Equal(Verdict.ExpertOnly, d.Verdict);
        Assert.Throws<PolicyViolationException>(() => PolicyViolationException.Enforce(d));
        PolicyViolationException.Enforce(d, new ExpertConfirmation(d.Target));
    }

    [Fact]
    public void Deny_cannot_be_overridden_by_expert_confirmation()
    {
        var d = Policy.CheckQuarantineMove(@"C:\Windows\WinSxS\x");
        Assert.Throws<PolicyViolationException>(() => PolicyViolationException.Enforce(d, new ExpertConfirmation(d.Target)));
    }

    [Fact]
    public void Default_decision_is_denied()
    {
        Assert.True(default(PolicyDecision).IsDenied);
        Assert.Equal(Verdict.Deny, default(Verdict));
        Assert.Throws<PolicyViolationException>(() => PolicyViolationException.Enforce(default));
    }

    [Fact]
    public void Expert_confirmation_is_bound_to_the_object()
    {
        var d = Policy.CheckQuarantineMove(@"C:\Users\Egoist\Documents\file.txt");
        Assert.Equal(Verdict.ExpertOnly, d.Verdict);
        Assert.Equal(@"C:\Users\Egoist\Documents\file.txt", d.Target);
        // подтверждение другого объекта не годится
        Assert.Throws<PolicyViolationException>(() =>
            PolicyViolationException.Enforce(d, new ExpertConfirmation(@"C:\Users\Egoist\Documents\other.txt")));
        Assert.Throws<PolicyViolationException>(() =>
            PolicyViolationException.Enforce(d, new ExpertConfirmation("")));
        // тот же объект в другом регистре подходит
        PolicyViolationException.Enforce(d, new ExpertConfirmation(@"c:\users\egoist\documents\FILE.txt"));
        // решение без цели подтвердить нельзя
        var noTarget = PolicyDecision.Expert("x", "y");
        Assert.Throws<PolicyViolationException>(() =>
            PolicyViolationException.Enforce(noTarget, new ExpertConfirmation("")));
    }

    // ---------- пункты ревью: любой том, точки соединения, меню «Пуск» ----------

    [Theory]
    [InlineData(@"D:\Users\X\NTUSER.DAT")]
    [InlineData(@"D:\Users\X\ntuser.ini")]
    [InlineData(@"D:\Users\X\Documents")]
    [InlineData(@"D:\Users\X\AppData\Roaming\Microsoft\Credentials")]
    [InlineData(@"D:\ProgramData\Microsoft\Crypto\RSA\MachineKeys")]
    [InlineData(@"D:\ProgramData\Microsoft\Windows\Start Menu\Programs")]
    [InlineData(@"D:\ProgramData\Microsoft")]
    [InlineData(@"D:\WindowsApps\Foo")]
    [InlineData(@"D:\WpSystem\x")]
    [InlineData(@"C:\Documents and Settings")]
    [InlineData(@"C:\Documents and Settings\Egoist\NTUSER.DAT")]
    [InlineData(@"D:\Documents and Settings\All Users")]
    [InlineData(@"C:\Program Files (x86)\Microsoft")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeUpdate\1.3")]
    [InlineData(@"C:\Program Files\Microsoft\EdgeCore")]
    [InlineData(@"D:\Program Files (x86)\Microsoft\Edge\Application")]
    [InlineData(@"C:\Program Files\Hyper-V")]
    [InlineData(@"C:\Program Files\RUXIM")]
    [InlineData(@"C:\Program Files\PackageManagement")]
    public void Any_volume_and_two_level_system_paths_are_denied(string path)
    {
        var d = Policy.CheckQuarantineMove(path);
        Assert.True(d.IsDenied, $"{path} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData("Application Data")]
    [InlineData("Local Settings")]
    [InlineData("My Documents")]
    [InlineData("Start Menu")]
    [InlineData("Templates")]
    [InlineData("NetHood")]
    [InlineData("PrintHood")]
    [InlineData("SendTo")]
    [InlineData("Recent")]
    [InlineData("Cookies")]
    public void User_profile_junctions_are_denied_with_everything_under_them(string name)
    {
        Assert.True(Policy.CheckQuarantineMove($@"C:\Users\Egoist\{name}").IsDenied);
        Assert.True(Policy.CheckQuarantineMove($@"C:\Users\Egoist\{name}\Foo\bar").IsDenied);
        Assert.True(Policy.CheckQuarantineMove($@"D:\Users\Egoist\{name}\Foo").IsDenied);
    }

    [Theory]
    [InlineData("Application Data")]
    [InlineData("Desktop")]
    [InlineData("Documents")]
    [InlineData("Start Menu")]
    [InlineData("Templates")]
    [InlineData("Favorites")]
    public void ProgramData_junctions_are_denied(string name)
    {
        Assert.True(Policy.CheckQuarantineMove($@"C:\ProgramData\{name}").IsDenied);
        Assert.True(Policy.CheckQuarantineMove($@"C:\ProgramData\{name}\Foo").IsDenied);
        Assert.True(Policy.CheckQuarantineMove($@"D:\ProgramData\{name}\Foo").IsDenied);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo\Foo.lnk")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Foo")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Foo.lnk")]
    [InlineData(@"D:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo")]
    public void Start_menu_shortcuts_of_leftovers_are_allowed(string path)
    {
        var d = Policy.CheckQuarantineMove(path);
        Assert.True(d.IsAllowed, $"{path} -> {d.Verdict} ({d.Rule})");
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\evil.lnk")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.lnk")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Windows PowerShell")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Foo\..\Startup")]
    [InlineData(@"C:\Users\Egoist\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\..\Foo")]
    [InlineData(@"C:\Users\Default\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Foo")]
    [InlineData(@"C:\Users\Egoist\AppData\Local\Microsoft\Windows\Start Menu\Programs\Foo")]
    [InlineData(@"C:\Windows\Start Menu\Programs\Foo")]
    public void Start_menu_exception_does_not_open_programs_itself_or_startup(string path)
    {
        var d = Policy.CheckQuarantineMove(path);
        Assert.True(d.IsDenied, $"{path} -> {d.Verdict} ({d.Rule})");
    }

    // ---------- стирание ----------

    [Theory]
    [InlineData(@"C:\Chistovik.Quarantine\op-1")]
    [InlineData(@"C:\Chistovik.Quarantine\op-1\3\file.txt")]
    [InlineData(@"D:\chistovik.quarantine\20261024-abc")]
    public void Purge_allowed_only_inside_operation_folder(string path)
        => Assert.True(Policy.CheckPurgeTarget(path).IsAllowed);

    [Theory]
    [InlineData(@"C:\Chistovik.Quarantine")]
    [InlineData(@"C:\Chistovik.Quarantine\")]
    [InlineData(@"C:\Chistovik.Quarantine\..\Windows")]
    [InlineData(@"C:\Chistovik.Quarantine\op-1\..\..\Windows\System32")]
    [InlineData(@"C:\Chistovik.Quarantine\bad name")]
    [InlineData(@"C:\Chistovik.Quarantine\-op")]
    [InlineData(@"C:\Chistovik.Quarantine.evil\op-1")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Users\Egoist\Documents")]
    [InlineData(@"\\?\C:\Chistovik.Quarantine\op-1\..")]
    [InlineData(null)]
    [InlineData("")]
    public void Purge_outside_quarantine_is_denied(string? path)
        => Assert.True(Policy.CheckPurgeTarget(path).IsDenied);

    [Theory]
    [InlineData("COM\u00B9")]
    [InlineData("COM\u00B2")]
    [InlineData("COM\u00B3")]
    [InlineData("LPT\u00B9")]
    [InlineData("LPT\u00B2")]
    [InlineData("LPT\u00B3")]
    [InlineData("com\u00B2.txt")]
    public void Superscript_device_names_are_rejected(string name)
        => Assert.True(Policy.CheckQuarantineMove(@"D:\Games\" + name).IsDenied);
}

public class PolicyContextTests
{
    [Fact]
    public void Fixed_context_is_valid()
        => PolicyContext.Fixed.Validate();

    [Fact]
    public void FromEnvironment_gives_a_consistent_context()
    {
        var ctx = PolicyContext.FromEnvironment();
        ctx.Validate();
        Assert.True(PathNormalizer.TryNormalize(ctx.UsersDir, out var users, out _));
        Assert.True(PathNormalizer.TryNormalize(ctx.WindowsDir, out var win, out _));
        Assert.False(PathNormalizer.IsEqualOrUnder(users, win));
        Assert.NotEqual(ctx.ProgramFiles, ctx.ProgramFilesX86, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Users_dir_under_windows_is_rejected()
        => Assert.Throws<PolicyContextException>(() => PolicyContext.CreateValidated(
            @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Windows\System32\config"));

    [Fact]
    public void Same_program_files_dirs_are_rejected()
        => Assert.Throws<PolicyContextException>(() => PolicyContext.CreateValidated(
            @"C:\Windows", @"C:\Program Files (x86)", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users"));

    [Fact]
    public void Program_data_under_windows_is_rejected()
        => Assert.Throws<PolicyContextException>(() => PolicyContext.CreateValidated(
            @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Windows\PD", @"C:\Users"));

    [Fact]
    public void Garbage_directory_is_rejected_and_is_an_invalid_operation()
        => Assert.ThrowsAny<InvalidOperationException>(() => PolicyContext.CreateValidated(
            @"C:\Windows", @"relative", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users"));

    [Fact]
    public void Policy_constructors_validate_through_context_norm()
        => Assert.Throws<PolicyContextException>(() =>
            new PathPolicy(new PolicyContext(@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", "bad")));
}
