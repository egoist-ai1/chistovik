using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;
using ChangeClass = Chistovik.Core.Policy.ChangeClass;

namespace Chistovik.Core.Tests.Policy;

/// <summary>Политика удаления программ: что можно удалять и все отказы (docs/C1-DESIGN.md).</summary>
public class PackagePolicyTests
{
    const string Guid1 = "{11111111-2222-3333-4444-555555555555}";
    const string Exe = @"C:\Program Files\Foo\uninst.exe";

    static PackageFacts Facts(
        string name = "Foo", string? quiet = null, string? uninstall = null, bool msi = false, bool system = false,
        string? parent = null, string? release = null, string? publisher = "Foo Ltd")
        => new(name, publisher, system, parent, release, msi, uninstall, quiet);

    static PackagePolicy Policy(FakeFileSecurity? fs = null) => new(PolicyContext.Fixed, fs ?? new FakeFileSecurity());

    static string MachineId(string key, string arch = "x64") => ArpId.Make(arch, key);

    static FakeFileSecurity Trusted(string exe = Exe) => new FakeFileSecurity().TrustFileAndDir(exe);

    // ---------- допустимое ----------

    [Fact]
    public void Msi_by_product_code_is_modifiable()
    {
        var c = Policy().Classify(MachineId(Guid1), Facts(msi: true));
        Assert.True(c.IsModifiable);
        Assert.Equal("msi", c.Rule);
        Assert.Equal(new UninstallCommand("msi", Guid1, null, null), c.Command);
    }

    [Fact]
    public void Exe_with_quiet_string_in_program_files_passes_arguments_through()
    {
        var c = Policy(Trusted()).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /quiet /norestart"));
        Assert.True(c.IsModifiable);
        Assert.Equal(new UninstallCommand("exe", null, Exe, "/quiet /norestart"), c.Command);
    }

    [Fact]
    public void Inno_uninstaller_gets_fixed_silent_arguments()
    {
        const string inno = @"C:\Program Files\Foo\unins000.exe";
        var fs = Trusted(inno).Trust(@"C:\Program Files\Foo\unins000.dat");
        var c = Policy(fs).Classify(MachineId("Foo"), Facts(uninstall: "\"" + inno + "\""));
        Assert.True(c.IsModifiable);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", c.Command!.Arguments);
    }

    [Fact]
    public void Nsis_uninstaller_gets_silent_switch()
    {
        var fs = Trusted().Nsis(Exe);
        var c = Policy(fs).Classify(MachineId("Foo"), Facts(uninstall: "\"" + Exe + "\""));
        Assert.True(c.IsModifiable);
        Assert.Equal("/S", c.Command!.Arguments);
    }

    [Fact]
    public void Allowed_decision_target_is_lowercase_id()
    {
        var d = Policy().CheckUninstall(MachineId(Guid1), Facts(msi: true));
        Assert.True(d.IsAllowed);
        Assert.Equal(MachineId(Guid1).ToLowerInvariant(), d.Target);
    }

    // ---------- отказы ----------

    static void AssertRefused(PackageClassification c, string rule, ChangeClass cls = ChangeClass.ReadOnly)
    {
        Assert.False(c.IsModifiable);
        Assert.Equal(rule, c.Rule);
        Assert.Equal(cls, c.Class);
        Assert.Null(c.Command);
    }

    [Theory]
    [InlineData("arp:user:x64:Foo", "hkcu-scope")]
    [InlineData("arp:machine:arm:Foo", "arp-id-invalid")]
    [InlineData("arp:machine:x64:", "arp-id-invalid")]
    [InlineData("arp:machine:x64:a\\b", "arp-id-invalid")]
    [InlineData("foo", "arp-id-invalid")]
    public void Wrong_scope_or_id_is_not_modifiable(string id, string rule)
        => AssertRefused(Policy().Classify(id, Facts(msi: true)), rule);

    [Fact]
    public void User_scope_is_never_allowed_even_for_msi()
        => Assert.False(Policy().CheckUninstall("arp:user:x64:" + Guid1, Facts(msi: true)).IsAllowed);

    [Fact]
    public void System_component_is_read_only()
        => AssertRefused(Policy().Classify(MachineId(Guid1), Facts(msi: true, system: true)), "system-component");

    [Fact]
    public void Entry_with_parent_is_read_only()
        => AssertRefused(Policy().Classify(MachineId(Guid1), Facts(msi: true, parent: "Foo")), "has-parent");

    [Theory]
    [InlineData("Update")]
    [InlineData("Hotfix")]
    [InlineData("Security Update")]
    [InlineData("Service Pack")]
    public void Updates_are_read_only(string release)
        => AssertRefused(Policy().Classify(MachineId(Guid1), Facts(msi: true, release: release)), "is-update");

    [Theory]
    [InlineData("Microsoft Edge")]
    [InlineData("Microsoft Edge WebView2 Runtime")]
    [InlineData("Microsoft .NET Framework 4.8 SDK")]
    [InlineData("Microsoft Store")]
    [InlineData("Windows Defender")]
    public void Windows_components_are_forbidden(string name)
        => AssertRefused(Policy().Classify(MachineId(Guid1), Facts(name: name, msi: true)), "protected-component", ChangeClass.Forbidden);

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.38")]
    [InlineData("Microsoft .NET Runtime - 8.0.4 (x64)")]
    [InlineData("Java 8 Update 401")]
    [InlineData("DirectX Runtime")]
    public void Shared_runtimes_are_expert_only_and_denied_by_gate(string name)
    {
        var f = Facts(name: name, msi: true);
        var c = Policy().Classify(MachineId(Guid1), f);
        AssertRefused(c, "runtime-shared");
        Assert.True(c.ExpertOnly);
        var d = Policy().CheckUninstall(MachineId(Guid1), f);
        Assert.False(d.IsAllowed);
        Assert.Equal(Verdict.ExpertOnly, d.Verdict);
    }

    [Fact]
    public void Exe_without_any_command_is_read_only()
        => AssertRefused(Policy().Classify(MachineId("Foo"), Facts()), "exe-no-quiet");

    [Fact]
    public void Exe_without_known_silent_mode_is_read_only()
        => AssertRefused(Policy(Trusted()).Classify(MachineId("Foo"), Facts(uninstall: "\"" + Exe + "\"")), "exe-no-quiet");

    [Fact]
    public void Msi_flag_with_non_guid_key_is_not_msi()
        => AssertRefused(Policy().Classify(MachineId("Foo"), Facts(msi: true)), "exe-no-quiet");

    [Theory]
    [InlineData("steam://uninstall/12345")]
    [InlineData("\"com.epicgames.launcher://apps/foo?action=uninstall\"")]
    [InlineData("uplay://uninstall/1")]
    [InlineData("goggalaxy://uninstall/1")]
    public void Launcher_managed_programs_are_read_only(string cmd)
        => AssertRefused(Policy().Classify(MachineId("Foo"), Facts(uninstall: cmd)), "managed-by-launcher");

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe /c del foo")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -Command x")]
    [InlineData(@"C:\Windows\System32\rundll32.exe foo.dll,Remove")]
    [InlineData(@"C:\Windows\System32\mshta.exe x")]
    public void Interpreters_in_quiet_string_are_read_only(string cmd)
        => AssertRefused(Policy().Classify(MachineId("Foo"), Facts(quiet: cmd)), "managed-by-launcher");

    [Fact]
    public void Uninstaller_outside_program_files_is_read_only()
    {
        const string exe = @"C:\Users\Test\AppData\Local\Foo\unins000.exe";
        AssertRefused(Policy(Trusted(exe)).Classify(MachineId("Foo"), Facts(quiet: exe + " /S")), "exe-writable-dir");
    }

    [Fact]
    public void Uninstaller_reached_by_dotdot_outside_program_files_is_read_only()
    {
        const string exe = @"C:\Program Files\..\Users\Test\unins000.exe";
        AssertRefused(Policy(Trusted(exe)).Classify(MachineId("Foo"), Facts(quiet: "\"" + exe + "\" /S")), "exe-writable-dir");
    }

    [Fact]
    public void Missing_uninstaller_is_read_only()
        => AssertRefused(Policy().Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "exe-missing");

    [Fact]
    public void Users_writable_file_is_read_only()
    {
        var fs = Trusted().UsersWritable(Exe);
        AssertRefused(Policy(fs).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "exe-writable-dir");
    }

    [Fact]
    public void Users_writable_directory_is_read_only()
    {
        var fs = Trusted().UsersWritable(@"C:\Program Files\Foo");
        AssertRefused(Policy(fs).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "exe-writable-dir");
    }

    [Fact]
    public void Untrusted_owner_is_read_only()
    {
        var fs = Trusted();
        fs.Add(Exe, new FileFacts(true, false, Exe, "S-1-5-21-1-2-3-1001",
            [new DirAce(FakeFileSecurity.Administrators, true, true)]));
        AssertRefused(Policy(fs).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "exe-writable-dir");
    }

    [Fact]
    public void Junction_to_outside_program_files_is_read_only()
    {
        var fs = Trusted();
        fs.Add(Exe, new FileFacts(true, false, @"C:\Users\Test\evil\uninst.exe", FakeFileSecurity.Administrators,
            [new DirAce(FakeFileSecurity.Administrators, true, true)]));
        AssertRefused(Policy(fs).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "exe-writable-dir");
    }

    [Fact]
    public void Junction_to_interpreter_in_trusted_directory_is_read_only()
    {
        const string target = @"C:\Program Files\Foo\cmd.exe";
        var fs = Trusted(target);
        fs.Add(Exe, new FileFacts(true, false, target, FakeFileSecurity.Administrators,
            [new DirAce(FakeFileSecurity.Administrators, true, true)]));
        AssertRefused(Policy(fs).Classify(MachineId("Foo"), Facts(quiet: "\"" + Exe + "\" /S")), "managed-by-launcher");
    }

    [Theory]
    [InlineData(@"C:\Program Files\Windows Defender\uninst.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Foo\uninst.exe")]
    [InlineData(@"C:\Windows\Installer\uninst.exe")]
    public void System_zones_are_forbidden(string exe)
        => AssertRefused(Policy(Trusted(exe)).Classify(MachineId("Foo"), Facts(quiet: "\"" + exe + "\" /S")), "protected-component", ChangeClass.Forbidden);

    [Theory]
    [InlineData("uninst.exe /S")]
    [InlineData("\"C:\\Program Files\\Foo\\uninst.exe /S")]
    [InlineData("%USERPROFILE%\\Foo\\uninst.exe /S")]
    public void Unparsable_or_relative_commands_are_read_only(string cmd)
    {
        var c = Policy(Trusted()).Classify(MachineId("Foo"), Facts(quiet: cmd));
        Assert.False(c.IsModifiable);
        Assert.Contains(c.Rule, new[] { "uninstall-unparsed", "exe-writable-dir", "exe-missing" });
    }

    [Fact]
    public void Deny_decision_carries_rule_for_the_journal()
    {
        var d = Policy().CheckUninstall(MachineId(Guid1), Facts(msi: true, system: true));
        Assert.True(d.IsDenied);
        Assert.Equal("system-component", d.Rule);
    }
}
