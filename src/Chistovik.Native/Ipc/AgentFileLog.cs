using System.Text;
using System.Text.RegularExpressions;

namespace Chistovik.Native.Ipc;

/// <summary>
/// Файловый журнал агента (agent.log в каталоге данных агента): Chistovik.Agent.exe это WinExe, stderr никуда не идёт.
/// Пишутся только короткие коды и имена типов ошибок, как в stderr; пути и SID вырезаются. Потокобезопасен, размер ограничен
/// (при превышении остаётся последняя четверть, начало обрезается по границе строки, поэтому символ UTF-8 не разрезается).
/// Никогда не бросает исключений.
/// Каталог не создаётся и до <see cref="Attach"/> файл не открывается: сообщения копятся в памяти. Агент подключает файл только когда
/// каталог журнала проверен и защищён (иначе обычный пользователь мог бы подсунуть ссылку на чужой файл).
/// </summary>
public sealed partial class AgentFileLog
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public const string FileName = "agent.log";
    const int MaxPending = 200;
    const int MaxLineChars = 400;

    readonly object _lock = new();
    readonly List<string> _pending = new();
    readonly long _maxBytes;
    readonly Func<DateTimeOffset> _now;
    string? _path;
    bool _closed;

    public AgentFileLog(long maxBytes = DefaultMaxBytes, Func<DateTimeOffset>? now = null)
    {
        _maxBytes = Math.Max(64, maxBytes);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Путь файла после успешного <see cref="Attach"/>, иначе null.</summary>
    public string? FilePath
    {
        get { lock (_lock) return _path; }
    }

    /// <summary>Подготавливает строку: без переводов строки, без путей и SID, ограниченной длины.</summary>
    internal static string Scrub(string message)
    {
        var s = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        // Консервативно: путь с пробелами (имя пользователя, «Program Files») вырезается до кавычки, <, >, | или конца строки,
        // лучше потерять хвост сообщения, чем оставить имя пользователя.
        s = QuotedPath().Replace(s, "<path>");
        s = DrivePath().Replace(s, "<path>");
        s = UncPath().Replace(s, "<path>");
        s = EnvPath().Replace(s, "<path>");
        s = Sid().Replace(s, "<sid>");
        return s.Length > MaxLineChars ? s[..MaxLineChars] + "..." : s;
    }

    // Путь в одинарных кавычках: до закрывающей кавычки, чтобы не резать на апострофе внутри имени (O'Brien) и сохранить текст после пути.
    [GeneratedRegex(@"(?<=')(?:[A-Za-z]:[\\/]|\\\\|%[A-Za-z_][A-Za-z0-9_]*%)[^'""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"\\\\[^""<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    [GeneratedRegex(@"%[A-Za-z_][A-Za-z0-9_]*%(?:[\\/][^""<>|]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex EnvPath();

    [GeneratedRegex(@"S-1-[0-9]+(-[0-9]+)+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Sid();

    /// <summary>Пишет строку. До подключения файла копит в памяти (не больше 200 строк); после <see cref="Close"/> игнорирует.</summary>
    public void Write(string message)
    {
        try
        {
            var line = _now().ToString("yyyy-MM-dd HH:mm:ss") + " " + Scrub(message);
            lock (_lock)
            {
                if (_closed) return;
                if (_path is null)
                {
                    if (_pending.Count < MaxPending) _pending.Add(line);
                    return;
                }
                Append(_path, [line]);
            }
        }
        catch (Exception)
        {
            // Журнал не должен ронять агента.
        }
    }

    /// <summary>
    /// Подключает файл в существующем каталоге (сам каталог не создаётся) и сбрасывает накопленные строки.
    /// false — каталога нет или писать нельзя; тогда журнал отключается.
    /// </summary>
    public bool Attach(string directory)
    {
        try
        {
            lock (_lock)
            {
                if (_closed) return false;
                if (!Directory.Exists(directory)) { Close(); return false; }
                var path = Path.Combine(directory, FileName);
                if (!Append(path, _pending)) { Close(); return false; }
                _pending.Clear();
                _path = path;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Отключает файл и сбрасывает накопленное (например, каталог журнала не доверенный).</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            _pending.Clear();
        }
    }

    bool Append(string path, IReadOnlyList<string> lines)
    {
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            Trim(path);
            if (lines.Count == 0) return true;
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append('\n');
            var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Файл вырос за предел: остаётся последняя четверть, начиная со следующей строки (0x0A никогда не бывает внутри символа UTF-8).</summary>
    void Trim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= _maxBytes) return;
            var all = File.ReadAllBytes(path);
            var keep = (int)Math.Max(1, _maxBytes / 4);
            var start = Math.Max(0, all.Length - keep);
            var nl = Array.IndexOf(all, (byte)'\n', start);
            var from = nl < 0 ? all.Length : nl + 1;
            File.WriteAllBytes(path, all.AsSpan(from).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не получилось обрезать: лучше лишний размер, чем потерянное сообщение.
        }
    }
}
