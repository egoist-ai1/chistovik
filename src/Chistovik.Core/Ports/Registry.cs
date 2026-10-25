using Chistovik.Core.Execution;

namespace Chistovik.Core.Ports;

/// <summary>Расположение значения. Root=User требует UserSid (SID проверенного клиента); всегда Registry64.</summary>
public sealed record RegLocation(RegRoot Root, string? UserSid, string KeyPath, string ValueName);

/// <summary>Состояние значения. Data: DWord — uint, QWord — ulong, String/ExpandString — string, Binary — byte[], MultiString — string[]. Exists=false: значения нет.</summary>
public sealed record RegValueState(bool Exists, RegType Type, object? Data);

public interface IRegistry
{
    /// <summary>null, если ключа нет (при createKey=false). Отказ в доступе: UnauthorizedAccessException (шаг получает код access-denied).</summary>
    IRegKeyHandle? Open(RegLocation loc, bool write, bool createKey);
}

public interface IRegKeyHandle : IDisposable
{
    /// <summary>Фактический путь открытого ключа (NtQueryKey, KeyNameInformation).</summary>
    string ResolvedPath { get; }
    bool IsSymbolicLink { get; }
    RegValueState Read(string name);
    /// <summary>exists=false → удаление значения.</summary>
    void Write(ApprovedRegistryWrite w);
    IReadOnlyList<string> ValueNames();
}

/// <summary>Сведения о ключе (RegQueryInfoKey): число подключей и значений.</summary>
public sealed record RegKeyInfo(int SubKeyCount, int ValueCount);

/// <summary>Обход и удаление поддерева реестра по хэндлам (C3). Ссылки не разыменовываются (REG_OPTION_OPEN_LINK).</summary>
public interface IRegistryTree
{
    /// <summary>null, если ключа нет. KEY_READ|KEY_WOW64_64KEY (+KEY_WRITE|DELETE при forWrite).</summary>
    IRegTreeHandle? OpenTree(RegLocation loc, bool forWrite);
}

public interface IRegTreeHandle : IDisposable
{
    string ResolvedPath { get; }
    bool IsSymbolicLink { get; }
    RegKeyInfo Info();
    IReadOnlyList<string> SubKeyNames();

    /// <summary>REG_OPTION_OPEN_LINK; null, если подключа нет.</summary>
    IRegTreeHandle? OpenSub(string name, bool forWrite);

    IReadOnlyList<(string Name, RegType Type, byte[] Raw)> RawValues();
    IRegTreeHandle CreateSub(string name);
    void SetRaw(string name, RegType type, byte[] raw);

    /// <summary>NtDeleteKey(handle); нужен DELETE в SAM.</summary>
    void DeleteSelf();
}
