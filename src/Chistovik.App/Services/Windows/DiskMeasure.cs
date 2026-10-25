using System.IO;
using System.Text.RegularExpressions;
using Chistovik.App.Services.Scanners;
using Chistovik.Core.Catalog;

namespace Chistovik.App.Services.Windows;

/// <summary>
/// Измеряет категории очистки пользователя без прав: те же корни, возраст, шаблоны и «оставить», что у агента, но только чтение
/// и без журнала. Ссылки не входит. Категории компьютера, корзину и обработчики не меряет (возвращает null): это дело агента.
/// Цифра ориентировочная: настоящий размер и удаляемое определяет агент.
/// </summary>
public sealed class FileSystemDiskMeasure : IDiskMeasure
{
    const int MaxItems = 200_000;

    readonly string? _local;
    readonly string? _roaming;
    readonly Func<DateTimeOffset> _now;

    /// <param name="localAppData">Каталог «AppData\Local»; null берёт настоящий.</param>
    /// <param name="roamingAppData">Каталог «AppData\Roaming»; null берёт настоящий.</param>
    public FileSystemDiskMeasure(string? localAppData = null, string? roamingAppData = null, Func<DateTimeOffset>? now = null)
    {
        _local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public DiskMeasurement? Measure(CleanupCategory c)
    {
        if (c.Mechanism != "files" || c.Scope != "user") return null;
        var roots = new List<string>();
        foreach (var r in c.Roots)
        {
            var baseDir = r.Base switch { "userlocal" => _local, "userroaming" => _roaming, _ => null };
            if (string.IsNullOrWhiteSpace(baseDir)) return null;
            roots.AddRange(Expand(Path.Combine(baseDir, r.Sub.Replace('\\', Path.DirectorySeparatorChar))));
        }

        var patterns = (c.Patterns ?? []).Select(Glob).ToList();
        long bytes = 0, files = 0;
        var exists = false;
        var budget = MaxItems;
        var now = _now();
        var minAge = TimeSpan.FromHours(c.MinAgeHours);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            exists = true;
            Walk(root, c, patterns, now, minAge, ref bytes, ref files, ref budget);
        }
        return new DiskMeasurement(bytes, files, exists);
    }

    // Подстановка «*» одним средним компонентом: перебираем настоящие подкаталоги, не ссылки.
    static IEnumerable<string> Expand(string template)
    {
        var sep = Path.DirectorySeparatorChar;
        var mid = sep + "*" + sep;
        var i = template.IndexOf(mid, StringComparison.Ordinal);
        if (i < 0) return [template];
        var parent = template[..i];
        var suffix = template[(i + 2)..];
        if (!Directory.Exists(parent)) return [];
        try
        {
            return new DirectoryInfo(parent).EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName + suffix).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static void Walk(string dir, CleanupCategory c, List<Regex> patterns, DateTimeOffset now, TimeSpan minAge,
        ref long bytes, ref long files, ref int budget)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var e in entries)
        {
            if (budget-- <= 0) return;
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (c.KeepNames is { } keep && keep.Any(k => k.Equals(e.Name, StringComparison.OrdinalIgnoreCase))) continue;
            if (e is DirectoryInfo d) { Walk(d.FullName, c, patterns, now, minAge, ref bytes, ref files, ref budget); continue; }
            if (e is not FileInfo f) continue;
            if (patterns.Count > 0 && !patterns.Any(p => p.IsMatch(f.Name))) continue;
            var stamp = f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;
            if (now - new DateTimeOffset(stamp, TimeSpan.Zero) < minAge) continue;
            try { bytes += f.Length; files++; }
            catch (IOException) { }
        }
    }

    static Regex Glob(string pattern)
        => new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
