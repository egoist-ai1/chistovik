using Chistovik.App.Services.Windows;
using Chistovik.Core.Catalog;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Scanners;

/// <summary>Сборка набора сканеров «Чистки» для настоящей машины: инвентарь, диск, реестр. Подключает экран «Чистка».</summary>
public static class FindingScanners
{
    public static IReadOnlyList<IFindingScanner> CreateReal(IProgramsSource programs, IStartupSource startup, IServiceSource services,
        ITaskSource tasks, IRegistryReader registry, PolicyContext ctx, IProcessList processes)
        =>
        [
            new InventoryFindingScanner(programs, startup, services, tasks),
            new DiskCleanupScanner(CleanupCatalog.Shared, new FileSystemDiskMeasure(), processes),
            new RegistryOrphanScanner(registry, new RealFileExists(), new MsiState(), new CommandResolver(ctx)),
        ];
}
