using System.Text.Json;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Шаг, который не удалось построить из записи журнала или каталога: любое открытие даёт отказ.</summary>
internal sealed class RefusedStep : PlannedStep
{
    public RefusedStep(StepKind kind, string targetKey, string targetJson, string desiredJson, Refusal refusal, bool isRevert)
        : base(kind, targetKey, targetJson, desiredJson)
    {
        IsRevert = isRevert;
        PresetRefusal = refusal;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite) => OpenOutcome.Refuse(PresetRefusal!);
}

/// <summary>
/// Строит шаг из записи журнала (откат и восстановление). target_json из базы НЕ доверенный (DESIGN-0.1 §4): цель выводится заново
/// из каталога, перечисления автозагрузки или power.json и должна совпасть с записью (target_key и поля target_json).
/// Цель, которую не строит ни один планировщик 0.1, это отказ. Желаемое состояние это <paramref name="desiredJson"/>
/// (исходный before при откате, after при восстановлении).
/// </summary>
internal static class StepFactory
{
    static PlannedStep Refuse(StepKind kind, string key, string target, string desired, string code, string rule, bool isRevert)
        => new RefusedStep(kind, key, target, desired, new Refusal(code, rule), isRevert);

    public static PlannedStep Rebuild(ExecEnv env, string clientSid, StepKind kind, string targetKey, string targetJson, string? desiredJson, bool isRevert)
    {
        var desired = desiredJson ?? "{}";
        PlannedStep Bad(string rule) => Refuse(kind, targetKey, targetJson, desired, StepCodes.PolicyDenied, rule, isRevert);
        if (desiredJson is null) return Bad("journal-no-state");
        try
        {
            return kind switch
            {
                StepKind.RegistryValue => Registry(env, clientSid, targetKey, targetJson, desired, isRevert),
                StepKind.StartupApproved => Startup(env, clientSid, targetKey, targetJson, desired, isRevert),
                StepKind.ServiceStart => Service(targetKey, targetJson, desired, isRevert),
                StepKind.TaskEnabled => Task(targetKey, targetJson, desired, isRevert),
                StepKind.PowerActiveScheme => PowerActive(env, targetKey, targetJson, desired, isRevert),
                StepKind.PowerSetting => PowerSetting(env, targetKey, targetJson, desired, isRevert),
                StepKind.PowerSchemeCreate => PowerCreate(env, targetKey, targetJson, desired, isRevert),
                _ => Bad("journal-unknown-kind"),
            };
        }
        catch (InvalidDataException)
        {
            return Bad("journal-bad-payload");
        }
    }

    static bool KeyEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------- реестр ----------

    static PlannedStep Registry(ExecEnv env, string sid, string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule, string code = StepCodes.PolicyDenied) => Refuse(StepKind.RegistryValue, key, target, desired, code, rule, isRevert);
        var t = StateJson.ParseRegistryTarget(target);
        if (t.View != "64" || (t.Root != "HKLM" && t.Root != "HKU")) return Bad("reg-bad-location");
        var hive = t.Root == "HKU" ? "HKCU" : "HKLM";
        var tweak = env.Tweaks.Tweaks.FirstOrDefault(x => x.Hive == hive && KeyEq(x.Key, t.Key) && KeyEq(x.Value, t.Name));
        if (tweak is null) return Bad("reg-target-not-built-by-planner");
        if (t.Root == "HKU" && (string.IsNullOrEmpty(t.Sid) || !KeyEq(t.Sid, sid))) return Bad("reg-foreign-sid");
        if (t.Root == "HKLM" && t.Sid is not null) return Bad("reg-bad-location");
        var g = env.Gate.ResolveTweakLocation(tweak, sid);
        if (!g.IsAllowed) return new RefusedStep(StepKind.RegistryValue, key, target, desired, Refusal.From(g), isRevert);
        var loc = g.Approved!;
        if (!KeyEq(StateJson.RegistryKey(loc), key)) return Bad("journal-target-key-mismatch");
        var state = StateJson.ParseRegistry(desired);
        if (state.Exists && state.Type != (tweak.ValueType == "DWord" ? RegType.DWord : RegType.String)) return Bad("journal-bad-value-type");
        return new RegistryValueStep(loc, state, sid, tweak.Restart) { IsRevert = isRevert };
    }

    // ---------- автозагрузка ----------

    static PlannedStep Startup(ExecEnv env, string sid, string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule, string code = StepCodes.PolicyDenied) => Refuse(StepKind.StartupApproved, key, target, desired, code, rule, isRevert);
        var t = StateJson.ParseStartupTarget(target);
        StartupLocation? loc = (t.Root, t.Approved) switch
        {
            ("HKLM", "Run") => StartupLocation.Hklm64,
            ("HKLM", "Run32") => StartupLocation.Hklm32,
            ("HKU", "Run") => StartupLocation.Hku,
            ("HKU", "StartupFolder") => StartupLocation.UserFolder,
            ("HKLM", "StartupFolder") => StartupLocation.CommonFolder,
            _ => null,
        };
        if (loc is null) return Bad("startup-bad-target");
        if (t.Root == "HKU" && (string.IsNullOrEmpty(t.Sid) || !KeyEq(t.Sid, sid))) return Bad("reg-foreign-sid");
        if (t.Root == "HKLM" && t.Sid is not null) return Bad("startup-bad-target");
        var id = new StartupEntryId(loc.Value, t.Name);
        if (!StartupEntryId.TryParse(id.ToString(), out var parsed, out _) || parsed != id) return Bad("startup-bad-id");
        var entry = StartupEnumerator.Find(env, sid, id);
        if (entry is null) return Bad("startup-entry-missing", StepCodes.NotFound);
        // Команда могла измениться с момента записи (программа обновилась и переписала Run): это конфликт отката, а не отказ.
        // Политика всё равно проверяет ТЕКУЩУЮ команду при открытии цели (StartupStep.OpenCore).
        var commandChanged = !KeyEq(entry.CommandSha256, t.CommandSha256);
        var et = entry.Target;
        if (!KeyEq(StateJson.StartupKey(et.Root, et.Sid, et.Approved, et.Name), key)) return Bad("journal-target-key-mismatch");
        var (exists, bytes) = StateJson.ParseStartup(desired);
        RegValueState state;
        if (!exists) state = new RegValueState(false, RegType.None, null);
        else if (bytes is { Length: 12 }) state = new RegValueState(true, RegType.Binary, bytes);
        else return Bad("journal-bad-value-type");
        return new StartupStep(id, et, entry.CommandSha256, entry.ApprovedLocation, state, sid) { IsRevert = isRevert, CommandChanged = commandChanged };
    }

    // ---------- службы и задачи ----------

    static PlannedStep Service(string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule) => Refuse(StepKind.ServiceStart, key, target, desired, StepCodes.PolicyDenied, rule, isRevert);
        var t = StateJson.ParseServiceTarget(target);
        if (t.Name is null || !Rules.ServiceName.IsMatch(t.Name) || t.Name.StartsWith(' ') || t.Name.EndsWith(' ')) return Bad("service-bad-name");
        if (!KeyEq(StateJson.ServiceKey(t.Name), key)) return Bad("journal-target-key-mismatch");
        if (!ServiceStep.TryFromState(StateJson.ParseService(desired), out var mode)) return Bad("service-boot-start");
        return new ServiceStep(t.Name, mode) { IsRevert = isRevert };
    }

    static PlannedStep Task(string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule) => Refuse(StepKind.TaskEnabled, key, target, desired, StepCodes.PolicyDenied, rule, isRevert);
        var t = StateJson.ParseTaskTarget(target);
        if (t.Path is null || t.Path.Length > SetTaskEnabledRequest.MaxTaskPathLength || !Rules.TaskPath.IsMatch(t.Path)) return Bad("task-bad-path");
        foreach (var seg in t.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (seg is "." or ".." || seg.EndsWith(' ') || seg.StartsWith(' ') || seg.EndsWith('.')) return Bad("task-bad-path");
        if (!KeyEq(StateJson.TaskKey(t.Path), key)) return Bad("journal-target-key-mismatch");
        var s = StateJson.ParseTask(desired);
        if (!s.Exists) return Bad("journal-bad-state");
        return new TaskStep(t.Path, s.Enabled) { IsRevert = isRevert };
    }

    // ---------- питание ----------

    /// <summary>Запись каталога для схемы: по GUID каталога либо та, чью копию создали мы.</summary>
    static PowerSchemeEntry? SchemeEntry(ExecEnv env, Guid scheme)
    {
        var byGuid = env.PowerCatalog.Schemes.FirstOrDefault(s => s.Guid == scheme);
        if (byGuid is not null) return byGuid;
        var installed = env.Power.ListSchemes();
        foreach (var s in env.PowerCatalog.Schemes)
            if (s.CreateFromTemplate is { } tpl && OwnSchemes.Find(env, tpl, installed) == scheme) return s;
        return null;
    }

    static PlannedStep PowerActive(ExecEnv env, string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule) => Refuse(StepKind.PowerActiveScheme, key, target, desired, StepCodes.PolicyDenied, rule, isRevert);
        _ = StateJson.ParsePowerActiveTarget(target);
        if (!KeyEq(StateJson.PowerActiveKey, key)) return Bad("journal-target-key-mismatch");
        var scheme = StateJson.ParsePowerActive(desired).Scheme;
        if (scheme == Guid.Empty) return Bad("power-bad-guid");
        return new PowerActiveStep(SchemeEntry(env, scheme), scheme) { IsRevert = isRevert };
    }

    static PlannedStep PowerSetting(ExecEnv env, string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule) => Refuse(StepKind.PowerSetting, key, target, desired, StepCodes.PolicyDenied, rule, isRevert);
        var t = StateJson.ParsePowerSettingTarget(target);
        var entry = env.PowerCatalog.Settings.FirstOrDefault(s => s.Subgroup == t.Subgroup && s.Setting == t.Setting);
        if (entry is null) return Bad("power-setting-not-in-catalog");
        if (!KeyEq(StateJson.PowerSettingKey(t.Scheme, t.Subgroup, t.Setting), key)) return Bad("journal-target-key-mismatch");
        var s = StateJson.ParsePowerSetting(desired);
        return new PowerSettingStep(entry, t.Scheme, s.Ac, s.Dc) { IsRevert = isRevert };
    }

    static PlannedStep PowerCreate(ExecEnv env, string key, string target, string desired, bool isRevert)
    {
        PlannedStep Bad(string rule) => Refuse(StepKind.PowerSchemeCreate, key, target, desired, StepCodes.PolicyDenied, rule, isRevert);
        var t = StateJson.ParsePowerSchemeTarget(target);
        var entry = env.PowerCatalog.Schemes.FirstOrDefault(s => s.CreateFromTemplate == t.Template && t.Template != Guid.Empty);
        if (entry is null || t.Scheme == Guid.Empty) return Bad("power-bad-template");
        if (!KeyEq(StateJson.PowerCreateKey(t.Template), key)) return Bad("journal-target-key-mismatch");
        return new PowerCreateStep(entry, t.Scheme, StateJson.ParsePowerScheme(desired).Exists) { IsRevert = isRevert };
    }
}
