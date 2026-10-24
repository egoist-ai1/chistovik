using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;

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

    public ProgramsViewModel(IProgramsSource source, IDialogService dialogs)
        : base("Установленные программы", "Всё, что установлено на этом компьютере. Список читается из реестра без прав администратора.")
    {
        _dialogs = dialogs;
        _source = source;
        SortCommand = new RelayCommand(p => SortBy(p as string ?? "Name"));
        DeleteCommand = new RelayCommand(Delete, () => SelectedCount > 0);
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

    private void Delete()
    {
        _dialogs.ShowStub("Удаление пока не подключено",
            $"Выбрано программ: {SelectedCount}, общий размер {SelectedSize}.\n\n" +
            "Удаление с карантином остатков появится в версии 0.2. Сейчас ничего не изменено.");
    }
}
