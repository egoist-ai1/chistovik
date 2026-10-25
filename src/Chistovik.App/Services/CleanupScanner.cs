using Chistovik.App.Models;
using Chistovik.Core.Findings;
using Chistovik.Core.Policy;

namespace Chistovik.App.Services;

/// <summary>Сканирование для экрана «Чистка»: список находок с вердиктами. Только чтение, ничего не меняет.</summary>
public interface ICleanupScanner
{
    Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default);
}

/// <summary>
/// Читает те же источники, что экраны «Инвентарь», «Автозагрузка», «Службы» и «Задачи», и превращает каждую запись в находку.
/// Класс «можно менять» берётся у источника как есть: совет не расширяет права.
/// </summary>
public sealed class CleanupScanner(IProgramsSource programs, IStartupSource startup, IServiceSource services, ITaskSource tasks,
    FindingClassifier? classifier = null) : ICleanupScanner
{
    private readonly FindingClassifier _classifier = classifier ?? FindingClassifier.Shared;

    public async Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default)
    {
        var startupTask = startup.GetStartupAsync(ct);
        var servicesTask = services.GetServicesAsync(ct);
        var tasksTask = tasks.GetTasksAsync(ct);
        var programsTask = programs.GetProgramsAsync();
        await Task.WhenAll(startupTask, servicesTask, tasksTask, programsTask).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        var all = new List<Finding>();
        all.AddRange(startupTask.Result.Select(x => _classifier.Startup(ToSubject(x))));
        all.AddRange(servicesTask.Result.Select(x => _classifier.Service(ToSubject(x))));
        all.AddRange(tasksTask.Result.Select(x => _classifier.Task(ToSubject(x))));
        all.AddRange(programsTask.Result.Select(x => _classifier.Program(ToSubject(x))));
        return all;
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

    internal static ProgramSubject ToSubject(ProgramInfo p) => new(p.Name, p.Publisher, p.Version, p.SizeBytes, p.Source);
}
