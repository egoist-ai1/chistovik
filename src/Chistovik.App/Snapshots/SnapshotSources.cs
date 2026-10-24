using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Snapshots;

// ДЕМО: источники для снимков экранов. Ни реестр, ни службы, ни файлы системы не читаются и не меняются.

internal sealed class EmptySources : IProgramsSource, IStartupSource, IServiceSource, ITaskSource, IJournalSource
{
    Task<IReadOnlyList<ProgramInfo>> IProgramsSource.GetProgramsAsync() => Task.FromResult<IReadOnlyList<ProgramInfo>>(Array.Empty<ProgramInfo>());
    Task<IReadOnlyList<StartupInfo>> IStartupSource.GetStartupAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StartupInfo>>(Array.Empty<StartupInfo>());
    Task<IReadOnlyList<ServiceInfo>> IServiceSource.GetServicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServiceInfo>>(Array.Empty<ServiceInfo>());
    Task<IReadOnlyList<TaskInfo>> ITaskSource.GetTasksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskInfo>>(Array.Empty<TaskInfo>());
    Task<IReadOnlyList<JournalOperation>> IJournalSource.GetOperationsAsync() => Task.FromResult<IReadOnlyList<JournalOperation>>(Array.Empty<JournalOperation>());
    Task<IReadOnlyList<JournalStep>> IJournalSource.GetStepsAsync(Guid operationId) => Task.FromResult<IReadOnlyList<JournalStep>>(Array.Empty<JournalStep>());
}

/// <summary>Источники, которые не отвечают: экран остаётся в состоянии «Загружаем…».</summary>
internal sealed class HangingSources : IProgramsSource, IStartupSource, IServiceSource, ITaskSource, IJournalSource, ITweakStateSource, IPowerSource
{
    private static async Task<T> Never<T>(CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("недостижимо");
    }

    Task<IReadOnlyList<ProgramInfo>> IProgramsSource.GetProgramsAsync() => Never<IReadOnlyList<ProgramInfo>>();
    Task<IReadOnlyList<StartupInfo>> IStartupSource.GetStartupAsync(CancellationToken ct) => Never<IReadOnlyList<StartupInfo>>(ct);
    Task<IReadOnlyList<ServiceInfo>> IServiceSource.GetServicesAsync(CancellationToken ct) => Never<IReadOnlyList<ServiceInfo>>(ct);
    Task<IReadOnlyList<TaskInfo>> ITaskSource.GetTasksAsync(CancellationToken ct) => Never<IReadOnlyList<TaskInfo>>(ct);
    Task<IReadOnlyList<JournalOperation>> IJournalSource.GetOperationsAsync() => Never<IReadOnlyList<JournalOperation>>();
    Task<IReadOnlyList<JournalStep>> IJournalSource.GetStepsAsync(Guid operationId) => Never<IReadOnlyList<JournalStep>>();
    Task<IReadOnlyDictionary<string, TweakState>> ITweakStateSource.ReadAsync(IReadOnlyList<TweakEntry> tweaks) => Never<IReadOnlyDictionary<string, TweakState>>();
    Task<PowerSnapshot> IPowerSource.ReadAsync() => Never<PowerSnapshot>();
}

/// <summary>Источники, которые падают: экран показывает ошибку загрузки.</summary>
internal sealed class FailingSources : IProgramsSource, IStartupSource, IServiceSource, ITaskSource, IJournalSource, ITweakStateSource, IPowerSource
{
    private static Exception Boom() => new InvalidOperationException("демо: источник данных недоступен");

    Task<IReadOnlyList<ProgramInfo>> IProgramsSource.GetProgramsAsync() => Task.FromException<IReadOnlyList<ProgramInfo>>(Boom());
    Task<IReadOnlyList<StartupInfo>> IStartupSource.GetStartupAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<StartupInfo>>(Boom());
    Task<IReadOnlyList<ServiceInfo>> IServiceSource.GetServicesAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<ServiceInfo>>(Boom());
    Task<IReadOnlyList<TaskInfo>> ITaskSource.GetTasksAsync(CancellationToken ct) => Task.FromException<IReadOnlyList<TaskInfo>>(Boom());
    Task<IReadOnlyList<JournalOperation>> IJournalSource.GetOperationsAsync() => Task.FromException<IReadOnlyList<JournalOperation>>(Boom());
    Task<IReadOnlyList<JournalStep>> IJournalSource.GetStepsAsync(Guid operationId) => Task.FromException<IReadOnlyList<JournalStep>>(Boom());
    Task<IReadOnlyDictionary<string, TweakState>> ITweakStateSource.ReadAsync(IReadOnlyList<TweakEntry> tweaks) => Task.FromException<IReadOnlyDictionary<string, TweakState>>(Boom());
    Task<PowerSnapshot> IPowerSource.ReadAsync() => Task.FromException<PowerSnapshot>(Boom());
}

/// <summary>Агент в режиме просмотра: права не выданы, запросы не уходят.</summary>
internal sealed class ViewOnlyAgent : IAgentClient
{
    public AgentAccess Access => AgentAccess.ViewOnly;
    public bool IsElevated => false;
    public event EventHandler? AccessChanged { add { } remove { } }
    public void AllowElevationRetry() { }

    public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default)
        => Task.FromResult(new AgentResponse(request.Id, false, ClientCodes.Declined, "демо: режим просмотра"));
}

/// <summary>Диалоги снимков ничего не показывают: окна подтверждения рисуются отдельно, по запросу раннера.</summary>
internal sealed class SilentDialogs : IDialogService
{
    public void ShowStub(string title, string message) { }
    public bool Confirm(ConfirmContent content) => false;
    public IReadOnlyList<UndoResolution>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts) => null;
}

internal static class SnapshotServices
{
    /// <summary>Набор служб для состояния снимка. Основа — демонстрационный набор приложения.</summary>
    public static AppServices Create(SnapshotState state)
    {
        var baseSet = CompositionRoot.CreateDemo(new SilentDialogs());
        switch (state)
        {
            case SnapshotState.Empty:
            {
                var e = new EmptySources();
                return With(baseSet, programs: e, startup: e, journal: e, services: e, tasks: e);
            }
            case SnapshotState.Loading:
            {
                var h = new HangingSources();
                return With(baseSet, h, h, h, h, h, h, h);
            }
            case SnapshotState.Error:
            {
                var f = new FailingSources();
                return With(baseSet, f, f, f, f, f, f, f);
            }
            case SnapshotState.ViewOnly:
                return With(baseSet, changes: new ChangeService(new ViewOnlyAgent(), baseSet.Effects, baseSet.Tweaks));
            default:
                return baseSet;
        }
    }

    private static AppServices With(AppServices s,
        IProgramsSource? programs = null, IStartupSource? startup = null, IJournalSource? journal = null,
        IServiceSource? services = null, ITaskSource? tasks = null, ITweakStateSource? tweakState = null,
        IPowerSource? power = null, IChangeService? changes = null) => new()
    {
        Programs = programs ?? s.Programs,
        Startup = startup ?? s.Startup,
        Journal = journal ?? s.Journal,
        Services = services ?? s.Services,
        Tasks = tasks ?? s.Tasks,
        TweakState = tweakState ?? s.TweakState,
        Power = power ?? s.Power,
        Effects = s.Effects,
        Changes = changes ?? s.Changes,
        Dialogs = s.Dialogs,
        Tweaks = s.Tweaks,
        PowerCatalog = s.PowerCatalog,
        IsDemo = true,
    };
}
