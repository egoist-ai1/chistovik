using Chistovik.App.Models;
using Chistovik.App.Services.Scanners;
using Chistovik.Core.Findings;
using Chistovik.Core.Policy;

namespace Chistovik.App.Services;

/// <summary>Сканирование для экрана «Чистка»: список находок с вердиктами. Только чтение, ничего не меняет.</summary>
public interface ICleanupScanner
{
    Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// Собирает находки из сканеров областей (<see cref="IFindingScanner"/>): инвентарь, диск, реестр. Сканеры идут параллельно;
/// исключение любого из них всплывает, экран покажет ошибку.
/// Старый конструктор (четыре источника) строит один <see cref="InventoryFindingScanner"/>.
/// Класс «можно менять» берётся у источника как есть: совет не расширяет права.
/// </summary>
public sealed class CleanupScanner : ICleanupScanner
{
    private readonly IReadOnlyList<IFindingScanner> _scanners;

    public CleanupScanner(IEnumerable<IFindingScanner> scanners) => _scanners = scanners.ToList();

    public CleanupScanner(IProgramsSource programs, IStartupSource startup, IServiceSource services, ITaskSource tasks,
        FindingClassifier? classifier = null)
        : this([new InventoryFindingScanner(programs, startup, services, tasks, classifier)])
    {
    }

    public async Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default)
    {
        var tasks = _scanners.Select(s => s.ScanAsync(ct)).ToList();
        await Task.WhenAll(tasks).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return tasks.SelectMany(t => t.Result).ToList();
    }

    internal static ChangeClass Map(Changeability c) => c switch
    {
        Changeability.Modifiable => ChangeClass.Modifiable,
        Changeability.Forbidden => ChangeClass.Forbidden,
        _ => ChangeClass.ReadOnly,
    };

    internal static StartupSubject ToSubject(StartupInfo s) => new(
        s.EntryId, s.Name, s.Command, s.Origin, Map(s.Class), s.ReadOnlyReason ?? "",
        s.ExePath, s.FileMissing, s.SignatureValid, s.MicrosoftSigned);

    internal static ServiceSubject ToSubject(ServiceInfo s) => new(s.Name, s.DisplayName, s.ImagePath, Map(s.Class), s.Reason);

    internal static TaskSubject ToSubject(TaskInfo t) => new(t.Path, t.Name, t.Summary, Map(t.Class), t.Reason);

    internal static ProgramSubject ToSubject(ProgramInfo p) => new(p.Name, p.Publisher, p.Version, p.SizeBytes, p.Source, p.Id, Map(p.Class), p.ReadOnlyReason ?? "");
}
