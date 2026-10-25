using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Microsoft.Data.Sqlite;

namespace Chistovik.Core.Execution;

/// <summary>
/// Обработчик операций агента (DESIGN-0.1 §3, §4). Операции строго последовательны (SemaphoreSlim(1)). Повтор запроса с тем же
/// Id возвращает сохранённый результат: операция создаётся BeginOrGet до чтения плана, повтор не выполняется заново.
/// Запрос уже прошёл Validate; цель каждого шага строится из каталога или собственного перечисления агента, не из запроса.
/// </summary>
public sealed class OperationHandler : IOperationHandler
{
    static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(30);

    readonly ExecEnv _env;
    readonly StepRunner _runner;
    readonly UndoService _undo;
    readonly SemaphoreSlim _gate = new(1, 1);

    public OperationHandler(ExecEnv env)
    {
        _env = env;
        _runner = new StepRunner(env);
        _undo = new UndoService(env, _runner);
    }

    public async Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(BusyTimeout, ct).ConfigureAwait(false))
            return new AgentResponse(request.Id, false, ResponseCodes.Busy, "Агент занят другой операцией");
        try
        {
            return Handle(request, client);
        }
        catch (JournalException e)
        {
            return new AgentResponse(request.Id, false,
                e.Code == JournalException.NewerSchema ? ResponseCodes.JournalNewerSchema : ResponseCodes.JournalUnavailable, "Журнал недоступен");
        }
        catch (SqliteException)
        {
            return new AgentResponse(request.Id, false, ResponseCodes.JournalUnavailable, "Журнал недоступен");
        }
        finally
        {
            _gate.Release();
        }
    }

    AgentResponse Handle(AgentRequest request, ClientContext client)
    {
        var sid = client.UserSid;
        if (string.IsNullOrWhiteSpace(sid))
            return new AgentResponse(request.Id, false, ResponseCodes.BadRequest, "Нет SID клиента");

        switch (request)
        {
            case ApplyTweaksRequest r:
                return Apply(r, sid, OperationKind.Tweaks, string.Join(",", r.Changes.Select(c => c.TweakId)), () => TweakPlanner.Plan(_env, r, sid));
            case ApplyModeRequest r:
                return Apply(r, sid, OperationKind.Mode, r.ModeId, () => ModePlanner.Plan(_env, r, sid));
            case SetStartupEntryRequest r:
                return Apply(r, sid, OperationKind.Startup, r.EntryId, () => StartupPlanner.Plan(_env, sid, r.EntryId, r.Enabled));
            case SetServiceStartRequest r:
                return Apply(r, sid, OperationKind.Service, r.ServiceName, () => ServicePlanner.Plan(r));
            case SetTaskEnabledRequest r:
                return Apply(r, sid, OperationKind.Task, r.TaskPath, () => TaskPlanner.Plan(r));
            case SetPowerSchemeRequest r:
                return Apply(r, sid, OperationKind.PowerScheme, PowerPlanner.PlanId(r.Plan), () => PowerPlanner.PlanScheme(_env, r));
            case SetPowerSettingRequest r:
                return Apply(r, sid, OperationKind.PowerSetting, r.SettingId, () => PowerPlanner.PlanSetting(_env, r));
            case UninstallRequest r:
                return Apply(r, sid, OperationKind.Uninstall, r.FindingId.ToLowerInvariant(), () => UninstallPlanner.Plan(_env, r));
            case CleanCategoriesRequest { MeasureOnly: true } r:
                return Measure(r, sid);
            case CleanCategoriesRequest r:
                return Apply(r, sid, OperationKind.Cleanup, string.Join(",", r.Categories), () => CleanupPlanner.Plan(_env, r, sid), withFacts: true);
            case ApplyFindingsRequest { DryRun: true } r:
                return DryRunFindings(r, sid);
            case ApplyFindingsRequest r:
                return Apply(r, sid, OperationKind.Registry, FindingsSubject(r), null, planById: id => FindingsPlanner.Plan(_env, r, sid, id));
            case UndoRequest r:
                return _undo.Undo(r, client);
            default:
                return new AgentResponse(request.Id, false, ResponseCodes.UnknownOperation, "Операция не разрешена");
        }
    }

    /// <summary>Измерение очистки: в журнал не пишется, идёт под тем же семафором. Факты по категориям в Facts.</summary>
    AgentResponse Measure(CleanCategoriesRequest r, string sid)
    {
        try
        {
            return new AgentResponse(r.Id, true, ResponseCodes.Ok, Facts: CleanupPlanner.Measure(_env, r, sid));
        }
        catch (PlanException e)
        {
            return e.Code == StepCodes.NotFound
                ? new AgentResponse(r.Id, false, ResponseCodes.NotFound, e.Message)
                : new AgentResponse(r.Id, false, ResponseCodes.PolicyDenied, e.Message);
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            return new AgentResponse(r.Id, false, ResponseCodes.Internal, "Измерение не удалось");
        }
    }

    static string FindingsSubject(ApplyFindingsRequest r)
    {
        if (r.Items.Count == 0) return "findings";
        var first = r.Items[0].FindingId;
        var subject = r.Items.Count == 1 ? first : first + " +" + (r.Items.Count - 1);
        return subject.Length > 300 ? subject[..300] : subject;
    }

    /// <summary>Пробный прогон чистки реестра: что произойдёт по каждой находке. Ни реестр, ни журнал не меняются.</summary>
    AgentResponse DryRunFindings(ApplyFindingsRequest r, string sid)
    {
        try
        {
            return new AgentResponse(r.Id, true, ResponseCodes.DryRun, Facts: FindingsPlanner.DryRun(_env, r, sid));
        }
        catch (PlanException e)
        {
            return e.Code switch
            {
                StepCodes.NotFound => new AgentResponse(r.Id, false, ResponseCodes.NotFound, e.Message),
                ResponseCodes.BadRequest => new AgentResponse(r.Id, false, ResponseCodes.BadRequest, e.Message),
                StepCodes.PolicyDenied => new AgentResponse(r.Id, false, ResponseCodes.PolicyDenied, e.Message),
                _ => new AgentResponse(r.Id, false, ResponseCodes.Internal, e.Message),
            };
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            return new AgentResponse(r.Id, false, ResponseCodes.Internal, "Пробный прогон не удался");
        }
    }

    AgentResponse Apply(AgentRequest request, string sid, OperationKind kind, string subject, Func<OperationPlan>? plan, bool withFacts = false,
        Func<Guid, OperationPlan>? planById = null)
    {
        var rec = _env.Journal.BeginOrGet(new NewOperation(_env.Ids.NewGuid(), request.Id, kind, subject, null, sid, _env.AgentVersion, _env.CatalogSha256));
        if (rec.AlreadyExisted) return Stored(request.Id, rec.Id, sid);

        OperationPlan p;
        try
        {
            p = planById is not null ? planById(rec.Id) : plan!();
        }
        catch (PlanException e)
        {
            if (e.Code is StepCodes.NotFound or StepCodes.PolicyDenied or ResponseCodes.BadRequest)
                _env.Journal.Finish(rec.Id, OperationState.Denied,
                    e.Code == StepCodes.NotFound ? ResponseCodes.NotFound : e.Code == ResponseCodes.BadRequest ? ResponseCodes.BadRequest : ResponseCodes.PolicyDenied);
            else
                _env.Journal.Finish(rec.Id, OperationState.Failed, e.Code);
            return Stored(request.Id, rec.Id, sid);
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            // Порт или планировщик бросил непредвиденное: операция закрывается failed/internal, повтор с тем же Id отдаст её же, а не busy.
            _env.Journal.Finish(rec.Id, OperationState.Failed, ResponseCodes.Internal);
            return Stored(request.Id, rec.Id, sid);
        }

        try
        {
            _runner.RunPlan(rec.Id, p);
        }
        catch (Exception e) when (IsUnexpected(e))
        {
            // Незавершённые (pending) шаги остаются: их закроет RecoveryService при старте агента.
            _env.Journal.Finish(rec.Id, OperationState.Failed, ResponseCodes.Internal);
        }
        var response = Stored(request.Id, rec.Id, sid);
        if (withFacts)
            response = response with { Facts = CleanupReports.Collect(_env, rec.Id, p) };
        return response;
    }

    /// <summary>Исключения, которые не превращаются в ответ internal: отмена, недоступный журнал (обрабатывается выше) и имитация гибели процесса в тестах.</summary>
    static bool IsUnexpected(Exception e) => e is not (OperationCanceledException or JournalException or SqliteException or ProcessAbortException);

    AgentResponse Stored(Guid requestId, Guid opId, string sid)
    {
        var view = _env.Journal.Get(opId);
        if (view is null) return new AgentResponse(requestId, false, ResponseCodes.Internal, "Запись операции не найдена");
        // Чужой SID: Id запроса совпал с операцией другого пользователя; её содержимое не раскрываем.
        if (!string.Equals(view.UserSid, sid, StringComparison.OrdinalIgnoreCase))
            return new AgentResponse(requestId, false, ResponseCodes.UnknownOperation, "Операция не найдена");
        return Responses.FromOperation(requestId, view);
    }
}
