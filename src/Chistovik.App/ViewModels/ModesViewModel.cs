using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Catalog;

namespace Chistovik.App.ViewModels;

public sealed class ModeRowViewModel : ObservableObject
{
    private bool _canApply = true;

    public ModeRowViewModel(ModeEntry mode, PowerCatalog power, RelayCommand apply)
    {
        Mode = mode;
        ApplyCommand = apply;
        var plan = power.FindScheme(mode.PowerPlan);
        PowerText = plan?.Title ?? mode.PowerPlan;
        TweaksText = mode.Tweaks.Count == 0 ? "без изменений реестра" : $"настроек: {mode.Tweaks.Count}";
    }

    public ModeEntry Mode { get; }
    public string Id => Mode.Id;
    public string Name => Mode.Name;
    public string Tag => Mode.Tag;
    public string Description => Mode.Description;
    public string PowerText { get; }
    public string TweaksText { get; }
    public bool TouchesMachine => Mode.RequiresAdmin;
    public RelayCommand ApplyCommand { get; }

    public bool CanApply
    {
        get => _canApply;
        set => SetProperty(ref _canApply, value);
    }
}

public sealed class ModesViewModel : StatefulPageViewModel
{
    private readonly TweakCatalog _catalog;
    private readonly PowerCatalog _power;
    private readonly IDialogService _dialogs;
    private readonly UndoFlow? _undo;
    private IReadOnlyList<ModeRowViewModel> _items = Array.Empty<ModeRowViewModel>();
    private Guid? _lastOperation;
    private string _lastModeName = "";

    public ModesViewModel(TweakCatalog catalog, PowerCatalog power, IChangeService? changes, IDialogService dialogs,
        INavigator? navigator = null)
        : base("Режимы", "Готовые наборы настроек и схема питания одним нажатием. Весь режим откатывается целиком.", changes, navigator)
    {
        _catalog = catalog;
        _power = power;
        _dialogs = dialogs;
        _undo = changes is null ? null : new UndoFlow(changes, dialogs);
        UndoLastCommand = new RelayCommand(() => LastAction = UndoLastAsync(), () => CanApply && _lastOperation is not null);
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public RelayCommand UndoLastCommand { get; }
    public IReadOnlyList<ModeRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }
    public bool CanUndoLast => _lastOperation is not null;
    public string UndoLastText => _lastModeName.Length == 0 ? "" : $"Откатить режим «{_lastModeName}»";

    public override Task ReloadAsync()
    {
        Items = _catalog.Modes.Select(m =>
        {
            ModeRowViewModel? row = null;
            var cmd = new RelayCommand(() => LastAction = ApplyAsync(row!.Mode), () => CanApply);
            row = new ModeRowViewModel(m, _power, cmd);
            return row;
        }).ToList();
        SetLoaded(Items.Count, "Список режимов пуст.");
        return Task.CompletedTask;
    }

    protected override void OnCanApplyChanged()
    {
        foreach (var r in Items) { r.CanApply = CanApply; r.ApplyCommand.RaiseCanExecuteChanged(); }
        UndoLastCommand.RaiseCanExecuteChanged();
    }

    public async Task ApplyAsync(ModeEntry mode)
    {
        if (Changes is null || !CanApply) return;
        if (!_dialogs.Confirm(new ConfirmContent($"Включить режим «{mode.Name}»?", Describe(mode), "Включить", Danger: mode.RequiresAdmin)))
            return;

        IsBusy = true;
        try
        {
            var outcome = await Changes.ApplyModeAsync(mode.Id);
            ShowOutcome(outcome);
            if (outcome.Changed && outcome.OperationId is { } id)
            {
                _lastOperation = id;
                _lastModeName = mode.Name;
                OnPropertyChanged(nameof(CanUndoLast));
                OnPropertyChanged(nameof(UndoLastText));
                UndoLastCommand.RaiseCanExecuteChanged();
            }
        }
        finally { IsBusy = false; }
    }

    private async Task UndoLastAsync()
    {
        if (_undo is null || _lastOperation is not { } id) return;
        IsBusy = true;
        try
        {
            var outcome = await _undo.RunAsync(id);
            ShowOutcome(outcome);
            if (outcome.Kind is OutcomeKind.Success or OutcomeKind.NoChange)
            {
                _lastOperation = null;
                _lastModeName = "";
                OnPropertyChanged(nameof(CanUndoLast));
                OnPropertyChanged(nameof(UndoLastText));
            }
        }
        finally
        {
            IsBusy = false;
            UndoLastCommand.RaiseCanExecuteChanged();
        }
    }

    public string Describe(ModeEntry mode)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(mode.Description).Append("\n\nЧто изменится:\n");
        sb.Append("• Схема питания: ").Append(_power.FindScheme(mode.PowerPlan)?.Title ?? mode.PowerPlan).Append('\n');
        foreach (var (id, on) in mode.Tweaks)
        {
            var t = _catalog.FindTweak(id);
            sb.Append("• ").Append(t?.Title ?? id).Append(on ? " — включить" : " — выключить").Append('\n');
        }
        if (mode.RequiresAdmin) sb.Append("\nЧасть настроек касается всех пользователей компьютера.");
        sb.Append("\nОткуда вернуть: раздел «Журнал и откат» или кнопка «Откатить режим» на этом экране. Уже подходящие значения пропускаются.");
        return sb.ToString();
    }
}
