using System.IO;
using Chistovik.App.Services.Scanners;
using Chistovik.Core.Inventory;
using Chistovik.Core.Ports;
using Microsoft.Win32;

namespace Chistovik.App.Services.Windows;

/// <summary>Чтение реестра без прав и без записи. Недоступное читается как отсутствующее.</summary>
public sealed class ReadOnlyRegistryReader : IRegistryReader, IRegistryDefaultValue
{
    public string? ReadDefault(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        try
        {
            using var key = Open(hive, view, path);
            return key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        try
        {
            using var key = Open(hive, view, path);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        try
        {
            using var key = Open(hive, view, path);
            if (key is null) return null;
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                result[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            return result;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    static RegistryKey? Open(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        using var root = RegistryKey.OpenBaseKey(
            hive == RegistryHiveKind.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == RegistryViewKind.Registry64 ? RegistryView.Registry64 : RegistryView.Registry32);
        return root.OpenSubKey(path, writable: false);
    }
}

/// <summary>Состояние продукта Windows Installer через <c>MsiQueryProductStateW</c> (только чтение). Тесты её не вызывают.</summary>
public sealed class MsiState : IMsiState
{
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    static extern int MsiQueryProductStateW(string product);

    /// <summary>5 установлен, 2 отсутствует, -1 неизвестен. Любой сбой вызова даёт 0 («не знаем»), а не -1: сироту по нему не объявляем.</summary>
    public int QueryProductState(string productCode)
    {
        try { return MsiQueryProductStateW(productCode); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}

/// <summary>Существование файлов, каталогов и томов на настоящей файловой системе.</summary>
public sealed class RealFileExists : IFileExists
{
    public bool FileExists(string path) { try { return File.Exists(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }

    public bool DirectoryExists(string path) { try { return Directory.Exists(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }

    public bool DriveExists(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}

/// <summary>Список файлов каталога для папок автозагрузки (только чтение).</summary>
public sealed class DirectoryFileSystem : IFileSystem
{
    public IReadOnlyList<string> ListFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Запоминает результат проверки подписи по пути файла: десятки служб и записей автозагрузки указывают на один и тот же файл
/// (например svchost.exe), а проверка подписи стоит заметного времени. Сама проверка (WindowsFileTrust) только читает файл.
/// </summary>
public sealed class CachingFileTrust(IFileTrust inner) : IFileTrust
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignatureFacts> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SignatureFacts GetSignature(string path)
        => _cache.GetOrAdd(path, p =>
        {
            try { return inner.GetSignature(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return new SignatureFacts(false, false); // не удалось проверить: считаем неподписанным, подсказка станет строже
            }
        });
}
