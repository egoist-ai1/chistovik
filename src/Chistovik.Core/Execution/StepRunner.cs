using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

internal enum StepEnd { Done, Skipped, Failed, Denied }

internal sealed record StepOutcome(StepEnd End, string? Code);

/// <summary>Контекст шага отмены: исходный шаг журнала, ожидаемое текущее состояние (after исходного) и код успеха.</summary>
internal sealed record RevertContext(long OriginalStepId, string ExpectedCurrentJson, bool AllowAny, string SuccessCode);

/// <summary>
/// Алгоритм применения DESIGN-0.1 §4: предпроверка всех шагов (чтение), затем по шагам открытие хэндла, повторная проверка
/// шлюзом по свежему состоянию, pending, запись, чтение и проверка. На первой ошибке остановка; выполненное не откатывается.
/// Вызовы портов обёрнуты в try; вызовы журнала нет: ошибка журнала выходит наружу и оставляет pending для восстановления.
/// </summary>
internal sealed class StepRunner(ExecEnv env)
{
    public const string RevertOkCode = "ok";

    StepOutcome Terminal(Guid op, int ord, PlannedStep step, string? before, StepState state, string code, string? rule)
    {
        env.Journal.AddTerminal(op, ord, step.Kind, step.TargetKey, step.TargetJson, before, step.DesiredJson, state, code, rule, step.Restart, step.Revertible);
        var end = state switch
        {
            StepState.Skipped => StepEnd.Skipped,
            StepState.Denied => StepEnd.Denied,
            _ => StepEnd.Failed,
        };
        return new StepOutcome(end, code);
    }

    /// <summary>Выполняет один шаг: повторная проверка, pending, запись, проверка результата. Шаг отмены завершается через CompleteRevert.</summary>
    public StepOutcome Execute(Guid op, int ord, PlannedStep step, RevertContext? revert)
    {
        OpenOutcome open;
        try { open = step.Open(env, null, true); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Terminal(op, ord, step, null, StepState.Failed, ErrorCodes.From(e), "port-error");
        }
        if (open.Refusal is { } r)
            return Terminal(op, ord, step, null, r.SkipNotFound ? StepState.Skipped : StepState.Denied, r.Code, r.Rule);

        using var session = open.Session!;
        string cur;
        try { cur = session.ReadStateJson(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Terminal(op, ord, step, null, StepState.Failed, ErrorCodes.From(e), "port-error");
        }

        var cmp = StateComparers.For(step.Kind);
        bool noChange;
        var expected = true;
        try
        {
            noChange = cmp.IsNoChange(cur, step.DesiredJson);
            if (revert is { AllowAny: false }) expected = cmp.IsSame(cur, revert.ExpectedCurrentJson);
        }
        catch (InvalidDataException)
        {
            return Terminal(op, ord, step, cur, StepState.Failed, StepCodes.VerifyMismatch, "bad-state");
        }

        if (noChange)
        {
            if (revert is null) return Terminal(op, ord, step, cur, StepState.Skipped, StepCodes.NoChange, null);
            var undoId = env.Journal.AddPending(op, ord, step.Kind, step.TargetKey, step.TargetJson, cur, step.DesiredJson, step.Restart, step.Revertible);
            env.Journal.CompleteRevert(undoId, revert.OriginalStepId, StepCodes.AlreadyReverted);
            return new StepOutcome(StepEnd.Done, StepCodes.AlreadyReverted);
        }
        if (!expected) return Terminal(op, ord, step, cur, StepState.Failed, StepCodes.ExternalChange, "changed-after-preflight");

        var stepId = env.Journal.AddPending(op, ord, step.Kind, step.TargetKey, step.TargetJson, cur, step.DesiredJson, step.Restart, step.Revertible);

        Exception? writeError = null;
        try { session.Write(); }
        catch (Exception e) when (e is not OperationCanceledException) { writeError = e; }

        string after;
        try { after = session.ReadStateJson(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var code = ErrorCodes.From(e);
            env.Journal.Complete(stepId, StepState.Failed, code, ErrorCodes.Text(e));
            return new StepOutcome(StepEnd.Failed, code);
        }

        bool ok, back;
        try
        {
            ok = cmp.IsNoChange(after, step.DesiredJson);
            back = !ok && cmp.IsSame(after, cur);
        }
        catch (InvalidDataException)
        {
            ok = false;
            back = false;
        }

        if (ok)
        {
            if (revert is null) env.Journal.Complete(stepId, StepState.Done, session.DoneCode, null);
            else env.Journal.CompleteRevert(stepId, revert.OriginalStepId, revert.SuccessCode);
            return new StepOutcome(StepEnd.Done, null);
        }

        var failCode = back ? (writeError is null ? StepCodes.AccessDenied : ErrorCodes.From(writeError)) : StepCodes.VerifyMismatch;
        env.Journal.Complete(stepId, StepState.Failed, failCode, writeError is null ? null : ErrorCodes.Text(writeError));
        return new StepOutcome(StepEnd.Failed, failCode);
    }

    /// <summary>Код ответа для отказа при предпроверке: политика и доступ это policy-denied, отсутствие цели это not-found.</summary>
    public static string DenialCode(IEnumerable<Refusal?> refusals)
    {
        var hard = refusals.Where(r => r is { SkipNotFound: false }).Select(r => r!.Code).ToList();
        if (hard.Any(c => c is StepCodes.PolicyDenied or StepCodes.AccessDenied)) return ResponseCodes.PolicyDenied;
        if (hard.Count > 0 && hard.All(c => c == StepCodes.NotFound)) return ResponseCodes.NotFound;
        return ResponseCodes.Internal;
    }

    /// <summary>Применение плана к уже созданной операции (BeginOrGet выполнен). Всегда завершает операцию через Finish.</summary>
    public void RunPlan(Guid opId, OperationPlan plan)
    {
        var steps = plan.Steps;
        var sim = new SimState(env.Power);
        var pre = new Refusal?[steps.Count];
        var denied = false;
        for (var i = 0; i < steps.Count; i++)
        {
            try
            {
                var o = steps[i].Open(env, sim, false);
                if (o.Refusal is { } r)
                {
                    pre[i] = r;
                    if (!r.SkipNotFound) denied = true;
                }
                else
                {
                    o.Session!.Dispose();
                    steps[i].Simulate(sim);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                pre[i] = new Refusal(ErrorCodes.From(e), "precheck-error");
                denied = true;
            }
        }

        if (denied)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var r = pre[i];
                if (r is { SkipNotFound: false })
                    env.Journal.AddTerminal(opId, i, s.Kind, s.TargetKey, s.TargetJson, null, s.DesiredJson, StepState.Denied, r.Code, r.Rule, s.Restart, s.Revertible);
                else
                    env.Journal.AddTerminal(opId, i, s.Kind, s.TargetKey, s.TargetJson, null, s.DesiredJson, StepState.Denied,
                        StepCodes.PolicyDenied, "operation-denied", s.Restart, s.Revertible);
            }
            env.Journal.Finish(opId, OperationState.Denied, DenialCode(pre));
            return;
        }

        var done = 0;
        string? failCode = null;
        var failed = false;
        for (var i = 0; i < steps.Count; i++)
        {
            var o = Execute(opId, i, steps[i], null);
            if (o.End == StepEnd.Done) done++;
            else if (o.End is StepEnd.Failed or StepEnd.Denied)
            {
                failed = true;
                failCode ??= o.Code;
                if (!plan.ContinueOnFailure) break;
            }
        }

        if (!failed) env.Journal.Finish(opId, OperationState.Done, done == 0 ? StepCodes.NoChange : null);
        else env.Journal.Finish(opId, done > 0 ? OperationState.Partial : OperationState.Failed, failCode);
    }
}
