using System.IO;
using System.Text;

namespace Chistovik.App.Services;

/// <summary>
/// Запись необработанных ошибок в crash.log (%LocalAppData%\Chistovik). Никогда не бросает исключений:
/// сбой записи не должен скрыть исходную ошибку. Размер файла ограничен: при превышении старая часть отбрасывается.
/// В журнал идут тип, сообщение и стек ошибки; пользовательские данные и значения из запросов не пишутся.
/// </summary>
public sealed class CrashLog
{
    public const long MaxBytes = 1024 * 1024;

    public string FilePath { get; }

    public CrashLog(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath();
    }

    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chistovik", "crash.log");

    /// <summary>Дописывает запись. Возвращает true, если запись удалась.</summary>
    public bool Write(string source, Exception? error, DateTimeOffset? now = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Trim();
            var sb = new StringBuilder();
            sb.Append("=== ").Append((now ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz")).Append(" [").Append(source).AppendLine("]");
            sb.Append("OS: ").AppendLine(Environment.OSVersion.VersionString);
            sb.Append("Runtime: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            sb.AppendLine(error?.ToString() ?? "(исключение не передано)");
            sb.AppendLine();
            File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Если файл вырос за предел, оставляет последнюю четверть.</summary>
    private void Trim()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes) return;
            var bytes = File.ReadAllBytes(FilePath);
            var keep = (int)(MaxBytes / 4);
            File.WriteAllBytes(FilePath, bytes.AsSpan(bytes.Length - keep).ToArray());
        }
        catch
        {
            // Не получилось обрезать: продолжаем дописывать, лучше лишний размер, чем потерянная ошибка.
        }
    }

    public string UserMessage() => $"Чистовик столкнулся с ошибкой. Подробности записаны в {FilePath}";
}
