using System.Globalization;
using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;

namespace Chistovik.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string Subtitle { get; }
}

public sealed class PlaceholderViewModel : PageViewModel
{
    public PlaceholderViewModel(string title, string subtitle) : base(title, subtitle) { }
    public string Note => "Этот раздел появится в следующих версиях. Сейчас доступны обзор, программы, автозагрузка и журнал.";
}

internal static class Fmt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Size(long bytes)
    {
        double v = bytes;
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0", Ru) : v.ToString("0.#", Ru)) + " " + u[i];
    }

    public static string Date(DateTime d) => d.ToString("dd.MM.yyyy", Ru);
    public static string DateTimeText(DateTime d) => d.ToString("dd.MM.yyyy HH:mm", Ru);
}

// ---------- Обзор ----------

public sealed class OverviewViewModel : PageViewModel
{
    public OverviewViewModel(IProgramsSource programs, IStartupSource startup, IJournalSource journal)
        : base("Обзор", "Состояние системы и то, что стоит улучшить.")
    {
        _ = LoadAsync(programs, startup, journal);
    }

    private double _score;
    private string _scoreText = "—";
    private string _programsCount = "—";
    private string _programsSize = "";
    private string _startupText = "—";
    private string _startupHint = "";
    private string _journalText = "—";
    private string _journalHint = "";

    public double Score { get => _score; private set => SetProperty(ref _score, value); }
    public string ScoreText { get => _scoreText; private set => SetProperty(ref _scoreText, value); }
    public string ProgramsCount { get => _programsCount; private set => SetProperty(ref _programsCount, value); }
    public string ProgramsSize { get => _programsSize; private set => SetProperty(ref _programsSize, value); }
    public string StartupText { get => _startupText; private set => SetProperty(ref _startupText, value); }
    public string StartupHint { get => _startupHint; private set => SetProperty(ref _startupHint, value); }
    public string JournalText { get => _journalText; private set => SetProperty(ref _journalText, value); }
    public string JournalHint { get => _journalHint; private set => SetProperty(ref _journalHint, value); }

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    private async Task LoadAsync(IProgramsSource programs, IStartupSource startup, IJournalSource journal)
    {
        var p = await programs.GetProgramsAsync();
        var s = await startup.GetStartupAsync();
        var j = await journal.GetOperationsAsync();

        ProgramsCount = p.Count.ToString(CultureInfo.GetCultureInfo("ru-RU"));
        ProgramsSize = "занимают " + Fmt.Size(p.Sum(x => x.SizeBytes));

        var enabled = s.Count(x => x.Enabled);
        var risky = s.Count(x => x.Enabled && x.Risk != RiskLevel.Known);
        var impact = s.Where(x => x.Enabled).Sum(x => x.ImpactMs);
        StartupText = $"{enabled} из {s.Count}";
        StartupHint = $"+{impact / 1000.0:0.0} с к загрузке, под вопросом: {risky}".Replace('.', ',');

        var reversible = j.Count(x => x.Reversible);
        JournalText = j.Count.ToString(CultureInfo.GetCultureInfo("ru-RU"));
        JournalHint = $"можно откатить: {reversible}";

        var score = Math.Max(0, 100 - risky * 6 - Math.Max(0, enabled - 6) * 2);
        Score = score;
        ScoreText = score.ToString("0", CultureInfo.InvariantCulture);

        Suggestions = new[]
        {
            $"В автозагрузке {risky} записи с неясным источником: проверьте их.",
            "Отключите то, что не нужно сразу после входа: система загрузится быстрее.",
            "Остатки удалённых программ ждут в карантине: очистите после проверки.",
        };
        OnPropertyChanged(nameof(Suggestions));
    }
}

// ---------- Программы ----------

public sealed class ProgramRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public ProgramRowViewModel(ProgramInfo model, Action changed)
    {
        Model = model;
        _changed = changed;
        SizeText = Fmt.Size(model.SizeBytes);
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

public sealed class ProgramsViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private List<ProgramRowViewModel> _all = new();
    private IReadOnlyList<ProgramRowViewModel> _items = Array.Empty<ProgramRowViewModel>();
    private string _search = "";
    private string _sortKey = "Name";
    private bool _descending;
    private int _selectedCount;
    private string _selectedSize = "";
    private bool _suppress;

    public ProgramsViewModel(IProgramsSource source, IDialogService dialogs)
        : base("Установленные программы", "Всё, что установлено на этом компьютере. Отметьте программы, которые хотите удалить.")
    {
        _dialogs = dialogs;
        SortCommand = new RelayCommand(p => SortBy(p as string ?? "Name"));
        DeleteCommand = new RelayCommand(Delete, () => SelectedCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        SelectAllCommand = new RelayCommand(SelectAllVisible);
        _ = LoadAsync(source);
    }

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

    private async Task LoadAsync(IProgramsSource source)
    {
        var data = await source.GetProgramsAsync();
        _all = data.Select(x => new ProgramRowViewModel(x, OnSelectionChanged)).ToList();
        Apply();
    }

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

    private void Delete()
    {
        _dialogs.ShowStub("Удаление — заглушка",
            $"Выбрано программ: {SelectedCount}, общий размер {SelectedSize}.\n\n" +
            "Удаление подключится вместе с ядром: предпросмотр, карантин остатков и откат. Сейчас ничего не изменено.");
    }
}

// ---------- Автозагрузка ----------

public sealed class StartupRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isEnabled;

    public StartupRowViewModel(StartupInfo model, Action changed)
    {
        Model = model;
        _changed = changed;
        _isEnabled = model.Enabled;
        ImpactText = $"{model.ImpactMs / 1000.0:0.0} с".Replace('.', ',');
        RiskText = model.Risk switch
        {
            RiskLevel.Known => "Известная",
            RiskLevel.Review => "Проверьте",
            _ => "Подозрительная",
        };
        AutomationName = $"{model.Name}, {RiskText}, влияние на загрузку {ImpactText}";
    }

    public StartupInfo Model { get; }
    public string Name => Model.Name;
    public string Command => Model.Command;
    public string Publisher => Model.Publisher;
    public RiskLevel Risk => Model.Risk;
    public string RiskText { get; }
    public string ImpactText { get; }
    public string AutomationName { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (SetProperty(ref _isEnabled, value)) _changed(); }
    }
}

public sealed class StartupViewModel : PageViewModel
{
    private IReadOnlyList<StartupRowViewModel> _items = Array.Empty<StartupRowViewModel>();

    public StartupViewModel(IStartupSource source)
        : base("Автозагрузка", "Что запускается при входе и сколько времени это добавляет. Изменения пока не применяются к системе.")
    {
        _ = LoadAsync(source);
    }

    public IReadOnlyList<StartupRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }

    public string SummaryText
    {
        get
        {
            var on = Items.Where(x => x.IsEnabled).ToList();
            var total = on.Sum(x => x.Model.ImpactMs) / 1000.0;
            return $"Включено {on.Count} из {Items.Count}, добавляют около {total:0.0} с к загрузке".Replace('.', ',');
        }
    }

    public string RiskSummary
    {
        get
        {
            var n = Items.Count(x => x.IsEnabled && x.Risk != RiskLevel.Known);
            return n == 0 ? "Подозрительных записей нет" : $"Требуют проверки: {n}";
        }
    }

    private async Task LoadAsync(IStartupSource source)
    {
        var data = await source.GetStartupAsync();
        Items = data.Select(x => new StartupRowViewModel(x, OnRowChanged)).ToList();
        OnRowChanged();
    }

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(RiskSummary));
    }
}

// ---------- Журнал ----------

public sealed class JournalOperationViewModel
{
    public JournalOperationViewModel(JournalOperation model)
    {
        Model = model;
        WhenText = Fmt.DateTimeText(model.When);
        Steps = model.Steps;
        CanRollback = model.Reversible && model.Status != "Откачено";
        AutomationName = $"{model.Title}, {WhenText}, {model.Status}";
    }

    public JournalOperation Model { get; }
    public string Title => Model.Title;
    public string Status => Model.Status;
    public string Id => Model.Id;
    public string WhenText { get; }
    public IReadOnlyList<JournalStep> Steps { get; }
    public bool CanRollback { get; }
    public string AutomationName { get; }
}

public sealed class JournalViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private IReadOnlyList<JournalOperationViewModel> _operations = Array.Empty<JournalOperationViewModel>();
    private JournalOperationViewModel? _selected;

    public JournalViewModel(IJournalSource source, IDialogService dialogs)
        : base("Журнал изменений", "История операций с шагами. Каждую можно проверить и, если нужно, откатить.")
    {
        _dialogs = dialogs;
        RollbackCommand = new RelayCommand(Rollback, () => Selected?.CanRollback == true);
        _ = LoadAsync(source);
    }

    public RelayCommand RollbackCommand { get; }

    public IReadOnlyList<JournalOperationViewModel> Operations
    {
        get => _operations;
        private set => SetProperty(ref _operations, value);
    }

    public JournalOperationViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RollbackCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    private async Task LoadAsync(IJournalSource source)
    {
        var data = await source.GetOperationsAsync();
        Operations = data.Select(x => new JournalOperationViewModel(x)).ToList();
        Selected = Operations.FirstOrDefault();
    }

    private void Rollback()
    {
        if (Selected is null) return;
        _dialogs.ShowStub("Откат — заглушка",
            $"Операция «{Selected.Title}» ({Selected.Id}).\n\nОткат подключится вместе с ядром: возврат из карантина и проверка. Сейчас ничего не изменено.");
    }
}
