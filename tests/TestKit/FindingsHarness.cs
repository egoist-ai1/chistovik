using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Результат прогона находок через планировщик и StepRunner без OperationHandler.</summary>
public sealed record FindingsResult(Guid OpId, OperationState State, string? Code, IReadOnlyList<StepRecord> Steps);

/// <summary>Запускает ApplyFindings: план FindingsPlanner, предпроверка и выполнение StepRunner, итог из журнала.</summary>
public static class FindingsHarness
{
    public static ApplyFindingsRequest Request(TestEnv t, IEnumerable<string> ids, IEnumerable<string>? expert = null, bool dryRun = false)
        => new()
        {
            Id = t.NextRequestId(),
            Items = ids.Select(i => new FindingAction(i, FindingActionKind.Remove)).ToList(),
            DryRun = dryRun,
            ExpertConfirmations = expert?.ToList(),
        };

    public static FindingsResult Run(TestEnv t, params string[] ids) => Run(t, ids, null);

    public static FindingsResult Run(TestEnv t, IEnumerable<string> ids, IEnumerable<string>? expert)
    {
        var req = Request(t, ids, expert);
        var rec = t.Env.Journal.BeginOrGet(new NewOperation(t.Env.Ids.NewGuid(), req.Id, OperationKind.Registry, "findings", null, TestEnv.Sid, "t", "t"));
        try
        {
            var plan = FindingsPlanner.Plan(t.Env, req, TestEnv.Sid, rec.Id);
            new StepRunner(t.Env).RunPlan(rec.Id, plan);
        }
        catch (PlanException e)
        {
            t.Env.Journal.Finish(rec.Id, OperationState.Denied, e.Code);
        }
        return Result(t, rec.Id);
    }

    public static FindingsResult Result(TestEnv t, Guid opId)
    {
        var op = t.Journal.AllOperations.First(o => o.Id == opId);
        return new FindingsResult(opId, op.State, op.Code, t.Journal.AllSteps.Where(s => s.OpId == opId).OrderBy(s => s.Ord).ToList());
    }

    public static IReadOnlyList<FactEntry> DryRun(TestEnv t, params string[] ids)
        => FindingsPlanner.DryRun(t.Env, Request(t, ids, null, true), TestEnv.Sid);
}
