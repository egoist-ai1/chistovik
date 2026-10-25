using Chistovik.Native.Cleanup;
using Chistovik.Native.Interop;
using Xunit;

namespace Chistovik.Native.Tests;

/// <summary>
/// Пути длиннее MAX_PATH (кэши браузеров): префикс «\\?\» добавляется перед вызовом Win32, а `..` и `/` убираются до него.
/// Всё создаётся и удаляется только во временном каталоге теста.
/// </summary>
public sealed class LongPathTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "Chistovik.Test." + Guid.NewGuid().ToString("N"));
    readonly WindowsCleanupFiles _files = new();

    public LongPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* уборка */ }
    }

    string LongDir()
    {
        var dir = _root;
        for (var i = 0; i < 7; i++) dir = Path.Combine(dir, new string((char)('a' + i), 50));
        Directory.CreateDirectory(dir);
        Assert.True(dir.Length > 260, "тест требует путь длиннее MAX_PATH");
        return dir;
    }

    [Fact]
    public void Extend_adds_prefix_for_drive_and_unc_paths_and_leaves_extended_ones()
    {
        Assert.Equal(@"\\?\C:\a\b.txt", ExtendedPath.Extend(@"C:\a\b.txt"));
        Assert.Equal(@"\\?\UNC\srv\share\x", ExtendedPath.Extend(@"\\srv\share\x"));
        Assert.Equal(@"\\?\C:\a", ExtendedPath.Extend(@"\\?\C:\a"));
        Assert.Equal(@"\\.\PhysicalDrive0", ExtendedPath.Extend(@"\\.\PhysicalDrive0"));
        Assert.Equal(@"C:\a\b", ExtendedPath.Strip(@"\\?\C:\a\b"));
        Assert.Equal(@"\\srv\share\x", ExtendedPath.Strip(@"\\?\UNC\srv\share\x"));
    }

    [Fact]
    public void Extend_normalizes_dotdot_and_slashes_before_adding_prefix()
    {
        var e = ExtendedPath.Extend(@"C:\a\b\..\..\c/d\.\e.txt");
        Assert.Equal(@"\\?\C:\c\d\e.txt", e);
        Assert.DoesNotContain(@"\..\", e);
        // Выше корня подняться нельзя: «..» не превращается в обход.
        Assert.Equal(@"\\?\C:\x", ExtendedPath.Extend(@"C:\..\..\x"));
    }

    [Fact]
    public void Inspect_sees_and_OpenForDelete_removes_file_deeper_than_260_chars()
    {
        var dir = LongDir();
        var file = Path.Combine(dir, "cache.bin");
        File.WriteAllText(file, "x");
        Assert.True(file.Length > 260);

        var facts = _files.Inspect(file);
        Assert.True(facts.Exists, "файл в длинном пути не должен считаться отсутствующим");
        Assert.StartsWith(@"\\?\Volume{", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\cache.bin", facts.FinalPath!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(_files.Enumerate(dir), e => e.Name == "cache.bin");

        using (var h = _files.OpenForDelete(file, false))
        {
            Assert.NotNull(h);
            Assert.False(h!.IsReparse);
            h.Delete();
        }
        Assert.False(File.Exists(file));
        Assert.False(_files.Inspect(file).Exists);
        Assert.Null(_files.OpenForDelete(file, false));
    }

    [Fact]
    public void Long_directory_is_deleted_by_handle_after_its_content()
    {
        var dir = LongDir();
        var file = Path.Combine(dir, "x.tmp");
        File.WriteAllText(file, "x");
        using (var h = _files.OpenForDelete(file, false)) h!.Delete();
        using (var h = _files.OpenForDelete(dir, true))
        {
            Assert.NotNull(h);
            h!.Delete();
        }
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Dotdot_path_resolves_to_the_same_object_and_does_not_reach_a_sibling_by_literal_name()
    {
        var keep = Path.Combine(_root, "keep.txt");
        File.WriteAllText(keep, "x");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var viaDotDot = Path.Combine(_root, "sub", "..", "keep.txt");
        var a = _files.Inspect(viaDotDot);
        var b = _files.Inspect(keep);
        Assert.True(a.Exists);
        // Хэндл смотрит на тот же реальный объект, что и обычный путь: FinalPath совпадает, сегмента «..» в нём нет.
        Assert.Equal(b.FinalPath, a.FinalPath, ignoreCase: true);
        Assert.DoesNotContain("..", a.FinalPath!);

        // «..» поднимает ровно на один уровень: такого файла внутри sub нет.
        Assert.False(_files.Inspect(Path.Combine(_root, "sub", "..", "sub", "keep.txt")).Exists);
        Assert.Null(_files.OpenForDelete(Path.Combine(_root, "sub", "..", "sub", "keep.txt"), false));
        Assert.True(File.Exists(keep));
    }
}
