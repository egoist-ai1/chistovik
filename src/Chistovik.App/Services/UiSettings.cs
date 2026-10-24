using System.IO;
using System.Text.Json;

namespace Chistovik.App.Services;

/// <summary>
/// Что окно помнит между запусками: размер, позицию, последний раздел и то, что приветствие уже показано.
/// Хранится в %LOCALAPPDATA%\Chistovik\ui.json. Это только удобство: сбой чтения или записи ни на что не влияет.
/// </summary>
public sealed class UiSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>Заголовок последнего открытого раздела (как в боковом меню).</summary>
    public string? LastSection { get; set; }

    /// <summary>Приветствие в обзоре закрыто и больше не показывается.</summary>
    public bool WelcomeSeen { get; set; }
}

public interface IUiSettingsStore
{
    /// <summary>Читает настройки; любой сбой даёт значения по умолчанию.</summary>
    UiSettings Load();

    /// <summary>Сохраняет настройки; ошибки записи игнорируются.</summary>
    void Save(UiSettings settings);
}

/// <summary>Файл ui.json в папке пользователя. Запись атомарная: временный файл и замена.</summary>
public sealed class FileUiSettingsStore(string path) : IUiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chistovik", "ui.json");

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new UiSettings();
            var s = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path, System.Text.Encoding.UTF8));
            return Sanitize(s ?? new UiSettings());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // не сохранилось: в следующий раз окно откроется с размером по умолчанию
        }
    }

    /// <summary>Отбрасывает нечисловые и нелепые значения, чтобы окно не открылось размером в точку.</summary>
    internal static UiSettings Sanitize(UiSettings s)
    {
        static bool Bad(double? v) => v is { } d && (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 100_000);
        if (Bad(s.Left) || Bad(s.Top)) { s.Left = null; s.Top = null; }
        if (Bad(s.Width) || Bad(s.Height) || s.Width < 300 || s.Height < 300) { s.Width = null; s.Height = null; }
        return s;
    }
}

/// <summary>Без диска: демонстрация, самопроверка, снимки и тесты.</summary>
public sealed class MemoryUiSettingsStore(UiSettings? initial = null) : IUiSettingsStore
{
    private UiSettings _value = initial ?? new UiSettings { WelcomeSeen = true };

    public UiSettings Load() => _value;

    public void Save(UiSettings settings) => _value = settings;
}
