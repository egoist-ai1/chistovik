using System.Globalization;
using Chistovik.Core.Execution;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>
/// Чистка реестра (C3): удаление ключей-сирот программ, записей Run/RunOnce, значений SharedDLLs и откат этих действий.
/// Существующие ветки (CheckSetValue, CheckDeleteKey) не меняются: Run* остаётся reg-run-keys, HKLM\SOFTWARE\Chistovik остаётся
/// reg-protected-node для обычных записей. Здесь только узкие разрешения с доказательством «сирота» и копией.
/// </summary>
public static partial class RegistryPolicy
{
    public const int MaxOrphanSubkeys = 50;
    public const int MaxOrphanSubkeysExpert = 500;
    public const int MaxRunDataChars = 2048;

    static readonly string[] CurrentVersionRel = ["Microsoft", "Windows", "CurrentVersion"];

    /// <summary>
    /// Часть пути после «Software» (HKLM: без WOW6432Node по правилам SoftwareRel; HKU клиента: WOW6432Node снимается здесь).
    /// null: ключ не в HKLM\SOFTWARE и не в HKU\&lt;sid клиента&gt;\Software (куст классов _Classes не подходит).
    /// </summary>
    static string[]? CleanupRel(RegKey k, string clientSid)
    {
        var s = k.Segments;
        switch (k.Hive)
        {
            case RegHive.LocalMachine:
                if (s.Length == 0 || !s[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)) return null;
                return SoftwareRel(k);
            case RegHive.Users:
                if (s.Length < 2 || !s[0].Equals(clientSid, StringComparison.OrdinalIgnoreCase)) return null;
                var rel = SoftwareRel(k);
                if (rel is { Length: > 0 } && rel[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) rel = rel[1..];
                return rel;
            default:
                return null;
        }
    }

    /// <summary>Родитель записи: имя последнего сегмента CurrentVersion (Uninstall, App Paths, Run…) и число сегментов после него.</summary>
    static bool UnderCurrentVersion(string[]? rel, string leaf, int extra)
        => rel is not null && rel.Length == CurrentVersionRel.Length + 1 + extra
           && StartsWith(rel, 0, CurrentVersionRel) && rel[CurrentVersionRel.Length].Equals(leaf, StringComparison.OrdinalIgnoreCase);

    static bool IsProgramEntry(string[]? rel)
        => UnderCurrentVersion(rel, "Uninstall", 1) || UnderCurrentVersion(rel, "App Paths", 1);

    // Узел Run или RunOnce (не RunOnceEx, RunServices): значения в нём единственное место, где чистим автозагрузку.
    static bool IsRunParent(string[]? rel) => UnderCurrentVersion(rel, "Run", 0) || UnderCurrentVersion(rel, "RunOnce", 0);

    /// <summary>
    /// Удаление ключа-сироты: прямой потомок Uninstall или App Paths (HKLM x64/x86 или HKU клиента), CheckDeleteKey разрешает, доказательство
    /// подтверждено, подключей не больше 50 (до 500 только с подтверждением эксперта на этот ключ).
    /// </summary>
    public static PolicyDecision CheckOrphanKeyDelete(RegKey k, OrphanEvidence e, int subkeyCount, bool expertConfirmed, string clientSid)
    {
        var d = CheckDeleteKey(k, clientSid);
        if (!d.IsAllowed) return PolicyDecision.Deny(d.Reason, d.Rule).WithTarget(k.ToString());
        if (!IsProgramEntry(CleanupRel(k, clientSid)))
            return PolicyDecision.Deny("Удаляются только прямые подразделы Uninstall и App Paths", "reg-orphan-parent").WithTarget(k.ToString());
        if (!e.Verified)
            return PolicyDecision.Deny("Доказательство «сирота» не подтвердилось", e.Rule ?? "evidence-failed").WithTarget(k.ToString());
        if (subkeyCount < 0) return PolicyDecision.Deny("Недопустимое число подключей", "reg-bad-count").WithTarget(k.ToString());
        if (subkeyCount <= MaxOrphanSubkeys) return PolicyDecision.Allow("reg-program-entry").WithTarget(k.ToString());
        if (subkeyCount <= MaxOrphanSubkeysExpert && expertConfirmed) return PolicyDecision.Allow("reg-tree-expert").WithTarget(k.ToString());
        return PolicyDecision.Deny("Слишком много подключей для удаления", "reg-tree-too-large").WithTarget(k.ToString());
    }

    /// <summary>Копия ключа создаётся только в HKLM\SOFTWARE\Chistovik\Quarantine\&lt;opId:N&gt;\&lt;n&gt; текущей операции.</summary>
    public static PolicyDecision CheckQuarantineDestination(RegKey dest, Guid opId, int n)
    {
        var s = dest.Segments;
        var ok = dest.Hive == RegHive.LocalMachine && n >= 1 && opId != Guid.Empty && s.Length == 5
                 && s[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)
                 && s[1].Equals("Chistovik", StringComparison.OrdinalIgnoreCase)
                 && s[2].Equals("Quarantine", StringComparison.OrdinalIgnoreCase)
                 && s[3].Equals(opId.ToString("N"), StringComparison.OrdinalIgnoreCase)
                 && s[4].Equals(n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return ok
            ? PolicyDecision.Allow("reg-quarantine-dest").WithTarget(dest.ToString())
            : PolicyDecision.Deny("Копия ключа создаётся только в Chistovik\\Quarantine текущей операции", "reg-quarantine-dest").WithTarget(dest.ToString());
    }

    static bool ValidValueName(string? name) => !string.IsNullOrEmpty(name) && name.Length <= 16383 && !name.Any(c => c < 0x20);

    /// <summary>Удаление значения из Run/RunOnce (HKLM x64/x86 или HKU клиента) с подтверждённым доказательством.</summary>
    public static PolicyDecision CheckStartupRemove(RegKey k, string valueName, OrphanEvidence e, string clientSid)
    {
        var g = ClientGate(k, clientSid);
        if (!g.IsAllowed) return g.WithTarget(k + "::" + valueName);
        var t = k + "::" + valueName;
        if (!ValidValueName(valueName)) return PolicyDecision.Deny("Недопустимое имя значения", "reg-bad-value").WithTarget(t);
        if (!IsRunParent(CleanupRel(k, clientSid)))
            return PolicyDecision.Deny("Только значения Run и RunOnce", "reg-run-keys").WithTarget(t);
        if (valueName.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase))
            return PolicyDecision.Deny("Запись защиты Windows не удаляется", "reg-protected-node").WithTarget(t);
        if (!e.Verified) return PolicyDecision.Deny("Доказательство «сирота» не подтвердилось", e.Rule ?? "evidence-failed").WithTarget(t);
        return PolicyDecision.Allow("reg-run-orphan").WithTarget(t);
    }

    /// <summary>Откат: возврат собственной удалённой записи Run/RunOnce. Только строка (String/ExpandString) до 2048 знаков.</summary>
    public static PolicyDecision CheckStartupRestore(RegKey k, string valueName, RegType type, string clientSid, int dataChars = 0)
    {
        var g = ClientGate(k, clientSid);
        var t = k + "::" + valueName;
        if (!g.IsAllowed) return g.WithTarget(t);
        if (!ValidValueName(valueName)) return PolicyDecision.Deny("Недопустимое имя значения", "reg-bad-value").WithTarget(t);
        if (!IsRunParent(CleanupRel(k, clientSid)))
            return PolicyDecision.Deny("Только значения Run и RunOnce", "reg-run-keys").WithTarget(t);
        if (type is not (RegType.String or RegType.ExpandString) || dataChars < 0 || dataChars > MaxRunDataChars)
            return PolicyDecision.Deny("Восстанавливается только строка до 2048 знаков", "reg-bad-value-type").WithTarget(t);
        return PolicyDecision.Allow("reg-run-restore").WithTarget(t);
    }

    /// <summary>Удаление значения из HKLM\...\CurrentVersion\SharedDLLs (x64 или WOW6432Node) с подтверждённым доказательством.</summary>
    public static PolicyDecision CheckSharedDllRemove(RegKey k, string valueName, OrphanEvidence e)
    {
        var t = k + "::" + valueName;
        if (k.Hive != RegHive.LocalMachine) return PolicyDecision.Deny("SharedDLLs только в HKLM", "reg-shareddll-hive").WithTarget(t);
        if (!ValidValueName(valueName)) return PolicyDecision.Deny("Недопустимое имя значения", "reg-bad-value").WithTarget(t);
        if (!UnderCurrentVersion(CleanupRel(k, string.Empty), "SharedDLLs", 0))
            return PolicyDecision.Deny("Только значения SharedDLLs", "reg-shareddll-parent").WithTarget(t);
        if (!e.Verified) return PolicyDecision.Deny("Доказательство «сирота» не подтвердилось", e.Rule ?? "evidence-failed").WithTarget(t);
        return PolicyDecision.Allow("reg-shareddll-orphan").WithTarget(t);
    }

    /// <summary>Откат удаления ключа: восстанавливаются только те же два родителя (прямые подразделы Uninstall и App Paths).</summary>
    public static PolicyDecision CheckOrphanKeyRestore(RegKey k, string clientSid)
    {
        var d = CheckDeleteKey(k, clientSid);
        if (!d.IsAllowed) return PolicyDecision.Deny(d.Reason, d.Rule).WithTarget(k.ToString());
        return IsProgramEntry(CleanupRel(k, clientSid))
            ? PolicyDecision.Allow("reg-orphan-restore").WithTarget(k.ToString())
            : PolicyDecision.Deny("Восстанавливаются только прямые подразделы Uninstall и App Paths", "reg-orphan-parent").WithTarget(k.ToString());
    }
}
