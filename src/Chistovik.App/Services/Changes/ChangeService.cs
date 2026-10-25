using Chistovik.App.Services.Agent;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Changes;

public enum OutcomeKind
{
    Success,
    NoChange,
    /// <summary>Часть шагов выполнена, остальное нет.</summary>
    Partial,
    /// <summary>Откат упёрся в изменения после операции; нужен выбор пользователя.</summary>
    Conflict,
    /// <summary>Политика или проверка отказала: ничего не изменено.</summary>
    Denied,
    /// <summary>Нет прав: режим просмотра.</summary>
    ViewOnly,
    /// <summary>Нет связи с агентом, таймаут, внутренняя ошибка.</summary>
    Unavailable,
    Cancelled,
}

/// <summary>Итог одного запроса в виде, готовом для показа пользователю.</summary>
public sealed record ChangeOutcome(
    OutcomeKind Kind,
    string Message,
    Guid? OperationId = null,
    int StepsDone = 0,
    int StepsTotal = 0,
    IReadOnlyList<ConflictInfo>? Conflicts = null,
    string? RestartHint = null,
    AgentResponse? Response = null,
    IReadOnlyList<FactEntry>? Facts = null)
{
    public bool Changed => Kind is OutcomeKind.Success or OutcomeKind.Partial;
}

public interface IChangeService
{
    /// <summary>Есть ли смысл показывать кнопки применения (false — режим просмотра).</summary>
    bool CanApply { get; }

    IAgentClient Agent { get; }

    event Action<ChangeOutcome>? Completed;

    Task<ChangeOutcome> ApplyTweaksAsync(IReadOnlyList<TweakChange> changes, CancellationToken ct = default);
    Task<ChangeOutcome> ApplyModeAsync(string modeId, CancellationToken ct = default);
    Task<ChangeOutcome> SetStartupAsync(string entryId, bool enabled, CancellationToken ct = default);
    Task<ChangeOutcome> SetServiceStartAsync(string serviceName, StartMode start, CancellationToken ct = default);
    Task<ChangeOutcome> SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default);
    Task<ChangeOutcome> SetPowerSchemeAsync(PowerPlan plan, CancellationToken ct = default);
    Task<ChangeOutcome> SetPowerSettingAsync(string settingId, uint? ac, uint? dc, CancellationToken ct = default);
    Task<ChangeOutcome> UndoAsync(Guid operationId, IReadOnlyList<UndoResolution>? resolutions, CancellationToken ct = default);

    /// <summary>Удаляет программу компьютера (arp:machine:...) штатным деинсталлятором через агента. Вернуть её откатом нельзя.</summary>
    Task<ChangeOutcome> UninstallAsync(string programId, CancellationToken ct = default);

    /// <summary>Измеряет категории очистки диска (идентификаторы каталога) через агента: ничего не удаляется, размеры в <c>Facts</c>.</summary>
    Task<ChangeOutcome> MeasureCleanupAsync(IReadOnlyList<string> categoryIds, CancellationToken ct = default);

    /// <summary>Очищает категории диска. Удаление необратимо, откатить его нельзя.</summary>
    Task<ChangeOutcome> CleanAsync(IReadOnlyList<string> categoryIds, CancellationToken ct = default);

    /// <summary>
    /// Убирает записи реестра по идентификаторам находок; агент заново проверяет доказательства и сохраняет копии для отката.
    /// <paramref name="dryRun"/>: только проверить, ничего не писать. <paramref name="expertConfirmations"/>: идентификаторы,
    /// подтверждённые отдельно (крупные ключи).
    /// </summary>
    Task<ChangeOutcome> ApplyFindingsAsync(IReadOnlyList<FindingAction> items, bool dryRun,
        IReadOnlyList<string>? expertConfirmations = null, CancellationToken ct = default);
}

/// <summary>
/// Собирает запросы из выбора пользователя, шлёт их агенту и переводит ответ на понятный язык.
/// После успешного применения запускает эффекты (SPI, WM_SETTINGCHANGE) для затронутых твиков.
/// </summary>
public sealed class ChangeService : IChangeService
{
    readonly IAgentClient _agent;
    readonly IShellEffects _effects;
    readonly TweakCatalog _catalog;
    readonly Func<Guid> _newId;

    public ChangeService(IAgentClient agent, IShellEffects effects, TweakCatalog? catalog = null, Func<Guid>? newId = null)
    {
        _agent = agent;
        _effects = effects;
        _catalog = catalog ?? TweakCatalog.Shared;
        _newId = newId ?? Guid.NewGuid;
    }

    public IAgentClient Agent => _agent;

    public bool CanApply => _agent.Access != AgentAccess.ViewOnly;

    public event Action<ChangeOutcome>? Completed;

    public Task<ChangeOutcome> ApplyTweaksAsync(IReadOnlyList<TweakChange> changes, CancellationToken ct = default)
        => RunAsync(new ApplyTweaksRequest { Id = _newId(), Changes = changes }, ResponseText.ForApply,
            ids: changes.Select(c => c.TweakId).ToList(), ct);

    public Task<ChangeOutcome> ApplyModeAsync(string modeId, CancellationToken ct = default)
        => RunAsync(new ApplyModeRequest { Id = _newId(), ModeId = modeId }, ResponseText.ForApply,
            ids: _catalog.FindMode(modeId)?.Tweaks.Keys.ToList(), ct);

    public Task<ChangeOutcome> SetStartupAsync(string entryId, bool enabled, CancellationToken ct = default)
        => RunAsync(new SetStartupEntryRequest { Id = _newId(), EntryId = entryId, Enabled = enabled }, ResponseText.ForApply, null, ct);

    public Task<ChangeOutcome> SetServiceStartAsync(string serviceName, StartMode start, CancellationToken ct = default)
        => RunAsync(new SetServiceStartRequest { Id = _newId(), ServiceName = serviceName, Start = start }, ResponseText.ForApply, null, ct);

    public Task<ChangeOutcome> SetTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default)
        => RunAsync(new SetTaskEnabledRequest { Id = _newId(), TaskPath = taskPath, Enabled = enabled }, ResponseText.ForApply, null, ct);

    public Task<ChangeOutcome> SetPowerSchemeAsync(PowerPlan plan, CancellationToken ct = default)
        => RunAsync(new SetPowerSchemeRequest { Id = _newId(), Plan = plan }, ResponseText.ForApply, null, ct);

    public Task<ChangeOutcome> SetPowerSettingAsync(string settingId, uint? ac, uint? dc, CancellationToken ct = default)
        => RunAsync(new SetPowerSettingRequest { Id = _newId(), SettingId = settingId, Ac = ac, Dc = dc }, ResponseText.ForApply, null, ct);

    public Task<ChangeOutcome> UndoAsync(Guid operationId, IReadOnlyList<UndoResolution>? resolutions, CancellationToken ct = default)
        => RunAsync(new UndoRequest { Id = _newId(), OperationId = operationId, Resolutions = resolutions }, ResponseText.ForUndo, null, ct);

    public Task<ChangeOutcome> UninstallAsync(string programId, CancellationToken ct = default)
        => RunAsync(new UninstallRequest { Id = _newId(), FindingId = programId }, ResponseText.ForUninstall, null, ct);

    public Task<ChangeOutcome> MeasureCleanupAsync(IReadOnlyList<string> categoryIds, CancellationToken ct = default)
        => RunAsync(new CleanCategoriesRequest { Id = _newId(), Categories = categoryIds, MeasureOnly = true },
            ResponseText.ForMeasure, null, ct);

    public Task<ChangeOutcome> CleanAsync(IReadOnlyList<string> categoryIds, CancellationToken ct = default)
        => RunAsync(new CleanCategoriesRequest { Id = _newId(), Categories = categoryIds, MeasureOnly = false },
            ResponseText.ForCleanup, null, ct);

    public Task<ChangeOutcome> ApplyFindingsAsync(IReadOnlyList<FindingAction> items, bool dryRun,
        IReadOnlyList<string>? expertConfirmations = null, CancellationToken ct = default)
        => RunAsync(new ApplyFindingsRequest
        {
            Id = _newId(),
            Items = items,
            DryRun = dryRun,
            ExpertConfirmations = expertConfirmations is { Count: > 0 } ? expertConfirmations : null,
        }, ResponseText.ForFindings, null, ct);

    async Task<ChangeOutcome> RunAsync(AgentRequest request, Func<AgentResponse, ChangeOutcome> map,
        IReadOnlyList<string>? ids, CancellationToken ct)
    {
        ChangeOutcome outcome;
        try
        {
            // Предварительная проверка поля-в-поле: тот же код, что у агента; плохой запрос не доходит до UAC.
            var problem = request.Validate();
            if (problem is not null)
                outcome = new ChangeOutcome(OutcomeKind.Denied, "Запрос не прошёл проверку: " + problem);
            else if (_agent.Access == AgentAccess.ViewOnly)
                outcome = ResponseText.ViewOnly();
            else
            {
                var response = await _agent.SendAsync(request, ct);
                outcome = map(response);
                if (outcome.Changed && ids is not null) outcome = ApplyEffects(outcome, ids);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = new ChangeOutcome(OutcomeKind.Cancelled, "Отменено. Если запрос уже ушёл, проверьте журнал.");
        }

        Completed?.Invoke(outcome);
        return outcome;
    }

    ChangeOutcome ApplyEffects(ChangeOutcome outcome, IReadOnlyList<string> tweakIds)
    {
        var restart = new HashSet<string>(StringComparer.Ordinal);
        var effects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in tweakIds)
        {
            var t = _catalog.FindTweak(id);
            if (t is null) continue;
            if (t.Effect != "none") effects.Add(t.Effect);
            if (t.Restart != "none") restart.Add(t.Restart);
        }

        string? effectProblem = null;
        foreach (var e in effects)
        {
            try { _effects.Apply(e); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                effectProblem = "Система приняла изменение, но сразу показать его не удалось: " + ex.Message;
            }
        }

        var hint = ResponseText.RestartHint(restart);
        var msg = outcome.Message;
        if (effectProblem is not null) msg += " " + effectProblem;
        return outcome with { Message = msg, RestartHint = hint };
    }
}
