using System.Text;
using System.Text.RegularExpressions;

namespace Chistovik.Core.Policy;

/// <summary>
/// Приведение пути к каноническому виду без обращения к файловой системе.
/// Всё, что нельзя привести однозначно, отклоняется: ошибка здесь безопасна, ошибка в сторону «пропустили» нет.
/// Окончательную проверку по реальному конечному пути (GetFinalPathNameByHandle) делает слой Native.
/// </summary>
public static partial class PathNormalizer
{
    public const int MaxLength = 32767;

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"~\d", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNameMarker();

    /// <summary>Результат: путь вида «X:\a\b» (буква диска заглавная, без хвостового «\», кроме корня «X:\»).</summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input)) { error = "Пустой путь"; return false; }
        if (input.Length > MaxLength) { error = "Слишком длинный путь"; return false; }

        var p = input.Replace('/', '\\');

        // Префиксы расширенных путей снимаем до проверки символов (в них есть «?»): допускаем только вариант с буквой диска.
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var rest = p[4..];
            if (rest.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase)) { error = "Сетевые пути не поддерживаются"; return false; }
            p = rest;
        }
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        else if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "Сетевые пути не поддерживаются";
            return false;
        }

        foreach (var ch in p)
        {
            if (ch < 0x20 || ch is '<' or '>' or '"' or '|' or '*' or '?')
            {
                error = "Недопустимые символы в пути";
                return false;
            }
            if (ch == '%') { error = "Переменные окружения должны быть раскрыты заранее"; return false; }
        }

        if (p.Length < 3 || !IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
        {
            error = "Нужен полный путь с буквой диска (тома по GUID и устройства сначала приводятся к букве)";
            return false;
        }

        var drive = char.ToUpperInvariant(p[0]);
        var stack = new List<string>();
        foreach (var raw in p[3..].Split('\\'))
        {
            if (raw.Length == 0) continue;               // «\\» схлопываем
            if (raw.Contains(':')) { error = "Альтернативные потоки и двоеточие в имени запрещены"; return false; }

            if (raw == ".") continue;
            if (raw == "..")
            {
                if (stack.Count == 0) { error = "Путь выходит выше корня"; return false; }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            // Win32 отбрасывает хвостовые точки и пробелы: «Windows.» это «Windows».
            var comp = raw.TrimEnd('.', ' ');
            if (comp.Length == 0) { error = "Имя из одних точек и пробелов"; return false; }
            if (comp.StartsWith(' ')) { /* ведущие пробелы допустимы в NTFS, оставляем как есть */ }

            if (ShortNameMarker().IsMatch(comp)) { error = "Короткое имя 8.3 не раскрыто"; return false; }

            var baseName = comp.Contains('.') ? comp[..comp.IndexOf('.')] : comp;
            if (ReservedNames.Contains(baseName.TrimEnd(' '))) { error = "Зарезервированное имя устройства"; return false; }

            stack.Add(comp);
        }

        var sb = new StringBuilder();
        sb.Append(drive).Append(':').Append('\\');
        sb.AppendJoin('\\', stack);
        normalized = sb.ToString();
        return true;
    }

    /// <summary>Разбор нормализованного пути на компоненты после «X:\».</summary>
    public static string[] Components(string normalized)
        => normalized.Length <= 3 ? [] : normalized[3..].Split('\\');

    /// <summary>true, если path совпадает с root или лежит внутри. Оба аргумента уже нормализованы.</summary>
    public static bool IsEqualOrUnder(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var r = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>true, если path является предком root или совпадает с ним.</summary>
    public static bool IsEqualOrAncestor(string path, string root) => IsEqualOrUnder(root, path);

    static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
