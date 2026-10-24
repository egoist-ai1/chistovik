using Chistovik.App.Mvvm;
using Chistovik.App.Services;

namespace Chistovik.App.ViewModels;

public sealed class NavItem
{
    public NavItem(string title, string group, bool groupStart, Func<PageViewModel> factory)
    {
        Title = title;
        Group = group;
        GroupStart = groupStart && group.Length > 0;
        Factory = factory;
    }

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
        "Обзор", "Режимы", "Питание", "Автозагрузка", "Службы", "Задачи", "Твики", "Журнал и откат",
    };

    private NavItem? _selectedNav;
    private PageViewModel? _current;

    public MainViewModel(AppServices s)
    {
        IsDemo = s.IsDemo;
        PageViewModel Ph(string t, string d) => new PlaceholderViewModel(t, d);

        var items = new List<(string Title, string Group, Func<PageViewModel> F)>
        {
            ("Обзор", "", () => new OverviewViewModel(s.Startup, s.Journal, s.Changes, s.Services, s.Tasks, s.IsDemo)),
            ("Инвентарь", "Программы", () => new ProgramsViewModel(s.Programs, s.Dialogs)),
            ("Предпросмотр", "Программы", () => Ph("Предпросмотр", "Что останется после штатного деинсталлятора и что уйдёт в карантин.")),
            ("Выполнение", "Программы", () => Ph("Выполнение", "Шаги операции с проверкой на каждом.")),
            ("Карантин", "Программы", () => Ph("Карантин", "Удалённое ждёт здесь. Срок виден на каждой операции.")),
            ("Очистка диска", "Программы", () => Ph("Очистка диска", "Временные файлы и кэш. Только вручную.")),
            ("Режимы", "Система", () => new ModesViewModel(s.Tweaks, s.PowerCatalog, s.Changes, s.Dialogs, this)),
            ("Питание", "Система", () => new PowerViewModel(s.PowerCatalog, s.Power, s.Changes, s.Dialogs, this)),
            ("Автозагрузка", "Система", () => new StartupViewModel(s.Startup, s.Changes, this)),
            ("Службы", "Система", () => new ServicesViewModel(s.Services, s.Changes, s.Dialogs, this)),
            ("Задачи", "Система", () => new TasksViewModel(s.Tasks, s.Changes, s.Dialogs, this)),
            ("Твики", "Система", () => new TweaksViewModel("Твики реестра",
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
            all.Add(new NavItem(title, group, false, f));
            if (!ReleaseSections.Contains(title)) continue;
            nav.Add(new NavItem(title, group, group != prev, f));
            prev = group;
        }
        NavItems = nav;
        AllNavItems = all;
        SelectedNav = nav[0];
    }

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
            Current = value.Cached ??= value.Factory();
        }
    }

    public PageViewModel? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }
}
