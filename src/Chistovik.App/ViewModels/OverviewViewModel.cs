using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
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

    private readonly IProgramsSource _programs;
    private readonly IStartupSource _startup;
    private readonly IJournalSource _journal;
    private readonly IServiceSource? _services;
    private readonly ITaskSource? _tasks;
    private readonly IAgentClient? _agent;
    private string _programsCount = "—";
    private string _programsSize = "";
    private string _startupText = "—";
    private string _startupHint = "";
    private string _servicesText = "—";
    private string _servicesHint = "";
    private string _tasksText = "—";
    private string _tasksHint = "";
    private string _journalText = "—";
    private string _journalHint = "";
    private IReadOnlyList<RecentOperationRow> _recent = Array.Empty<RecentOperationRow>();
    private string _recentEmpty = "";

    public OverviewViewModel(IProgramsSource programs, IStartupSource startup, IJournalSource journal, IAgentClient? agent = null,
        IServiceSource? services = null, ITaskSource? tasks = null)
        : base("Обзор", "Состояние системы по реальным данным. Чтение идёт без прав администратора.")
    {
        _programs = programs;
        _startup = startup;
        _journal = journal;
        _services = services;
        _tasks = tasks;
        _agent = agent;
        if (agent is not null) agent.AccessChanged += (_, _) => Post(() => { OnPropertyChanged(nameof(RightsTitle)); OnPropertyChanged(nameof(RightsText)); });
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    public string ProgramsCount { get => _programsCount; private set => SetProperty(ref _programsCount, value); }
    public string ProgramsSize { get => _programsSize; private set => SetProperty(ref _programsSize, value); }
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

    public string RightsText => _agent?.Access switch
    {
        AgentAccess.Connected => "Агент работает с правами администратора, изменения применяются и пишутся в журнал.",
        AgentAccess.ViewOnly => "Вы отказали в правах: всё можно смотреть, но нельзя менять. Запросить снова можно на любом экране изменений.",
        AgentAccess.Lost => "Агент перестал отвечать. Следующее изменение подключится заново.",
        _ => "Агент не запущен, права не запрашивались: просмотр работает без них. Окно UAC появится при первом изменении.",
    };

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var tips = new List<string>();
        await Task.WhenAll(
            Guarded(LoadPrograms, ct, () => { ProgramsCount = NoData; ProgramsSize = "не удалось прочитать список программ"; }),
            Guarded(c => LoadStartup(tips, c), ct, () => { StartupText = NoData; StartupHint = "не удалось прочитать автозагрузку"; }),
            Guarded(LoadServices, ct, () => { ServicesText = NoData; ServicesHint = "не удалось прочитать службы"; }),
            Guarded(LoadTasks, ct, () => { TasksText = NoData; TasksHint = "не удалось прочитать планировщик"; }),
            Guarded(LoadJournal, ct, () => { JournalText = NoData; JournalHint = "не удалось прочитать журнал"; RecentEmptyText = "Журнал не прочитан."; }));
        ct.ThrowIfCancellationRequested();

        var unverified = TweakCatalog.Shared.Tweaks.Count(t => !t.Verified);
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

    private async Task LoadPrograms(CancellationToken ct)
    {
        var p = await _programs.GetProgramsAsync();
        ct.ThrowIfCancellationRequested();
        ProgramsCount = Fmt.Number(p.Count);
        var known = p.Sum(x => x.SizeBytes);
        ProgramsSize = known > 0 ? "по данным реестра занимают " + Fmt.Size(known) : "размер в реестре не указан";
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
        RecentOperations = j.Take(5).Select(x => new RecentOperationRow(x.Title, Fmt.DateTimeText(x.When), x.StatusText)).ToList();
        RecentEmptyText = j.Count == 0 ? "Изменений ещё не было: журнал появится после первого изменения." : "";
    }
}
