namespace Chistovik.App.Services.Changes;

/// <summary>
/// Откат операции в два хода: сначала без решений; если агент нашёл конфликты, показываем окно с выбором
/// Overwrite/Skip по каждому и повторяем с решениями. Ничего не пишется, пока пользователь не ответил.
/// </summary>
public sealed class UndoFlow(IChangeService changes, IDialogService dialogs)
{
    public async Task<ChangeOutcome> RunAsync(Guid operationId, CancellationToken ct = default)
    {
        var first = await changes.UndoAsync(operationId, null, ct);
        if (first.Kind != OutcomeKind.Conflict || first.Conflicts is not { Count: > 0 } conflicts) return first;

        var picked = dialogs.ResolveConflicts(conflicts);
        if (picked is null)
            return new ChangeOutcome(OutcomeKind.Cancelled, "Откат отменён: ничего не изменено.", operationId);

        // Агенту нужен ответ по каждому конфликту; пропущенные считаем отказом (Skip).
        var byStep = picked.ToDictionary(x => x.StepId);
        var all = conflicts.Select(c => byStep.TryGetValue(c.StepId, out var r)
            ? r
            : new Core.Ipc.UndoResolution { StepId = c.StepId, Action = Core.Ipc.ConflictAction.Skip }).ToList();
        return await changes.UndoAsync(operationId, all, ct);
    }
}
