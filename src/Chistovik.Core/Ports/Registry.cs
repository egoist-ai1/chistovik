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
