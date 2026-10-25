using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Шаг закончился известным кодом журнала (in-use, timeout, win32:n): ErrorCodes превращает исключение в код шага без разбора текста.</summary>
internal sealed class StepFailureException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Чтение записи Uninstall из HKLM: факты для PackagePolicy берёт агент, а не клиент.</summary>
internal static class UninstallProbe
{
    /// <summary>Расположение подраздела ARP (Registry64; x86-представление это WOW6432Node).</summary>
    public static RegLocation Location(string arch, string key)
        => new(RegRoot.Machine, null, ArpId.KeyPath(arch, key), "DisplayName");

    static string? Text(IRegKeyHandle h, string name)
    {
        var v = h.Read(name);
        return v.Exists && v.Type is RegType.String or RegType.ExpandString && v.Data is string s && s.Length > 0 ? s : null;
    }

    static bool Flag(IRegKeyHandle h, string name)
    {
        var v = h.Read(name);
        return v.Exists && v.Type == RegType.DWord && v.Data is uint u && u != 0;
    }

    /// <summary>
    /// Открывает подраздел на чтение и собирает факты. Ключа нет: not-found; ссылка или чужой ResolvedPath: policy-denied.
    /// Хэндл закрывается до возврата (команда исполняется отдельно, ключ не удерживается).
    /// </summary>
    public static Refusal? Read(ExecEnv env, string arch, string key, out PackageFacts? facts)
    {
        facts = null;
        var loc = Location(arch, key);
        using var h = env.Registry.Open(loc, false, false);
        if (h is null) return new Refusal(StepCodes.NotFound, "program-not-found");
        if (h.IsSymbolicLink) return new Refusal(StepCodes.PolicyDenied, "reg-symlink");
        var resolved = h.ResolvedPath;
        if (string.IsNullOrEmpty(resolved) || !StepGate.SameKey(resolved, StepGate.ExpectedPath(loc)))
            return new Refusal(StepCodes.PolicyDenied, "reg-resolved-mismatch");
        facts = new PackageFacts(
            Text(h, "DisplayName") ?? string.Empty, Text(h, "Publisher"), Flag(h, "SystemComponent"), Text(h, "ParentKeyName"),
            Text(h, "ReleaseType"), Flag(h, "WindowsInstaller"), Text(h, "UninstallString"), Text(h, "QuietUninstallString"));
        return null;
    }
}

/// <summary>
/// Сессия удаления: состояние «установлена или нет» и запуск деинсталлятора. approved = null: только чтение (предпроверка, восстановление).
/// Код результата: 0 → успех, 3010 → успех с перезагрузкой (DoneCode), 1618 → in-use, срок → timeout, прочее → win32:n.
/// Успешный код при по-прежнему установленной программе это verify-mismatch.
/// </summary>
internal sealed class UninstallSession(ExecEnv env, UninstallTarget target, ApprovedUninstall? approved) : IStepSession
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    public string? DoneCode { get; private set; }

    // INSTALLSTATE_LOCAL (3), SOURCE (4), DEFAULT (5): продукт установлен.
    bool Installed()
    {
        if (target.Kind == "msi") return env.Uninstaller.QueryMsiState(target.ProductCode!) is 3 or 4 or 5;
        using var h = env.Registry.Open(UninstallProbe.Location(target.Arch, target.Key), false, false);
        return h is not null;
    }

    public string ReadStateJson() => StateJson.UninstallJson(Installed());

    public void Write()
    {
        if (approved is null) throw new InvalidOperationException("Сессия открыта без права записи");
        var c = approved.Command;
        var o = c.Kind == "msi"
            ? env.Uninstaller.RunMsi(c.ProductCode!, RunTimeout)
            : env.Uninstaller.RunExe(c.ExePath!, c.Arguments ?? string.Empty, RunTimeout);
        if (o.TimedOut) throw new StepFailureException(StepCodes.Timeout);
        switch (o.ExitCode)
        {
            case 0:
                break;
            case 3010:
                DoneCode = "win32:3010";
                break;
            case 1618:
                throw new StepFailureException(StepCodes.InUse);
            default:
                throw new StepFailureException("win32:" + unchecked((uint)o.ExitCode));
        }
        if (Installed()) throw new StepFailureException(StepCodes.VerifyMismatch);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Удаление программы из HKLM. Не откатывается (Revertible=false). Каждое открытие заново читает запись Uninstall и проверяет её
/// шлюзом: решение принимается по свежим фактам, а не по плану. RecoveryOnly: шаг из журнала, только чтение состояния.
/// </summary>
internal sealed class UninstallStep : PlannedStep
{
    readonly UninstallTarget _target;

    public UninstallStep(UninstallTarget target)
        : base(StepKind.UninstallRun, StateJson.UninstallKey(target.Arch, target.Key), StateJson.UninstallTargetJson(target),
            StateJson.UninstallJson(false))
    {
        _target = target;
        Revertible = false;
    }

    /// <summary>Шаг построен из записи журнала для восстановления: состояние читается, запись невозможна.</summary>
    public bool RecoveryOnly { get; init; }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        if (RecoveryOnly) return OpenOutcome.Ok(new UninstallSession(env, _target, null));

        var refusal = UninstallProbe.Read(env, _target.Arch, _target.Key, out var facts);
        if (refusal is not null) return OpenOutcome.Refuse(refusal);

        var g = env.Gate.CheckUninstall(ArpId.Make(_target.Arch, _target.Key), facts!, env.Security);
        if (!g.IsAllowed) return OpenOutcome.Refuse(g);

        var c = g.Approved!.Command;
        if (c.Kind != _target.Kind || !string.Equals(c.ProductCode, _target.ProductCode, StringComparison.OrdinalIgnoreCase))
            return OpenOutcome.Refuse(StepCodes.PolicyDenied, "uninstall-target-changed");
        return OpenOutcome.Ok(new UninstallSession(env, _target, g.Approved));
    }
}

internal static class UninstallPlanner
{
    public static OperationPlan Plan(ExecEnv env, UninstallRequest r)
    {
        if (!ArpId.TryParse(r.FindingId, out var scope, out var arch, out var key) || scope != "machine")
            throw new PlanException(StepCodes.PolicyDenied, "FindingId: допустимы только программы HKLM");
        var id = ArpId.Make(arch, key);

        var refusal = UninstallProbe.Read(env, arch, key, out var facts);
        if (refusal is { Code: StepCodes.NotFound }) throw new PlanException(StepCodes.NotFound, "Программа не найдена в списке установленных");
        if (refusal is not null) return Refused(id, arch, key, refusal);

        var g = env.Gate.CheckUninstall(id, facts!, env.Security);
        if (!g.IsAllowed) return Refused(id, arch, key, Refusal.From(g));

        var c = g.Approved!.Command;
        var step = new UninstallStep(new UninstallTarget("machine", arch, key, c.Kind, c.ProductCode));
        return new OperationPlan(OperationKind.Uninstall, id.ToLowerInvariant(), [step]);
    }

    // Отказ политики фиксируется как denied-шаг журнала с правилом (как у твиков): причина видна в ответе.
    static OperationPlan Refused(string id, string arch, string key, Refusal refusal)
    {
        var step = new UninstallStep(new UninstallTarget("machine", arch, key, "exe", null)) { PresetRefusal = refusal };
        return new OperationPlan(OperationKind.Uninstall, id.ToLowerInvariant(), [step]);
    }
}
