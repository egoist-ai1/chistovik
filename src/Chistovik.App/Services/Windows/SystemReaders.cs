using System.IO;
using Chistovik.Core.Inventory;
using Chistovik.Core.Ports;
using Microsoft.Win32;

namespace Chistovik.App.Services.Windows;

/// <summary>Чтение реестра без прав и без записи. Недоступное читается как отсутствующее.</summary>
public sealed class ReadOnlyRegistryReader : IRegistryReader
{
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
/// Проверка подписи в App недоступна (её делает Native.Trust у агента), поэтому подсказки по службам консервативны:
/// «подпись не подтверждена». Агент всё равно перепроверяет каждый запрос.
/// </summary>
public sealed class UnknownFileTrust : IFileTrust
{
    public SignatureFacts GetSignature(string path) => new(false, false);
}
