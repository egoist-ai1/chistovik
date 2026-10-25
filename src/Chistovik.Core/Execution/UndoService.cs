using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Откат операции (DESIGN-0.1 §4). Кандидаты это шаги done в обратном порядке. Предпроход только читает: цель каждого шага
/// строится заново через <see cref="StepFactory"/> и проверяется шлюзом (target_json из базы не доверенный). cur ≈ before →
/// already-reverted; cur ≈ after → можно вернуть; иначе конфликт. Конфликты без решений дают ответ conflict, ничего не пишется.
/// Любой отказ политики при предпроходе отклоняет откат целиком до первой записи.
/// </summary>
internal sealed class UndoService(ExecEnv env, StepRunner runner)
{
    enum Plan { Revert, Already, Conflict, NotFound }

    sealed record Item(StepRecord Original, PlannedStep Step, Plan Plan, string? Current);

    static bool SameSid(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static AgentResponse Reject(Guid id, string code, string message) => new(id, false, code, message);

    public AgentResponse Undo(UndoRequest req, ClientContext client)
    {
        var view = env.Journal.Get(req.OperationId);
        if (view is null || !SameSid(view.UserSid, client.UserSid))
            return Reject(req.Id, ResponseCodes.UnknownOperation, "Операция не найдена");
        if (view.Summary.Kind == OperationKind.Undo)
            return Reject(req.Id, ResponseCodes.BadRequest, "Откат отката запрещён");
        if (view.Summary.State == OperationState.Running)
            return Reject(req.Id, ResponseCodes.Busy, "Операция ещё выполняется");

        var resolutions = new Dictionary<long, ConflictAction>();
        foreach (var r in req.Resolutions ?? [])
        {
            if (view.Steps.All(s => s.Id != r.StepId))
                return Reject(req.Id, ResponseCodes.BadRequest, "Resolutions: шаг не принадлежит операции");
            resolutions[r.StepId] = r.Action;
        }

        // Удаление программы возвращается только установкой заново: любой выполненный необратимый шаг закрывает откат всей операции.
        if (view.Steps.Any(s => s.State == StepState.Done && !s.Revertible))
            return Reject(req.Id, ResponseCodes.NotRevertible, "Эта операция необратима: программу можно вернуть только установкой заново");

        // ---- предпроход: только чтение ----
        var sid = client.UserSid;
        var sim = new SimState(env.Power);
        var items = new List<Item>();
        foreach (var s in view.Steps.Where(x => x.State == StepState.Done).OrderByDescending(x => x.Ord))
        {
            PlannedStep planned;
            OpenOutcome o;
            try
            {
                planned = StepFactory.Rebuild(env, sid, s.Kind, s.TargetKey, s.TargetJson, s.BeforeJson, true);
                o = planned.Open(env, sim, false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Reject(req.Id, ResponseCodes.Internal, "Не удалось прочитать состояние цели: " + ErrorCodes.From(e));
            }

            if (o.Refusal is { } refusal)
            {
                if (refusal.Code == StepCodes.NotFound)
                {
                    items.Add(new Item(s, planned, Plan.NotFound, null));
                    continue;
                }
                var code = refusal.Code is StepCodes.PolicyDenied or StepCodes.AccessDenied ? ResponseCodes.PolicyDenied : ResponseCodes.Internal;
                return Reject(req.Id, code, "Откат отклонён: " + refusal.Rule);
            }

            string cur;
            try
            {
                using var session = o.Session!;
                cur = session.ReadStateJson();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Reject(req.Id, ResponseCodes.Internal, "Не удалось прочитать состояние цели: " + ErrorCodes.From(e));
            }

            Plan plan;
            try
            {
                var cmp = StateComparers.For(s.Kind);
                if (cmp.IsNoChange(cur, s.BeforeJson!)) plan = Plan.Already;
                else if (cmp.IsSame(cur, s.AfterJson)) plan = Plan.Revert;
                else plan = Plan.Conflict;
            }
            catch (InvalidDataException)
            {
                return Reject(req.Id, ResponseCodes.PolicyDenied, "Откат отклонён: journal-bad-payload");
            }

            // Программа переписала команду автозагрузки: возвращать флаг можно только по явному решению пользователя.
            if (plan == Plan.Revert && planned is StartupStep { CommandChanged: true }) plan = Plan.Conflict;

            // Для последующих шагов предпрохода считаем, что этот будет возвращён: Revert и Conflict с решением Overwrite (не Skip).
            // Питание: активная схема и удаление копии.
            if (plan == Plan.Revert || (plan == Plan.Conflict && resolutions.TryGetValue(s.Id, out var act) && act == ConflictAction.Overwrite))
                planned.Simulate(sim);
            items.Add(new Item(s, planned, plan, cur));
        }

        var unresolved = items.Where(i => i.Plan == Plan.Conflict && !resolutions.ContainsKey(i.Original.Id)).ToList();
        if (unresolved.Count > 0)
        {
            var conflicts = unresolved.Select(i =>
            {
                var latest = env.Journal.LatestEffective(i.Original.TargetKey);
                return new ConflictInfo(i.Original.Id, i.Original.Kind, StateDisplay.Of(i.Original.Kind, i.Current!),
                    latest is not null && latest.OpId != view.Summary.Id ? latest.OpId : null);
            }).ToList();
            return new AgentResponse(req.Id, false, ResponseCodes.Conflict, "Состояние изменено после операции", null, null, conflicts);
        }

        // ---- запись ----
        var rec = env.Journal.BeginOrGet(new NewOperation(env.Ids.NewGuid(), req.Id, OperationKind.Undo, view.Summary.Subject,
            view.Summary.Id, sid, env.AgentVersion, env.CatalogSha256));
        if (rec.AlreadyExisted)
        {
            var stored = env.Journal.Get(rec.Id);
            return stored is null ? Reject(req.Id, ResponseCodes.Internal, "Запись операции не найдена") : Responses.FromOperation(req.Id, stored);
        }

        var ord = 0;
        var done = 0;
        var failed = false;
        string? failCode = null;
        foreach (var i in items)
        {
            var step = i.Step;
            if (i.Plan == Plan.NotFound)
            {
                env.Journal.AddTerminal(rec.Id, ord, step.Kind, step.TargetKey, step.TargetJson, null, step.DesiredJson, StepState.Skipped,
                    StepCodes.NotFound, "undo-target-missing", step.Restart);
            }
            else if (i.Plan == Plan.Conflict && resolutions[i.Original.Id] == ConflictAction.Skip)
            {
                env.Journal.AddTerminal(rec.Id, ord, step.Kind, step.TargetKey, step.TargetJson, i.Current, step.DesiredJson, StepState.Skipped,
                    StepCodes.ExternalChange, "undo-skipped-by-user", step.Restart);
            }
            else
            {
                var overwrite = i.Plan == Plan.Conflict;
                var ctx = new RevertContext(i.Original.Id, i.Original.AfterJson, overwrite, overwrite ? StepCodes.ExternalChange : StepRunner.RevertOkCode);
                var o = runner.Execute(rec.Id, ord, step, ctx);
                if (o.End == StepEnd.Done) done++;
                else if (o.End is StepEnd.Failed or StepEnd.Denied)
                {
                    failed = true;
                    failCode = o.Code;
                    break;
                }
            }
            ord++;
        }

        if (!failed) env.Journal.Finish(rec.Id, OperationState.Done, done == 0 ? StepCodes.NoChange : null);
        else env.Journal.Finish(rec.Id, done > 0 ? OperationState.Partial : OperationState.Failed, failCode);

        if (done > 0)
        {
            var after = env.Journal.Get(view.Summary.Id);
            var remaining = after is not null && after.Steps.Any(s => s.State == StepState.Done);
            env.Journal.Finish(view.Summary.Id, remaining ? OperationState.PartiallyUndone : OperationState.Undone, view.Summary.Code);
        }

        var result = env.Journal.Get(rec.Id);
        return result is null ? Reject(req.Id, ResponseCodes.Internal, "Запись операции не найдена") : Responses.FromOperation(req.Id, result);
    }
}
