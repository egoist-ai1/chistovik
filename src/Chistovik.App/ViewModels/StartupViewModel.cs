using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;

namespace Chistovik.App.ViewModels;

public sealed class StartupRowViewModel : ObservableObject, IListRow
{
    private readonly Action<StartupRowViewModel, bool> _toggled;
    private readonly Func<bool> _canApply;
    private bool _isEnabled;
    private bool _silent;

    public StartupRowViewModel(StartupInfo model, Action<StartupRowViewModel, bool> toggled, Func<bool> canApply,
        IIconService? icons = null)
    {
        Model = model;
        AppIcon = new IconSlot(icons, model.IconSource ?? model.ExePath ?? model.Command);
        _toggled = toggled;
        _canApply = canApply;
        _isEnabled = model.Enabled;
        SearchText = $"{model.Name} {model.Command} {model.Origin}";
    }

    public StartupInfo Model { get; }
    public IconSlot AppIcon { get; }
    public string Name => Model.Name;
    public string Command => Model.Command;
    public string Origin => Model.Origin;
    public Changeability Class => Model.Class;
    public string SearchText { get; }

    /// <summary>Подсказка о возможности: «можно менять / только чтение / нельзя».</summary>
    public string ClassText => ChangeabilityText.Name(
        Model.CanToggle ? Changeability.Modifiable : Model.Class == Changeability.Forbidden ? Changeability.Forbidden : Changeability.ReadOnly);

    public string AutomationName =>
        $"{Name}, {(IsEnabled ? "включена" : "выключена")}, {Origin}, {ClassText.ToLowerInvariant()}";

    public override string ToString() => AutomationName;

    /// <summary>Переключатель доступен: запись можно менять и нет режима просмотра.</summary>
    public bool CanChange => Model.CanToggle && _canApply();

    /// <summary>Причина, по которой переключатель отключён (или пусто, если он доступен).</summary>
    public string ToggleHint
    {
        get
        {
            if (!Model.CanToggle)
            {
                var why = ChangeabilityText.WhyNot(Model.Class, false, Model.ReadOnlyReason ?? ReasonTexts.Unsure);
                return why.Length > 0 ? why : "Только чтение: " + (Model.ReadOnlyReason ?? ReasonTexts.Unsure);
            }
            return _canApply() ? "" : ChangeabilityText.ViewOnlyTip;
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(AutomationName));
            if (!_silent) _toggled(this, value);
        }
    }

    /// <summary>Переключение с клавиатуры (Enter/Space на строке): только если запись можно менять.</summary>
    public void Toggle()
    {
        if (CanChange) IsEnabled = !IsEnabled;
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

public sealed class StartupViewModel : ListPageViewModel<StartupRowViewModel>
{
    private readonly IStartupSource _source;
    private readonly IIconService? _icons;

    private static readonly SortOption[] Sorts =
    {
        new("name", "По названию"), new("state", "Сначала включённые"), new("origin", "По источнику"), new("class", "Сначала те, что можно менять"),
    };

    public StartupViewModel(IStartupSource source, IChangeService? changes = null, INavigator? navigator = null, IDialogService? dialogs = null,
        IIconService? icons = null)
        : base("Автозагрузка", "Что запускается при входе в Windows. Переключатель меняет запись через агента, любое изменение можно откатить в журнале.", Sorts, changes, navigator)
    {
        _source = source;
        _icons = icons;
        UndoDialogs = dialogs;
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    public string SummaryText
    {
        get
        {
            var on = AllRows.Count(x => x.IsEnabled);
            var free = AllRows.Count(x => x.Model.CanToggle);
            return $"Включено {on} из {AllRows.Count}; можно менять: {free}";
        }
    }

    protected override IComparer<StartupRowViewModel>? Comparer(string key) => key switch
    {
        "state" => Comparer<StartupRowViewModel>.Create((a, b) =>
        {
            var c = a.IsEnabled == b.IsEnabled ? 0 : a.IsEnabled ? -1 : 1;
            return c != 0 ? c : ByName(a, b);
        }),
        "origin" => Comparer<StartupRowViewModel>.Create((a, b) =>
        {
            var c = string.Compare(a.Origin, b.Origin, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : ByName(a, b);
        }),
        "class" => Comparer<StartupRowViewModel>.Create((a, b) =>
        {
            var c = (a.Model.CanToggle ? 0 : 1).CompareTo(b.Model.CanToggle ? 0 : 1);
            return c != 0 ? c : ByName(a, b);
        }),
        _ => Comparer<StartupRowViewModel>.Create(ByName),
    };

    private static int ByName(StartupRowViewModel a, StartupRowViewModel b)
        => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var data = await _source.GetStartupAsync(ct);
        ct.ThrowIfCancellationRequested();
        SetRows(data.Select(x => new StartupRowViewModel(x, OnToggled, () => CanApply, _icons)));
        OnPropertyChanged(nameof(SummaryText));
        return AllRows.Count;
    }, "В автозагрузке ничего нет.");

    protected override void OnCanApplyChanged()
    {
        foreach (var r in AllRows) r.RefreshAccess();
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
