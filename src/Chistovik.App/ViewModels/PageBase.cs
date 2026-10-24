using System.Globalization;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;

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
    public string Note => "Этот раздел появится в следующих версиях. Сейчас доступны обзор, программы, режимы, питание, автозагрузка, службы, задачи, твики и журнал.";
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

    public static string Date(DateTime? d) => d is null ? "—" : d.Value.ToString("dd.MM.yyyy", Ru);
    public static string DateTimeText(DateTime d) => d.ToString("dd.MM.yyyy HH:mm", Ru);
    public static string Number(int n) => n.ToString("N0", Ru);
}

public enum PageState { Loading, Ready, Empty, Error }

public enum ResultKind { None, Success, Info, Warning, Error }

/// <summary>
/// Общая основа экранов с данными и изменениями: состояния (загрузка, пусто, ошибка, готово), режим просмотра без прав,
/// итог последнего действия (в том числе частичный). Обновления из других потоков уходят в контекст интерфейса.
/// </summary>
public abstract class StatefulPageViewModel : PageViewModel
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private PageState _state = PageState.Loading;
    private string _stateMessage = "Загружаем…";
    private ResultKind _result;
    private string _resultText = "";
    private string _resultHint = "";
    private bool _busy;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private int _loadedCount;

    protected StatefulPageViewModel(string title, string subtitle, IChangeService? changes = null, INavigator? navigator = null)
        : base(title, subtitle)
    {
        Changes = changes;
        Navigator = navigator;
        ReloadCommand = new RelayCommand(() => _ = ReloadAsync());
        CancelLoadCommand = new RelayCommand(CancelLoad, () => IsLoading);
        RequestRightsCommand = new RelayCommand(RequestRights, () => ViewOnly);
        OpenJournalCommand = new RelayCommand(() => Navigator?.Open("Журнал и откат"));
        if (changes is not null) changes.Agent.AccessChanged += (_, _) => Post(OnAccessChanged);
    }

    protected IChangeService? Changes { get; }
    protected INavigator? Navigator { get; }

    public RelayCommand ReloadCommand { get; }
    public RelayCommand CancelLoadCommand { get; }
    public RelayCommand RequestRightsCommand { get; }
    public RelayCommand OpenJournalCommand { get; }

    /// <summary>Перечитывает данные экрана.</summary>
    public abstract Task ReloadAsync();

    // ----- состояние страницы -----

    public PageState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(ShowStateLine));
        }
    }

    public string StateMessage { get => _stateMessage; private set => SetProperty(ref _stateMessage, value); }
    public bool IsLoading => State == PageState.Loading;
    public bool IsReady => State == PageState.Ready;
    public bool IsEmpty => State == PageState.Empty;
    public bool IsError => State == PageState.Error;
    public bool ShowStateLine => State != PageState.Ready;

    protected Task LoadAsync(Func<Task<int>> load, string emptyMessage) => LoadAsync(_ => load(), emptyMessage);

    /// <summary>
    /// Загрузка в фоне с отменой. Новая загрузка отменяет предыдущую (устаревший результат не применяется);
    /// кнопка «Отменить» оставляет прежние данные, если они были, иначе показывает пустое состояние с объяснением.
    /// </summary>
    protected async Task LoadAsync(Func<CancellationToken, Task<int>> load, string emptyMessage)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var version = ++_loadVersion;
        State = PageState.Loading;
        StateMessage = "Загружаем…";
        CancelLoadCommand.RaiseCanExecuteChanged();
        try
        {
            var count = await load(cts.Token);
            if (version != _loadVersion) return;
            cts.Token.ThrowIfCancellationRequested();
            _loadedCount = count;
            if (count == 0) { StateMessage = emptyMessage; State = PageState.Empty; }
            else State = PageState.Ready;
        }
        catch (OperationCanceledException) when (version == _loadVersion)
        {
            if (_loadedCount > 0) { StateMessage = ""; State = PageState.Ready; }
            else { StateMessage = "Загрузка отменена. Нажмите «Обновить», чтобы загрузить снова."; State = PageState.Empty; }
        }
        catch (Exception e) when (version == _loadVersion && e is not OperationCanceledException)
        {
            StateMessage = "Не удалось загрузить данные: " + e.Message;
            State = PageState.Error;
        }
        catch (Exception)
        {
            // загрузка уже заменена более новой: её ошибка не важна
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
            CancelLoadCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Отменяет идущую загрузку (кнопка «Отменить»).</summary>
    public void CancelLoad() => _loadCts?.Cancel();

    /// <summary>Для экранов без асинхронной загрузки (данные из каталогов в памяти).</summary>
    protected void SetLoaded(int count, string emptyMessage)
    {
        StateMessage = emptyMessage;
        State = count == 0 ? PageState.Empty : PageState.Ready;
    }

    // ----- права и применение -----

    public bool ViewOnly => Changes?.Agent.Access == AgentAccess.ViewOnly;
    public string ViewOnlyText => "Режим просмотра: права администратора не выданы, изменения отключены.";

    public bool IsBusy
    {
        get => _busy;
        protected set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanApply));
            OnCanApplyChanged();
        }
    }

    /// <summary>Кнопки применения доступны: не режим просмотра и нет запроса в работе.</summary>
    public bool CanApply => Changes is not null && !ViewOnly && !IsBusy;

    public string ApplyHint => ViewOnly ? "Режим просмотра: изменения отключены" : "";

    private void OnAccessChanged()
    {
        OnPropertyChanged(nameof(ViewOnly));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyHint));
        RequestRightsCommand.RaiseCanExecuteChanged();
        OnCanApplyChanged();
    }

    /// <summary>Повторно показывает запрос UAC: следующее изменение запустит агент заново.</summary>
    private void RequestRights()
    {
        Changes?.Agent.AllowElevationRetry();
        SetResult(ResultKind.Info, "Права снова доступны к запросу: при следующем изменении появится окно UAC.", "");
    }

    /// <summary>Обновить IsEnabled-команды и строки после смены CanApply.</summary>
    protected virtual void OnCanApplyChanged() { }

    // ----- итог действия -----

    public ResultKind Result { get => _result; private set => SetProperty(ref _result, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public string ResultHint { get => _resultHint; private set => SetProperty(ref _resultHint, value); }
    public bool HasResult => Result != ResultKind.None;
    public bool ResultIsGood => Result is ResultKind.Success or ResultKind.Info;
    public bool ResultIsWarn => Result == ResultKind.Warning;
    public bool ResultIsBad => Result == ResultKind.Error;
    public bool HasResultHint => ResultHint.Length > 0;
    public bool HasChange { get; private set; }

    protected void SetResult(ResultKind kind, string text, string hint, bool changed = false)
    {
        Result = kind;
        ResultText = text;
        ResultHint = hint;
        HasChange = changed;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultIsGood));
        OnPropertyChanged(nameof(ResultIsWarn));
        OnPropertyChanged(nameof(ResultIsBad));
        OnPropertyChanged(nameof(HasResultHint));
        OnPropertyChanged(nameof(HasChange));
    }

    protected void ShowOutcome(ChangeOutcome o)
    {
        var kind = o.Kind switch
        {
            OutcomeKind.Success => ResultKind.Success,
            OutcomeKind.NoChange => ResultKind.Info,
            OutcomeKind.Cancelled => ResultKind.Info,
            OutcomeKind.Partial or OutcomeKind.Conflict => ResultKind.Warning,
            OutcomeKind.ViewOnly => ResultKind.Warning,
            _ => ResultKind.Error,
        };
        SetResult(kind, o.Message, o.RestartHint ?? "", o.Changed);
    }

    protected void ClearResult() => SetResult(ResultKind.None, "", "");

    protected void Post(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Post(_ => a(), null);
    }
}
