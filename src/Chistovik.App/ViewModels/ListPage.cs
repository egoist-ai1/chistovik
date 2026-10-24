using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;

namespace Chistovik.App.ViewModels;

/// <summary>Строка списка с текстом для поиска (собирается один раз при загрузке) и классом «можно менять».</summary>
public interface IListRow
{
    string SearchText { get; }
    Changeability Class { get; }
}

public sealed record SortOption(string Key, string Label)
{
    /// <summary>Подпись для поля выбора в ComboBox: показывается без DisplayMemberPath.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Общая основа списков служб, задач и автозагрузки: поиск по тексту, фильтр по классу, сортировка. Короткие списки
/// фильтруются сразу; длинные (больше <see cref="SyncLimit"/> строк) считаются в фоне, новый ввод отменяет прежний расчёт,
/// поэтому окно не подвисает на каждой букве. Результат доступен после <see cref="FilterPending"/>.
/// </summary>
public abstract class ListPageViewModel<TRow> : StatefulPageViewModel where TRow : class, IListRow
{
    public const int SyncLimit = 2000;

    private List<TRow> _all = new();
    private IReadOnlyList<TRow> _items = Array.Empty<TRow>();
    private string _search = "";
    private string _filter = "all";
    private SortOption _sort;
    private CancellationTokenSource? _filterCts;

    protected ListPageViewModel(string title, string subtitle, IReadOnlyList<SortOption> sortOptions,
        IChangeService? changes, INavigator? navigator)
        : base(title, subtitle, changes, navigator)
    {
        SortOptions = sortOptions;
        _sort = sortOptions[0];
        FilterCommand = new RelayCommand(p => { Filter = p as string ?? "all"; });
        SortCommand = new RelayCommand(p =>
        {
            var found = SortOptions.FirstOrDefault(o => o.Key == p as string);
            if (found is not null) SelectedSort = found;
        });
    }

    public RelayCommand FilterCommand { get; }
    public RelayCommand SortCommand { get; }

    /// <summary>Расчёт последнего фильтра (для коротких списков уже завершён).</summary>
    public Task FilterPending { get; private set; } = Task.CompletedTask;

    protected IReadOnlyList<TRow> AllRows => _all;

    public IReadOnlyList<TRow> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public IReadOnlyList<SortOption> SortOptions { get; }

    public SortOption SelectedSort
    {
        get => _sort;
        set { if (value is not null && SetProperty(ref _sort, value)) Refilter(); }
    }

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? "")) Refilter(); }
    }

    public string Filter
    {
        get => _filter;
        private set
        {
            if (!SetProperty(ref _filter, value)) return;
            OnPropertyChanged(nameof(FilterAll));
            OnPropertyChanged(nameof(FilterModifiable));
            OnPropertyChanged(nameof(FilterReadOnly));
            Refilter();
        }
    }

    public bool FilterAll => Filter == "all";
    public bool FilterModifiable => Filter == "mod";
    public bool FilterReadOnly => Filter == "ro";

    public string StatusText
    {
        get
        {
            var filter = Filter switch { "mod" => "можно менять", "ro" => "только чтение и закрытые", _ => "все" };
            return $"Показано {Items.Count} из {_all.Count} · фильтр: {filter} · можно менять: {_all.Count(x => x.Class == Changeability.Modifiable)}";
        }
    }

    /// <summary>Порядок строк для ключа сортировки; null — как пришло из источника.</summary>
    protected abstract IComparer<TRow>? Comparer(string sortKey);

    /// <summary>Строки изменились (новый список или новый результат фильтра): обновить зависимые подсказки.</summary>
    protected virtual void OnItemsChanged() { }

    protected void SetRows(IEnumerable<TRow> rows)
    {
        _all = rows.ToList();
        Refilter();
    }

    private void Refilter()
    {
        _filterCts?.Cancel();
        var all = _all;
        var search = _search.Trim();
        var filter = _filter;
        var comparer = Comparer(_sort.Key);

        List<TRow> Compute(CancellationToken ct)
        {
            IEnumerable<TRow> q = all;
            if (filter == "mod") q = q.Where(x => x.Class == Changeability.Modifiable);
            else if (filter == "ro") q = q.Where(x => x.Class != Changeability.Modifiable);
            if (search.Length > 0) q = q.Where(x => x.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase));
            var list = new List<TRow>();
            foreach (var row in q)
            {
                if ((list.Count & 0xFF) == 0) ct.ThrowIfCancellationRequested();
                list.Add(row);
            }
            ct.ThrowIfCancellationRequested();
            return comparer is null ? list : list.OrderBy(x => x, comparer).ToList();
        }

        if (all.Count <= SyncLimit)
        {
            _filterCts = null;
            FilterPending = Task.CompletedTask;
            Publish(Compute(CancellationToken.None));
            return;
        }

        var cts = new CancellationTokenSource();
        _filterCts = cts;
        FilterPending = RunFilterAsync(Compute, cts);
    }

    private async Task RunFilterAsync(Func<CancellationToken, List<TRow>> compute, CancellationTokenSource cts)
    {
        try
        {
            var result = await Task.Run(() => compute(cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) Publish(result);
        }
        catch (OperationCanceledException) { /* пришёл более новый ввод */ }
    }

    private void Publish(List<TRow> result)
    {
        Items = result;
        OnPropertyChanged(nameof(StatusText));
        OnItemsChanged();
    }
}
