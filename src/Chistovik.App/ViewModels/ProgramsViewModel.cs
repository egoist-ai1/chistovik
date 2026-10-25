using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;

namespace Chistovik.App.ViewModels;

public sealed class ProgramRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public ProgramRowViewModel(ProgramInfo model, Action changed)
    {
        Model = model;
        _changed = changed;
        SizeText = model.SizeBytes > 0 ? Fmt.Size(model.SizeBytes) : "—";
        DateText = Fmt.Date(model.InstalledOn);
        AutomationName = $"{model.Name}, {model.Publisher}, {SizeText}";
    }

    public ProgramInfo Model { get; }
    public string Name => Model.Name;
    public string Publisher => Model.Publisher;
    public string Version => Model.Version;
    public string Source => Model.Source;
    public string SizeText { get; }
    public string DateText { get; }
    public string AutomationName { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetProperty(ref _isChecked, value)) _changed(); }
    }
}

public sealed class ProgramsViewModel : StatefulPageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IProgramsSource _source;
    private List<ProgramRowViewModel> _all = new();
    private IReadOnlyList<ProgramRowViewModel> _items = Array.Empty<ProgramRowViewModel>();
    private string _search = "";
    private string _sortKey = "Name";
    private bool _descending;
    private int _selectedCount;
    private string _selectedSize = "";
    private bool _suppress;

    public ProgramsViewModel(IProgramsSource source, IDialogService dialogs, IChangeService? changes = null, INavigator? navigator = null)
        : base("Установленные программы",
            "Всё, что установлено на этом компьютере. Список читается из реестра без прав администратора; удалить можно программы, которые разрешает политика Чистовика.",
            changes, navigator)
    {
        _dialogs = dialogs;
        _source = source;
        SortCommand = new RelayCommand(p => SortBy(p as string ?? "Name"));
        DeleteCommand = new RelayCommand(() => LastDelete = DeleteAsync(), () => SelectedCount > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        SelectAllCommand = new RelayCommand(SelectAllVisible);
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    public RelayCommand SortCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }

    public IReadOnlyList<ProgramRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) Apply(); }
    }

    public int SelectedCount { get => _selectedCount; private set => SetProperty(ref _selectedCount, value); }
    public string SelectedSize { get => _selectedSize; private set => SetProperty(ref _selectedSize, value); }
    public string StatusText => $"Показано {Items.Count} из {_all.Count}";
    public string SelectionText => SelectedCount == 0 ? "Ничего не выбрано" : $"Выбрано: {SelectedCount} ({SelectedSize})";

    public string NameHeader => "Название" + Arrow("Name");
    public string PublisherHeader => "Издатель" + Arrow("Publisher");
    public string SizeHeader => "Размер" + Arrow("Size");
    public string DateHeader => "Дата" + Arrow("Date");

    private string Arrow(string key) => _sortKey == key ? (_descending ? " ▼" : " ▲") : "";

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var data = await _source.GetProgramsAsync();
        _all = data.Select(x => new ProgramRowViewModel(x, OnSelectionChanged)).ToList();
        Apply();
        return _all.Count;
    }, "Программы не найдены: в реестре нет записей об установленных программах.");

    private void SortBy(string key)
    {
        if (_sortKey == key) _descending = !_descending;
        else { _sortKey = key; _descending = key is "Size" or "Date"; }
        Apply();
        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(PublisherHeader));
        OnPropertyChanged(nameof(SizeHeader));
        OnPropertyChanged(nameof(DateHeader));
    }

    private void Apply()
    {
        IEnumerable<ProgramRowViewModel> q = _all;
        var s = _search.Trim();
        if (s.Length > 0)
            q = q.Where(x => x.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase)
                          || x.Publisher.Contains(s, StringComparison.CurrentCultureIgnoreCase));

        q = _sortKey switch
        {
            "Publisher" => Order(q, x => x.Publisher),
            "Size" => Order(q, x => x.Model.SizeBytes),
            "Date" => Order(q, x => x.Model.InstalledOn),
            _ => Order(q, x => x.Name),
        };
        Items = q.ToList();
        OnPropertyChanged(nameof(StatusText));
    }

    private IOrderedEnumerable<ProgramRowViewModel> Order<TKey>(IEnumerable<ProgramRowViewModel> q, Func<ProgramRowViewModel, TKey> key)
        => _descending ? q.OrderByDescending(key) : q.OrderBy(key);

    private void OnSelectionChanged()
    {
        if (_suppress) return;
        var sel = _all.Where(x => x.IsChecked).ToList();
        SelectedCount = sel.Count;
        SelectedSize = Fmt.Size(sel.Sum(x => x.Model.SizeBytes));
        OnPropertyChanged(nameof(SelectionText));
        DeleteCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    private void SetChecked(IEnumerable<ProgramRowViewModel> rows, bool value)
    {
        _suppress = true;
        foreach (var r in rows) r.IsChecked = value;
        _suppress = false;
        OnSelectionChanged();
    }

    private void ClearSelection() => SetChecked(_all, false);

    private void SelectAllVisible()
    {
        var allOn = Items.Count > 0 && Items.All(x => x.IsChecked);
        SetChecked(Items, !allOn);
    }

    /// <summary>Последнее удаление (для тестов и ожидания).</summary>
    public Task LastDelete { get; private set; } = Task.CompletedTask;

    protected override void OnCanApplyChanged() => DeleteCommand.RaiseCanExecuteChanged();

    /// <summary>Эту программу можно отдать агенту: политика пометила её изменяемой, а запись это программа компьютера (HKLM).</summary>
    private static bool CanUninstall(ProgramInfo p)
        => p.Class == Changeability.Modifiable && p.Id.StartsWith("arp:machine:", StringComparison.Ordinal);

    /// <summary>
    /// Удаляет отмеченные программы по одной через агента: после подтверждения, с итогом по каждой. Программы, которые политика
    /// не разрешает (пользовательские, системные, закрытые), пропускаются с причиной. Остановка при отказе в правах или занятом агенте.
    /// </summary>
    private async Task DeleteAsync()
    {
        var picked = _all.Where(x => x.IsChecked).Select(x => x.Model).ToList();
        if (picked.Count == 0) return;

        if (Changes is null)
        {
            SetResult(ResultKind.Warning, "Удаление недоступно: нет связи с агентом.", "");
            return;
        }
        if (ViewOnly)
        {
            SetResult(ResultKind.Warning, "Права администратора не выданы: доступен только просмотр.", "Нажмите «Запросить права», чтобы удалять программы.");
            return;
        }

        var allowed = picked.Where(CanUninstall).ToList();
        var skipped = picked.Where(p => !CanUninstall(p)).ToList();
        if (allowed.Count == 0)
        {
            var why = skipped.Select(p => $"{p.Name}: {p.ReadOnlyReason ?? "удалить отсюда нельзя"}").Take(5).ToList();
            SetResult(ResultKind.Info, "Выбранные программы отсюда удалить нельзя.", string.Join("\n", why));
            return;
        }

        var names = string.Join("\n", allowed.Take(8).Select(p => "• " + p.Name)) + (allowed.Count > 8 ? $"\n… и ещё {allowed.Count - 8}" : "");
        var skipNote = skipped.Count > 0 ? $"\n\nПропущено (отсюда удалить нельзя): {skipped.Count}." : "";
        var confirmed = _dialogs.Confirm(new ConfirmContent(
            allowed.Count == 1 ? $"Удалить «{allowed[0].Name}»?" : $"Удалить программ: {allowed.Count}?",
            "Агент с правами администратора запустит штатный деинсталлятор каждой программы без окон. " +
            "Вернуть программу откатом из журнала нельзя: только установить заново. " +
            "Личные файлы деинсталлятор обычно не трогает, но настройки программ могут пропасть.\n\n" + names + skipNote,
            "Удалить", Danger: true));
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var removed = 0;
            var problems = new List<string>();
            var notStarted = new List<string>();
            var restart = new List<string>();
            var stop = false;
            ChangeOutcome? last = null;
            foreach (var p in allowed)
            {
                if (stop) { notStarted.Add(p.Name); continue; }
                var outcome = await Changes.UninstallAsync(p.Id);
                last = outcome;
                if (outcome.RestartHint is { Length: > 0 } rh && !restart.Contains(rh)) restart.Add(rh);
                if (outcome.Kind == OutcomeKind.Success) { removed++; continue; }
                problems.Add($"{p.Name}: {outcome.Message}");
                // Нет прав, UAC отклонён, агент занят или недоступен, связь оборвалась: остальные программы не запускаем.
                // Отказ деинсталлятора одной программы (агент ответил Partial) остальные не останавливает.
                if (outcome.Kind is OutcomeKind.ViewOnly or OutcomeKind.Cancelled
                    || (outcome.Kind == OutcomeKind.Unavailable && outcome.Response?.Code != ResponseCodes.Partial))
                    stop = true;
            }

            if (allowed.Count == 1 && skipped.Count == 0 && last is not null)
                ShowOutcome(last, undoable: false);
            else
            {
                var text = removed == allowed.Count
                    ? $"Удалено программ: {removed}."
                    : $"Удалено {removed} из {allowed.Count}.";
                var hint = string.Join("\n", problems
                    .Concat(notStarted.Select(n => $"{n}: не запускалась"))
                    .Concat(skipped.Select(p => $"{p.Name}: пропущена, отсюда удалить нельзя"))
                    .Concat(restart)
                    .Take(8));
                SetResult(removed == allowed.Count ? ResultKind.Success : removed > 0 ? ResultKind.Warning : ResultKind.Error,
                    text, hint, removed > 0);
            }
        }
        finally { IsBusy = false; }

        // Список перечитывается всегда: при таймауте или обрыве связи программа могла всё же удалиться.
        SetChecked(_all, false);
        OnShown();
    }
}
