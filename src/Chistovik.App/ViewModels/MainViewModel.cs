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
    private NavItem? _selectedNav;
    private PageViewModel? _current;

    public MainViewModel(AppServices s)
    {
        IsDemo = s.IsDemo;
        PageViewModel Ph(string t, string d) => new PlaceholderViewModel(t, d);

        var items = new List<(string Title, string Group, Func<PageViewModel> F)>
        {
            ("Обзор", "", () => new OverviewViewModel(s.Programs, s.Startup, s.Journal, s.Changes.Agent, s.Services, s.Tasks)),
            ("Инвентарь", "Программы", () => new ProgramsViewModel(s.Programs, s.Dialogs)),
            ("Предпросмотр", "Программы", () => Ph("Предпросмотр", "Что останется после штатного деинсталлятора и что уйдёт в карантин.")),
            ("Выполнение", "Программы", () => Ph("Выполнение", "Шаги операции с проверкой на каждом.")),
            ("Журнал и откат", "Программы", () => new JournalViewModel(s.Journal, s.Changes, s.Dialogs, this)),
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
            ("Эффекты", "Система", () => new TweaksViewModel("Эффекты",
                "Анимации, прозрачность и плавность. Применяются сразу, без перезагрузки, где это возможно.",
                "fx", s.Tweaks, s.TweakState, s.Changes, s.Dialogs, s.Effects, this)),
            ("Окна", "Система", () => Ph("Окна", "Раскладка окон по мониторам и сохранённые расстановки.")),
            ("Мониторинг", "Система", () => Ph("Мониторинг", "Процессор, память, диск и сеть в реальном времени.")),
            ("Доверие", "Служебное", () => Ph("Доверие", "Как мы докажем точность. Своих измерений ещё нет.")),
            ("Настройки", "Служебное", () => Ph("Настройки", "Главные настройки, внешний вид и горячие клавиши.")),
        };

        var nav = new List<NavItem>();
        string prev = "\0";
        foreach (var (title, group, f) in items)
        {
            nav.Add(new NavItem(title, group, group != prev, f));
            prev = group;
        }
        NavItems = nav;
        SelectedNav = nav[0];
    }

    public IReadOnlyList<NavItem> NavItems { get; }

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
