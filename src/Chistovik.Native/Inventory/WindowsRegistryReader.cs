using Chistovik.Core.Inventory;
using Microsoft.Win32;

namespace Chistovik.Native.Inventory;

/// <summary>Чтение реестра Windows для инвентаря. Ключи открываются только на чтение, запись не используется.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
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
                // Переменные окружения не раскрываем: нужно исходное значение.
                result[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            return result;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static RegistryKey? Open(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        using var root = RegistryKey.OpenBaseKey(
            hive == RegistryHiveKind.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == RegistryViewKind.Registry64 ? RegistryView.Registry64 : RegistryView.Registry32);
        return root.OpenSubKey(path, writable: false);
    }
}
