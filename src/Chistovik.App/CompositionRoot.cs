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
        var policy = new ServicePolicy(PolicyContext.FromEnvironment());
        IReadOnlyList<InstalledProgram>? installed = null;
        IReadOnlyList<InstalledProgram> Installed() => installed ??= InventoryProgramsSource.Read(registry);

        return new AppServices
        {
            Programs = new InventoryProgramsSource(registry),
            Startup = new RegistryStartupSource(registry, new DirectoryFileSystem(),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)),
            Journal = new JournalSource(CreateJournalReader),
            Services = new RegistryServiceSource(registry, Installed, new UnknownFileTrust(), policy),
            Tasks = new FolderTaskSource(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks"),
                new ConservativeTaskClassifier()),
            TweakState = new RegistryTweakStateSource(registry),
            Power = new PowrProfPowerSource(power),
            Effects = effects,
            Changes = new ChangeService(agent, effects, tweaks),
            Dialogs = dialogs,
            Tweaks = tweaks,
            PowerCatalog = power,
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
