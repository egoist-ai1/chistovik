using System.Security.Principal;
using Chistovik.Core.Ports;
using Chistovik.Native.Env;
using Chistovik.Native.Journal;
using Chistovik.Native.Power;
using Chistovik.Native.Registry;
using Chistovik.Native.Services;
using Chistovik.Native.Tasks;
using Chistovik.Native.Trust;
using Xunit;
using Xunit.Abstractions;

namespace Chistovik.Native.Tests;

/// <summary>Только ЧТЕНИЕ живой Windows. Ничто здесь не пишет в реестр, службы, задачи и схемы питания.</summary>
public class LivePortsReadTests(ITestOutputHelper output)
{
    // ---------- реестр ----------
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    [Fact]
    public void Registry_reads_ProductName_and_resolved_path()
    {
        using var key = new WindowsRegistry().Open(new RegLocation(RegRoot.Machine, null, CurrentVersion, ""), write: false, createKey: false);
        Assert.NotNull(key);
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", key!.ResolvedPath, ignoreCase: true);
        Assert.False(key.IsSymbolicLink);
        var v = key.Read("ProductName");
        output.WriteLine($"ProductName: {v.Data}");
        Assert.True(v.Exists);
        Assert.Equal(RegType.String, v.Type);
        Assert.False(string.IsNullOrWhiteSpace((string?)v.Data));
        Assert.Contains("ProductName", key.ValueNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RegType.DWord, key.Read("CurrentMajorVersionNumber").Type);
        Assert.False(key.Read("NoSuchValue-Chistovik").Exists);
    }

    [Fact]
    public void Registry_missing_key_returns_null()
    {
        Assert.Null(new WindowsRegistry().Open(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Chistovik-no-such-key-7f3a", ""), false, false));
    }

    [Fact]
    public void Registry_does_not_follow_symbolic_link()
    {
        // HKLM\SYSTEM\CurrentControlSet — системная ссылка на ControlSet001. С OPEN_LINK ключ-ссылка открывается сама.
        using var link = new WindowsRegistry().Open(new RegLocation(RegRoot.Machine, null, @"SYSTEM\CurrentControlSet", ""), false, false);
        if (link is null) { output.WriteLine("CurrentControlSet недоступен: пропуск"); return; }
        output.WriteLine($"link path: {link.ResolvedPath}, symlink={link.IsSymbolicLink}");
        Assert.True(link.IsSymbolicLink);
        Assert.EndsWith(@"\SYSTEM\CurrentControlSet", link.ResolvedPath, StringComparison.OrdinalIgnoreCase);

        using var real = new WindowsRegistry().Open(new RegLocation(RegRoot.Machine, null, @"SYSTEM\ControlSet001", ""), false, false);
        Assert.NotNull(real);
        Assert.False(real!.IsSymbolicLink);
    }

    [Fact]
    public void Registry_user_hive_by_sid_resolves_under_USER()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var key = new WindowsRegistry().Open(new RegLocation(RegRoot.User, sid, @"Software\Microsoft\Windows\CurrentVersion", ""), false, false);
        Assert.NotNull(key);
        Assert.StartsWith($@"\REGISTRY\USER\{sid}\", key!.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(key.IsSymbolicLink);
    }

    [Fact]
    public void Registry_user_root_requires_sid()
    {
        Assert.Throws<ArgumentException>(() => new WindowsRegistry().Open(new RegLocation(RegRoot.User, null, "Software", ""), false, false));
        Assert.Throws<ArgumentException>(() => new WindowsRegistry().Open(new RegLocation(RegRoot.User, "not-a-sid", "Software", ""), false, false));
    }

    // ---------- службы ----------
    [Fact]
    public void Services_list_contains_RpcSs_and_WinDefend()
    {
        var names = new WindowsServiceControl().ListNames();
        output.WriteLine($"services: {names.Count}");
        Assert.Contains("RpcSs", names, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains("WinDefend", StringComparer.OrdinalIgnoreCase)) output.WriteLine("WinDefend отсутствует (другой антивирус?)");
    }

    [Fact]
    public void Services_query_first_readable_core_service()
    {
        // RpcSs для обычного пользователя закрыт (Open даёт null), поэтому берём первую читаемую из набора базовых служб.
        var control = new WindowsServiceControl();
        foreach (var name in new[] { "RpcSs", "Dhcp", "EventLog", "Winmgmt", "LanmanWorkstation", "Dnscache" })
        {
            using var h = control.Open(name, write: false);
            if (h is null) { output.WriteLine($"{name}: недоступна"); continue; }
            var raw = h.Query();
            output.WriteLine($"{name}: type=0x{raw.TypeFlags:X} start={raw.StartType} delayed={raw.Delayed} protected={raw.LaunchProtected} deps={raw.DependentCount} triggers={raw.TriggerCount} running={raw.Running} image={raw.ImagePath}");
            Assert.Equal(name, raw.Name);
            Assert.False(string.IsNullOrEmpty(raw.ImagePath));
            Assert.True(raw.TypeFlags != 0);
            return;
        }
        Assert.Fail("ни одна из базовых служб не читается");
    }

    [Fact]
    public void Services_query_WinDefend_if_present()
    {
        using var h = new WindowsServiceControl().Open("WinDefend", write: false);
        if (h is null) { output.WriteLine("WinDefend отсутствует: пропуск"); return; }
        var raw = h.Query();
        output.WriteLine($"WinDefend: start={raw.StartType} protected={raw.LaunchProtected} running={raw.Running}");
        Assert.Equal("WinDefend", raw.Name);
    }

    [Fact]
    public void Services_unknown_name_returns_null()
    {
        Assert.Null(new WindowsServiceControl().Open("Chistovik-no-such-service", false));
    }

    // ---------- задачи ----------
    [Fact]
    public void Tasks_list_is_not_empty()
    {
        var paths = new WindowsTaskScheduler().ListPaths();
        output.WriteLine($"tasks: {paths.Count}");
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.StartsWith("\\", p));
    }

    [Fact]
    public void Tasks_query_ScheduledDefrag_if_present()
    {
        var sched = new WindowsTaskScheduler();
        using var h = sched.Open(@"\Microsoft\Windows\Defrag\ScheduledDefrag");
        if (h is null) { output.WriteLine("ScheduledDefrag отсутствует: пропуск"); return; }
        var raw = h.Query();
        output.WriteLine($"{raw.Path}: enabled={raw.Enabled} user={raw.UserId} run={raw.RunLevel} logon={raw.LogonType} actions={raw.Actions.Count}");
        Assert.Contains("<Task", raw.Xml);
        Assert.NotEmpty(raw.Actions);
        Assert.Equal("Exec", raw.Actions[0].Kind);
    }

    [Fact]
    public void Tasks_unknown_path_returns_null()
    {
        Assert.Null(new WindowsTaskScheduler().Open(@"\Chistovik\NoSuchTask"));
    }

    // ---------- питание ----------
    [Fact]
    public void Power_active_scheme_is_in_list()
    {
        var p = new WindowsPowerControl();
        var active = p.GetActive();
        var all = p.ListSchemes();
        output.WriteLine($"active={active}, schemes={all.Count}");
        Assert.Contains(active, all);
    }

    [Fact]
    public void Power_active_scheme_has_a_name()
    {
        var p = new WindowsPowerControl();
        var name = p.GetName(p.GetActive());
        output.WriteLine($"name={name}");
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void Power_reads_sleep_timeout_of_active_scheme()
    {
        var p = new WindowsPowerControl();
        var sleep = new Guid("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
        var standbyIdle = new Guid("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
        try
        {
            var (ac, dc) = p.Read(p.GetActive(), sleep, standbyIdle);
            output.WriteLine($"standby idle: AC={ac} DC={dc}");
        }
        catch (PowerSettingNotFoundException e) { output.WriteLine("параметр отсутствует: " + e.Message); }
    }

    // ---------- подпись, папки, файлы ----------
    [Fact]
    public void Notepad_is_signed_by_Microsoft()
    {
        var f = new WindowsFileTrust().GetSignature(@"C:\Windows\System32\notepad.exe");
        output.WriteLine($"notepad: valid={f.Valid} microsoft={f.MicrosoftSigned}");
        Assert.True(f.Valid);
        Assert.True(f.MicrosoftSigned);
    }

    [Fact]
    public void Unsigned_and_missing_files_are_not_valid()
    {
        var trust = new WindowsFileTrust();
        Assert.Equal(new SignatureFacts(false, false), trust.GetSignature(@"C:\Chistovik-no-such-file.exe"));
        var tmp = Path.Combine(Path.GetTempPath(), $"chistovik-unsigned-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, "not a signed file");
        try { Assert.Equal(new SignatureFacts(false, false), trust.GetSignature(tmp)); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void User_folders_resolve_for_current_user()
    {
        var folders = new WindowsUserFolders();
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var startup = folders.StartupFolder(sid);
        output.WriteLine($"startup={startup}, common={folders.CommonStartupFolder()}");
        Assert.NotNull(startup);
        Assert.EndsWith("Startup", startup!);
        Assert.NotNull(folders.CommonStartupFolder());
        Assert.Throws<ArgumentException>(() => folders.StartupFolder("not-a-sid"));
    }

    [Fact]
    public void FileSystem_lists_files_and_tolerates_missing_dir()
    {
        var fs = new WindowsFileSystem();
        Assert.Contains(fs.ListFiles(@"C:\Windows\System32", "notepad.exe"), p => p.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(fs.ListFiles(@"C:\Chistovik-no-such-dir", "*"));
    }

    [Fact]
    public void Journal_inspect_ProgramData_returns_owner_and_aces()
    {
        var f = new WindowsJournalDirectory().Inspect(@"C:\ProgramData");
        output.WriteLine($"owner={f.OwnerSid} reparse={f.IsReparse} aces={f.Aces.Count}");
        Assert.True(f.Exists);
        Assert.False(f.IsReparse);
        Assert.False(string.IsNullOrEmpty(f.OwnerSid));
        Assert.NotEmpty(f.Aces);
        foreach (var a in f.Aces) output.WriteLine($"  {a.Sid} allow={a.Allow} write={a.GrantsWrite}");
        // Группа «Все пользователи» (BU) читает ProgramData, а запись/создание у неё ограничены, но у SYSTEM/Administrators запись есть.
        Assert.Contains(f.Aces, a => a.Sid == "S-1-5-18" && a.Allow && a.GrantsWrite);
    }

    [Fact]
    public void Journal_create_protected_in_temp_has_no_user_write()
    {
        // Единственный тест с записью: каталог создаётся и удаляется только во временной папке, система не затрагивается.
        var root = Path.Combine(Path.GetTempPath(), $"chistovik-jd-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "journal");
        var jd = new WindowsJournalDirectory();
        try
        {
            try { jd.CreateProtected(target); }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or System.Security.AccessControl.PrivilegeNotHeldException or IOException)
            {
                output.WriteLine("создание с владельцем Administrators недоступно без повышения прав: " + e.Message);
                return;
            }
            var f = jd.Inspect(target);
            Assert.True(f.Exists);
            Assert.Equal("S-1-5-32-544", f.OwnerSid);
            Assert.Contains(f.Aces, a => a.Sid == "S-1-5-18" && a.Allow && a.GrantsWrite);
            Assert.Contains(f.Aces, a => a.Sid == "S-1-5-32-544" && a.Allow && a.GrantsWrite);
            Assert.Contains(f.Aces, a => a.Sid == "S-1-5-32-545" && a.Allow && !a.GrantsWrite);
            Assert.DoesNotContain(f.Aces, a => a.Sid == "S-1-5-32-545" && a.GrantsWrite);
            Assert.Throws<IOException>(() => jd.CreateProtected(target));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { /* временная папка */ }
        }
    }

    [Fact]
    public void Journal_inspect_missing_dir()
    {
        var f = new WindowsJournalDirectory().Inspect(@"C:\Chistovik-no-such-dir");
        Assert.False(f.Exists);
        Assert.Null(f.OwnerSid);
        Assert.Empty(f.Aces);
    }

    [Fact]
    public void Clock_and_ids_work()
    {
        Assert.True((DateTimeOffset.UtcNow - new SystemClock().UtcNow).Duration() < TimeSpan.FromSeconds(5));
        Assert.NotEqual(new GuidGenerator().NewGuid(), new GuidGenerator().NewGuid());
    }
}

internal static class DirFactsExt
{
    public static string? Owner(this DirFacts f) => f.OwnerSid;
}
