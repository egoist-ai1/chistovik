namespace Chistovik.Core.Ports;

public sealed record OperationSummary(Guid Id, OperationKind Kind, string Subject, OperationState State, string? Code,
    DateTimeOffset CreatedUtc, DateTimeOffset? FinishedUtc);

public sealed record OperationView(OperationSummary Summary, Guid? RevertsOpId, string UserSid, string? Error,
    IReadOnlyList<StepRecord> Steps);

/// <summary>Результат BeginOrGet. AlreadyExisted=true: запрос с тем же request_id уже есть, выполнять заново нельзя.</summary>
public sealed record OperationRecord(Guid Id, OperationKind Kind, string Subject, OperationState State, string? Code,
    bool AlreadyExisted);

/// <summary>Новая операция. RequestId это Id запроса клиента (UNIQUE); Id создаёт агент через IIdGenerator.</summary>
public sealed record NewOperation(Guid Id, Guid? RequestId, OperationKind Kind, string Subject, Guid? RevertsOpId,
    string UserSid, string AgentVersion, string CatalogSha256);

/// <summary>
/// Шаг журнала. TargetJson, BeforeJson, AfterJson это payload из JournalModel (все начинаются с "v":1). Restart: none|explorer|logoff|reboot.
/// Revertible=false: шаг вернуть нельзя (удаление программы), Undo отвечает not-revertible (журнал v2; в v1 всегда true).
/// </summary>
public sealed record StepRecord(long Id, Guid OpId, int Ord, StepKind Kind, string TargetKey, string TargetJson,
    string? BeforeJson, string AfterJson, StepState State, string? Code, string? PolicyRule, long? RevertsStepId,
    string Restart, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, bool Revertible = true);

/// <summary>Шаг в состоянии pending, найденный при восстановлении после обрыва.</summary>
public sealed record PendingStep(long StepId, Guid OpId, OperationKind OpKind, int Ord, StepKind Kind, string TargetKey,
    string TargetJson, string? BeforeJson, string AfterJson, long? RevertsStepId);

public interface IJournalReader
{
    IReadOnlyList<OperationSummary> List(int skip, int take);
    OperationView? Get(Guid id);
    /// <summary>Последний шаг done по цели (для ChangedByOperationId в конфликтах).</summary>
    StepRecord? LatestEffective(string targetKey);
    int SchemaVersion { get; }
}

public interface IJournalWriter : IJournalReader
{
    /// <summary>Дубликат request_id → существующая операция (AlreadyExisted=true).</summary>
    OperationRecord BeginOrGet(NewOperation op);
    long AddPending(Guid op, int ord, StepKind k, string targetKey, string target, string before, string after,
        string restart = "none", bool revertible = true);
    void AddTerminal(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after,
        StepState s, string code, string? rule, string restart = "none", bool revertible = true);
    void Complete(long stepId, StepState s, string? code, string? error);
    /// <summary>Одна транзакция: шаг отмены → done, исходный шаг → undone.</summary>
    void CompleteRevert(long undoStepId, long originalStepId, string code);
    void Finish(Guid op, OperationState s, string? code);
    IReadOnlyList<PendingStep> GetPending();
    int Purge(DateTimeOffset cutoff);
}
