using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Findings;
using Chistovik.Core.Policy;

namespace Chistovik.App.ViewModels;

/// <summary>Кнопка-фильтр: ключ, подпись и признак «выбрана» (форма и цвет рамки, не только цвет).</summary>
public sealed class FilterChip : ObservableObject
{
    private bool _active;

    public FilterChip(string key, string label, bool active = false)
    {
        Key = key;
        Label = label;
        _active = active;
    }

    public string Key { get; }
    public string Label { get; }
    public bool IsActive { get => _active; set => SetProperty(ref _active, value); }
    public string AutomationName => IsActive ? Label + ", выбрано" : Label;

    internal void Refresh() => OnPropertyChanged(nameof(AutomationName));
}

/// <summary>Одна строка списка «Чистки» и панель подробностей: четыре фразы, причины, риск, обратимость.</summary>
public sealed class FindingRowViewModel : ObservableObject
{
    public FindingRowViewModel(Finding finding, Action<string>? open)
    {
        Finding = finding;
        // В поиск входит и идентификатор: службу и задачу ищут по системному имени, а не только по понятному названию.
        SearchText = $"{finding.Title} {finding.Detail} {finding.Id} {AreaText} {finding.Verdict.Headline}";
        Reasons = finding.Verdict.Reasons.Select(r => new ReasonLine(r.Text, r.Evidence ?? "")).ToList();
        OpenCommand = new RelayCommand(() => open?.Invoke(finding.OpenIn), () => open is not null && finding.OpenIn.Length > 0);
    }

    public Finding Finding { get; }
    public string SearchText { get; }
    public RelayCommand OpenCommand { get; }

    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public bool HasDetail => Finding.Detail.Length > 0;

    public string WhatIsIt => Finding.Explanation.WhatIsIt;
    public string WhyNeeded => Finding.Explanation.WhyNeeded;
    public string IfRemoved => Finding.Explanation.IfRemoved;
    public string HowToUndo => Finding.Explanation.HowToUndo;

    public IReadOnlyList<ReasonLine> Reasons { get; }

    public string AreaText => AreaName(Finding.Area);
    public string AreaIcon => Finding.Area switch
    {
        FindingArea.Programs => "package",
        FindingArea.Startup => "login",
        FindingArea.Services => "server",
        _ => "clock",
    };

    public VerdictKind Kind => Finding.Verdict.Kind;
    public string VerdictText => Finding.Verdict.Headline;

    /// <summary>Каждый вердикт имеет свою форму значка и свой оттенок: различать можно и без цвета.</summary>
    public string VerdictIcon => Kind switch
    {
        VerdictKind.Remove => "trash",
        VerdictKind.Optional => "sliders",
        VerdictKind.Keep => "check-circle",
        VerdictKind.Protected => "lock",
        _ => "info",
    };

    public string VerdictHue => Kind switch
    {
        VerdictKind.Remove => "Orange",
        VerdictKind.Optional => "Blue",
        VerdictKind.Keep => "Mint",
        VerdictKind.Protected => "Slate",
        _ => "Violet",
    };

    public string RiskText => "Риск: " + Finding.Verdict.Risk switch { Risk.Low => "низкий", Risk.Medium => "средний", _ => "высокий" };

    public string ReversibilityText => Finding.Verdict.Reversibility switch
    {
        Reversibility.Undoable => "Можно вернуть из журнала",
        Reversibility.ReinstallOnly => "Вернуть можно только повторной установкой",
        Reversibility.Irreversible => "Вернуть нельзя",
        _ => "Изменение недоступно",
    };

    public string ConfidenceText => Finding.Verdict.Confidence switch
    {
        Confidence.L0 => "Подтверждено системой",
        Confidence.L1 => "Известная запись каталога",
        Confidence.L2 => "Прямое свидетельство",
        Confidence.L3 => "Оценка по признакам",
        _ => "Данных мало",
    };

    /// <summary>Программу можно удалить через агента: политика пометила её изменяемой, а запись это программа компьютера.</summary>
    public bool CanUninstall => Finding.Area == FindingArea.Programs && Finding.Class == ChangeClass.Modifiable
        && Finding.Id.StartsWith("arp:machine:", StringComparison.Ordinal);

    /// <summary>Что Чистовик может сделать с объектом сейчас.</summary>
    public string ClassText => Finding.Class switch
    {
        ChangeClass.Forbidden => "Windows и политика Чистовика не разрешают менять этот объект.",
        ChangeClass.Modifiable when CanUninstall => "Эту программу можно удалить прямо здесь: агент запустит её штатный деинсталлятор без окон.",
        ChangeClass.ReadOnly when Finding.Area == FindingArea.Programs => "Удалить эту программу отсюда нельзя, причина указана выше.",
        ChangeClass.ReadOnly when Finding.OpenIn.Length == 0 => "Здесь только просмотр.",
        ChangeClass.ReadOnly => "Только просмотр: этот объект Чистовик не меняет.",
        _ => Finding.OpenIn.Length == 0 ? "Здесь только просмотр." : $"Здесь только просмотр. Менять можно в разделе «{Finding.OpenIn}».",
    };

    public bool CanOpen => Finding.OpenIn.Length > 0;
    public string OpenText => $"Открыть раздел «{Finding.OpenIn}»";

    public string AutomationName => $"{Title}, {AreaText}, {VerdictText}";

    public static string AreaName(FindingArea area) => area switch
    {
        FindingArea.Programs => "Программы",
        FindingArea.Startup => "Автозагрузка",
        FindingArea.Services => "Службы",
        _ => "Задачи",
    };
}

public sealed record ReasonLine(string Text, string Evidence)
{
    public bool HasEvidence => Evidence.Length > 0;
}

/// <summary>
/// Экран «Чистка»: что в системе можно убрать и что лучше оставить, с объяснением по каждому объекту. Галочек и счётчиков
/// «найдено проблем» нет, ничего не выбрано заранее. Единственное действие здесь это удаление одной выбранной программы
/// через агента, с подтверждением; остальные изменения идут через разделы «Автозагрузка», «Службы» и «Задачи».
/// </summary>
public sealed class CleanupViewModel : StatefulPageViewModel
{
    private static readonly VerdictKind[] Order =
        { VerdictKind.Remove, VerdictKind.Optional, VerdictKind.Unknown, VerdictKind.Keep, VerdictKind.Protected };

    private readonly ICleanupScanner _scanner;
    private List<FindingRowViewModel> _all = new();
    private IReadOnlyList<FindingRowViewModel> _items = Array.Empty<FindingRowViewModel>();
    private FindingRowViewModel? _selected;
    private string _search = "";
    private string _area = "all";
    private string _verdict = "all";

    private readonly IDialogService? _dialogs;

    public CleanupViewModel(ICleanupScanner scanner, INavigator? navigator = null, IChangeService? changes = null,
        IDialogService? dialogs = null)
        : base("Чистка",
            "Что в системе можно убрать и что лучше оставить, с объяснением по каждому объекту. Ничего не выбирается заранее: программу можно удалить только по вашей кнопке и после подтверждения.",
            changes, navigator)
    {
        _scanner = scanner;
        _dialogs = dialogs;
        UninstallCommand = new RelayCommand(() => LastUninstall = UninstallSelectedAsync(),
            () => CanApply && _dialogs is not null && Selected is { CanUninstall: true });
        AreaChips = new[]
        {
            new FilterChip("all", "Всё", true), new FilterChip("Programs", "Программы"), new FilterChip("Startup", "Автозагрузка"),
            new FilterChip("Services", "Службы"), new FilterChip("Tasks", "Задачи"),
        };
        VerdictChips = new[]
        {
            new FilterChip("all", "Любой совет", true), new FilterChip("Remove", "Можно убрать"), new FilterChip("Optional", "На усмотрение"),
            new FilterChip("Keep", "Лучше оставить"), new FilterChip("Protected", "Защищено"), new FilterChip("Unknown", "Мало данных"),
        };
        AreaCommand = new RelayCommand(p => { _area = p as string ?? "all"; Mark(AreaChips, _area); Refilter(); });
        VerdictCommand = new RelayCommand(p => { _verdict = p as string ?? "all"; Mark(VerdictChips, _verdict); Refilter(); });
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    /// <summary>Удаление выбранной программы (кнопка в подробностях).</summary>
    public RelayCommand UninstallCommand { get; }

    /// <summary>Последнее удаление (для тестов и ожидания).</summary>
    public Task LastUninstall { get; private set; } = Task.CompletedTask;

    protected override void OnCanApplyChanged() => UninstallCommand.RaiseCanExecuteChanged();

    private async Task UninstallSelectedAsync()
    {
        var row = Selected;
        if (Changes is null || _dialogs is null || row is not { CanUninstall: true }) return;
        var f = row.Finding;

        var confirmed = _dialogs.Confirm(new ConfirmContent(
            $"Удалить «{f.Title}»?",
            "Агент с правами администратора запустит штатный деинсталлятор программы без окон. " +
            "Вернуть программу откатом из журнала нельзя: только установить заново. " +
            "Личные файлы деинсталлятор обычно не трогает, но настройки программы могут пропасть.",
            "Удалить", Danger: true));
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var outcome = await Changes.UninstallAsync(f.Id);
            ShowOutcome(outcome, undoable: false);
        }
        finally { IsBusy = false; }

        // Список перечитывается всегда: при таймауте или обрыве связи программа могла всё же удалиться.
        OnShown();
    }

    public IReadOnlyList<FilterChip> AreaChips { get; }
    public IReadOnlyList<FilterChip> VerdictChips { get; }
    public RelayCommand AreaCommand { get; }
    public RelayCommand VerdictCommand { get; }

    public IReadOnlyList<FindingRowViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public FindingRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(NoSelection));
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => _selected is not null;
    public bool NoSelection => _selected is null;
    /// <summary>Фильтры и поиск ничего не оставили (показывается только в готовом экране).</summary>
    public bool NothingMatches => Items.Count == 0;

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? "")) Refilter(); }
    }

    public string StatusText => $"Показано {Items.Count} из {_all.Count}";

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var findings = await _scanner.ScanAsync(ct);
        ct.ThrowIfCancellationRequested();
        _all = findings
            .OrderBy(f => Array.IndexOf(Order, f.Verdict.Kind))
            .ThenBy(f => f.Area)
            .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new FindingRowViewModel(f, title => Navigator?.Open(title)))
            .ToList();
        Refilter();
        return _all.Count;
    }, "Не нашлось ни программ, ни записей автозагрузки, ни служб, ни задач для разбора.");

    private static void Mark(IReadOnlyList<FilterChip> chips, string key)
    {
        foreach (var c in chips)
        {
            c.IsActive = c.Key == key;
            c.Refresh();
        }
    }

    private void Refilter()
    {
        var search = _search.Trim();
        IEnumerable<FindingRowViewModel> q = _all;
        if (_area != "all") q = q.Where(x => x.Finding.Area.ToString() == _area);
        if (_verdict != "all") q = q.Where(x => x.Kind.ToString() == _verdict);
        if (search.Length > 0) q = q.Where(x => x.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        Items = list;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NothingMatches));
        // Подробности показывают то, что выбрано; если выбранное пропало из списка, выбирается первая строка (только для чтения).
        if (_selected is null || !list.Contains(_selected)) Selected = list.FirstOrDefault();
    }
}
