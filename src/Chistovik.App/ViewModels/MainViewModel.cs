using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;

namespace Chistovik.App.ViewModels;

public sealed class NavItem
{
    public NavItem(string title, string group, bool groupStart, Func<PageViewModel> factory, string icon = "home", string hue = "Lime")
    {
        Title = title;
        Group = group;
        GroupStart = groupStart && group.Length > 0;
        Factory = factory;
        Icon = icon;
        Hue = hue;
    }

    /// <summary>Имя значка из Themes/Icons.xaml (без «Ico.»).</summary>
    public string Icon { get; }

    /// <summary>Оттенок раздела: ключ из Themes/Tokens.xaml (Lime, Blue, Orange, Cyan, Violet, Mint, Pink, Slate).</summary>
    public string Hue { get; }

    public string Title { get; }
    public string Group { get; }
    public bool GroupStart { get; }
    public Func<PageViewModel> Factory { get; }
    internal PageViewModel? Cached { get; set; }
}

public sealed class MainViewModel : ObservableObject, INavigator
{
    /// <summary>
    /// Разделы, доступные в этом релизе (0.1). Единственное место, где решается, что видно в боковом меню:
    /// чтобы вернуть раздел 0.2+, добавьте его заголовок сюда. Код и экраны скрытых разделов остаются в сборке.
    /// </summary>
    public static readonly IReadOnlyList<string> ReleaseSections = new[]
    {
        "Обзор", "Чистка", "Программы", "Режимы", "Питание", "Автозагрузка", "Службы", "Задачи", "Твики", "Журнал и откат",
    };

    /// <summary>Значок и оттенок каждого раздела: имена из Themes/Icons.xaml и Themes/Tokens.xaml.</summary>
    public static readonly IReadOnlyDictionary<string, (string Icon, string Hue)> Looks = new Dictionary<string, (string, string)>
    {
        ["Обзор"] = ("home", "Lime"),
        ["Чистка"] = ("trash", "Orange"),
        ["Программы"] = ("package", "Pink"),
        ["Предпросмотр"] = ("eye", "Pink"),
        ["Выполнение"] = ("play", "Pink"),
        ["Карантин"] = ("archive", "Pink"),
        ["Режимы"] = ("layers", "Blue"),
        ["Питание"] = ("zap", "Orange"),
        ["Автозагрузка"] = ("login", "Cyan"),
        ["Службы"] = ("server", "Violet"),
        ["Задачи"] = ("clock", "Mint"),
        ["Твики"] = ("sliders", "Pink"),
        ["Журнал и откат"] = ("history", "Slate"),
        ["Эффекты"] = ("sparkles", "Violet"),
        ["Окна"] = ("layout", "Blue"),
        ["Мониторинг"] = ("activity", "Cyan"),
        ["Доверие"] = ("shield-check", "Slate"),
        ["Настройки"] = ("settings", "Slate"),
    };

    private NavItem? _selectedNav;
    private PageViewModel? _current;
    private readonly IAgentClient _agent;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public MainViewModel(AppServices s)
    {
        IsDemo = s.IsDemo;
        UiStore = s.UiSettings;
        _agent = s.Changes.Agent;
        _agent.AccessChanged += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(AgentStatusText));
            OnPropertyChanged(nameof(AgentStatusDetail));
            OnPropertyChanged(nameof(AgentStatusIcon));
            OnPropertyChanged(nameof(AgentStatusKind));
        });
        ReloadCurrentCommand = new RelayCommand(() => (Current as StatefulPageViewModel)?.ReloadCommand.Execute(null));
        OpenByIndexCommand = new RelayCommand(OpenByIndex);
        PageViewModel Ph(string t, string d) => new PlaceholderViewModel(t, d);

        var items = new List<(string Title, string Group, Func<PageViewModel> F)>
        {
            ("Обзор", "", () => new OverviewViewModel(s.Startup, s.Journal, s.Changes, s.Services, s.Tasks, s.IsDemo, s.UiSettings, this)),
            ("Чистка", "", () => new CleanupViewModel(s.Scanner ?? new CleanupScanner(s.Programs, s.Startup, s.Services, s.Tasks), this, s.Changes, s.Dialogs, s.Icons)),
            // Единственный видимый пункт бывшей группы «Программы»: заголовок группы не нужен, пункт так и называется.
            ("Программы", "", () => new ProgramsViewModel(s.Programs, s.Dialogs, s.Changes, this, s.Icons)),
            ("Предпросмотр", "Программы", () => Ph("Предпросмотр", "Что останется после штатного деинсталлятора и что уйдёт в карантин.")),
            ("Выполнение", "Программы", () => Ph("Выполнение", "Шаги операции с проверкой на каждом.")),
            ("Карантин", "Программы", () => Ph("Карантин", "Удалённое ждёт здесь. Срок виден на каждой операции.")),
            ("Режимы", "Система", () => new ModesViewModel(s.Tweaks, s.PowerCatalog, s.Changes, s.Dialogs, this, s.Power)),
            ("Питание", "Система", () => new PowerViewModel(s.PowerCatalog, s.Power, s.Changes, s.Dialogs, this)),
            ("Автозагрузка", "Система", () => new StartupViewModel(s.Startup, s.Changes, this, s.Dialogs, s.Icons)),
            ("Службы", "Система", () => new ServicesViewModel(s.Services, s.Changes, s.Dialogs, this, s.Icons)),
            ("Задачи", "Система", () => new TasksViewModel(s.Tasks, s.Changes, s.Dialogs, this, s.Icons)),
            ("Твики", "Система", () => new TweaksViewModel("Твики",
                "Проверенные настройки Windows. Текущее значение читается из реестра; применение идёт через агента с записью в журнал.",
                "tw", s.Tweaks, s.TweakState, s.Changes, s.Dialogs, s.Effects, this)),
            ("Журнал и откат", "Система", () => new JournalViewModel(s.Journal, s.Changes, s.Dialogs, this)),
            ("Эффекты", "Система", () => new TweaksViewModel("Эффекты",
                "Анимации, прозрачность и плавность. Применяются сразу, без перезагрузки, где это возможно.",
                "fx", s.Tweaks, s.TweakState, s.Changes, s.Dialogs, s.Effects, this)),
            ("Окна", "Система", () => Ph("Окна", "Раскладка окон по мониторам и сохранённые расстановки.")),
            ("Мониторинг", "Система", () => Ph("Мониторинг", "Процессор, память, диск и сеть в реальном времени.")),
            ("Доверие", "Служебное", () => Ph("Доверие", "Как мы докажем точность. Своих измерений ещё нет.")),
            ("Настройки", "Служебное", () => Ph("Настройки", "Главные настройки, внешний вид и горячие клавиши.")),
        };

        // Заголовок группы ставится у первого пункта группы среди тех, что видны в этом релизе.
        var all = new List<NavItem>();
        var nav = new List<NavItem>();
        string prev = "\0";
        foreach (var (title, group, f) in items)
        {
            var (icon, hue) = Looks.TryGetValue(title, out var look) ? look : ("home", "Lime");
            all.Add(new NavItem(title, group, false, f, icon, hue));
            if (!ReleaseSections.Contains(title)) continue;
            nav.Add(new NavItem(title, group, group != prev, f, icon, hue));
            prev = group;
        }
        NavItems = nav;
        AllNavItems = all;
        var last = UiStore.Load().LastSection;
        SelectedNav = nav.FirstOrDefault(n => n.Title == last) ?? nav[0];
    }

    /// <summary>Файл ui.json (или память в демонстрации и снимках).</summary>
    public IUiSettingsStore UiStore { get; }

    /// <summary>F5: перечитать текущий раздел.</summary>
    public RelayCommand ReloadCurrentCommand { get; }

    /// <summary>Ctrl+1…8: открыть раздел по номеру в меню (параметр — строка с номером).</summary>
    public RelayCommand OpenByIndexCommand { get; }

    private void OpenByIndex(object? p)
    {
        var items = NavItems;
        if (p is string t && int.TryParse(t, out var i) && i >= 1 && i <= items.Count) SelectedNav = items[i - 1];
    }

    private void Post(Action a)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) a();
        else _ui.Post(_ => a(), null);
    }

    // ----- строка состояния агента: слово, значок-форма и пояснение -----

    public string AgentStatusText => _agent.Access switch
    {
        AgentAccess.Connected => "Агент запущен",
        AgentAccess.ViewOnly => "Только просмотр",
        AgentAccess.Lost => "Связь с агентом потеряна",
        _ => "Агент не запущен",
    };

    public string AgentStatusDetail => _agent.Access switch
    {
        AgentAccess.Connected => "Права администратора есть, изменения применяются и пишутся в журнал.",
        AgentAccess.ViewOnly => _agent.ViewOnlyReason == AgentViewOnlyReason.NoAdminAccount
            ? "Учётная запись без прав администратора: смотреть можно, менять нельзя."
            : "Права не выданы: смотреть можно, менять нельзя.",
        AgentAccess.Lost => "Следующее изменение подключит агента заново.",
        _ => "Запустится при первом изменении: Windows покажет окно UAC.",
    };

    /// <summary>Имя векторного значка: у каждого состояния своя форма, а не только цвет.</summary>
    public string AgentStatusIcon => _agent.Access switch
    {
        AgentAccess.Connected => "shield-check",
        AgentAccess.ViewOnly => "eye",
        AgentAccess.Lost => "alert",
        _ => "info",
    };

    /// <summary>ok / warn / info: цвет значка.</summary>
    public string AgentStatusKind => _agent.Access switch
    {
        AgentAccess.Connected => "ok",
        AgentAccess.Lost => "warn",
        _ => "info",
    };

    /// <summary>Видимые в меню разделы этого релиза.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Все разделы, включая скрытые 0.2+: для самопроверки и снимков.</summary>
    public IReadOnlyList<NavItem> AllNavItems { get; }

    /// <summary>Данные демонстрационные (самопроверка, предпросмотр дизайна): на экране показывается пометка.</summary>
    public bool IsDemo { get; }

    /// <summary>Переход к разделу по заголовку (из экранов: «Открыть журнал»).</summary>
    public void Open(string title)
    {
        var item = NavItems.FirstOrDefault(x => x.Title == title);
        if (item is not null) SelectedNav = item;
    }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null || !SetProperty(ref _selectedNav, value)) return;
            var revisit = value.Cached is not null;
            Current = value.Cached ??= value.Factory();
            if (revisit) Current.OnShown();
            OnPropertyChanged(nameof(SelectedTitle));
        }
    }

    public string SelectedTitle => _selectedNav?.Title ?? "";

    public PageViewModel? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }
}
