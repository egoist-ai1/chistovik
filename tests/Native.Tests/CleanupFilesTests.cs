using System.Diagnostics;
using Chistovik.Core.Ports;
using Chistovik.Native.Cleanup;
using Chistovik.Native.Registry;
using Xunit;

namespace Chistovik.Native.Tests;

/// <summary>
/// Безопасно на хосте: файлы создаются и удаляются только внутри временного каталога теста (Temp\Chistovik.Test.guid).
/// Корзина и NtDeleteKey здесь не вызываются (проверяются в Sandbox). Реестр читается без записи.
/// </summary>
public sealed class CleanupFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "Chistovik.Test." + Guid.NewGuid().ToString("N"));
    readonly WindowsCleanupFiles _files = new();

    public CleanupFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Сначала убираем junction'ы, чтобы рекурсивное удаление не пошло по ссылке (в тестах они внутри _root).
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
        }
        catch { /* уборка */ }
        try { Directory.Delete(_root, true); } catch { /* уборка */ }
    }

    static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "mklink /J не удалось: " + p.StandardError.ReadToEnd());
    }

    [Fact]
    public void Inspect_reports_existing_missing_and_guid_final_path()
    {
        var f = Path.Combine(_root, "a.txt");
        File.WriteAllText(f, "x");
        var facts = _files.Inspect(f);
        Assert.True(facts.Exists);
        Assert.False(facts.IsReparse);
        Assert.StartsWith(@"\\?\Volume{", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\a.txt", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(facts.OwnerSid);
        Assert.False(_files.Inspect(Path.Combine(_root, "nope")).Exists);
    }

    [Fact]
    public void Inspect_reports_volume_of_the_requested_drive_letter()
    {
        var f = Path.Combine(_root, "v.txt");
        File.WriteAllText(f, "x");
        var facts = _files.Inspect(f);
        Assert.NotNull(facts.RequestedVolume);
        Assert.StartsWith(@"\\?\Volume{", facts.RequestedVolume!, StringComparison.OrdinalIgnoreCase);
        Assert.False(facts.RequestedVolume!.EndsWith('\\'));
        // Тот же том, что в финальном пути хэндла: по ним Core ловит корень, переехавший на другой том.
        Assert.StartsWith(facts.RequestedVolume + @"\", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_of_junction_reads_everything_from_the_link_itself_not_its_target()
    {
        var target = Path.Combine(_root, "tgt");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "lnk");
        MakeJunction(link, target);
        var facts = _files.Inspect(link);
        Assert.True(facts.Exists);
        Assert.True(facts.IsReparse);
        Assert.False(_files.Inspect(Path.Combine(link, "missing")).Exists);
    }

    [Fact]
    public void Cleanup_log_store_refuses_a_directory_with_untrusted_acl()
    {
        // Каталог в Temp принадлежит обычному пользователю и пишется им: это не защищённый каталог агента.
        var dir = Path.Combine(_root, "cleanup");
        var store = new WindowsCleanupLogStore(dir);
        Assert.Throws<IOException>(() => store.Write("a.json", [1, 2, 3]));
        Assert.False(File.Exists(Path.Combine(dir, "a.json")));
    }

    [Fact]
    public void Quarantine_store_refuses_a_directory_with_untrusted_acl()
    {
        var dir = Path.Combine(_root, "quarantine");
        var store = new WindowsQuarantineStore(dir);
        var rel = Guid.NewGuid().ToString("N") + @"\1.reg";
        Assert.Throws<IOException>(() => store.WriteFile(rel, [1, 2, 3]));
        Assert.False(store.Exists(rel));
    }

    [Fact]
    public void Enumerate_lists_entries_with_facts_and_not_recursive()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "inner.txt"), "i");
        File.WriteAllText(Path.Combine(_root, "f.bin"), "12345");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        MakeJunction(Path.Combine(_root, "jn"), other);

        var list = _files.Enumerate(_root).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, list.Count); // sub, f.bin, other, jn
        Assert.Equal(5, list["f.bin"].Length);
        Assert.False(list["f.bin"].IsDirectory);
        Assert.True(list["sub"].IsDirectory);
        Assert.False(list["sub"].IsReparse);
        Assert.True(list["jn"].IsDirectory);
        Assert.True(list["jn"].IsReparse);
        Assert.True(list["f.bin"].Created > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True(list["f.bin"].LastWrite > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Empty(_files.Enumerate(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Delete_file_by_handle_removes_it()
    {
        var f = Path.Combine(_root, "d.txt");
        File.WriteAllText(f, "x");
        File.SetAttributes(f, FileAttributes.ReadOnly); // IGNORE_READONLY_ATTRIBUTE
        using (var h = _files.OpenForDelete(f, false))
        {
            Assert.NotNull(h);
            Assert.False(h!.IsReparse);
            Assert.StartsWith(_files.Inspect(_root).FinalPath!, h.FinalPath, StringComparison.OrdinalIgnoreCase);
            h.Delete();
        }
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void OpenForDelete_missing_returns_null()
    {
        Assert.Null(_files.OpenForDelete(Path.Combine(_root, "nope.txt"), false));
        Assert.Null(_files.OpenForDelete(Path.Combine(_root, "nodir"), true));
    }

    [Fact]
    public void Junction_handle_is_reparse_and_delete_removes_only_the_link()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "k");
        var link = Path.Combine(_root, "link");
        MakeJunction(link, target);

        using (var h = _files.OpenForDelete(link, true))
        {
            Assert.NotNull(h);
            Assert.True(h!.IsReparse);
            // FinalPath самой ссылки, а не цели.
            Assert.EndsWith(@"\link", h.FinalPath, StringComparison.OrdinalIgnoreCase);
            h.Delete();
        }
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Inspect_marks_junction_root_as_reparse_and_reports_the_link_itself()
    {
        var target = Path.Combine(_root, "t2");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "l2");
        MakeJunction(link, target);
        var viaLink = _files.Inspect(link);
        var direct = _files.Inspect(target);
        Assert.True(viaLink.IsReparse);
        Assert.False(direct.IsReparse);
        // Один хэндл с OPEN_REPARSE_POINT: финальный путь это сама ссылка, а не её цель (атрибут и путь с одного объекта).
        Assert.EndsWith(@"\l2", viaLink.FinalPath!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(direct.FinalPath, viaLink.FinalPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_resolves_a_junction_in_a_parent_component_to_the_real_location()
    {
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(Path.Combine(target, "inner"));
        var link = Path.Combine(_root, "parentlink");
        MakeJunction(link, target);

        var facts = _files.Inspect(Path.Combine(link, "inner"));

        Assert.True(facts.Exists);
        Assert.False(facts.IsReparse);
        Assert.EndsWith(@"\real\inner", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locked_file_gives_sharing_violation()
    {
        var f = Path.Combine(_root, "busy.txt");
        File.WriteAllText(f, "x");
        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<SharingViolationException>(() => _files.OpenForDelete(f, false));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Non_empty_directory_is_not_deleted()
    {
        var d = Path.Combine(_root, "full");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "x.txt"), "x");
        using (var h = _files.OpenForDelete(d, true))
        {
            Assert.NotNull(h);
            var ex = Assert.Throws<IOException>(() => h!.Delete());
            Assert.Equal(unchecked((int)0x80070091), ex.HResult);
        }
        Assert.True(File.Exists(Path.Combine(d, "x.txt")));
    }

    [Fact]
    public void Empty_directory_is_deleted()
    {
        var d = Path.Combine(_root, "empty");
        Directory.CreateDirectory(d);
        using (var h = _files.OpenForDelete(d, true)) { Assert.NotNull(h); h!.Delete(); }
        Assert.False(Directory.Exists(d));
    }

    // ---------- процессы и корзина (только безопасное чтение) ----------

    [Fact]
    public void ProcessList_sees_own_process_for_own_sid_and_not_a_foreign_sid()
    {
        var sid = new WindowsRecycleBin().CurrentTokenSid();
        var me = Process.GetCurrentProcess().ProcessName;
        var list = new WindowsProcessList();
        Assert.True(list.IsRunning(me, sid));
        Assert.True(list.IsRunning(me + ".exe", sid));
        Assert.False(list.IsRunning("no-such-process-chistovik", sid));
    }

    // ---------- реестр: только чтение ----------

    [Fact]
    public void RegistryTree_reads_subkeys_and_raw_values_without_writing()
    {
        var loc = new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Microsoft\Windows\CurrentVersion", "");
        using var h = new WindowsRegistryTree().OpenTree(loc, forWrite: false);
        Assert.NotNull(h);
        Assert.False(h!.IsSymbolicLink);
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", h.ResolvedPath, ignoreCase: true);
        var subs = h.SubKeyNames();
        Assert.Contains("Uninstall", subs, StringComparer.OrdinalIgnoreCase);
        var info = h.Info();
        Assert.Equal(subs.Count, info.SubKeyCount);
        var values = h.RawValues();
        Assert.Equal(info.ValueCount, values.Count);
        Assert.Contains(values, v => v.Name.Equals("ProgramFilesDir", StringComparison.OrdinalIgnoreCase) && v.Type == RegType.String && v.Raw.Length >= 4);

        using var sub = h.OpenSub("Uninstall", false);
        Assert.NotNull(sub);
        Assert.False(sub!.IsSymbolicLink);
        Assert.EndsWith(@"\CurrentVersion\Uninstall", sub.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.OpenSub("NoSuchKey-Chistovik", false));
        Assert.Throws<ArgumentException>(() => h.OpenSub(@"Uninstall\X", false));
        Assert.Throws<InvalidOperationException>(() => h.DeleteSelf()); // открыт без записи
        Assert.Null(new WindowsRegistryTree().OpenTree(loc with { KeyPath = @"SOFTWARE\NoSuchKey-Chistovik" }, false));
    }
}
