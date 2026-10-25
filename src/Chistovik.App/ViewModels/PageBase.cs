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

    /// <summary>Вызывается, когда пользователь возвращается на уже открытый раздел: данные могли измениться снаружи.</summary>
    public virtual void OnShown() { }
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
    private bool _quietNext;
    private int _loadVersion;
    private int _loadedCount;

    protected StatefulPageViewModel(string title, string subtitle, IChangeService? changes = null, INavigator? navigator = null)
        : base(title, subtitle)
    {
        Changes = changes;
        Navigator = navigator;
        ReloadCommand = new RelayCommand(() => _ = ReloadAsync());
        CancelLoadCommand = new RelayCommand(CancelLoad, () => IsLoading);
        RequestRightsCommand = new RelayCommand(RequestRights, () => ViewOnly && !NoAdminAccount);
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

    /// <summary>Тихое обновление при возврате на экран: прежний список остаётся на месте, скелетон не показывается.</summary>
    public override void OnShown()
    {
        if (_loadedCount == 0 || IsLoading || IsBusy) return;
        _quietNext = true;
        _ = ReloadAsync();
    }

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
    /// <summary>Техническая строка ошибки (тип и исходное сообщение) для ToolTip и диагностики; в основной текст не выводится.</summary>
    public string ErrorDetails { get => _errorDetails; private set => SetProperty(ref _errorDetails, value); }
    private string _errorDetails = "";
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
        var quiet = _quietNext && _loadedCount > 0;
        _quietNext = false;
        if (!quiet)
        {
            State = PageState.Loading;
            StateMessage = "Загружаем…";
        }
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
        catch (Exception) when (quiet && version == _loadVersion)
        {
            // Тихое обновление не вышло: остаётся прежний список, ошибку не показываем.
        }
        catch (OperationCanceledException) when (version == _loadVersion)
        {
            if (_loadedCount > 0) { StateMessage = ""; State = PageState.Ready; }
            else { StateMessage = "Загрузка отменена. Нажмите «Обновить», чтобы загрузить снова."; State = PageState.Empty; }
        }
        catch (Exception e) when (version == _loadVersion && e is not OperationCanceledException)
        {
            var info = ErrorText.Describe(e);
            StateMessage = info.Text;
            ErrorDetails = info.Details;
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
    /// <summary>Общая полоса «режим просмотра» в PageBanner; экран, у которого своя полоса прав, отключает её.</summary>
    public virtual bool ShowViewOnlyBanner => ViewOnly;
    /// <summary>Обычный пользователь без прав администратора: запрос прав бесполезен.</summary>
    public bool NoAdminAccount => Changes?.Agent.ViewOnlyReason == AgentViewOnlyReason.NoAdminAccount;
    public string ViewOnlyText => NoAdminAccount
        ? LaunchMessages.NoAdminAccount
        : "Режим просмотра: права администратора не выданы, изменения отключены.";

    public bool IsBusy
    {
        get => _busy;
        protected set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanApply));
            OnCanApplyChanged();
            RefreshUndoResult();
        }
    }

    /// <summary>Кнопки применения доступны: не режим просмотра и нет запроса в работе.</summary>
    public bool CanApply => Changes is not null && !ViewOnly && !IsBusy;

    public string ApplyHint => NoAdminAccount ? "Только просмотр: нужна учётная запись администратора"
        : ViewOnly ? "Режим просмотра: изменения отключены" : "";

    private void OnAccessChanged()
    {
        OnPropertyChanged(nameof(ViewOnly));
        OnPropertyChanged(nameof(ShowViewOnlyBanner));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyHint));
        RequestRightsCommand.RaiseCanExecuteChanged();
        OnCanApplyChanged();
        RefreshUndoResult();
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

    /// <summary>Диалоги для отката из итога (окно конфликтов); экран с диалогами задаёт их в конструкторе.</summary>
    protected IDialogService? UndoDialogs { get; set; }

    private Guid? _resultOperation;
    private string _resultCounts = "";
    private IReadOnlyList<string> _resultProblems = Array.Empty<string>();
    private string _resultIcon = "";

    /// <summary>Откатывает операцию, о которой сообщает итог (кнопка «Откатить» рядом с итогом).</summary>
    public RelayCommand UndoResultCommand => _undoResultCommand ??= new RelayCommand(() => ResultUndoTask = UndoResultAsync(), () => CanUndoResult);
    private RelayCommand? _undoResultCommand;
    public RelayCommand DismissResultCommand => _dismissResultCommand ??= new RelayCommand(ClearResult);
    private RelayCommand? _dismissResultCommand;

    public Task ResultUndoTask { get; private set; } = Task.CompletedTask;

    /// <summary>Есть что откатывать, права выданы и ничего другого не выполняется.</summary>
    public bool CanUndoResult => _resultOperation is not null && CanApply && UndoDialogs is not null;

    /// <summary>Сводка «Применено: 3 · Пропущено: 1 · Не получилось: 1»; пусто, если шагов не было.</summary>
    public string ResultCounts { get => _resultCounts; private set => SetProperty(ref _resultCounts, value); }

    /// <summary>Пропущенные и неудавшиеся шаги с причиной понятным языком.</summary>
    public IReadOnlyList<string> ResultProblems { get => _resultProblems; private set => SetProperty(ref _resultProblems, value); }

    public bool HasResultCounts => ResultCounts.Length > 0;
    public bool HasResultProblems => ResultProblems.Count > 0;

    /// <summary>Имя векторного значка итога: форма различает исход без опоры на цвет.</summary>
    public string ResultIcon { get => _resultIcon; private set => SetProperty(ref _resultIcon, value); }

    private async Task UndoResultAsync()
    {
        if (Changes is null || UndoDialogs is null || _resultOperation is not { } id) return;
        IsBusy = true;
        try
        {
            var outcome = await new UndoFlow(Changes, UndoDialogs).RunAsync(id);
            ShowOutcome(outcome, undoable: false);
            if (outcome.Changed) await ReloadAsync();
        }
        finally { IsBusy = false; }
    }

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
        _resultOperation = null;
        ResultCounts = "";
        ResultProblems = Array.Empty<string>();
        ResultIcon = kind switch
        {
            ResultKind.Success => "check-circle",
            ResultKind.Info => "info",
            ResultKind.Warning => "alert",
            ResultKind.Error => "x-circle",
            _ => "",
        };
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultIsGood));
        OnPropertyChanged(nameof(ResultIsWarn));
        OnPropertyChanged(nameof(ResultIsBad));
        OnPropertyChanged(nameof(HasResultHint));
        OnPropertyChanged(nameof(HasChange));
        OnPropertyChanged(nameof(HasResultCounts));
        OnPropertyChanged(nameof(HasResultProblems));
        RefreshUndoResult();
    }

    private void RefreshUndoResult()
    {
        OnPropertyChanged(nameof(CanUndoResult));
        UndoResultCommand.RaiseCanExecuteChanged();
    }

    /// <param name="undoable">false для самого отката: откат отката не предлагаем, подписи «Возвращено».</param>
    protected void ShowOutcome(ChangeOutcome o, bool undoable = true)
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
        if (o.Response?.Steps is { Count: > 0 } steps)
        {
            var (summary, problems) = ResultSummary.Build(steps, undoable);
            ResultCounts = summary;
            ResultProblems = problems;
            OnPropertyChanged(nameof(HasResultCounts));
            OnPropertyChanged(nameof(HasResultProblems));
        }
        _resultOperation = undoable && o.Changed ? o.OperationId : null;
        RefreshUndoResult();
    }

    /// <summary>Показывает готовый итог действия так же, как после ответа агента (снимки экранов: частичный результат).</summary>
    internal void ShowDemoOutcome(ChangeOutcome outcome) => ShowOutcome(outcome);

    protected void ClearResult() => SetResult(ResultKind.None, "", "");

    protected void Post(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Post(_ => a(), null);
    }
}
