using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Компараторы состояний по видам шагов (DESIGN-0.1 §1). Все чистые; невалидный payload даёт InvalidDataException.</summary>
public static class StateComparers
{
    static readonly IStateComparer[] All =
    [
        new RegistryStateComparer(), new StartupStateComparer(), new ServiceStateComparer(), new TaskStateComparer(),
        new PowerActiveStateComparer(), new PowerSettingStateComparer(), new PowerSchemeStateComparer(), new UninstallStateComparer(),
        new CleanupStateComparer(StepKind.CleanupCategory), new CleanupStateComparer(StepKind.RecycleBinEmpty),
        new RegKeyStateComparer(),
        new RegistryValueRemoveComparer(StepKind.StartupRemove), new RegistryValueRemoveComparer(StepKind.SharedDllRemove),
    ];

    public static IStateComparer For(StepKind kind)
    {
        foreach (var c in All)
            if (c.Kind == kind) return c;
        throw new ArgumentOutOfRangeException(nameof(kind));
    }
}

/// <summary>exists + type + data. Строки ordinal без хвостовых NUL, QWord как число, Binary побайтно.</summary>
public sealed class RegistryStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.RegistryValue;

    public bool IsSame(string a, string b) => ValueEquals(StateJson.ParseRegistry(a), StateJson.ParseRegistry(b));

    public bool IsNoChange(string a, string b) => IsSame(a, b);

    public static bool ValueEquals(RegValueState x, RegValueState y)
    {
        if (x.Exists != y.Exists) return false;
        if (!x.Exists) return true;
        if (x.Type != y.Type) return false;
        switch (x.Type)
        {
            case RegType.DWord:
                return Equals(x.Data, y.Data) || (x.Data is uint a && y.Data is uint b && a == b);
            case RegType.QWord:
                return x.Data is ulong p && y.Data is ulong q && p == q;
            case RegType.String or RegType.ExpandString:
                return string.Equals(TrimNul((string?)x.Data), TrimNul((string?)y.Data), StringComparison.Ordinal);
            case RegType.Binary:
                return ((byte[]?)x.Data ?? []).AsSpan().SequenceEqual((byte[]?)y.Data ?? []);
            case RegType.MultiString:
            {
                var l = (string[]?)x.Data ?? [];
                var r = (string[]?)y.Data ?? [];
                if (l.Length != r.Length) return false;
                for (var i = 0; i < l.Length; i++)
                    if (!string.Equals(TrimNul(l[i]), TrimNul(r[i]), StringComparison.Ordinal)) return false;
                return true;
            }
            default:
                return true;
        }
    }

    static string TrimNul(string? s) => (s ?? string.Empty).TrimEnd('\0');
}

/// <summary>
/// StartupApproved: конфликт и проверка записи по точным байтам; «ничего менять не нужно» по классу включён/выключен
/// (значения нет или первый байт чётный — включено; нечётный — выключено; прочее не распознаётся и считается «не равно»).
/// </summary>
public sealed class StartupStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.StartupApproved;

    public bool IsSame(string a, string b)
    {
        var (ea, ba) = StateJson.ParseStartup(a);
        var (eb, bb) = StateJson.ParseStartup(b);
        if (ea != eb) return false;
        if (!ea) return true;
        return ba is not null && bb is not null && ba.AsSpan().SequenceEqual(bb);
    }

    public bool IsNoChange(string a, string b)
    {
        if (IsSame(a, b)) return true;
        var ca = Class(StateJson.ParseStartup(a));
        var cb = Class(StateJson.ParseStartup(b));
        return ca is not null && ca == cb;
    }

    /// <summary>true включено, false выключено, null не распознано.</summary>
    public static bool? Class((bool Exists, byte[]? Bytes) s)
    {
        if (!s.Exists) return true;
        if (s.Bytes is not { Length: 12 }) return null;
        return (s.Bytes[0] & 1) == 0;
    }
}

/// <summary>Запуск службы: start и признак отложенного (различает Automatic и AutomaticDelayed). TriggerStart и Running не сравниваются.</summary>
public sealed class ServiceStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.ServiceStart;

    public bool IsSame(string a, string b)
    {
        var x = StateJson.ParseService(a);
        var y = StateJson.ParseService(b);
        return x.Start == y.Start && (x.Start != 2 || x.Delayed == y.Delayed);
    }

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

public sealed class TaskStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.TaskEnabled;

    public bool IsSame(string a, string b)
    {
        var x = StateJson.ParseTask(a);
        var y = StateJson.ParseTask(b);
        if (x.Exists != y.Exists) return false;
        return !x.Exists || x.Enabled == y.Enabled;
    }

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

public sealed class PowerActiveStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.PowerActiveScheme;

    public bool IsSame(string a, string b) => StateJson.ParsePowerActive(a).Scheme == StateJson.ParsePowerActive(b).Scheme;

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

public sealed class PowerSettingStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.PowerSetting;

    public bool IsSame(string a, string b)
    {
        var x = StateJson.ParsePowerSetting(a);
        var y = StateJson.ParsePowerSetting(b);
        return x.Ac == y.Ac && x.Dc == y.Dc;
    }

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

public sealed class PowerSchemeStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.PowerSchemeCreate;

    public bool IsSame(string a, string b) => StateJson.ParsePowerScheme(a).Exists == StateJson.ParsePowerScheme(b).Exists;

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

/// <summary>Удаление программы: сравнивается только «установлена / нет».</summary>
public sealed class UninstallStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.UninstallRun;

    public bool IsSame(string a, string b) => StateJson.ParseUninstall(a).Installed == StateJson.ParseUninstall(b).Installed;

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

/// <summary>Разбор payload'ов C2/C3 с тем же контрактом ошибок, что у StateJson: невалидный JSON даёт InvalidDataException.</summary>
internal static class C2C3Json
{
    public static T Parse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try { return System.Text.Json.JsonSerializer.Deserialize(json, info) ?? throw new InvalidDataException("Пустое состояние"); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException("Недопустимое состояние", e); }
    }
}

/// <summary>
/// CleanupCategory и RecycleBinEmpty. IsSame: совпадает флаг Done. IsNoChange(текущее, желаемое): в категории не осталось файлов
/// (Files == 0) либо корзина пуста (Bytes == 0).
/// </summary>
public sealed class CleanupStateComparer(StepKind kind) : IStateComparer
{
    public StepKind Kind => kind;

    public bool IsSame(string a, string b)
        => C2C3Json.Parse(a, JournalJson.Default.CleanupState).Done == C2C3Json.Parse(b, JournalJson.Default.CleanupState).Done;

    public bool IsNoChange(string current, string desired)
    {
        var cur = C2C3Json.Parse(current, JournalJson.Default.CleanupState);
        _ = C2C3Json.Parse(desired, JournalJson.Default.CleanupState);
        return cur.Done || (kind == StepKind.RecycleBinEmpty ? cur.Bytes == 0 : cur.Files == 0);
    }
}

/// <summary>RegistryKeyDelete: сравнивается только «ключ есть / нет».</summary>
public sealed class RegKeyStateComparer : IStateComparer
{
    public StepKind Kind => StepKind.RegistryKeyDelete;

    public bool IsSame(string a, string b)
        => C2C3Json.Parse(a, JournalJson.Default.RegKeyState).Exists == C2C3Json.Parse(b, JournalJson.Default.RegKeyState).Exists;

    public bool IsNoChange(string a, string b) => IsSame(a, b);
}

/// <summary>StartupRemove и SharedDllRemove: состояние это значение реестра, сравнение как у RegistryValue.</summary>
public sealed class RegistryValueRemoveComparer(StepKind kind) : IStateComparer
{
    static readonly RegistryStateComparer Inner = new();

    public StepKind Kind => kind;

    public bool IsSame(string a, string b) => Inner.IsSame(a, b);

    public bool IsNoChange(string a, string b) => Inner.IsNoChange(a, b);
}
