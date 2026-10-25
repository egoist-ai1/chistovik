using System.Text.Json;
using System.Text.Json.Serialization;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Journal;

// Payload'ы колонок target_json, before_json, after_json (DESIGN-0.1 §1). Все состояния начинаются с "v":1.
// Перечисления хранятся строками (имена как в Ports), числа вместо имён журнал не принимает.

/// <summary>RegistryValue. Root: HKLM | HKU; View всегда "64".</summary>
public sealed record RegistryTarget(string Root, string? Sid, string View, string Key, string Name);

/// <summary>RegistryValue. Type — имя RegType; Data: DWord числом, QWord строкой, Binary base64, String строкой, MultiString массивом строк.</summary>
public sealed record RegistryState(int V, bool Exists, string? Type, JsonElement? Data);

/// <summary>StartupApproved. Approved: Run | Run32 | StartupFolder.</summary>
public sealed record StartupTarget(string Root, string? Sid, string Approved, string Name, string CommandSha256);

/// <summary>StartupApproved. Bytes — base64 от 12 байт значения StartupApproved.</summary>
public sealed record StartupState(int V, bool Exists, string? Bytes);

public sealed record ServiceTarget(string Name);

/// <summary>ServiceStart. Start — сырой тип запуска Windows (0 Boot … 4 Disabled). Delayed нужен, чтобы откат вернул «отложенный». Сравнивается Start и Delayed.</summary>
public sealed record ServiceState(int V, uint Start, bool Delayed, bool TriggerStart, bool Running);

public sealed record TaskTarget(string Path);

/// <summary>TaskEnabled. Xml сохраняется, если не больше 64 КБ. Сравнивается Exists и Enabled.</summary>
public sealed record TaskState(int V, bool Exists, bool Enabled, string? XmlSha256, string? Xml);

public sealed record PowerActiveTarget();

public sealed record PowerActiveState(int V, Guid Scheme);

public sealed record PowerSettingTarget(Guid Scheme, Guid Subgroup, Guid Setting);

public sealed record PowerSettingState(int V, uint Ac, uint Dc);

/// <summary>PowerSchemeCreate. Template — GUID шаблона, Scheme — GUID, который агент выбрал ДО выполнения.</summary>
public sealed record PowerSchemeTarget(Guid Template, Guid Scheme);

public sealed record PowerSchemeState(int V, bool Exists);

/// <summary>
/// UninstallRun. Scope всегда "machine" (HKCU агент не открывает), Arch "x64" | "x86" (представление реестра), Key — имя ключа ARP,
/// Kind "msi" | "exe", ProductCode — GUID с фигурными скобками для msi. Name — DisplayName на момент плана: после удаления записи ARP нет,
/// а журналу нужно имя для человека (не участвует в решениях агента; в старых записях отсутствует).
/// </summary>
public sealed record UninstallTarget(string Scope, string Arch, string Key, string Kind, string? ProductCode, string? Name = null);

/// <summary>UninstallRun. Installed: MSI — состояние продукта «установлен»; EXE — ключ ARP существует.</summary>
public sealed record UninstallState(int V, bool Installed);

/// <summary>CleanupCategory. Scope: machine | user, Sid — SID клиента для user (иначе null), Roots — раскрытые корни на момент плана. DESIGN C2C3 §2.2.</summary>
public sealed record CleanupTarget(int V, string Category, string Scope, string? Sid, IReadOnlyList<string> Roots, int MinAgeHours);

/// <summary>
/// CleanupCategory / RecycleBinEmpty. before: Done=false, Bytes, Files. after: Done=true, Deleted, FreedBytes, Skipped (причина → число), Failed.
/// Необратимо: Undo отвечает not-revertible.
/// </summary>
public sealed record CleanupState(
    int V, bool Done, long? Bytes = null, long? Files = null, long? Deleted = null, long? FreedBytes = null,
    IReadOnlyDictionary<string, int>? Skipped = null, int? Failed = null);

/// <summary>Доказательство «сирота», найденное агентом заново: Kind uninstall-orphan | msi-unknown | file-missing | command-unresolved.</summary>
public sealed record OrphanEvidenceJson(string Kind, IReadOnlyList<string> Paths, string? ProductCode);

/// <summary>Копия удаляемого ключа: Key внутри HKLM, File относительно %ProgramData%\Chistovik\quarantine.</summary>
public sealed record RegKeyBackup(string Key, string File);

/// <summary>RegistryKeyDelete. Root: HKLM | HKU; View: x64 | x86.</summary>
public sealed record RegKeyDeleteTarget(
    int V, string Root, string? Sid, string View, string Key, OrphanEvidenceJson Evidence, RegKeyBackup Backup, bool Expert, string? Display);

/// <summary>RegistryKeyDelete. before: Exists=true, Subkeys, Values, TreeSha256. after: Exists=false.</summary>
public sealed record RegKeyState(int V, bool Exists, int? Subkeys = null, int? Values = null, string? TreeSha256 = null);

/// <summary>StartupRemove / SharedDllRemove. Расположение значения как в RegistryTarget, состояние — RegistryState.</summary>
public sealed record ValueRemoveTarget(int V, string Root, string? Sid, string View, string Key, string Name, OrphanEvidenceJson Evidence);

/// <summary>Source-generated сериализация payload'ов: без рефлексии, лишние поля запрещены.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    WriteIndented = false)]
[JsonSerializable(typeof(RegistryTarget))]
[JsonSerializable(typeof(RegistryState))]
[JsonSerializable(typeof(StartupTarget))]
[JsonSerializable(typeof(StartupState))]
[JsonSerializable(typeof(ServiceTarget))]
[JsonSerializable(typeof(ServiceState))]
[JsonSerializable(typeof(TaskTarget))]
[JsonSerializable(typeof(TaskState))]
[JsonSerializable(typeof(PowerActiveTarget))]
[JsonSerializable(typeof(PowerActiveState))]
[JsonSerializable(typeof(PowerSettingTarget))]
[JsonSerializable(typeof(PowerSettingState))]
[JsonSerializable(typeof(PowerSchemeTarget))]
[JsonSerializable(typeof(PowerSchemeState))]
[JsonSerializable(typeof(UninstallTarget))]
[JsonSerializable(typeof(UninstallState))]
[JsonSerializable(typeof(CleanupTarget))]
[JsonSerializable(typeof(CleanupState))]
[JsonSerializable(typeof(RegKeyDeleteTarget))]
[JsonSerializable(typeof(RegKeyState))]
[JsonSerializable(typeof(ValueRemoveTarget))]
public sealed partial class JournalJson : JsonSerializerContext
{
    /// <summary>Версия формата состояний; пишется в "v".</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Закрытый список кодов шага (DESIGN-0.1 §1). win32:&lt;n&gt; формируется через <see cref="Win32"/>.</summary>
public static class StepCodes
{
    public const string NoChange = "no-change";
    public const string ExternalChange = "external-change";
    public const string PolicyDenied = "policy-denied";
    public const string NotFound = "not-found";
    public const string AccessDenied = "access-denied";
    public const string VerifyMismatch = "verify-mismatch";
    public const string RecoveredDone = "recovered-done";
    public const string RecoveredNotApplied = "recovered-not-applied";
    public const string AlreadyReverted = "already-reverted";
    /// <summary>Удаление программы вернуть нельзя: Undo ничего не трогает.</summary>
    public const string NotRevertible = "not-revertible";
    /// <summary>Деинсталлятор не уложился во время; процесс не прерывался.</summary>
    public const string Timeout = "timeout";
    /// <summary>Установщик занят другой установкой (MSI 1618) или программа запущена.</summary>
    public const string InUse = "in-use";
    public const string SkippedByRule = "skipped-by-rule";
    /// <summary>Агент заново проверил доказательство «сирота» и оно не подтвердилось.</summary>
    public const string EvidenceFailed = "evidence-failed";
    public const string RegTreeTooLarge = "reg-tree-too-large";
    public const string RegTreeHasLink = "reg-tree-has-link";
    /// <summary>Хэш копии ключа не совпал с оригиналом (или с записанным в журнале).</summary>
    public const string BackupMismatch = "backup-mismatch";
    public const string BackupMissing = "backup-missing";
    /// <summary>Необратимый шаг был прерван перестартом агента.</summary>
    public const string Interrupted = "interrupted";

    public static string Win32(int error) => "win32:" + error;

    public static bool IsKnown(string? code)
    {
        if (code is null) return false;
        if (code.StartsWith("win32:", StringComparison.Ordinal))
            return int.TryParse(code.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
        return code is NoChange or ExternalChange or PolicyDenied or NotFound or AccessDenied or VerifyMismatch
            or RecoveredDone or RecoveredNotApplied or AlreadyReverted or NotRevertible or Timeout or InUse or SkippedByRule
            or EvidenceFailed or RegTreeTooLarge or RegTreeHasLink or BackupMismatch or BackupMissing or Interrupted;
    }
}

/// <summary>
/// Сравнение состояний одного вида шага (before/after из журнала и текущее состояние, сериализованное тем же payload'ом).
/// Реализации по одной на StepKind; чистые функции без доступа к системе. Не доверяют JSON: невалидный payload
/// даёт ошибку, а не «равно».
/// </summary>
public interface IStateComparer
{
    StepKind Kind { get; }

    /// <summary>Состояния совпадают для проверки конфликта (RegistryValue: exists, type, data; Startup: точные байты; Service: start; Task: exists и enabled; Power: по таблице §1).</summary>
    bool IsSame(string stateJsonA, string stateJsonB);

    /// <summary>Достаточно для «ничего менять не нужно»: как IsSame, кроме StartupApproved, где сравнивается только класс включён/выключен.</summary>
    bool IsNoChange(string stateJsonA, string stateJsonB);
}
