using Chistovik.App.Models;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Services;

public interface IProgramsSource { Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(); }

public interface IStartupSource { Task<IReadOnlyList<StartupInfo>> GetStartupAsync(); }

public interface IJournalSource
{
    /// <summary>Операции от новых к старым. Нет журнала (агент ещё ничего не менял) — пустой список, не ошибка.</summary>
    Task<IReadOnlyList<JournalOperation>> GetOperationsAsync();

    Task<IReadOnlyList<JournalStep>> GetStepsAsync(Guid operationId);
}

public interface IServiceSource { Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(); }

public interface ITaskSource { Task<IReadOnlyList<TaskInfo>> GetTasksAsync(); }

/// <summary>Текущее значение твиков в реестре (только чтение, без прав).</summary>
public interface ITweakStateSource { Task<IReadOnlyDictionary<string, TweakState>> ReadAsync(IReadOnlyList<TweakEntry> tweaks); }

public interface IPowerSource { Task<PowerSnapshot> ReadAsync(); }

/// <summary>Применённые эффекты после ответа агента: SystemParametersInfo без UPDATEINIFILE, WM_SETTINGCHANGE, перезапуск Проводника.</summary>
public interface IShellEffects
{
    /// <summary>effect из tweaks.json: none | spi-animation | spi-dragfull | spi-mouse | broadcast.</summary>
    void Apply(string effect);

    void RestartExplorer();
}

/// <summary>Переход к разделу по заголовку (например, «Журнал и откат»).</summary>
public interface INavigator { void Open(string title); }

public sealed record ConfirmContent(string Title, string Message, string ConfirmText, bool Danger = false);

public interface IDialogService
{
    void ShowStub(string title, string message);

    /// <summary>Окно подтверждения; true — пользователь нажал кнопку действия.</summary>
    bool Confirm(ConfirmContent content);

    /// <summary>Окно конфликтов отката. null — отменить; иначе решение по каждому конфликту.</summary>
    IReadOnlyList<UndoResolution>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts);
}
