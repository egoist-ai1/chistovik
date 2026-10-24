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

public sealed class MainViewModel : ObservableObject
{
    private NavItem? _selectedNav;
    private PageViewModel? _current;

    public MainViewModel(IProgramsSource programs, IStartupSource startup, IJournalSource journal, IDialogService dialogs)
    {
        PageViewModel Ph(string t, string s) => new PlaceholderViewModel(t, s);

        var items = new List<(string Title, string Group, Func<PageViewModel> F)>
        {
            ("Обзор", "", () => new OverviewViewModel(programs, startup, journal)),
            ("Инвентарь", "Программы", () => new ProgramsViewModel(programs, dialogs)),
            ("Предпросмотр", "Программы", () => Ph("Предпросмотр", "Что останется после штатного деинсталлятора и что уйдёт в карантин.")),
            ("Выполнение", "Программы", () => Ph("Выполнение", "Шаги операции с проверкой на каждом.")),
            ("Чек операции", "Программы", () => Ph("Чек операции", "Что сделано, что можно вернуть и чем это подтверждено.")),
            ("Журнал и откат", "Программы", () => new JournalViewModel(journal, dialogs)),
            ("Карантин", "Программы", () => Ph("Карантин", "Удалённое ждёт здесь. Срок виден на каждой операции.")),
            ("Защита", "Программы", () => Ph("Защита", "Уровни уверенности и зоны, которые программа не трогает никогда.")),
            ("Очистка диска", "Программы", () => Ph("Очистка диска", "Временные файлы и кэш. Только вручную.")),
            ("Режимы", "Система", () => Ph("Режимы", "Готовые наборы настроек одним нажатием, всё с откатом.")),
            ("Питание", "Система", () => Ph("Питание", "Планы электропитания и их настройка для сети и батареи.")),
            ("Автозагрузка", "Система", () => new StartupViewModel(startup)),
            ("Службы и задачи", "Система", () => Ph("Службы и задачи", "Тип запуска служб и задачи планировщика. Защищённые службы заблокированы.")),
            ("Реестр", "Система", () => Ph("Реестр", "Проверенные твики и обозреватель ключей.")),
            ("Эффекты", "Система", () => Ph("Эффекты", "Плавность, анимации и прозрачность.")),
            ("Окна", "Система", () => Ph("Окна", "Раскладка окон по мониторам и сохранённые расстановки.")),
            ("Мониторинг", "Система", () => Ph("Мониторинг", "Процессор, память, диск и сеть в реальном времени.")),
            ("Изменения", "Система", () => Ph("Изменения", "Каждая правка системы с командами и откатом.")),
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
