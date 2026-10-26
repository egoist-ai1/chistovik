using System.Text.Json;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Планы твиков: цель реестра строится только из каталога. Отказ Policy при планировании сохраняется в шаге (PresetRefusal) и даёт denied.</summary>
internal static class TweakPlanner
{
    public static RegValueState Desired(TweakEntry t, bool enabled)
    {
        JsonElement? v = enabled ? t.OnValue : t.OffValue;
        if (v is null || v.Value.ValueKind == JsonValueKind.Null) return new RegValueState(false, RegType.None, null);
        return t.ValueType == "DWord"
            ? new RegValueState(true, RegType.DWord, v.Value.GetUInt32())
            : new RegValueState(true, RegType.String, v.Value.GetString() ?? string.Empty);
    }

    public static PlannedStep Step(ExecEnv env, TweakEntry tweak, bool enabled, string clientSid)
    {
        var desired = Desired(tweak, enabled);
        var g = env.Gate.ResolveTweakLocation(tweak, clientSid);
        if (g.IsAllowed) return new RegistryValueStep(g.Approved!, desired, clientSid, tweak.Restart);
        // Расположение для журнала; запись не будет выполнена: PresetRefusal проверяется первым.
        var raw = tweak.Hive == "HKCU"
            ? new RegLocation(RegRoot.User, clientSid, tweak.Key, tweak.Value)
            : new RegLocation(RegRoot.Machine, null, tweak.Key, tweak.Value);
        return new RegistryValueStep(raw, desired, clientSid, tweak.Restart) { PresetRefusal = Refusal.From(g) };
    }

    public static OperationPlan Plan(ExecEnv env, ApplyTweaksRequest request, string clientSid)
    {
        var steps = new List<PlannedStep>();
        foreach (var c in request.Changes)
        {
            var tweak = env.Tweaks.FindTweak(c.TweakId) ?? throw new PlanException(StepCodes.NotFound, "Твик не найден в каталоге");
            steps.Add(Step(env, tweak, c.Enabled, clientSid));
        }
        return new OperationPlan(OperationKind.Tweaks, string.Join(",", request.Changes.Select(c => c.TweakId)), steps);
    }
}

internal static class PowerPlanner
{
    public static string PlanId(PowerPlan p) => p switch
    {
        PowerPlan.Saver => "saver",
        PowerPlan.Bal => "bal",
        PowerPlan.High => "high",
        _ => "ult",
    };

    /// <summary>Шаги выбора плана питания. GUID берёт агент; для схемы с шаблоном создаётся наша копия, если её ещё нет.</summary>
    public static List<PlannedStep> SchemeSteps(ExecEnv env, string planId)
    {
        var entry = env.PowerCatalog.FindScheme(planId) ?? throw new PlanException(StepCodes.NotFound, "Схема не найдена в каталоге");
        if (entry.CreateFromTemplate is not { } template) return [new PowerActiveStep(entry, entry.Guid)];

        IReadOnlyList<Guid> installed;
        try { installed = env.Power.ListSchemes(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new PlanException(ErrorCodes.From(e), ErrorCodes.Text(e)); }
        if (OwnSchemes.Find(env, template, installed) is { } own) return [new PowerActiveStep(entry, own)];

        // Modern Standby (S0): скрытые схемы-шаблоны не дублируются, вместо попытки с неизвестным исходом даём понятный отказ.
        bool modernStandby;
        try { modernStandby = env.Power.IsModernStandby(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new PlanException(ErrorCodes.From(e), ErrorCodes.Text(e)); }
        if (modernStandby)
            throw new SchemeUnavailableException("Схемы нет на этом устройстве: на ноутбуках с Modern Standby доступна только «Сбалансированная»");

        var fresh = env.Ids.NewGuid();
        return [new PowerCreateStep(entry, fresh, true), new PowerActiveStep(entry, fresh)];
    }

    public static OperationPlan PlanScheme(ExecEnv env, SetPowerSchemeRequest request)
    {
        var id = PlanId(request.Plan);
        return new OperationPlan(OperationKind.PowerScheme, id, SchemeSteps(env, id));
    }

    public static OperationPlan PlanSetting(ExecEnv env, SetPowerSettingRequest request)
    {
        var entry = env.PowerCatalog.FindSetting(request.SettingId) ?? throw new PlanException(StepCodes.NotFound, "Параметр не найден в каталоге");
        uint ac, dc;
        Guid active;
        try
        {
            active = env.Power.GetActive();
            (ac, dc) = env.Power.Read(active, entry.Subgroup, entry.Setting);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new PlanException(ErrorCodes.From(e), ErrorCodes.Text(e));
        }
        return new OperationPlan(OperationKind.PowerSetting, entry.Id, [new PowerSettingStep(entry, active, request.Ac ?? ac, request.Dc ?? dc)]);
    }
}

internal static class ModePlanner
{
    /// <summary>Ключ факта ответа: схема питания режима пропущена, твики применены.</summary>
    public const string SchemeSkippedFact = "mode:power-scheme-skipped";

    /// <summary>Режим это набор твиков каталога и план питания. Одна операция, одним откатом.</summary>
    public static OperationPlan Plan(ExecEnv env, ApplyModeRequest request, string clientSid)
    {
        var mode = env.Tweaks.FindMode(request.ModeId) ?? throw new PlanException(StepCodes.NotFound, "Режим не найден в каталоге");
        var steps = new List<PlannedStep>();
        foreach (var (id, enabled) in mode.Tweaks)
        {
            var tweak = env.Tweaks.FindTweak(id) ?? throw new PlanException(StepCodes.NotFound, "Твик режима не найден в каталоге");
            steps.Add(TweakPlanner.Step(env, tweak, enabled, clientSid));
        }
        IReadOnlyList<FactEntry>? facts = null;
        try
        {
            steps.AddRange(PowerPlanner.SchemeSteps(env, mode.PowerPlan));
        }
        catch (SchemeUnavailableException) when (steps.Count > 0)
        {
            // Схема недоступна на устройстве: твики режима применяются, схема пропускается и сообщается клиенту.
            facts = [new FactEntry(SchemeSkippedFact, JsonSerializer.Serialize(new { scheme = mode.PowerPlan, reason = "modern-standby" }))];
        }
        return new OperationPlan(OperationKind.Mode, mode.Id, steps) { Facts = facts };
    }
}

internal static class ServicePlanner
{
    public static OperationPlan Plan(SetServiceStartRequest request)
        => new(OperationKind.Service, request.ServiceName, [new ServiceStep(request.ServiceName, request.Start)]);
}

internal static class TaskPlanner
{
    public static OperationPlan Plan(SetTaskEnabledRequest request)
        => new(OperationKind.Task, request.TaskPath, [new TaskStep(request.TaskPath, request.Enabled)]);
}
