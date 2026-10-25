using Chistovik.Core.Findings;

namespace Chistovik.App.Services.Scanners;

/// <summary>
/// Прежняя логика «Чистки»: читает те же источники, что экраны «Инвентарь», «Автозагрузка», «Службы» и «Задачи»,
/// и превращает каждую запись в находку. Класс «можно менять» берётся у источника как есть: совет не расширяет права.
/// Находки четырёх областей идут одним списком, поэтому <see cref="Area"/> здесь условная (Programs).
/// </summary>
public sealed class InventoryFindingScanner(IProgramsSource programs, IStartupSource startup, IServiceSource services, ITaskSource tasks,
    FindingClassifier? classifier = null) : IFindingScanner
{
    private readonly FindingClassifier _classifier = classifier ?? FindingClassifier.Shared;

    public FindingArea Area => FindingArea.Programs;

    public async Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default)
    {
        var startupTask = startup.GetStartupAsync(ct);
        var servicesTask = services.GetServicesAsync(ct);
        var tasksTask = tasks.GetTasksAsync(ct);
        var programsTask = programs.GetProgramsAsync();
        await Task.WhenAll(startupTask, servicesTask, tasksTask, programsTask).WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        var all = new List<Finding>();
        // Источник значка берётся из той же записи, что и находка: файл программы, команда автозагрузки, образ службы, действие задачи.
        all.AddRange(startupTask.Result.Select(x => _classifier.Startup(CleanupScanner.ToSubject(x)) with { IconSource = x.IconSource ?? x.ExePath ?? x.Command }));
        all.AddRange(servicesTask.Result.Select(x => _classifier.Service(CleanupScanner.ToSubject(x)) with { IconSource = x.IconSource ?? x.ImagePath }));
        all.AddRange(tasksTask.Result.Select(x => _classifier.Task(CleanupScanner.ToSubject(x)) with { IconSource = x.IconSource }));
        all.AddRange(programsTask.Result.Select(x => _classifier.Program(CleanupScanner.ToSubject(x)) with { IconSource = x.IconSource }));
        return all;
    }
}
