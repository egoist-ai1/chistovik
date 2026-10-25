using System.IO;
using System.Text.RegularExpressions;
using Chistovik.Core.Inventory;

namespace Chistovik.App.Services;

/// <summary>Откуда брать значок: локальный файл и индекс значка в нём (null — значок по умолчанию для файла).</summary>
public sealed record IconRequest(string Path, int? Index)
{
    /// <summary>Каноническая запись «"путь",индекс»: её разбирает <see cref="IconSources.Parse"/> без потерь.</summary>
    public string Format() => "\"" + Path + "\"" + (Index is { } i ? "," + i : "");
}

/// <summary>
/// Разбор строки-источника значка. Источник это значение DisplayIcon («путь[,индекс]») либо командная строка
/// (Run, служба, задача: путь с аргументами, кавычки, переменные среды, «\SystemRoot\», «rundll32 …»).
/// Только текст и пути: файл не открывается и не запускается. Всё сомнительное (сеть, устройства, «..», длинное) отвергается.
/// </summary>
public static partial class IconSources
{
    public const int MaxPathLength = 1024;
    public const int MaxSourceLength = 2048;

    // Расширения, у которых бывает собственный значок. Драйверы (.sys) и прочее не показываем: там нечего показывать.
    private static readonly HashSet<string> IconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".ico", ".cpl", ".scr", ".ocx", ".lnk", ".msc", ".com", ".bat", ".cmd",
    };

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"\.(exe|dll|ico|cpl|scr|ocx|lnk|msc|com|bat|cmd|sys|drv)(?=$|[\s,""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionEnd();

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRoot();

    /// <summary>Разбирает источник; null — значок не нужен или источник не годится (мусор, сеть, «..», устройства, слишком длинное).</summary>
    public static IconRequest? Parse(string? raw) => Parse(raw, 0);

    private static IconRequest? Parse(string? raw, int depth)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxSourceLength || depth > 1) return null;
        var text = raw.Trim();

        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        else if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) text = "%SystemRoot%" + text[11..];
        if (text.Length == 0) return null;

        string pathPart;
        string rest;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) return null;
            pathPart = text[1..close];
            rest = text[(close + 1)..];
        }
        else
        {
            var expanded = Expand(text);
            if (expanded is null) return null;
            var m = ExtensionEnd().Match(expanded);
            var firstSpace = expanded.IndexOfAny([' ', '\t']);
            var bareFirst = firstSpace > 0 && expanded.IndexOfAny(['\\', '/', ':'], 0, firstSpace) < 0;
            if (m.Success && !(bareFirst && m.Index > firstSpace))
            {
                pathPart = expanded[..(m.Index + m.Length)];
                rest = expanded[(m.Index + m.Length)..];
            }
            else if (bareFirst)
            {
                // «rundll32 x.dll,Func»: первое слово без каталога это имя программы, расширение дальше относится к аргументу.
                pathPart = expanded[..firstSpace];
                rest = expanded[firstSpace..];
            }
            else
            {
                var cut = expanded.IndexOfAny([' ', '\t', ',']);
                pathPart = cut < 0 ? expanded : expanded[..cut];
                rest = cut < 0 ? "" : expanded[cut..];
            }
        }

        var path = Resolve(pathPart);
        if (path is null) return null;

        // «rundll32 x.dll,Func»: значок берём у DLL, а не у самого rundll32.
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Equals("rundll32", StringComparison.OrdinalIgnoreCase) && rest.Trim().Length > 0)
        {
            var inner = Parse(rest.Trim(), depth + 1);
            return inner is null ? null : inner with { Index = null };
        }

        if (!IconExtensions.Contains(System.IO.Path.GetExtension(path))) return null;
        return new IconRequest(path, ParseIndex(rest));
    }

    private static int? ParseIndex(string rest)
    {
        var s = rest.TrimStart();
        if (s.Length == 0 || s[0] != ',') return null;
        s = s[1..].TrimStart();
        var i = 0;
        if (i < s.Length && s[i] == '-') i++;
        var digitsStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i == digitsStart || i - digitsStart > 6) return null;
        return int.TryParse(s.AsSpan(0, i), out var n) ? n : null;
    }

    private static string? Expand(string text)
    {
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(text); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
        // Неизвестная переменная остаётся как «%X%»: путь с процентом не берём.
        return expanded.Contains('%') ? null : expanded;
    }

    /// <summary>Приводит путь к абсолютному локальному виду «X:\…» или null, если путь не годится.</summary>
    public static string? Resolve(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxPathLength) return null;
        var p = candidate.Trim().Trim('"').Replace('/', '\\');
        var expanded = Expand(p);
        if (expanded is null) return null;
        p = expanded.Trim();
        if (p.Length == 0 || p.Length > MaxPathLength) return null;

        // Голое имя («notepad.exe», «rundll32»): только системный каталог, PATH не ищем.
        if (p.IndexOfAny(['\\', ':']) < 0 && p != "." && !p.Contains(".."))
        {
            if (p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
            var file = System.IO.Path.HasExtension(p) ? p : p + ".exe";
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (sys.Length == 0) return null;
            p = System.IO.Path.Combine(sys, file);
        }

        // Только «X:\…»: UNC (\\сервер), устройства (\\.\ и \\?\), относительные пути и потоки (файл:поток) отвергаются.
        if (!DriveRoot().IsMatch(p)) return null;
        if (p.Contains("..", StringComparison.Ordinal)) return null;
        if (p.IndexOf(':', 2) >= 0) return null;
        foreach (var ch in p)
            if (ch < ' ' || ch is '<' or '>' or '"' or '|' or '?' or '*') return null;
        foreach (var segment in p[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var stem = segment.Split('.')[0].TrimEnd();
            if (DeviceNames.Contains(stem)) return null;
        }

        try
        {
            var full = System.IO.Path.GetFullPath(p);
            return DriveRoot().IsMatch(full) && full.Length <= MaxPathLength ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // ----- программы: DisplayIcon, затем каталог установки, затем деинсталлятор -----

    /// <summary>Проверки файловой системы для выбора значка программы. За интерфейсом, чтобы тесты не касались диска.</summary>
    public interface IProbe
    {
        bool FileExists(string path);
        IEnumerable<string> ListExecutables(string directory);
    }

    /// <summary>
    /// Лучший источник значка для программы или null. Порядок: DisplayIcon, исполняемый файл в InstallLocation,
    /// путь деинсталлятора из UninstallString (запасной вариант: значок деинсталлятора лучше общего).
    /// </summary>
    public static string? ForProgram(InstalledProgram p, IProbe probe)
    {
        try
        {
            if (Parse(p.DisplayIcon) is { } icon && probe.FileExists(icon.Path)) return icon.Format();

            if (Resolve(p.InstallLocation) is { } dir)
            {
                var exe = PickExecutable(probe.ListExecutables(dir), p.DisplayName);
                if (exe is not null) return new IconRequest(exe, null).Format();
            }

            foreach (var command in new[] { p.QuietUninstallString, p.UninstallString })
            {
                if (command.Length == 0 || command.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) continue;
                if (Parse(command) is { } u && probe.FileExists(u.Path)) return new IconRequest(u.Path, null).Format();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Файл недоступен: значок не нужен, останется общий.
        }
        return null;
    }

    private static readonly string[] NotMainExecutable =
        ["unins", "uninst", "setup", "install", "update", "crash", "helper", "redist", "vc_", "dotnet"];

    /// <summary>Главный exe каталога: совпадающий по имени с программой, иначе первый не служебный.</summary>
    public static string? PickExecutable(IEnumerable<string> executables, string displayName)
    {
        var wanted = Letters(displayName);
        string? fallback = null;
        foreach (var path in executables.Take(60).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            if (NotMainExecutable.Any(w => stem.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var l = Letters(stem);
            if (l.Length >= 3 && (wanted.Contains(l, StringComparison.Ordinal) || l.Contains(wanted, StringComparison.Ordinal)))
                return path;
            fallback ??= path;
        }
        return fallback;
    }

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>Настоящая файловая система для <see cref="IconSources.IProbe"/>: только фиксированные диски, только чтение.</summary>
public sealed class DiskIconProbe : IconSources.IProbe
{
    public bool FileExists(string path) => LocalFiles.IsFixedFile(path);

    public IEnumerable<string> ListExecutables(string directory)
    {
        if (!LocalFiles.IsFixedDirectory(directory)) return [];
        try { return Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).Take(60).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Проверки «файл на фиксированном диске». Сеть, съёмные диски и приводы не трогаем: обращение к ним может надолго подвесить.</summary>
public static class LocalFiles
{
    public static bool IsFixedDrive(string path)
    {
        if (path.Length < 3 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return false;
        try { return new DriveInfo(path[..1]).DriveType == DriveType.Fixed; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsFixedFile(string path)
    {
        try { return IsFixedDrive(path) && File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsFixedDirectory(string path)
    {
        try { return IsFixedDrive(path) && Directory.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
