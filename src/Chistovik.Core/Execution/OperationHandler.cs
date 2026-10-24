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
            case UndoRequest r:
                return _undo.Undo(r, client);
            default:
                return new AgentResponse(request.Id, false, ResponseCodes.UnknownOperation, "Операция не разрешена");
        }
    }

    AgentResponse Apply(AgentRequest request, string sid, OperationKind kind, string subject, Func<OperationPlan> plan)
    {
        var rec = _env.Journal.BeginOrGet(new NewOperation(_env.Ids.NewGuid(), request.Id, kind, subject, null, sid, _env.AgentVersion, _env.CatalogSha256));
        if (rec.AlreadyExisted) return Stored(request.Id, rec.Id);

        OperationPlan p;
        try
        {
            p = plan();
        }
        catch (PlanException e)
        {
            if (e.Code is StepCodes.NotFound or StepCodes.PolicyDenied)
                _env.Journal.Finish(rec.Id, OperationState.Denied, e.Code == StepCodes.NotFound ? ResponseCodes.NotFound : ResponseCodes.PolicyDenied);
            else
                _env.Journal.Finish(rec.Id, OperationState.Failed, e.Code);
            return Stored(request.Id, rec.Id);
        }

        _runner.RunPlan(rec.Id, p);
        return Stored(request.Id, rec.Id);
    }

    AgentResponse Stored(Guid requestId, Guid opId)
    {
        var view = _env.Journal.Get(opId);
        return view is null
            ? new AgentResponse(requestId, false, ResponseCodes.Internal, "Запись операции не найдена")
            : Responses.FromOperation(requestId, view);
    }
}
