using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Findings;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using StepResult = Chistovik.Core.Ipc.StepResult;
using StepState = Chistovik.Core.Ports.StepState;

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
    private readonly Action? _changed;
    private bool _isChecked;

    public FindingRowViewModel(Finding finding, Action<string>? open, Action? changed = null)
    {
        Finding = finding;
        _changed = changed;
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
        FindingArea.Disk => "drive",
        FindingArea.Registry => "database",
        _ => "clock",
    };

    /// <summary>Категорию диска можно очистить здесь: не защищена и агент вправе её трогать (карточки с обработчиком Windows это только просмотр).</summary>
    public bool CanClean => Finding.Area == FindingArea.Disk && Finding.Verdict.Kind != VerdictKind.Protected
        && Finding.Class == ChangeClass.Modifiable;

    /// <summary>Запись реестра с подтверждённым свидетельством сироты: её можно убрать здесь (копия сохраняется).</summary>
    public bool CanRemoveKey => Finding.Area == FindingArea.Registry && Finding.Verdict.Kind == VerdictKind.Remove;

    /// <summary>У строки есть флажок. Защищённые, сомнительные и только просматриваемые строки без флажка и без кнопки.</summary>
    public bool CanSelect => CanClean || CanRemoveKey;

    /// <summary>Отмечена для очистки диска или удаления записей реестра. Недоступная строка отметку не принимает.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && !CanSelect) return;
            if (SetProperty(ref _isChecked, value)) _changed?.Invoke();
        }
    }

    /// <summary>Идентификатор категории каталога очистки (находка диска называется «cleanup:{id}»).</summary>
    public string CategoryId => Finding.Id.StartsWith("cleanup:", StringComparison.Ordinal) ? Finding.Id["cleanup:".Length..] : Finding.Id;

    /// <summary>Размер из описания находки диска («Около 1,2 ГБ»); у остальных областей пусто.</summary>
    public string SizeText
    {
        get
        {
            if (Finding.Area != FindingArea.Disk) return "";
            var m = System.Text.RegularExpressions.Regex.Match(Finding.Detail, @"^Около\s+(\S+\s+\S+?)(?=[,.\s]|$)");
            return m.Success ? m.Groups[1].Value : "размер неизвестен";
        }
    }

    /// <summary>Можно ли вернуть сделанное: «нет» для диска, «да, 90 дней» для реестра; пусто для остальных областей.</summary>
    public string ReversibleText => Finding.Area switch
    {
        FindingArea.Disk => "нет",
        FindingArea.Registry when CanRemoveKey => "да, 90 дней",
        _ => "",
    };

    public bool HasReversibleText => ReversibleText.Length > 0;
    public string ReversiblePill => "Обратимо: " + ReversibleText;
    public string CheckName => "Выбрать: " + Title;

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
        ChangeClass.Modifiable when CanClean => "Эту категорию можно очистить прямо здесь: отметьте её флажком и нажмите «Очистить выбранные категории». Вернуть удалённое нельзя.",
        ChangeClass.Modifiable when CanRemoveKey => "Эту запись можно убрать прямо здесь: отметьте её флажком и нажмите «Удалить выбранные записи реестра». Копия сохранится на 90 дней, вернуть можно из журнала.",
        ChangeClass.ReadOnly when Finding.Area == FindingArea.Disk => "Только просмотр: эту очистку выполняют штатные средства Windows.",
        ChangeClass.ReadOnly when Finding.Area == FindingArea.Programs => "Удалить эту программу отсюда нельзя, причина указана выше.",
        ChangeClass.ReadOnly when Finding.OpenIn.Length == 0 => "Здесь только просмотр.",
        ChangeClass.ReadOnly => "Только просмотр: этот объект Чистовик не меняет.",
        _ => Finding.OpenIn.Length == 0 ? "Здесь только просмотр." : $"Здесь только просмотр. Менять можно в разделе «{Finding.OpenIn}».",
    };

    public bool CanOpen => Finding.OpenIn.Length > 0;
    public string OpenText => $"Открыть раздел «{Finding.OpenIn}»";

    /// <summary>«{название}, {область}, {совет}, {размер}»; размер есть только у находок диска.</summary>
    public string AutomationName => $"{Title}, {AreaText}, {VerdictText}" + (SizeText.Length > 0 ? ", " + SizeText : "");

    public static string AreaName(FindingArea area) => area switch
    {
        FindingArea.Programs => "Программы",
        FindingArea.Startup => "Автозагрузка",
        FindingArea.Services => "Службы",
        FindingArea.Disk => "Диск",
        FindingArea.Registry => "Реестр",
        _ => "Задачи",
    };
}

public sealed record ReasonLine(string Text, string Evidence)
{
    public bool HasEvidence => Evidence.Length > 0;
}

/// <summary>
/// Экран «Чистка»: что в системе можно убрать и что лучше оставить, с объяснением по каждому объекту. Счётчиков
/// «найдено проблем» нет, ничего не выбрано заранее. Действия здесь три, все через агента и после подтверждения:
/// удаление одной выбранной программы, очистка отмеченных категорий диска (необратимо) и удаление отмеченных записей реестра
/// (копия на 90 дней, откат из журнала). Остальные изменения идут через разделы «Автозагрузка», «Службы» и «Задачи».
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
            "Что в системе можно убрать и что лучше оставить, с объяснением по каждому объекту. Ничего не выбирается заранее: удалить, очистить или убрать запись можно только по вашей кнопке и после подтверждения.",
            changes, navigator)
    {
        _scanner = scanner;
        _dialogs = dialogs;
        UndoDialogs = dialogs;
        UninstallCommand = new RelayCommand(() => LastUninstall = UninstallSelectedAsync(),
            () => CanApply && _dialogs is not null && Selected is { CanUninstall: true });
        CleanDiskCommand = new RelayCommand(() => LastClean = CleanDiskAsync(),
            () => CanApply && _dialogs is not null && CheckedDiskCount > 0);
        ApplyRegistryCommand = new RelayCommand(() => LastRegistry = ApplyRegistryAsync(),
            () => CanApply && _dialogs is not null && CheckedRegistryCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => CheckedDiskCount + CheckedRegistryCount > 0);
        AreaChips = new[]
        {
            new FilterChip("all", "Всё", true), new FilterChip("Programs", "Программы"), new FilterChip("Startup", "Автозагрузка"),
            new FilterChip("Services", "Службы"), new FilterChip("Tasks", "Задачи"),
            new FilterChip("Disk", "Диск"), new FilterChip("Registry", "Реестр"),
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

    protected override void OnCanApplyChanged()
    {
        UninstallCommand.RaiseCanExecuteChanged();
        CleanDiskCommand.RaiseCanExecuteChanged();
        ApplyRegistryCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ActionHint));
    }

    // ----- выбор строк, очистка диска и реестр -----

    /// <summary>«Очистить выбранные категории».</summary>
    public RelayCommand CleanDiskCommand { get; }

    /// <summary>«Удалить выбранные записи реестра».</summary>
    public RelayCommand ApplyRegistryCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>Последняя очистка диска и последнее удаление записей (для тестов и ожидания).</summary>
    public Task LastClean { get; private set; } = Task.CompletedTask;

    public Task LastRegistry { get; private set; } = Task.CompletedTask;

    private int _checkedDisk;
    private int _checkedRegistry;
    private string _busyText = "";
    private bool _suppress;

    public int CheckedDiskCount { get => _checkedDisk; private set => SetProperty(ref _checkedDisk, value); }
    public int CheckedRegistryCount { get => _checkedRegistry; private set => SetProperty(ref _checkedRegistry, value); }
    public string DiskSelectionText => CheckedDiskCount == 0 ? "Категории диска не выбраны" : $"Выбрано категорий диска: {CheckedDiskCount}";
    public string RegistrySelectionText => CheckedRegistryCount == 0 ? "Записи реестра не выбраны" : $"Выбрано записей реестра: {CheckedRegistryCount}";

    /// <summary>Есть отмеченные строки: показывается панель действий.</summary>
    public bool HasChecked => CheckedDiskCount + CheckedRegistryCount > 0;
    public bool HasCheckedDisk => CheckedDiskCount > 0;
    public bool HasCheckedRegistry => CheckedRegistryCount > 0;

    /// <summary>Что делает агент прямо сейчас (измерение или выполнение); пусто, пока ничего не идёт.</summary>
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    /// <summary>Почему кнопки отключены: режим просмотра. Пусто, если права есть.</summary>
    public string ActionHint => ViewOnly ? "Режим просмотра: очистка и удаление записей отключены" : "";

    private void OnRowChecked()
    {
        if (_suppress) return;
        Recount();
    }

    private void Recount()
    {
        CheckedDiskCount = _all.Count(r => r.IsChecked && r.CanClean);
        CheckedRegistryCount = _all.Count(r => r.IsChecked && r.CanRemoveKey);
        OnPropertyChanged(nameof(DiskSelectionText));
        OnPropertyChanged(nameof(RegistrySelectionText));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(HasCheckedDisk));
        OnPropertyChanged(nameof(HasCheckedRegistry));
        CleanDiskCommand.RaiseCanExecuteChanged();
        ApplyRegistryCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    private void ClearSelection()
    {
        _suppress = true;
        foreach (var r in _all) r.IsChecked = false;
        _suppress = false;
        Recount();
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var m100 = n % 100;
        var m10 = n % 10;
        var word = m100 is >= 11 and <= 14 ? many : m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }

    private bool NotReady(string what)
    {
        if (Changes is null || _dialogs is null)
        {
            SetResult(ResultKind.Warning, $"{what} недоступно: нет связи с агентом.", "");
            return true;
        }
        if (ViewOnly)
        {
            SetResult(ResultKind.Warning, "Права администратора не выданы: доступен только просмотр.", "Нажмите «Запросить права», чтобы менять систему.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Очистка диска: измерение (ничего не удаляется) → подтверждение с объёмом и условиями → удаление → итог.
    /// Удаление необратимо, поэтому в подтверждении это сказано прямо, а откат после итога не предлагается.
    /// </summary>
    private async Task CleanDiskAsync()
    {
        var rows = _all.Where(r => r.IsChecked && r.CanClean).ToList();
        if (rows.Count == 0 || NotReady("Очистка")) return;
        var ids = rows.Select(r => r.CategoryId).ToList();

        ChangeOutcome measure;
        BusyText = "Считаем, сколько можно освободить…";
        IsBusy = true;
        try { measure = await Changes!.MeasureCleanupAsync(ids); }
        finally { IsBusy = false; BusyText = ""; }

        // Отказ, нет связи, отмена: объясняем и ничего не удаляем. Частичный ответ годится для показа объёма.
        if (measure.Kind is not (OutcomeKind.Success or OutcomeKind.NoChange or OutcomeKind.Partial))
        {
            ShowOutcome(measure, undoable: false);
            return;
        }

        var facts = ResponseText.ParseCleanupFacts(measure.Facts).GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.First());
        if (!_dialogs!.Confirm(new ConfirmContent(
                "Очистить: " + Plural(rows.Count, "категория", "категории", "категорий"),
                DiskConfirmText(rows, facts), "Очистить", Danger: true)))
            return;

        ChangeOutcome outcome;
        BusyText = "Очищаем диск…";
        IsBusy = true;
        try { outcome = await Changes!.CleanAsync(ids); }
        finally { IsBusy = false; BusyText = ""; }

        ShowOutcome(DiskOutcome(outcome), undoable: false);
        ClearSelection();
        OnShown();
    }

    /// <summary>Текст подтверждения очистки: объём, список с размерами, условия, предупреждения.</summary>
    internal static string DiskConfirmText(IReadOnlyList<FindingRowViewModel> rows, IReadOnlyDictionary<string, ResponseText.CleanupFact> facts)
    {
        var total = rows.Sum(r => facts.TryGetValue(r.CategoryId, out var f) ? f.Bytes : 0);
        var counted = rows.Any(r => facts.ContainsKey(r.CategoryId));
        var head = counted
            ? $"Будет удалено ~{Fmt.Size(total)}. "
            : "Объём заранее не посчитан: его определит агент при очистке. ";
        var sb = new System.Text.StringBuilder(head);
        sb.Append("Свежие файлы (обычно моложе 24 часов), занятые и ссылки остаются. ");
        sb.Append("Откат невозможен: это кэш и временные файлы, они создаются заново.\n");

        sb.Append("Обратимо: нет.\n\n");
        foreach (var r in rows.Take(10))
        {
            var size = facts.TryGetValue(r.CategoryId, out var f) ? "~" + Fmt.Size(f.Bytes) : "размер определит агент";
            var running = r.Finding.Verdict.Reasons.Any(x => x.Code == ReasonCodes.BrowserRunning) ? ", браузер запущен: категорию пропустим" : "";
            sb.Append("• ").Append(r.Title).Append(": ").Append(size).Append(running).Append('\n');
        }
        if (rows.Count > 10) sb.Append($"… и ещё {rows.Count - 10}\n");

        var catalog = Chistovik.Core.Catalog.CleanupCatalog.Shared;
        var explorer = rows.Where(r => catalog.Find(r.CategoryId)?.Restart == "explorer").Select(r => "«" + r.Title + "»").ToList();
        if (explorer.Count > 0)
            sb.Append("\nДля ").Append(string.Join(", ", explorer))
              .Append(" может понадобиться перезапуск Проводника: значки и окна папок на мгновение моргнут.");
        if (rows.Any(r => catalog.Find(r.CategoryId)?.Mechanism == "recyclebin"))
            sb.Append("\nКорзина очищается навсегда: файлы из неё вернуть нельзя.");
        sb.Append("\nЗапись об очистке появится в журнале.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Итог очистки одной строкой: «Освобождено X МБ; пропущено: занято n, свежих m».</summary>
    internal static ChangeOutcome DiskOutcome(ChangeOutcome o)
    {
        if (o.Kind is not (OutcomeKind.Success or OutcomeKind.Partial)) return o;
        var facts = ResponseText.ParseCleanupFacts(o.Facts);
        var freed = facts.Sum(f => f.FreedBytes);
        int Skipped(string key) => facts.Sum(f => f.Skipped.GetValueOrDefault(key));
        var busy = Skipped("in-use");
        var fresh = Skipped("too-fresh");
        var other = facts.Sum(f => f.Skipped.Values.Sum()) - busy - fresh;
        var parts = new List<string>();
        if (busy > 0) parts.Add($"занято {busy}");
        if (fresh > 0) parts.Add($"свежих {fresh}");
        if (other > 0) parts.Add($"прочих {other}");
        var text = $"Освобождено {Fmt.Size(freed)}" + (parts.Count > 0 ? "; пропущено: " + string.Join(", ", parts) : "") + ".";
        if (o.Kind == OutcomeKind.Partial) text += " Часть категорий не очищена, подробности в журнале.";
        return o with { Message = text };
    }

    private sealed record RegistryPreview(string Key, string Display, int Subkeys, bool Revertible, string? Rule);

    private static IReadOnlyList<RegistryPreview> ParseRegistryFacts(IReadOnlyList<Chistovik.Core.Ipc.FactEntry>? facts)
    {
        var list = new List<RegistryPreview>();
        if (facts is null) return list;
        foreach (var f in facts)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(f.Json);
                var e = doc.RootElement;
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var display = e.TryGetProperty("display", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null;
                var sub = e.TryGetProperty("subkeys", out var s) && s.TryGetInt32(out var n) ? n : 0;
                var rev = !e.TryGetProperty("revertible", out var r) || r.ValueKind != System.Text.Json.JsonValueKind.False;
                var rule = e.TryGetProperty("rule", out var ru) && ru.ValueKind == System.Text.Json.JsonValueKind.String ? ru.GetString() : null;
                list.Add(new RegistryPreview(f.Key, string.IsNullOrWhiteSpace(display) ? f.Key : display!, sub, rev, rule));
            }
            catch (System.Text.Json.JsonException) { }
        }
        return list;
    }

    /// <summary>
    /// Удаление записей реестра: пробная проверка агентом (ничего не пишется) → подтверждение со списком, обратимостью и
    /// отказами → боевой запуск → итог с кнопкой «Откатить». Крупные ключи сами не подтверждаются (ExpertConfirmations = null).
    /// </summary>
    private async Task ApplyRegistryAsync()
    {
        var rows = _all.Where(r => r.IsChecked && r.CanRemoveKey).ToList();
        if (rows.Count == 0 || NotReady("Удаление записей")) return;
        var items = rows.Select(r => new FindingAction(r.Finding.Id, FindingActionKind.Remove)).ToList();

        ChangeOutcome dry;
        BusyText = "Проверяем записи реестра…";
        IsBusy = true;
        try { dry = await Changes!.ApplyFindingsAsync(items, dryRun: true, null); }
        finally { IsBusy = false; BusyText = ""; }

        if (dry.Response?.Code != ResponseCodes.DryRun)
        {
            ShowOutcome(dry, undoable: false);
            return;
        }

        var preview = ParseRegistryFacts(dry.Facts);
        var allowed = preview.Where(p => p.Rule is null).ToList();
        var denied = preview.Where(p => p.Rule is not null).Select(p => $"{p.Display}: {ResponseText.StepCodeName(p.Rule)}").ToList();
        if (preview.Count == 0)
        {
            // Агент не прислал подробностей: причины берём из шагов, а число допустимых считаем по ним.
            var bad = dry.Response.Steps?.Where(s => s.State is StepState.Denied or StepState.Failed or StepState.Skipped).ToList()
                      ?? new List<StepResult>();
            denied = bad.Select(s => "Запись не будет убрана: " + ResponseText.StepCodeName(s.Code)).ToList();
            allowed = Enumerable.Range(0, Math.Max(0, rows.Count - bad.Count))
                .Select(i => new RegistryPreview("", rows[i].Title, 0, true, null)).ToList();
        }

        if (allowed.Count == 0)
        {
            SetResult(ResultKind.Warning, "Выбранные записи убрать нельзя: ничего не изменено.", string.Join("\n", denied.Take(8)));
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Агент ещё раз проверит каждую запись и перед удалением сохранит её копию. ");
        sb.Append("Обратимо: да, 90 дней. Копия сохранится на 90 дней, откат из журнала: «Журнал и откат», кнопка «Откатить».\n\n");
        foreach (var p in allowed.Take(10))
            sb.Append("• ").Append(p.Display).Append(p.Key.Length > 0 || p.Subkeys > 0 ? $" — подключей: {p.Subkeys}" : "").Append('\n');
        if (allowed.Count > 10) sb.Append($"… и ещё {allowed.Count - 10}\n");
        if (denied.Count > 0)
        {
            sb.Append("\nНе будут тронуты:\n");
            foreach (var d in denied.Take(6)) sb.Append("• ").Append(d).Append('\n');
            if (denied.Count > 6) sb.Append($"… и ещё {denied.Count - 6}\n");
        }

        if (!_dialogs!.Confirm(new ConfirmContent(
                "Удалить из реестра: " + Plural(allowed.Count, "запись", "записи", "записей"),
                sb.ToString().TrimEnd(), "Удалить")))
            return;

        ChangeOutcome outcome;
        BusyText = "Убираем записи реестра…";
        IsBusy = true;
        try { outcome = await Changes!.ApplyFindingsAsync(items, dryRun: false, null); }
        finally { IsBusy = false; BusyText = ""; }

        ShowOutcome(outcome, undoable: true);
        ClearSelection();
        OnShown();
    }

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
            .Select(f => new FindingRowViewModel(f, title => Navigator?.Open(title), OnRowChecked))
            .ToList();
        Recount();
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
