using Chistovik.Core.Catalog;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Все порты и шлюз, нужные исполнению. Собирается агентом один раз (в тестах из подставных портов).</summary>
public sealed class ExecEnv
{
    public required IRegistry Registry { get; init; }
    public required IServiceControl Services { get; init; }
    public required ITaskScheduler Tasks { get; init; }
    public required IPowerControl Power { get; init; }
    public required IUserFolders Folders { get; init; }
    public required IFileSystem Files { get; init; }
    public required IClock Clock { get; init; }
    public required IIdGenerator Ids { get; init; }
    public required IJournalWriter Journal { get; init; }
    public required StepGate Gate { get; init; }

    public string AgentVersion { get; init; } = "0.1";
    public string CatalogSha256 { get; init; } = "";

    public TweakCatalog Tweaks => Gate.Tweaks;
    public PowerCatalog PowerCatalog => Gate.Power;
}

/// <summary>Отказ при открытии цели: Code — код шага (policy-denied, not-found, access-denied, win32:n); SkipNotFound — не ошибка, шаг пропускается (схемы питания вне устройства).</summary>
internal sealed record Refusal(string Code, string? Rule, bool SkipNotFound = false)
{
    public static Refusal From<T>(GateResult<T> g) where T : class => new(g.Code ?? StepCodes.PolicyDenied, g.Rule);
}

/// <summary>Открытая цель шага: чтение текущего состояния и запись через тот же хэндл.</summary>
internal interface IStepSession : IDisposable
{
    /// <summary>Текущее состояние цели в виде payload журнала.</summary>
    string ReadStateJson();

    /// <summary>Выполняет разрешённую запись (Approved* создан шлюзом при открытии). Только для сессии, открытой на запись.</summary>
    void Write();
}

internal sealed class OpenOutcome
{
    public IStepSession? Session { get; }
    public Refusal? Refusal { get; }

    OpenOutcome(IStepSession? s, Refusal? r)
    {
        Session = s;
        Refusal = r;
    }

    public static OpenOutcome Ok(IStepSession s) => new(s, null);
    public static OpenOutcome Refuse(Refusal r) => new(null, r);
    public static OpenOutcome Refuse<T>(GateResult<T> g) where T : class => new(null, Refusal.From(g));
    public static OpenOutcome Refuse(string code, string? rule) => new(null, new Refusal(code, rule));
}

/// <summary>Ожидаемое состояние питания после предыдущих шагов операции (для предпроверки, которая ничего не меняет).</summary>
internal sealed class SimState(IPowerControl power)
{
    HashSet<Guid>? _installed;
    Guid? _active;

    public HashSet<Guid> Installed => _installed ??= [.. power.ListSchemes()];

    /// <summary>Схемы, которые создадут предыдущие шаги этой же операции (журнал о них ещё не знает).</summary>
    public HashSet<Guid> PlannedOwn { get; } = [];

    public Guid Active
    {
        get => _active ??= power.GetActive();
        set => _active = value;
    }
}

/// <summary>Шаг плана операции: цель, желаемое состояние и способ открыть цель с проверкой шлюзом по свежему состоянию.</summary>
internal abstract class PlannedStep
{
    protected PlannedStep(StepKind kind, string targetKey, string targetJson, string desiredJson, string restart = "none")
    {
        Kind = kind;
        TargetKey = targetKey;
        TargetJson = targetJson;
        DesiredJson = desiredJson;
        Restart = restart;
    }

    public StepKind Kind { get; }
    public string TargetKey { get; }
    public string TargetJson { get; }

    /// <summary>Желаемое состояние (after при применении, исходный before при откате).</summary>
    public string DesiredJson { get; }
    public string Restart { get; }

    /// <summary>Шаг откатывает собственный шаг журнала (выставляет только UndoService).</summary>
    public bool IsRevert { get; init; }

    /// <summary>Отказ, найденный уже при планировании (например, Policy запретила цель каталога). Проверяется первым.</summary>
    public Refusal? PresetRefusal { get; init; }

    /// <summary>
    /// Открывает цель и повторяет проверку шлюзом по свежему состоянию того же хэндла. forWrite=false: ничего не меняет
    /// (предпроверка), sim описывает ожидаемое состояние питания после предыдущих шагов.
    /// </summary>
    public OpenOutcome Open(ExecEnv env, SimState? sim, bool forWrite)
        => PresetRefusal is { } r ? OpenOutcome.Refuse(r) : OpenCore(env, sim, forWrite);

    protected abstract OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite);

    /// <summary>Применяет ожидаемый эффект шага к sim (питание), когда предпроверка прошла.</summary>
    public virtual void Simulate(SimState sim)
    {
    }
}

/// <summary>План операции: вид, предмет и шаги по порядку.</summary>
internal sealed class OperationPlan(OperationKind kind, string subject, IReadOnlyList<PlannedStep> steps)
{
    public OperationKind Kind { get; } = kind;
    public string Subject { get; } = subject;
    public IReadOnlyList<PlannedStep> Steps { get; } = steps;
}

/// <summary>Ошибка планирования до записи в журнал (не найдена запись автозагрузки, служба и т. п.).</summary>
internal sealed class PlanException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Сопоставление исключений портов кодам шага журнала.</summary>
internal static class ErrorCodes
{
    public static string From(Exception e) => e switch
    {
        UnauthorizedAccessException => StepCodes.AccessDenied,
        System.Security.SecurityException => StepCodes.AccessDenied,
        System.ComponentModel.Win32Exception w => w.NativeErrorCode == 5 ? StepCodes.AccessDenied : StepCodes.Win32(Math.Max(0, w.NativeErrorCode)),
        _ => StepCodes.Win32(e.HResult & 0xFFFF),
    };

    public static string Text(Exception e)
    {
        var m = e.GetType().Name + ": " + e.Message;
        return m.Length <= 512 ? m : m[..512];
    }
}

/// <summary>Наши схемы питания по журналу: копия шаблона, созданная нами и всё ещё установленная.</summary>
internal static class OwnSchemes
{
    /// <summary>GUID нашей копии по шаблону или null (в журнале нет действующего шага создания либо схемы уже нет в системе).</summary>
    public static Guid? Find(ExecEnv env, Guid template, IReadOnlyCollection<Guid> installed)
    {
        var latest = env.Journal.LatestEffective(StateJson.PowerCreateKey(template));
        if (latest is null || latest.Kind != StepKind.PowerSchemeCreate) return null;
        try
        {
            var t = StateJson.ParsePowerSchemeTarget(latest.TargetJson);
            if (t.Template != template || t.Scheme == Guid.Empty) return null;
            if (!StateJson.ParsePowerScheme(latest.AfterJson).Exists) return null;
            return installed.Contains(t.Scheme) ? t.Scheme : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Создана ли схема нами (по любому шаблону каталога).</summary>
    public static bool IsOurs(ExecEnv env, Guid scheme, IReadOnlyCollection<Guid> installed)
    {
        foreach (var s in env.PowerCatalog.Schemes)
            if (s.CreateFromTemplate is { } t && Find(env, t, installed) == scheme) return true;
        return false;
    }

    /// <summary>Журнал считает схему созданной нами независимо от того, установлена ли она (для отката шага создания).</summary>
    public static bool JournalSaysOurs(ExecEnv env, Guid template, Guid scheme)
    {
        var latest = env.Journal.LatestEffective(StateJson.PowerCreateKey(template));
        if (latest is null || latest.Kind != StepKind.PowerSchemeCreate) return false;
        try
        {
            var t = StateJson.ParsePowerSchemeTarget(latest.TargetJson);
            return t.Template == template && t.Scheme == scheme && StateJson.ParsePowerScheme(latest.AfterJson).Exists;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
