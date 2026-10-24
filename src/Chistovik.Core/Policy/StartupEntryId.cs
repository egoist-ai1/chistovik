namespace Chistovik.Core.Policy;

/// <summary>Расположение записи автозагрузки (DESIGN-0.1 §3).</summary>
public enum StartupLocation
{
    /// <summary>HKLM\Software\Microsoft\Windows\CurrentVersion\Run (64-разрядный вид).</summary>
    Hklm64,
    /// <summary>HKLM\Software\WOW6432Node\...\Run (32-разрядный вид).</summary>
    Hklm32,
    /// <summary>HKU\&lt;SID проверенного клиента&gt;\...\Run.</summary>
    Hku,
    /// <summary>Папка «Автозагрузка» пользователя.</summary>
    UserFolder,
    /// <summary>Общая папка «Автозагрузка».</summary>
    CommonFolder,
}

/// <summary>
/// Идентификатор записи автозагрузки. Формат: <c>run:{hklm64|hklm32|hku}:{имя значения}</c> или
/// <c>folder:{user|common}:{имя файла}</c>, всего не более 300 символов. Идентификатор лишь указывает, что искать
/// в собственном перечислении агента; сам по себе он ничего не разрешает.
/// </summary>
public sealed record StartupEntryId(StartupLocation Location, string Name)
{
    public const int MaxLength = 300;

    /// <summary>true для записей реестра (Run), false для файлов папки автозагрузки.</summary>
    public bool IsRun => Location is StartupLocation.Hklm64 or StartupLocation.Hklm32 or StartupLocation.Hku;

    /// <summary>
    /// Разбирает и проверяет идентификатор. Допускает любой регистр в служебных частях; имя берётся как есть, после второго «:».
    /// Отказ: длина больше 300, неизвестный вид или расположение, пустое имя, управляющие символы, пробелы по краям имени;
    /// для папок дополнительно «\», «/», «:», «.» и «..», хвостовые точки и пробелы, зарезервированные имена устройств,
    /// подстановочные знаки и 8.3-имена. При false значение <paramref name="id"/> недействительно.
    /// </summary>
    public static bool TryParse(string? text, out StartupEntryId id, out string error)
    {
        id = new StartupEntryId(StartupLocation.Hklm64, string.Empty);
        error = string.Empty;
        if (string.IsNullOrEmpty(text)) { error = "Пустой идентификатор"; return false; }
        if (text.Length > MaxLength) { error = "Идентификатор длиннее 300 символов"; return false; }

        var first = text.IndexOf(':');
        var second = first < 0 ? -1 : text.IndexOf(':', first + 1);
        if (second < 0) { error = "Нужен формат вид:расположение:имя"; return false; }
        var kind = text[..first];
        var where = text[(first + 1)..second];
        var name = text[(second + 1)..];

        StartupLocation loc;
        if (kind.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            if (where.Equals("hklm64", StringComparison.OrdinalIgnoreCase)) loc = StartupLocation.Hklm64;
            else if (where.Equals("hklm32", StringComparison.OrdinalIgnoreCase)) loc = StartupLocation.Hklm32;
            else if (where.Equals("hku", StringComparison.OrdinalIgnoreCase)) loc = StartupLocation.Hku;
            else { error = "Неизвестное расположение записи run"; return false; }
        }
        else if (kind.Equals("folder", StringComparison.OrdinalIgnoreCase))
        {
            if (where.Equals("user", StringComparison.OrdinalIgnoreCase)) loc = StartupLocation.UserFolder;
            else if (where.Equals("common", StringComparison.OrdinalIgnoreCase)) loc = StartupLocation.CommonFolder;
            else { error = "Неизвестное расположение папки"; return false; }
        }
        else { error = "Неизвестный вид записи"; return false; }

        if (name.Length == 0) { error = "Пустое имя"; return false; }
        foreach (var ch in name)
            if (char.IsControl(ch)) { error = "Управляющие символы в имени"; return false; }
        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1])) { error = "Пробелы по краям имени"; return false; }

        if (loc is StartupLocation.UserFolder or StartupLocation.CommonFolder && !ValidFileName(name, out error))
            return false;

        id = new StartupEntryId(loc, name);
        return true;
    }

    static bool ValidFileName(string name, out string error)
    {
        error = string.Empty;
        if (name.Length > 255) { error = "Слишком длинное имя файла"; return false; }
        if (name.AsSpan().IndexOfAny('\\', '/', ':') >= 0) { error = "Разделители пути в имени файла"; return false; }
        if (name is "." or "..") { error = "Недопустимое имя файла"; return false; }
        // Нормализатор отсекает подстановочные знаки, зарезервированные имена, 8.3 и сообщает об отброшенных точках и пробелах.
        if (!PathNormalizer.TryNormalize(@"C:\" + name, out var n, out var err)) { error = err; return false; }
        if (!n.Equals(@"C:\" + name, StringComparison.Ordinal)) { error = "Имя файла изменяется при нормализации"; return false; }
        return true;
    }

    /// <summary>Канонический вид идентификатора; TryParse(ToString()) возвращает равную запись.</summary>
    public override string ToString()
    {
        var kind = IsRun ? "run" : "folder";
        var where = Location switch
        {
            StartupLocation.Hklm64 => "hklm64",
            StartupLocation.Hklm32 => "hklm32",
            StartupLocation.Hku => "hku",
            StartupLocation.UserFolder => "user",
            _ => "common",
        };
        return kind + ":" + where + ":" + Name;
    }
}
