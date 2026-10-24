using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;

namespace Chistovik.App.ViewModels;

/// <summary>Строка «последние операции» на обзоре: только то, что записано в журнале.</summary>
public sealed record RecentOperationRow(string Title, string WhenText, string StateText)
{
    public string AutomationName => $"{Title}, {WhenText}, {StateText}";
}

/// <summary>
/// Главный экран: только реальные факты. Каждая плитка грузится отдельно: если один источник не прочитался,
/// в плитке написано «нет данных» с причиной, остальные плитки остаются. Выдуманных цифр здесь нет.
/// </summary>
public sealed class OverviewViewModel : StatefulPageViewModel
{
    private const string NoData = "нет данных";

    private readonly IStartupSource _startup;
    private readonly IJournalSource _journal;
    private readonly IServiceSource? _services;
    private readonly ITaskSource? _tasks;
    private readonly IUiSettingsStore? _ui;
    private bool _showWelcome;
    private readonly IAgentClient? _agent;
    private string _startupText = "—";
    private string _startupHint = "";
    private string _servicesText = "—";
    private string _servicesHint = "";
    private string _tasksText = "—";
    private string _tasksHint = "";
    private string _journalText = "—";
    private string _journalHint = "";
    private string _modeText = "—";
    private string _modeHint = "";
    private IReadOnlyList<RecentOperationRow> _recent = Array.Empty<RecentOperationRow>();
    private string _recentEmpty = "";

    public OverviewViewModel(IStartupSource startup, IJournalSource journal, IChangeService? changes = null,
        IServiceSource? services = null, ITaskSource? tasks = null, bool isDemo = false, IUiSettingsStore? ui = null)
        : base("Обзор", isDemo ? "Демонстрационные данные"
            : "Состояние системы по реальным данным. Чтение идёт без прав администратора.", changes)
    {
        _startup = startup;
        _journal = journal;
        _services = services;
        _tasks = tasks;
        _ui = ui;
        _showWelcome = ui is not null && !ui.Load().WelcomeSeen;
        DismissWelcomeCommand = new RelayCommand(DismissWelcome);
        var agent = _agent = changes?.Agent;
        if (agent is not null) agent.AccessChanged += (_, _) => Post(() => { OnPropertyChanged(nameof(RightsTitle)); OnPropertyChanged(nameof(RightsText)); });
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    /// <summary>Приветствие при первом запуске: что делает программа и как вернуть изменения. Закрывается и больше не появляется.</summary>
    public bool ShowWelcome { get => _showWelcome; private set => SetProperty(ref _showWelcome, value); }

    public RelayCommand DismissWelcomeCommand { get; }

    private void DismissWelcome()
    {
        ShowWelcome = false;
        if (_ui is null) return;
        var s = _ui.Load();
        s.WelcomeSeen = true;
        _ui.Save(s);
    }

    /// <summary>У обзора своя полоса прав сверху, общая полоса «режим просмотра» не нужна.</summary>
    public override bool ShowViewOnlyBanner => false;

    public string TweaksText { get; } = Fmt.Number(TweakCatalog.Shared.Tweaks.Count(t => t.Area == "tw"));
    public string TweaksHint { get; } = TweaksHintText();
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }
    public string ModeHint { get => _modeHint; private set => SetProperty(ref _modeHint, value); }

    private static string TweaksHintText()
    {
        var unverified = TweakCatalog.Shared.Tweaks.Count(t => t.Area == "tw" && !t.Verified);
        return unverified > 0 ? $"не проверено: {unverified}" : "все проверены";
    }
    public string StartupText { get => _startupText; private set => SetProperty(ref _startupText, value); }
    public string StartupHint { get => _startupHint; private set => SetProperty(ref _startupHint, value); }
    public string ServicesText { get => _servicesText; private set => SetProperty(ref _servicesText, value); }
    public string ServicesHint { get => _servicesHint; private set => SetProperty(ref _servicesHint, value); }
    public string TasksText { get => _tasksText; private set => SetProperty(ref _tasksText, value); }
    public string TasksHint { get => _tasksHint; private set => SetProperty(ref _tasksHint, value); }
    public string JournalText { get => _journalText; private set => SetProperty(ref _journalText, value); }
    public string JournalHint { get => _journalHint; private set => SetProperty(ref _journalHint, value); }

    public IReadOnlyList<RecentOperationRow> RecentOperations { get => _recent; private set { if (SetProperty(ref _recent, value)) OnPropertyChanged(nameof(HasRecent)); } }
    public bool HasRecent => RecentOperations.Count > 0;
    public string RecentEmptyText { get => _recentEmpty; private set => SetProperty(ref _recentEmpty, value); }

    public string RightsTitle => _agent?.Access switch
    {
        AgentAccess.Connected => "Агент запущен",
        AgentAccess.ViewOnly => "Режим просмотра",
        AgentAccess.Lost => "Связь потеряна",
        _ => "Режим просмотра",
    };

    /// <summary>Короткое пояснение для узкой полосы прав: одна строка.</summary>
    public string RightsText => _agent?.Access switch
    {
        AgentAccess.Connected => "Права администратора есть, изменения применяются и пишутся в журнал.",
        AgentAccess.ViewOnly => "Права не выданы: смотреть можно, менять нельзя.",
        AgentAccess.Lost => "Агент не отвечает, следующее изменение подключит его заново.",
        _ => "Права не запрашивались, окно UAC появится при первом изменении.",
    };

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var tips = new List<string>();
        await Task.WhenAll(
            Guarded(c => LoadStartup(tips, c), ct, () => { StartupText = NoData; StartupHint = "не удалось прочитать автозагрузку"; }),
            Guarded(LoadServices, ct, () => { ServicesText = NoData; ServicesHint = "не удалось прочитать службы"; }),
            Guarded(LoadTasks, ct, () => { TasksText = NoData; TasksHint = "не удалось прочитать планировщик"; }),
            Guarded(LoadJournal, ct, () => { JournalText = NoData; JournalHint = "не удалось прочитать журнал"; ModeText = NoData; ModeHint = "не удалось прочитать журнал"; RecentEmptyText = "Журнал не прочитан."; }));
        ct.ThrowIfCancellationRequested();

        var unverified = TweakCatalog.Shared.Tweaks.Count(t => t.Area == "tw" && !t.Verified);
        if (unverified > 0) tips.Add($"Твиков с пометкой «не проверено»: {unverified}. Они ещё не проходили проверку в песочнице, применяйте осознанно.");
        tips.Add("Любое изменение попадает в журнал с шагами. Если что-то пошло не так, откатите его на экране «Журнал и откат».");
        Suggestions = tips;
        OnPropertyChanged(nameof(Suggestions));
        return 1;
    }, "");

    /// <summary>Загрузка одной плитки: отмена пробрасывается, любая другая ошибка превращается в «нет данных».</summary>
    private static async Task Guarded(Func<CancellationToken, Task> load, CancellationToken ct, Action onFail)
    {
        try { await load(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { onFail(); }
    }

    private async Task LoadStartup(List<string> tips, CancellationToken ct)
    {
        var s = await _startup.GetStartupAsync(ct);
        ct.ThrowIfCancellationRequested();
        var enabled = s.Count(x => x.Enabled);
        StartupText = $"{enabled} из {s.Count}";
        StartupHint = $"включено записей автозагрузки; можно менять: {s.Count(x => x.CanToggle)}";
        if (enabled > 0)
            tips.Add($"В автозагрузке включено {enabled}. Ненужное отключите на экране «Автозагрузка»: каждое отключение можно откатить.");
    }

    private async Task LoadServices(CancellationToken ct)
    {
        if (_services is null) { ServicesText = "—"; ServicesHint = "источник служб не подключён"; return; }
        var list = await _services.GetServicesAsync(ct);
        ct.ThrowIfCancellationRequested();
        ServicesText = Fmt.Number(list.Count);
        var noAccess = list.Count(x => x.NoAccess);
        ServicesHint = $"можно менять: {list.Count(x => x.Class == Changeability.Modifiable)}" + (noAccess > 0 ? $"; нет доступа: {noAccess}" : "");
    }

    private async Task LoadTasks(CancellationToken ct)
    {
        if (_tasks is null) { TasksText = "—"; TasksHint = "источник задач не подключён"; return; }
        var list = await _tasks.GetTasksAsync(ct);
        ct.ThrowIfCancellationRequested();
        TasksText = Fmt.Number(list.Count);
        var noAccess = list.Count(x => x.NoAccess);
        TasksHint = $"можно менять: {list.Count(x => x.Class == Changeability.Modifiable)}" + (noAccess > 0 ? $"; нет доступа: {noAccess}" : "");
    }

    private async Task LoadJournal(CancellationToken ct)
    {
        var j = await _journal.GetOperationsAsync();
        ct.ThrowIfCancellationRequested();
        var reversible = j.Count(x => x.Reversible);
        JournalText = Fmt.Number(j.Count);
        JournalHint = j.Count == 0 ? "изменений ещё не было" : $"можно откатить: {reversible}";
        var lastMode = j.Where(x => x.Kind == OperationKind.Mode).OrderByDescending(x => x.When).FirstOrDefault();
        const string ModePrefix = "Режим: ";
        ModeText = lastMode is null ? "не применялся" : lastMode.Title.StartsWith(ModePrefix, StringComparison.Ordinal) ? lastMode.Title[ModePrefix.Length..] : lastMode.Title;
        ModeHint = lastMode is null ? "готовые наборы настроек" : Fmt.DateTimeText(lastMode.When);
        RecentOperations = j.Take(5).Select(x => new RecentOperationRow(x.Title, Fmt.DateTimeText(x.When), x.StatusText)).ToList();
        RecentEmptyText = j.Count == 0 ? "Изменений ещё не было: журнал появится после первого изменения." : "";
    }
}
