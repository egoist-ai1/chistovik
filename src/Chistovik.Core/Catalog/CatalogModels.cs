using System.Text.Json;

namespace Chistovik.Core.Catalog;

/// <summary>
/// Твик реестра из каталога. OffValue = null означает «удалить значение».
/// Editions: редакции Windows, на которых твик действует (null: на всех). RevertedBy: что возвращает значение
/// (feature-update | cumulative-update | none; null: не указано). Scope: user | machine, совпадает с Hive.
/// MinBuild/MaxBuild: номера сборок Windows (включительно), на которых твик действует (null: без ограничения).
/// </summary>
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
    string Effect,
    bool RequiresAdmin,
    string Source,
    bool Verified,
    string[]? Editions = null,
    string? RevertedBy = null,
    string? Scope = null,
    int? MinBuild = null,
    int? MaxBuild = null)
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
