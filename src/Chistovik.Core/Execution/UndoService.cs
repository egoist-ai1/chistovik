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

    /// <summary>Оба состояния «ключ есть», но хэши деревьев различаются (или хэш текущего не посчитан): содержимое чужое.</summary>
    static bool KeyContentDiffers(string current, string before)
    {
        var c = RegTreeJson.ParseKeyState(current);
        var b = RegTreeJson.ParseKeyState(before);
        if (!c.Exists || !b.Exists) return false;
        return !string.Equals(c.TreeSha256, b.TreeSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Пересчитывает состояние откатываемой операции: остались шаги done → PartiallyUndone, иначе Undone.
    /// onlyActive: трогать только операции в Done/Partial/PartiallyUndone (восстановление после обрыва), остальные не менять.
    /// </summary>
    internal static void RecomputeOriginal(ExecEnv env, Guid originalOp, bool onlyActive = false)
    {
        var after = env.Journal.Get(originalOp);
        if (after is null) return;
        if (onlyActive && after.Summary.State is not (OperationState.Done or OperationState.Partial or OperationState.PartiallyUndone)) return;
        // Откат этой операции не начинался (нет ни одного возвращённого шага): состояние не трогаем.
        if (!after.Steps.Any(s => s.State == StepState.Undone)) return;
        var remaining = after.Steps.Any(s => s.State == StepState.Done);
        env.Journal.Finish(originalOp, remaining ? OperationState.PartiallyUndone : OperationState.Undone, after.Summary.Code);
    }

    /// <summary>Отказ в доступе при чтении цели это отказ политики (как у шагов), остальное внутренняя ошибка.</summary>
    static string ReadFailureCode(Exception e) => ErrorCodes.From(e) == StepCodes.AccessDenied ? ResponseCodes.PolicyDenied : ResponseCodes.Internal;

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
            var owned = view.Steps.FirstOrDefault(s => s.Id == r.StepId);
            if (owned is null)
                return Reject(req.Id, ResponseCodes.BadRequest, "Resolutions: шаг не принадлежит операции");
            // Ключ реестра, появившийся на месте удалённого, перезаписывать нельзя: потеряются чужие данные. Допустим только Skip.
            if (owned.Kind == StepKind.RegistryKeyDelete && r.Action != ConflictAction.Skip)
                return Reject(req.Id, ResponseCodes.BadRequest, "Resolutions: для ключа реестра допустимо только Skip");
            resolutions[r.StepId] = r.Action;
        }

        // Очистка диска необратима целиком (файлы удалены, корзина пуста): откатывать нечего.
        if (view.Summary.Kind == OperationKind.Cleanup)
            return Reject(req.Id, ResponseCodes.NotRevertible, "Очистку диска вернуть нельзя: удалённые файлы не восстанавливаются");

        // Удаление программы возвращается только установкой заново: любой выполненный необратимый шаг закрывает откат всей операции.
        // Для удаления это верно и при неудаче: незавершённая попытка могла что-то удалить, откатывать нечем.
        if (view.Summary.Kind == OperationKind.Uninstall || view.Steps.Any(s => s.State == StepState.Done && !s.Revertible))
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
                return Reject(req.Id, ReadFailureCode(e), "Не удалось прочитать состояние цели: " + ErrorCodes.From(e));
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
                return Reject(req.Id, ReadFailureCode(e), "Не удалось прочитать состояние цели: " + ErrorCodes.From(e));
            }

            Plan plan;
            try
            {
                var cmp = StateComparers.For(s.Kind);
                if (cmp.IsNoChange(cur, s.BeforeJson!)) plan = Plan.Already;
                else if (cmp.IsSame(cur, s.AfterJson)) plan = Plan.Revert;
                else plan = Plan.Conflict;
                // Ключ на месте, но это не наша копия (программу поставили заново): конфликт, а не «уже откатано». Решение только Skip.
                if (plan == Plan.Already && s.Kind == StepKind.RegistryKeyDelete && KeyContentDiffers(cur, s.BeforeJson!)) plan = Plan.Conflict;
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

        // Пересчёт и при done == 0: возвращённые шаги могли появиться раньше (прерванный откат), RecomputeOriginal сам выйдет, если их нет.
        RecomputeOriginal(env, view.Summary.Id);

        var result = env.Journal.Get(rec.Id);
        return result is null ? Reject(req.Id, ResponseCodes.Internal, "Запись операции не найдена") : Responses.FromOperation(req.Id, result);
    }
}
