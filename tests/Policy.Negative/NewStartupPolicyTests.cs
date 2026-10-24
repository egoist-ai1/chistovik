using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

public class NewStartupPolicyTests
{
    static readonly StartupPolicy Policy = new(PolicyContext.Fixed);

    static readonly SignatureFacts Unsigned = new(false, false);
    static readonly SignatureFacts ThirdParty = new(true, false);
    static readonly SignatureFacts Microsoft = new(true, true);

    static StartupEntryId Run(string name = "Foo", StartupLocation loc = StartupLocation.Hklm64) => new(loc, name);

    // ---------- StartupEntryId ----------

    [Theory]
    [InlineData("run:hklm64:Foo", StartupLocation.Hklm64, "Foo")]
    [InlineData("run:hklm32:Foo Bar", StartupLocation.Hklm32, "Foo Bar")]
    [InlineData("run:hku:OneDrive", StartupLocation.Hku, "OneDrive")]
    [InlineData("RUN:HKLM64:Foo", StartupLocation.Hklm64, "Foo")]
    [InlineData("run:hklm64:Name:with:colons", StartupLocation.Hklm64, "Name:with:colons")]
    [InlineData("run:hku:Back\\slash", StartupLocation.Hku, "Back\\slash")]
    [InlineData("folder:user:Foo.lnk", StartupLocation.UserFolder, "Foo.lnk")]
    [InlineData("folder:common:Foo Bar.lnk", StartupLocation.CommonFolder, "Foo Bar.lnk")]
    [InlineData("Folder:User:x.url", StartupLocation.UserFolder, "x.url")]
    public void Valid_ids_parse_and_round_trip(string text, StartupLocation loc, string name)
    {
        Assert.True(StartupEntryId.TryParse(text, out var id, out var err), err);
        Assert.Equal(loc, id.Location);
        Assert.Equal(name, id.Name);
        Assert.True(StartupEntryId.TryParse(id.ToString(), out var again, out _));
        Assert.Equal(id, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("run:hklm64")]
    [InlineData("run:hklm64:")]
    [InlineData("run:hkcu:Foo")]
    [InlineData("run:user:Foo")]
    [InlineData("folder:hklm64:Foo")]
    [InlineData("folder:user:")]
    [InlineData("service:user:Foo")]
    [InlineData("run:hklm64: Foo")]
    [InlineData("run:hklm64:Foo ")]
    [InlineData("run:hklm64:Foo\n")]
    [InlineData("run:hklm64:Fo\0o")]
    [InlineData("run:hklm64:Fo\to")]
    [InlineData("folder:user:..\\x.lnk")]
    [InlineData("folder:user:a\\b.lnk")]
    [InlineData("folder:user:a/b.lnk")]
    [InlineData("folder:user:C:\\x.lnk")]
    [InlineData("folder:user:x.lnk:stream")]
    [InlineData("folder:user:..")]
    [InlineData("folder:user:.")]
    [InlineData("folder:user:x.lnk.")]
    [InlineData("folder:user:CON")]
    [InlineData("folder:user:nul.lnk")]
    [InlineData("folder:user:*.lnk")]
    [InlineData("folder:user:PROGRA~1.lnk")]
    public void Malformed_ids_are_rejected(string? text)
        => Assert.False(StartupEntryId.TryParse(text, out _, out _));

    [Fact]
    public void Id_longer_than_300_chars_is_rejected()
    {
        Assert.True(StartupEntryId.TryParse("run:hklm64:" + new string('a', 300 - 11), out _, out _));
        Assert.False(StartupEntryId.TryParse("run:hklm64:" + new string('a', 300 - 11 + 1), out _, out _));
    }

    // ---------- защищённые записи ----------

    [Theory]
    [InlineData("%windir%\\system32\\SecurityHealthSystray.exe")]
    [InlineData("%SystemRoot%\\System32\\SecurityHealthSystray.exe")]
    [InlineData("C:\\Windows\\System32\\SecurityHealthSystray.exe")]
    [InlineData("\"C:\\Windows\\System32\\SecurityHealthSystray.exe\" /autorun")]
    [InlineData("SecurityHealthSystray.exe")]
    [InlineData("securityhealthsystray.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c foo")]
    [InlineData("C:\\Windows\\explorer.exe")]
    [InlineData("c:/windows/system32/x.exe")]
    [InlineData("\\\\?\\C:\\Windows\\System32\\x.exe")]
    [InlineData("C:\\Program Files\\..\\Windows\\System32\\x.exe")]
    [InlineData("C:\\Windows.\\System32\\x.exe")]
    [InlineData("\"C:\\Program Files\\Windows Defender\\MSASCuiL.exe\"")]
    [InlineData("\"C:\\Program Files\\Windows Defender\\x.exe\"")]
    [InlineData("\"C:\\Program Files (x86)\\Windows Defender\\x.exe\"")]
    [InlineData("\"C:\\Program Files\\Windows Security\\x.exe\"")]
    [InlineData("C:\\ProgramData\\Microsoft\\Windows Security Health\\x.exe")]
    [InlineData("C:\\ProgramData\\Microsoft\\Windows Defender\\Platform\\4.18\\MsMpEng.exe")]
    [InlineData("C:\\Program Files\\WindowsApps\\Foo_1.0\\a.exe")]
    public void System_security_and_windows_files_are_forbidden(string command)
    {
        foreach (var sig in new[] { Unsigned, ThirdParty, Microsoft })
        {
            var d = Policy.CheckToggle(Run("SecurityHealth"), command, sig);
            Assert.True(d.IsDenied, command);
            Assert.Equal(ChangeClass.Forbidden, Policy.Classify(Run("Foo"), command, sig).Class);
        }
    }

    [Theory]
    [InlineData("SecurityHealth")]
    [InlineData("securityhealth")]
    [InlineData("WindowsDefender")]
    public void Security_run_value_names_are_forbidden_whatever_the_command(string name)
        => Assert.True(Policy.CheckToggle(Run(name), "C:\\Program Files\\Foo\\a.exe", ThirdParty).IsDenied);

    [Fact]
    public void Microsoft_signed_file_from_windows_directory_is_forbidden_with_dedicated_rule()
    {
        var d = Policy.CheckToggle(Run("Foo"), "C:\\Windows\\System32\\OneDriveSetup.exe", Microsoft);
        Assert.True(d.IsDenied);
        Assert.Equal("startup-microsoft-in-windows", d.Rule);
    }

    [Fact]
    public void Folder_entry_whose_shortcut_target_is_in_windows_is_forbidden()
        => Assert.True(Policy.CheckToggle(new(StartupLocation.UserFolder, "Sys.lnk"), "C:\\Windows\\System32\\x.exe", Microsoft).IsDenied);

    // ---------- неразобранное ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo.exe")]
    [InlineData("foo")]
    [InlineData("\"C:\\Foo\\a.exe")]
    [InlineData("\"\"")]
    [InlineData("%LOCALAPPDATA%\\Foo\\a.exe")]
    [InlineData("%UNKNOWN%\\system32\\a.exe")]
    [InlineData("\\\\srv\\share\\a.exe")]
    [InlineData("C:\\Foo Bar\\a")]
    [InlineData("C:\\Progra~1\\Foo\\a.exe")]
    [InlineData("C:\\Foo\\a.exe\u0001 /x")]
    public void Unparsed_commands_are_read_only(string? command)
    {
        var c = Policy.Classify(Run(), command, ThirdParty);
        Assert.Equal(ChangeClass.ReadOnly, c.Class);
        Assert.True(Policy.CheckToggle(Run(), command, ThirdParty).IsDenied);
    }

    [Fact]
    public void Contradictory_signature_facts_are_read_only()
        => Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Run(), "C:\\Program Files\\Foo\\a.exe", new SignatureFacts(false, true)).Class);

    // ---------- обычные программы ----------

    [Theory]
    [InlineData("\"C:\\Program Files\\Foo\\foo.exe\" --minimized")]
    [InlineData("C:\\Program Files\\Foo\\foo.exe")]
    [InlineData("C:\\Program Files\\Foo Bar\\foo.exe -silent")]
    [InlineData("%ProgramFiles%\\Foo\\foo.exe")]
    [InlineData("%ProgramFiles(x86)%\\Foo\\foo.exe /background")]
    [InlineData("C:\\Users\\Egoist\\AppData\\Local\\Foo\\foo.exe --autostart")]
    [InlineData("D:\\Tools\\foo.exe")]
    [InlineData("\"C:\\Users\\Egoist\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe\" /background")]
    public void Ordinary_third_party_entries_are_modifiable(string command)
    {
        foreach (var sig in new[] { Unsigned, ThirdParty })
        {
            var d = Policy.CheckToggle(Run(), command, sig);
            Assert.True(d.IsAllowed, command);
            Assert.Equal("startup:run:hklm64:Foo", d.Target);
        }
    }

    [Fact]
    public void Microsoft_signed_onedrive_outside_windows_is_modifiable()
        => Assert.True(Policy.CheckToggle(Run("OneDrive", StartupLocation.Hku),
            "\"C:\\Users\\Egoist\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe\" /background", Microsoft).IsAllowed);

    [Fact]
    public void Folder_entry_with_ordinary_target_is_modifiable()
        => Assert.True(Policy.CheckToggle(new(StartupLocation.CommonFolder, "Foo.lnk"), "C:\\Program Files\\Foo\\foo.exe", ThirdParty).IsAllowed);

    [Fact]
    public void TryResolveCommand_handles_quotes_arguments_and_variables()
    {
        Assert.True(Policy.TryResolveCommand("\"%ProgramFiles%\\A B\\x.exe\" -y \"z\"", out var exe, out _));
        Assert.Equal("C:\\Program Files\\A B\\x.exe", exe);
        Assert.False(Policy.TryResolveCommand("%FOO%\\x.exe", out _, out _));
    }
}
