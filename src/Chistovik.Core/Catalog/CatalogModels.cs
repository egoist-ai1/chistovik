using System.Text.Json;

namespace Chistovik.Core.Catalog;

/// <summary>Твик реестра из каталога. OffValue = null означает «удалить значение».</summary>
public sealed record TweakEntry(
    string Id,
    string Title,
    string Description,
    string? Category,
    string Area,
    string Risk,
    string Hive,
    string Key,
    string Value,
    string ValueType,
    JsonElement OnValue,
    JsonElement? OffValue,
    string Restart,
    bool RequiresAdmin,
    string Source,
    bool Verified)
{
    /// <summary>Полный путь ключа вида «HKCU\Software\...» для проверки политикой.</summary>
    public string FullKey => Hive + "\\" + Key;
}

/// <summary>Режим: план питания и набор состояний твиков (id → включён).</summary>
public sealed record ModeEntry(
    string Id,
    string Name,
    string Tag,
    string Description,
    string PowerPlan,
    bool RequiresAdmin,
    IReadOnlyDictionary<string, bool> Tweaks);
