using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ports;

namespace Chistovik.App.ViewModels;

public sealed class JournalStepViewModel(JournalStep model)
{
    public string Title => model.Title;
    public string Detail => model.Detail;
    public string StateText => model.StateText;
    public StepState State => model.State;
    public bool IsDone => model.State is StepState.Done or StepState.Undone;
    public bool IsProblem => model.State is StepState.Failed or StepState.Denied or StepState.Pending;
}

public sealed class JournalOperationViewModel
{
    public JournalOperationViewModel(JournalOperation model)
    {
        Model = model;
        WhenText = Fmt.DateTimeText(model.When);
        AutomationName = $"{model.Title}, {WhenText}, {model.StatusText}";
    }

    public JournalOperation Model { get; }
    public string Title => Model.Title;
    public string Status => Model.StatusText;
    public string Id => Model.Id.ToString("D")[..8];
    public string WhenText { get; }
    public bool CanRollback => Model.Reversible;
    public string AutomationName { get; }
    public bool IsGood => Model.State is OperationState.Done or OperationState.Undone;
    public bool IsWarn => Model.State is OperationState.Partial or OperationState.PartiallyUndone;
    public bool IsBad => Model.State is OperationState.Failed or OperationState.Denied or OperationState.Interrupted;
}

public sealed class JournalViewModel : StatefulPageViewModel
{
    private readonly IJournalSource _source;
    private readonly IDialogService _dialogs;
    private readonly UndoFlow? _undo;
    private IReadOnlyList<JournalOperationViewModel> _operations = Array.Empty<JournalOperationViewModel>();
    private IReadOnlyList<JournalStepViewModel> _steps = Array.Empty<JournalStepViewModel>();
    private JournalOperationViewModel? _selected;

    public JournalViewModel(IJournalSource source, IChangeService? changes, IDialogService dialogs, INavigator? navigator = null)
        : base("Журнал и откат", "История изменений с шагами. Любую операцию можно проверить и откатить; если после неё что-то изменилось, спросим, что делать.", changes, navigator)
    {
        _source = source;
        _dialogs = dialogs;
        _undo = changes is null ? null : new UndoFlow(changes, dialogs);
        RollbackCommand = new RelayCommand(() => LastAction = RollbackAsync(), () => CanApply && Selected?.CanRollback == true);
        // Новые операции появляются в журнале сразу после ответа агента.
        if (changes is not null) changes.Completed += o => { if (o.Changed || o.Kind == OutcomeKind.Conflict) Post(() => _ = ReloadAsync()); };
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;
    public Task LastStepsLoad { get; private set; } = Task.CompletedTask;

    public RelayCommand RollbackCommand { get; }

    public IReadOnlyList<JournalOperationViewModel> Operations { get => _operations; private set => SetProperty(ref _operations, value); }
    public IReadOnlyList<JournalStepViewModel> Steps { get => _steps; private set => SetProperty(ref _steps, value); }

    public JournalOperationViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(RollbackHint));
            RollbackCommand.RaiseCanExecuteChanged();
            LastStepsLoad = LoadStepsAsync(value);
        }
    }

    public bool HasSelection => Selected is not null;

    public string RollbackHint => Selected is null ? "" :
        !Selected.CanRollback ? "Эту операцию откатить нельзя: она уже откачена, не выполнена или сама является откатом." :
        ViewOnly ? "Режим просмотра: откат отключён" : "";

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var keep = Selected?.Model.Id;
        var data = await _source.GetOperationsAsync();
        Operations = data.Select(x => new JournalOperationViewModel(x)).ToList();
        Selected = Operations.FirstOrDefault(x => x.Model.Id == keep) ?? Operations.FirstOrDefault();
        return Operations.Count;
    }, "Журнал пока пуст: изменений ещё не было. Операции появятся здесь после первого изменения.");

    protected override void OnCanApplyChanged()
    {
        OnPropertyChanged(nameof(RollbackHint));
        RollbackCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadStepsAsync(JournalOperationViewModel? op)
    {
        if (op is null) { Steps = Array.Empty<JournalStepViewModel>(); return; }
        try
        {
            var steps = await _source.GetStepsAsync(op.Model.Id);
            if (Selected == op) Steps = steps.Select(s => new JournalStepViewModel(s)).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Steps = Array.Empty<JournalStepViewModel>();
            SetResult(ResultKind.Error, "Не удалось прочитать шаги операции: " + e.Message, "");
        }
    }

    public async Task RollbackAsync()
    {
        if (_undo is null || !CanApply || Selected is not { CanRollback: true } op) return;

        var ok = _dialogs.Confirm(new ConfirmContent($"Откатить «{op.Title}»?",
            $"Операция от {op.WhenText}. Агент вернёт значения, которые были до неё, и проверит каждый шаг.\n\n" +
            "Если что-то успело измениться после этой операции, мы покажем список и спросим, перезаписывать это или оставить.",
            "Откатить"));
        if (!ok) return;

        IsBusy = true;
        try
        {
            var outcome = await _undo.RunAsync(op.Model.Id);
            ShowOutcome(outcome, undoable: false);
            if (outcome.Changed) await ReloadAsync();
        }
        finally { IsBusy = false; }
    }
}
