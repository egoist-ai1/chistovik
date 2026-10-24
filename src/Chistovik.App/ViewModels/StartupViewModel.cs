using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;

namespace Chistovik.App.ViewModels;

public sealed class StartupRowViewModel : ObservableObject
{
    private readonly Action<StartupRowViewModel, bool> _toggled;
    private readonly Func<bool> _canApply;
    private bool _isEnabled;
    private bool _silent;

    public StartupRowViewModel(StartupInfo model, Action<StartupRowViewModel, bool> toggled, Func<bool> canApply)
    {
        Model = model;
        _toggled = toggled;
        _canApply = canApply;
        _isEnabled = model.Enabled;
        AutomationName = $"{model.Name}, {(model.Enabled ? "включена" : "выключена")}, {model.Origin}";
    }

    public StartupInfo Model { get; }
    public string Name => Model.Name;
    public string Command => Model.Command;
    public string Origin => Model.Origin;
    public string AutomationName { get; }

    /// <summary>Переключатель доступен: запись можно менять и нет режима просмотра.</summary>
    public bool CanChange => Model.CanToggle && _canApply();

    public string ToggleHint => !Model.CanToggle ? Model.ReadOnlyReason ?? "Только чтение" : !_canApply() ? "Режим просмотра: изменения отключены" : "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            if (!_silent) _toggled(this, value);
        }
    }

    /// <summary>Вернуть переключатель без нового запроса (изменение не прошло).</summary>
    public void SetSilently(bool value)
    {
        _silent = true;
        IsEnabled = value;
        _silent = false;
    }

    public void RefreshAccess()
    {
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(ToggleHint));
    }
}

public sealed class StartupViewModel : StatefulPageViewModel
{
    private readonly IStartupSource _source;
    private IReadOnlyList<StartupRowViewModel> _items = Array.Empty<StartupRowViewModel>();

    public StartupViewModel(IStartupSource source, IChangeService? changes = null, INavigator? navigator = null)
        : base("Автозагрузка", "Что запускается при входе в Windows. Переключатель меняет запись через агента, любое изменение можно откатить в журнале.", changes, navigator)
    {
        _source = source;
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    public IReadOnlyList<StartupRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }

    public string SummaryText
    {
        get
        {
            var on = Items.Count(x => x.IsEnabled);
            return $"Включено {on} из {Items.Count}";
        }
    }

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var data = await _source.GetStartupAsync();
        Items = data.Select(x => new StartupRowViewModel(x, OnToggled, () => CanApply)).ToList();
        OnPropertyChanged(nameof(SummaryText));
        return Items.Count;
    }, "В автозагрузке ничего нет.");

    protected override void OnCanApplyChanged()
    {
        foreach (var r in Items) r.RefreshAccess();
    }

    /// <summary>Последнее переключение (для тестов и ожидания).</summary>
    public Task LastToggle { get; private set; } = Task.CompletedTask;

    private void OnToggled(StartupRowViewModel row, bool enabled) => LastToggle = ToggleAsync(row, enabled);

    private async Task ToggleAsync(StartupRowViewModel row, bool enabled)
    {
        OnPropertyChanged(nameof(SummaryText));
        if (Changes is null || !row.Model.CanToggle)
        {
            row.SetSilently(!enabled);
            OnPropertyChanged(nameof(SummaryText));
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await Changes.SetStartupAsync(row.Model.EntryId, enabled);
            ShowOutcome(outcome);
            // Выключатель показывает то, что реально стало: при отказе и ошибке возвращаем прежнее положение.
            if (outcome.Kind is not (OutcomeKind.Success or OutcomeKind.NoChange)) row.SetSilently(!enabled);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(SummaryText));
        }
    }
}
