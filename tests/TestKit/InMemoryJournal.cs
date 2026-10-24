using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Имитация обрыва процесса: бросается из Probe журнала и из подставных портов, не перехватывается кодом исполнения.</summary>
public sealed class SimulatedCrash(string where) : Exception("Имитация обрыва: " + where)
{
    public string Where { get; } = where;
}

/// <summary>
/// Журнал в памяти с теми же правилами, что у SqliteJournal: request_id уникален, (op, ord) уникальны, Complete только из pending,
/// CompleteRevert атомарен (шаг отмены pending → done, исходный done → undone), LatestEffective это последний по id шаг done с целью,
/// Get отдаёт шаги по ord, List от новых к старым. Probe вызывается с «Метод:before» и «Метод:after»: тест бросает SimulatedCrash
/// в нужной точке (обрыв между AddPending, записью и Complete).
/// </summary>
public sealed class InMemoryJournal(IClock clock) : IJournalWriter
{
    sealed class Op
    {
        public required Guid Id { get; init; }
        public Guid? RequestId { get; init; }
        public required OperationKind Kind { get; init; }
        public required string Subject { get; init; }
        public Guid? RevertsOpId { get; init; }
        public required string UserSid { get; init; }
        public required DateTimeOffset Created { get; init; }
        public required long Seq { get; init; }
        public DateTimeOffset? Finished { get; set; }
        public OperationState State { get; set; } = OperationState.Running;
        public string? Code { get; set; }
        public string? Error { get; set; }
    }

    sealed class Step
    {
        public required long Id { get; init; }
        public required Guid OpId { get; init; }
        public required int Ord { get; init; }
        public required StepKind Kind { get; init; }
        public required string TargetKey { get; set; }
        public required string TargetJson { get; set; }
        public string? BeforeJson { get; set; }
        public required string AfterJson { get; set; }
        public StepState State { get; set; }
        public string? Code { get; set; }
        public string? Rule { get; init; }
        public long? RevertsStepId { get; set; }
        public required string Restart { get; init; }
        public required DateTimeOffset Created { get; init; }
        public DateTimeOffset Updated { get; set; }
    }

    readonly Dictionary<Guid, Op> _ops = [];
    readonly List<Step> _steps = [];
    long _nextStep = 1;
    long _seq;

    public Action<string>? Probe { get; set; }

    public int SchemaVersion => 1;

    public IReadOnlyList<StepRecord> AllSteps => _steps.Select(ToRecord).ToList();

    public IReadOnlyList<OperationSummary> AllOperations => _ops.Values.OrderBy(o => o.Seq).Select(ToSummary).ToList();

    public int AddPendingCount { get; private set; }

    void P(string name) => Probe?.Invoke(name);

    /// <summary>Подмена полей шага в журнале (имитация порчи или ручной правки базы).</summary>
    public void Tamper(long stepId, string? targetKey = null, string? targetJson = null, string? beforeJson = null, string? afterJson = null)
    {
        var s = _steps.First(x => x.Id == stepId);
        if (targetKey is not null) s.TargetKey = targetKey;
        if (targetJson is not null) s.TargetJson = targetJson;
        if (beforeJson is not null) s.BeforeJson = beforeJson;
        if (afterJson is not null) s.AfterJson = afterJson;
    }

    static OperationSummary ToSummary(Op o) => new(o.Id, o.Kind, o.Subject, o.State, o.Code, o.Created, o.Finished);

    static StepRecord ToRecord(Step s) => new(s.Id, s.OpId, s.Ord, s.Kind, s.TargetKey, s.TargetJson, s.BeforeJson, s.AfterJson, s.State, s.Code,
        s.Rule, s.RevertsStepId, s.Restart, s.Created, s.Updated);

    static void ValidRestart(string restart)
    {
        if (restart is not ("none" or "explorer" or "logoff" or "reboot")) throw new ArgumentException("restart must be none|explorer|logoff|reboot", nameof(restart));
    }

    public IReadOnlyList<OperationSummary> List(int skip, int take)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 0) throw new ArgumentOutOfRangeException(nameof(take));
        return _ops.Values.OrderByDescending(o => o.Seq).Skip(skip).Take(take).Select(ToSummary).ToList();
    }

    public OperationView? Get(Guid id)
    {
        if (!_ops.TryGetValue(id, out var o)) return null;
        var steps = _steps.Where(s => s.OpId == id).OrderBy(s => s.Ord).Select(ToRecord).ToList();
        return new OperationView(ToSummary(o), o.RevertsOpId, o.UserSid, o.Error, steps);
    }

    public StepRecord? LatestEffective(string targetKey)
    {
        var s = _steps.Where(x => x.TargetKey == targetKey && x.State == StepState.Done).OrderByDescending(x => x.Id).FirstOrDefault();
        return s is null ? null : ToRecord(s);
    }

    public OperationRecord BeginOrGet(NewOperation op)
    {
        P("BeginOrGet:before");
        if (op.RequestId is { } rid)
        {
            var existing = _ops.Values.FirstOrDefault(o => o.RequestId == rid);
            if (existing is not null)
                return new OperationRecord(existing.Id, existing.Kind, existing.Subject, existing.State, existing.Code, true);
        }
        if (_ops.ContainsKey(op.Id)) throw new InvalidOperationException("Операция с таким Id уже есть");
        _ops[op.Id] = new Op
        {
            Id = op.Id, RequestId = op.RequestId, Kind = op.Kind, Subject = op.Subject, RevertsOpId = op.RevertsOpId, UserSid = op.UserSid,
            Created = clock.UtcNow, Seq = ++_seq,
        };
        P("BeginOrGet:after");
        return new OperationRecord(op.Id, op.Kind, op.Subject, OperationState.Running, null, false);
    }

    Step Insert(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after, StepState s, string? code, string? rule, string restart)
    {
        ValidRestart(restart);
        if (!_ops.ContainsKey(op)) throw new InvalidOperationException("Операции нет");
        if (_steps.Any(x => x.OpId == op && x.Ord == ord)) throw new InvalidOperationException($"Шаг с ord={ord} уже есть в операции");
        var now = clock.UtcNow;
        var step = new Step
        {
            Id = _nextStep++, OpId = op, Ord = ord, Kind = k, TargetKey = targetKey, TargetJson = target, BeforeJson = before, AfterJson = after,
            State = s, Code = code, Rule = rule, Restart = restart, Created = now, Updated = now,
        };
        _steps.Add(step);
        return step;
    }

    public long AddPending(Guid op, int ord, StepKind k, string targetKey, string target, string before, string after, string restart = "none")
    {
        P("AddPending:before");
        var s = Insert(op, ord, k, targetKey, target, before, after, StepState.Pending, null, null, restart);
        AddPendingCount++;
        P("AddPending:after");
        return s.Id;
    }

    public void AddTerminal(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after,
        StepState s, string code, string? rule, string restart = "none")
    {
        if (s == StepState.Pending) throw new ArgumentException("AddTerminal takes a terminal state, not pending.", nameof(s));
        P("AddTerminal:before");
        Insert(op, ord, k, targetKey, target, before, after, s, code, rule, restart);
        P("AddTerminal:after");
    }

    public void Complete(long stepId, StepState s, string? code, string? error)
    {
        if (s == StepState.Pending) throw new ArgumentException("Complete takes a terminal state, not pending.", nameof(s));
        P("Complete:before");
        var step = _steps.FirstOrDefault(x => x.Id == stepId);
        if (step is null || step.State != StepState.Pending) throw new InvalidOperationException($"Step {stepId} is not pending or does not exist.");
        step.State = s;
        step.Code = code;
        step.Updated = clock.UtcNow;
        if (error is not null && _ops[step.OpId].Error is null) _ops[step.OpId].Error = error.Length <= 1024 ? error : error[..1024];
        P("Complete:after");
    }

    public void CompleteRevert(long undoStepId, long originalStepId, string code)
    {
        P("CompleteRevert:before");
        var undo = _steps.FirstOrDefault(x => x.Id == undoStepId);
        if (undo is null || undo.State != StepState.Pending) throw new InvalidOperationException($"Undo step {undoStepId} is not pending or does not exist.");
        var orig = _steps.FirstOrDefault(x => x.Id == originalStepId);
        if (orig is null || orig.State != StepState.Done) throw new InvalidOperationException($"Original step {originalStepId} is not done or does not exist.");
        var now = clock.UtcNow;
        undo.State = StepState.Done;
        undo.Code = code;
        undo.RevertsStepId = originalStepId;
        undo.Updated = now;
        orig.State = StepState.Undone;
        orig.Updated = now;
        P("CompleteRevert:after");
    }

    public void Finish(Guid op, OperationState s, string? code)
    {
        P("Finish:before");
        if (!_ops.TryGetValue(op, out var o)) throw new InvalidOperationException($"Operation {op} does not exist.");
        o.State = s;
        o.Code = code;
        o.Finished = clock.UtcNow;
        P("Finish:after");
    }

    public IReadOnlyList<PendingStep> GetPending()
        => _steps.Where(s => s.State == StepState.Pending).OrderBy(s => s.Id).Select(s =>
            new PendingStep(s.Id, s.OpId, _ops[s.OpId].Kind, s.Ord, s.Kind, s.TargetKey, s.TargetJson, s.BeforeJson, s.AfterJson, s.RevertsStepId)).ToList();

    public int Purge(DateTimeOffset cutoff)
    {
        var gone = _ops.Values.Where(o => o.State != OperationState.Running && o.Finished is { } f && f < cutoff).Select(o => o.Id).ToList();
        foreach (var id in gone)
        {
            _ops.Remove(id);
            _steps.RemoveAll(s => s.OpId == id);
        }
        return gone.Count;
    }
}
