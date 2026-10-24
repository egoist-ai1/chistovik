namespace Chistovik.Core.Inventory;

public enum RegistryHiveKind { LocalMachine, CurrentUser }

public enum RegistryViewKind { Registry64, Registry32 }

/// <summary>Чтение реестра по пути внутри куста. Только чтение; Core не обращается к Microsoft.Win32.</summary>
public interface IRegistryReader
{
    /// <summary>Имена подключей; пустой список, если ключа нет или нет доступа.</summary>
    IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path);

    /// <summary>Значения ключа (DWORD приходит как int, строки как string); null, если ключа нет или нет доступа.</summary>
    IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path);
}
