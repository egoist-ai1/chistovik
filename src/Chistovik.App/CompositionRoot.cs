using System.IO;
using Chistovik.App.Mocks;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.App.Services.Windows;
using Chistovik.Core.Catalog;
using Chistovik.Core.Inventory;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.Native.Services;
using Chistovik.Native.Setup;
using Chistovik.Native.Tasks;
using Chistovik.Native.Trust;

namespace Chistovik.App;

/// <summary>Всё, что нужно главному окну. Собирается в одном месте: реальный вариант или демонстрационный.</summary>
public sealed class AppServices
{
    public required IProgramsSource Programs { get; init; }
    public required IStartupSource Startup { get; init; }
    public required IJournalSource Journal { get; init; }
    public required IServiceSource Services { get; init; }
    public required ITaskSource Tasks { get; init; }
    public required ITweakStateSource TweakState { get; init; }
    public required IPowerSource Power { get; init; }
    public required IShellEffects Effects { get; init; }
    public required IChangeService Changes { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required TweakCatalog Tweaks { get; init; }
    public required PowerCatalog PowerCatalog { get; init; }

    /// <summary>true — данные демонстрационные (самопроверка и предпросмотр дизайна), на экране об этом написано.</summary>
    public bool IsDemo { get; init; }

    /// <summary>Размер и позиция окна, последний раздел, флаг приветствия. По умолчанию без диска.</summary>
    public IUiSettingsStore UiSettings { get; init; } = new MemoryUiSettingsStore();
}

public static class CompositionRoot
{
    /// <summary>Файл журнала агента. Его создаёт и пишет только агент; App читает с Mode=ReadOnly.</summary>
    public static string JournalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Chistovik", "chistovik.db");

    /// <summary>
    /// Читатель журнала. Единственное место, где подключается реализация: нет файла (агент ничего не менял) —
    /// <see cref="NullJournalReader"/>; SqliteJournalReader сам бросит JournalException(NotFound), источник это учитывает.
    /// </summary>
    public static IJournalReader CreateJournalReader() => new SqliteJournalReader(JournalPath);

    public static AppServices CreateReal(IDialogService dialogs)
    {
        var registry = new ReadOnlyRegistryReader();
        var tweaks = TweakCatalog.Shared;
        var power = PowerCatalog.Shared;
        var effects = new WindowsShellEffects();
        var agent = new AgentClient(new ProcessAgentLauncher(), new NamedPipeAgentTransport());
        var ctx = PolicyContext.FromEnvironment();
        // Подпись проверяет настоящий WinVerifyTrust (только чтение файла); результат запоминается по пути.
        var trust = new CachingFileTrust(new WindowsFileTrust());
        IReadOnlyList<InstalledProgram>? installed = null;
        IReadOnlyList<InstalledProgram> Installed() => installed ??= InventoryProgramsSource.Read(registry);

        return new AppServices
        {
            // Подсказка «можно удалить» считается той же политикой, что у агента; файлы проверяются только на чтение.
            Programs = new InventoryProgramsSource(registry, new PackagePolicy(ctx, new WindowsFileSecurity())),
            Startup = new RegistryStartupSource(registry, new DirectoryFileSystem(),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                new StartupPolicy(ctx), trust, File.Exists),
            Journal = new JournalSource(CreateJournalReader),
            Services = new ScmServiceSource(new WindowsServiceControl(), registry, Installed, trust, new ServicePolicy(ctx)),
            Tasks = new SchedulerTaskSource(new WindowsTaskScheduler(), new TaskPolicy(ctx)),
            TweakState = new RegistryTweakStateSource(registry),
            Power = new PowrProfPowerSource(power),
            Effects = effects,
            Changes = new ChangeService(agent, effects, tweaks),
            Dialogs = dialogs,
            Tweaks = tweaks,
            PowerCatalog = power,
            UiSettings = new FileUiSettingsStore(FileUiSettingsStore.DefaultPath),
        };
    }

    /// <summary>Демонстрационный набор для --selftest и --demo: подставные данные, агент не запускается.</summary>
    public static AppServices CreateDemo(IDialogService dialogs)
    {
        var tweaks = TweakCatalog.Shared;
        var effects = new DemoShellEffects();
        return new AppServices
        {
            Programs = new MockProgramsSource(),
            Startup = new MockStartupSource(),
            Journal = new MockJournalSource(),
            Services = new MockServiceSource(),
            Tasks = new MockTaskSource(),
            TweakState = new MockTweakStateSource(),
            Power = new MockPowerSource(),
            Effects = effects,
            Changes = new ChangeService(new DemoAgentClient(), effects, tweaks),
            Dialogs = dialogs,
            Tweaks = tweaks,
            PowerCatalog = PowerCatalog.Shared,
            IsDemo = true,
        };
    }
}
