using System.IO;
using Chistovik.App.Models;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Windows;

/// <summary>Журнала ещё нет (агент ничего не менял): читатель пустой.</summary>
public sealed class NullJournalReader : IJournalReader
{
    public IReadOnlyList<OperationSummary> List(int skip, int take) => Array.Empty<OperationSummary>();
    public OperationView? Get(Guid id) => null;
    public StepRecord? LatestEffective(string targetKey) => null;
    public int SchemaVersion => 0;
}

/// <summary>
/// Журнал для интерфейса поверх <see cref="IJournalReader"/> (в работе SqliteJournalReader, режим только чтение).
/// Читатель открывается на каждый запрос: база появляется после первого изменения, а между запросами файл не держим.
/// </summary>
public sealed class JournalSource(Func<IJournalReader> readerFactory, int pageSize = 300) : IJournalSource
{
    public Task<IReadOnlyList<JournalOperation>> GetOperationsAsync() => Task.Run<IReadOnlyList<JournalOperation>>(() =>
    {
        using var lease = Open();
        return lease.Reader.List(0, pageSize).Select(ToOperation).ToList();
    });

    public Task<IReadOnlyList<JournalStep>> GetStepsAsync(Guid operationId) => Task.Run<IReadOnlyList<JournalStep>>(() =>
    {
        using var lease = Open();
        var view = lease.Reader.Get(operationId);
        return view is null ? Array.Empty<JournalStep>() : view.Steps.OrderBy(s => s.Ord).Select(ToStep).ToList();
    });

    Lease Open()
    {
        try { return new Lease(readerFactory()); }
        catch (JournalException e) when (e.Code == JournalException.NotFound) { return new Lease(new NullJournalReader()); }
    }

    sealed class Lease(IJournalReader reader) : IDisposable
    {
        public IJournalReader Reader { get; } = reader;
        public void Dispose() => (Reader as IDisposable)?.Dispose();
    }

    public static JournalOperation ToOperation(OperationSummary s)
    {
        var when = s.CreatedUtc.ToLocalTime().DateTime;
        var reversible = s.Kind != OperationKind.Undo
            && s.State is OperationState.Done or OperationState.Partial or OperationState.PartiallyUndone;
        var text = SubjectText.OfOperation(s.Kind, s.Subject);
        var subject = text.Length == 0 ? "" : ": " + text;
        return new JournalOperation(s.Id, s.Kind, ResponseText.KindName(s.Kind) + subject, when, s.State,
            ResponseText.StateName(s.State), reversible, null);
    }

    public static JournalStep ToStep(StepRecord r)
    {
        var code = ResponseText.StepCodeName(r.Code);
        var detail = SubjectText.OfTarget(r.TargetKey) + (code.Length > 0 ? " · " + code : "");
        return new JournalStep(r.Id, ResponseText.StepKindName(r.Kind), detail, r.State, ResponseText.StepStateName(r.State));
    }
}
