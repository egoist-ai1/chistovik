namespace Chistovik.Native.Interop;

/// <summary>
/// Префикс расширенных путей Win32 (<c>\\?\</c>, для UNC <c>\\?\UNC\</c>): снимает лимит MAX_PATH (260) при прямых вызовах Win32.
/// Префикс добавляется только в самом низу, прямо перед вызовом Win32: все проверки политики идут над обычным путём.
/// С этим префиксом Windows не нормализует путь (<c>..</c>, <c>/</c>, «.» и пробелы в конце), поэтому
/// нормализация выполняется здесь, до добавления префикса, и результат не содержит сегментов <c>..</c>.
/// </summary>
internal static class ExtendedPath
{
    const string Prefix = @"\\?\";
    const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Полный нормализованный путь с префиксом <c>\\?\</c>. Уже расширенные пути и пути устройств (<c>\\.\</c>) возвращаются как есть.
    /// Путь, который не удалось нормализовать (недопустимые символы), возвращается без изменений: решение остаётся за Win32, как раньше.
    /// </summary>
    public static string Extend(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(Prefix, StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            // «\\server\share\x»; устройства и уже расширенные формы после GetFullPath сюда не попадают с другим смыслом.
            return full.StartsWith(Prefix, StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? full
                : UncPrefix + full[2..];

        return full.Length >= 3 && full[1] == ':' && full[2] == '\\' ? Prefix + full : full;
    }

    /// <summary>Обратное преобразование: убирает <c>\\?\</c> и <c>\\?\UNC\</c> (для сравнения с обычными путями).</summary>
    public static string Strip(string p)
    {
        if (p.StartsWith(UncPrefix, StringComparison.Ordinal)) return @"\\" + p[UncPrefix.Length..];
        return p.StartsWith(Prefix, StringComparison.Ordinal) ? p[Prefix.Length..] : p;
    }
}
