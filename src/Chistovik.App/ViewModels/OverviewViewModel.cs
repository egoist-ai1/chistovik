using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;

namespace Chistovik.App.ViewModels;

public sealed class OverviewViewModel : StatefulPageViewModel
{
    private readonly IProgramsSource _programs;
    private readonly IStartupSource _startup;
    private readonly IJournalSource _journal;
    private readonly IAgentClient? _agent;
    private string _programsCount = "—";
    private string _programsSize = "";
    private string _startupText = "—";
    private string _startupHint = "";
    private string _journalText = "—";
    private string _journalHint = "";

    public OverviewViewModel(IProgramsSource programs, IStartupSource startup, IJournalSource journal, IAgentClient? agent = null)
        : base("Обзор", "Состояние системы по реальным данным. Чтение идёт без прав администратора.")
    {
        _programs = programs;
        _startup = startup;
        _journal = journal;
        _agent = agent;
        if (agent is not null) agent.AccessChanged += (_, _) => Post(() => { OnPropertyChanged(nameof(RightsTitle)); OnPropertyChanged(nameof(RightsText)); });
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }

    public string ProgramsCount { get => _programsCount; private set => SetProperty(ref _programsCount, value); }
    public string ProgramsSize { get => _programsSize; private set => SetProperty(ref _programsSize, value); }
    public string StartupText { get => _startupText; private set => SetProperty(ref _startupText, value); }
    public string StartupHint { get => _startupHint; private set => SetProperty(ref _startupHint, value); }
    public string JournalText { get => _journalText; private set => SetProperty(ref _journalText, value); }
    public string JournalHint { get => _journalHint; private set => SetProperty(ref _journalHint, value); }

    public string RightsTitle => _agent?.Access switch
    {
        AgentAccess.Connected => "Права получены",
        AgentAccess.ViewOnly => "Режим просмотра",
        AgentAccess.Lost => "Связь потеряна",
        _ => "Права не запрошены",
    };

    public string RightsText => _agent?.Access switch
    {
        AgentAccess.Connected => "Агент работает с правами администратора, изменения применяются и пишутся в журнал.",
        AgentAccess.ViewOnly => "Вы отказали в правах: всё можно смотреть, но нельзя менять. Запросить снова можно на любом экране изменений.",
        AgentAccess.Lost => "Агент перестал отвечать. Следующее изменение подключится заново.",
        _ => "Окно UAC появится при первом изменении. Просмотр работает без прав.",
    };

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var p = await _programs.GetProgramsAsync();
        var s = await _startup.GetStartupAsync();
        var j = await _journal.GetOperationsAsync();

        ProgramsCount = Fmt.Number(p.Count);
        var known = p.Sum(x => x.SizeBytes);
        ProgramsSize = known > 0 ? "по данным реестра занимают " + Fmt.Size(known) : "размер в реестре не указан";

        var enabled = s.Count(x => x.Enabled);
        StartupText = $"{enabled} из {s.Count}";
        StartupHint = "включено записей автозагрузки";

        var reversible = j.Count(x => x.Reversible);
        JournalText = Fmt.Number(j.Count);
        JournalHint = j.Count == 0 ? "изменений ещё не было" : $"можно откатить: {reversible}";

        var tips = new List<string>();
        if (enabled > 0) tips.Add($"В автозагрузке включено {enabled}. Ненужное отключите на экране «Автозагрузка»: каждое отключение можно откатить.");
        var unverified = TweakCatalog.Shared.Tweaks.Count(t => !t.Verified);
        if (unverified > 0) tips.Add($"Твиков с пометкой «не проверено»: {unverified}. Они ещё не проходили проверку в песочнице, применяйте осознанно.");
        tips.Add("Любое изменение попадает в журнал с шагами. Если что-то пошло не так, откатите его на экране «Журнал и откат».");
        Suggestions = tips;
        OnPropertyChanged(nameof(Suggestions));
        return 1;
    }, "");
}
