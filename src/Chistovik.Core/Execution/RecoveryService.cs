using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Итог восстановления: сколько шагов стало done (recovered-done), skipped (recovered-not-applied), failed и сколько операций закрыто как interrupted.</summary>
public sealed record RecoveryReport(int RecoveredDone, int NotApplied, int Failed, int InterruptedOperations);

/// <summary>
/// Восстановление при старте агента до открытия канала (DESIGN-0.1 §4). Для каждого pending-шага читает текущее состояние
/// (только чтение, цель строится заново, а не берётся из журнала на веру) и сравнивает: cur ≈ after → done(recovered-done),
/// cur ≈ before → skipped(recovered-not-applied), иначе failed(external-change). Ничего не записывает в систему и не продолжает
/// операции. Операции в состоянии running закрываются как interrupted.
/// </summary>
public sealed class RecoveryService(ExecEnv env)
{
    const int PageSize = 200;

    public RecoveryReport Run()
    {
        int done = 0, skipped = 0, failed = 0;
        var views = new Dictionary<Guid, OperationView?>();

        OperationView? View(Guid id)
        {
            if (!views.TryGetValue(id, out var v)) views[id] = v = env.Journal.Get(id);
            return v;
        }

        foreach (var p in env.Journal.GetPending())
        {
            var view = View(p.OpId);
            var sid = view?.UserSid ?? string.Empty;
            var isUndo = p.OpKind == OperationKind.Undo;

            // Очистка диска необратима и не перечитывается: после обрыва состояние «до» уже не восстановить, шаг закрываем как прерванный.
            if (p.Kind is StepKind.CleanupCategory or StepKind.RecycleBinEmpty)
            {
                env.Journal.Complete(p.StepId, StepState.Failed, StepCodes.Interrupted, "recovery: cleanup interrupted");
                failed++;
                continue;
            }

            string? cur = null;
            string? failCode = null;
            string? failRule = null;
            try
            {
                var step = StepFactory.Rebuild(env, sid, p.Kind, p.TargetKey, p.TargetJson, p.AfterJson, isUndo);
                var o = step.Open(env, null, false);
                if (o.Refusal is { } r)
                {
                    failCode = r.Code;
                    failRule = r.Rule;
                }
                else
                {
                    using var session = o.Session!;
                    cur = session.ReadStateJson();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failCode = ErrorCodes.From(e);
                failRule = "recovery-port-error";
            }

            if (cur is null)
            {
                env.Journal.Complete(p.StepId, StepState.Failed, failCode ?? StepCodes.ExternalChange, failRule is null ? null : "recovery: " + failRule);
                failed++;
                continue;
            }

            var cmp = StateComparers.For(p.Kind);
            var verdict = Decide(cmp, cur, p.BeforeJson, p.AfterJson);
            switch (verdict)
            {
                case Verdict.Applied:
                    if (!isUndo)
                    {
                        env.Journal.Complete(p.StepId, StepState.Done, StepCodes.RecoveredDone, null);
                        done++;
                    }
                    else if (FindOriginal(view, p, View) is { } orig)
                    {
                        try
                        {
                            env.Journal.CompleteRevert(p.StepId, orig, StepCodes.RecoveredDone);
                            done++;
                        }
                        catch (InvalidOperationException)
                        {
                            // Исходный шаг уже не done (его закрыл другой шаг отмены или он изменён): одна плохая запись не должна останавливать агент.
                            env.Journal.Complete(p.StepId, StepState.Failed, StepCodes.ExternalChange, "recovery: original step is not done");
                            failed++;
                        }
                    }
                    else
                    {
                        env.Journal.Complete(p.StepId, StepState.Failed, StepCodes.ExternalChange, "recovery: original step not found");
                        failed++;
                    }
                    break;
                case Verdict.NotApplied:
                    env.Journal.Complete(p.StepId, StepState.Skipped, StepCodes.RecoveredNotApplied, null);
                    skipped++;
                    break;
                default:
                    env.Journal.Complete(p.StepId, StepState.Failed, StepCodes.ExternalChange, null);
                    failed++;
                    break;
            }
        }

        var interrupted = 0;
        for (var skip = 0; ; skip += PageSize)
        {
            var page = env.Journal.List(skip, PageSize);
            foreach (var s in page.Where(x => x.State == OperationState.Running))
            {
                env.Journal.Finish(s.Id, OperationState.Interrupted, null);
                interrupted++;
            }
            if (page.Count < PageSize) break;
        }
        return new RecoveryReport(done, skipped, failed, interrupted);
    }

    enum Verdict { Applied, NotApplied, Changed }

    static Verdict Decide(IStateComparer cmp, string cur, string? before, string after)
    {
        try
        {
            if (cmp.IsNoChange(cur, after)) return Verdict.Applied;
            if (before is not null && cmp.IsSame(cur, before)) return Verdict.NotApplied;
        }
        catch (InvalidDataException)
        {
        }
        return Verdict.Changed;
    }

    /// <summary>Исходный шаг для pending-шага отмены: done-шаг откатываемой операции с той же целью (последний по ord).</summary>
    static long? FindOriginal(OperationView? undoOp, PendingStep p, Func<Guid, OperationView?> view)
    {
        if (undoOp?.RevertsOpId is not { } origOp) return null;
        var orig = view(origOp);
        var step = orig?.Steps.Where(s => s.State == StepState.Done && s.Kind == p.Kind && s.TargetKey == p.TargetKey)
            .OrderByDescending(s => s.Ord).FirstOrDefault();
        return step?.Id;
    }
}
