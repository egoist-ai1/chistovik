using Chistovik.App.Services.Changes;
using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.Core.Ports;

namespace Chistovik.App.ViewModels;

public static class ChangeabilityText
{
    public static string Name(Changeability c) => c switch
    {
        Changeability.Modifiable => "Можно менять",
        Changeability.ReadOnly => "Только чтение",
        _ => "Закрыто",
    };

    public static string StartName(StartMode? m) => m switch
    {
        StartMode.Automatic => "Автоматически",
        StartMode.AutomaticDelayed => "Автоматически (отложенно)",
        StartMode.Manual => "Вручную",
        StartMode.Disabled => "Отключена",
        _ => "Системный запуск",
    };

    /// <summary>Почему объект нельзя менять, человеческим языком. Пусто для изменяемых.</summary>
    public static string WhyNot(Changeability c, bool noAccess, string reason) => c == Changeability.Modifiable ? ""
        : (noAccess ? "Нет доступа: " : c == Changeability.Forbidden ? "Закрыто: " : "Только чтение: ") + reason;

    public const string ViewOnlyTip = "Режим просмотра: изменения отключены. Нажмите «Запросить права».";
    public const string BusyTip = "Дождитесь окончания предыдущего изменения.";
}

/// <summary>
/// Пользовательские экземпляры служб: в системе рядом с шаблоном «CDPUserSvc» живёт «CDPUserSvc_4b92f»
/// с тем же отображаемым именем и случайным хвостом. Хвост человеку ничего не говорит.
/// </summary>
public static partial class PerUser
{
    [System.Text.RegularExpressions.GeneratedRegex(@"_[0-9a-fA-F]{4,8}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Suffix();

    /// <summary>Имя шаблона, если <paramref name="name"/> — пользовательский экземпляр вида «Имя_1a2b3»; иначе null.</summary>
    public static string? TemplateName(string name)
    {
        var m = Suffix().Match(name);
        return m.Success && m.Index > 0 ? name[..m.Index] : null;
    }

    /// <summary>Отображаемое имя без хвоста экземпляра: убирается только такой же хвост, что и в имени службы.</summary>
    public static string CleanDisplayName(string name, string displayName)
    {
        var m = Suffix().Match(name);
        if (!m.Success || m.Index == 0) return displayName;
        return displayName.EndsWith(m.Value, StringComparison.OrdinalIgnoreCase) ? displayName[..^m.Value.Length] : displayName;
    }

    /// <summary>Убирает экземпляры, у которых в списке есть шаблон: на экране остаётся одна строка.</summary>
    public static IReadOnlyList<ServiceInfo> CollapseInstances(IReadOnlyList<ServiceInfo> services)
    {
        var names = new HashSet<string>(services.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        return services.Where(s => TemplateName(s.Name) is not { } t || !names.Contains(t)).ToList();
    }
}

public sealed class ServiceRowViewModel(ServiceInfo model) : IListRow
{
    public ServiceInfo Model { get; } = model;
    public string Name => Model.Name;
    public string DisplayName => PerUser.CleanDisplayName(Model.Name, Model.DisplayName);
    public string ClassText => Model.NoAccess ? "Нет доступа" : ChangeabilityText.Name(Model.Class);
    public string StartText => Model.NoAccess ? "Нет доступа" : ChangeabilityText.StartName(Model.Start);
    public Changeability Class => Model.Class;
    public string Reason => Model.Reason;
    public bool CarveOut => Model.CarveOut;
    public string ImagePath => Model.ImagePath;
    public string AutomationName => $"{DisplayName}, {StartText}, {ClassText}";
    public string SearchText { get; } = $"{PerUser.CleanDisplayName(model.Name, model.DisplayName)} {model.Name} {model.ImagePath}";

    /// <summary>Почему службу нельзя менять; пусто для изменяемых.</summary>
    public string NotChangeableText => ChangeabilityText.WhyNot(Class, Model.NoAccess, Reason);
}

public sealed class ServicesViewModel : ListPageViewModel<ServiceRowViewModel>
{
    private readonly IServiceSource _source;
    private readonly IDialogService _dialogs;
    private ServiceRowViewModel? _selected;

    private static readonly SortOption[] Sorts =
    {
        new("name", "По названию"), new("start", "По типу запуска"), new("class", "Сначала те, что можно менять"),
    };

    public ServicesViewModel(IServiceSource source, IChangeService? changes, IDialogService dialogs, INavigator? navigator = null)
        : base("Службы", "Тип запуска служб программ. Защищённые и системные службы заблокированы, их можно только смотреть.", Sorts, changes, navigator)
    {
        _source = source;
        _dialogs = dialogs;
        UndoDialogs = dialogs;
        SetStartCommand = new RelayCommand(p => LastAction = SetStartAsync(p as string ?? ""), p => CanChangeSelected && IsStartAllowed(p as string ?? ""));
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public RelayCommand SetStartCommand { get; }

    public ServiceRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RefreshSelectionState();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool CanChangeSelected => CanApply && Selected?.Class == Changeability.Modifiable;

    public const string CarveOutServiceHint = "Эту службу можно только отключить или перевести с автоматического запуска на ручной. Включить автозапуск напрямую нельзя: только откатом из журнала.";

    public string DetailHint => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? Selected.NotChangeableText :
        ViewOnly ? "Режим просмотра: изменения отключены" :
        Selected.CarveOut ? Selected.Reason + " " + CarveOutServiceHint : Selected.Reason;

    /// <summary>Подсказка к отключённым кнопкам применения: почему нельзя. Пусто, если кнопки доступны.</summary>
    public string ApplyTooltip => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? Selected.NotChangeableText :
        ViewOnly ? ChangeabilityText.ViewOnlyTip :
        IsBusy ? ChangeabilityText.BusyTip :
        Selected.CarveOut ? CarveOutServiceHint : "";

    /// <summary>
    /// Для службы из каталога-исключения допустимы «Отключить» и «Вручную» (только как понижение с автоматического запуска);
    /// остальные службы ограничений интерфейса не имеют, политика агента проверяет их сама.
    /// </summary>
    private bool IsStartAllowed(string startKey)
    {
        if (Selected is not { CarveOut: true } svc) return true;
        if (!TryParseStart(startKey, out var mode)) return false;
        return mode == StartMode.Disabled
            || (mode == StartMode.Manual && svc.Model.Start is StartMode.Automatic or StartMode.AutomaticDelayed);
    }

    protected override IComparer<ServiceRowViewModel>? Comparer(string key) => key switch
    {
        "start" => Comparer<ServiceRowViewModel>.Create((a, b) =>
        {
            var c = StartOrder(a).CompareTo(StartOrder(b));
            return c != 0 ? c : ByName(a, b);
        }),
        "class" => Comparer<ServiceRowViewModel>.Create((a, b) =>
        {
            var c = a.Class.CompareTo(b.Class);
            return c != 0 ? c : ByName(a, b);
        }),
        _ => Comparer<ServiceRowViewModel>.Create(ByName),
    };

    private static int ByName(ServiceRowViewModel a, ServiceRowViewModel b)
        => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);

    private static int StartOrder(ServiceRowViewModel r) => r.Model.NoAccess ? 9 : r.Model.Start switch
    {
        StartMode.Automatic => 0,
        StartMode.AutomaticDelayed => 1,
        StartMode.Manual => 2,
        StartMode.Disabled => 3,
        _ => 4,
    };

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var data = await _source.GetServicesAsync(ct);
        ct.ThrowIfCancellationRequested();
        SetRows(PerUser.CollapseInstances(data).Select(x => new ServiceRowViewModel(x)));
        return AllRows.Count;
    }, "Службы не найдены.");

    protected override void OnCanApplyChanged() => RefreshSelectionState();

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(CanChangeSelected));
        OnPropertyChanged(nameof(DetailHint));
        OnPropertyChanged(nameof(ApplyTooltip));
        SetStartCommand.RaiseCanExecuteChanged();
    }

    private static bool TryParseStart(string key, out StartMode mode)
        => Enum.TryParse(key, out mode) && Enum.IsDefined(mode);

    public async Task SetStartAsync(string startKey)
    {
        if (Changes is null || !CanChangeSelected || Selected is not { } svc) return;
        if (!TryParseStart(startKey, out var mode)) return;
        if (!IsStartAllowed(startKey)) return;
        if (svc.Model.Start == mode)
        {
            SetResult(ResultKind.Info, "Служба уже в этом режиме запуска.", "");
            return;
        }

        var ok = _dialogs.Confirm(new ConfirmContent($"Изменить запуск службы «{svc.DisplayName}»?",
            $"Что изменится:\n• Служба {svc.Name}: {ChangeabilityText.StartName(svc.Model.Start)} → {ChangeabilityText.StartName(mode)}\n" +
            $"• Файл службы: {(string.IsNullOrEmpty(svc.ImagePath) ? "не определён" : svc.ImagePath)}\n" +
            "• Это изменение касается всего компьютера (HKLM, ветка Services).\n" +
            (svc.CarveOut
                ? "• Отключение не останавливает уже запущенную службу — она перестанет работать после перезагрузки.\n" +
                  "• Крупное обновление Windows может вернуть службу; откат после этого может отказать.\n"
                : "") + "\n" +
            "Откуда вернуть: раздел «Журнал и откат», кнопка «Откатить». Прежний режим запуска записывается в журнал до изменения.",
            "Изменить", Danger: true));
        if (!ok) return;

        IsBusy = true;
        try
        {
            var outcome = await Changes.SetServiceStartAsync(svc.Name, mode);
            ShowOutcome(outcome);
            if (outcome.Changed) await ReloadKeepSelectionAsync(svc.Name);
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadKeepSelectionAsync(string name)
    {
        await ReloadAsync();
        await FilterPending;
        Selected = AllRows.FirstOrDefault(x => x.Name == name);
    }
}

public sealed class TaskRowViewModel(TaskInfo model) : IListRow
{
    public TaskInfo Model { get; } = model;
    public string Path => Model.Path;
    public string Name => Model.Name;
    public bool Enabled => Model.Enabled;
    public string EnabledText => Model.NoAccess ? "Нет доступа" : Model.Enabled ? "Включена" : "Выключена";
    public Changeability Class => Model.Class;
    public string ClassText => Model.NoAccess ? "Нет доступа" : ChangeabilityText.Name(Model.Class);
    public string Reason => Model.Reason;
    public bool DisableOnly => Model.DisableOnly;
    public string Summary => Model.Summary;
    public string AutomationName => $"{Name}, {EnabledText}, {ClassText}";
    public string SearchText { get; } = $"{model.Path} {model.Summary}";

    /// <summary>Почему задачу нельзя менять; пусто для изменяемых.</summary>
    public string NotChangeableText => ChangeabilityText.WhyNot(Class, Model.NoAccess, Reason);
}

public sealed class TasksViewModel : ListPageViewModel<TaskRowViewModel>
{
    private readonly ITaskSource _source;
    private readonly IDialogService _dialogs;
    private TaskRowViewModel? _selected;

    private static readonly SortOption[] Sorts =
    {
        new("name", "По названию"), new("path", "По пути"), new("state", "Сначала включённые"), new("class", "Сначала те, что можно менять"),
    };

    public TasksViewModel(ITaskSource source, IChangeService? changes, IDialogService dialogs, INavigator? navigator = null)
        : base("Задачи", "Задачи планировщика Windows. Служебные задачи заблокированы; задачи сторонних программ можно включать и выключать, остальные только смотреть.", Sorts, changes, navigator)
    {
        _source = source;
        _dialogs = dialogs;
        UndoDialogs = dialogs;
        SetEnabledCommand = new RelayCommand(p => LastAction = SetEnabledAsync(string.Equals(p as string, "on", StringComparison.Ordinal)),
            p => CanChangeSelected && !(Selected?.DisableOnly == true && string.Equals(p as string, "on", StringComparison.Ordinal)));
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public RelayCommand SetEnabledCommand { get; }

    public TaskRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            RefreshSelectionState();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool CanChangeSelected => CanApply && Selected?.Class == Changeability.Modifiable;

    public const string DisableOnlyHint = "Эту задачу можно только выключить. Включить её обратно можно откатом из журнала.";

    /// <summary>Кнопка «Включить» скрыта для задач каталога-исключения: напрямую их не включаем.</summary>
    public bool ShowEnableButton => Selected?.DisableOnly != true;

    public string DetailHint => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? Selected.NotChangeableText :
        ViewOnly ? "Режим просмотра: изменения отключены" :
        Selected.DisableOnly ? Selected.Reason + " " + DisableOnlyHint : Selected.Reason;

    /// <summary>Подсказка к отключённым кнопкам применения: почему нельзя. Пусто, если кнопки доступны.</summary>
    public string ApplyTooltip => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? Selected.NotChangeableText :
        ViewOnly ? ChangeabilityText.ViewOnlyTip :
        IsBusy ? ChangeabilityText.BusyTip : "";

    protected override IComparer<TaskRowViewModel>? Comparer(string key) => key switch
    {
        "path" => Comparer<TaskRowViewModel>.Create((a, b) => string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase)),
        "state" => Comparer<TaskRowViewModel>.Create((a, b) =>
        {
            var c = a.Enabled == b.Enabled ? 0 : a.Enabled ? -1 : 1;
            return c != 0 ? c : ByName(a, b);
        }),
        "class" => Comparer<TaskRowViewModel>.Create((a, b) =>
        {
            var c = a.Class.CompareTo(b.Class);
            return c != 0 ? c : ByName(a, b);
        }),
        _ => Comparer<TaskRowViewModel>.Create(ByName),
    };

    private static int ByName(TaskRowViewModel a, TaskRowViewModel b)
        => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

    public override Task ReloadAsync() => LoadAsync(async ct =>
    {
        var data = await _source.GetTasksAsync(ct);
        ct.ThrowIfCancellationRequested();
        SetRows(data.Select(x => new TaskRowViewModel(x)));
        return AllRows.Count;
    }, "Задачи не найдены или нет доступа к планировщику.");

    protected override void OnCanApplyChanged() => RefreshSelectionState();

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(CanChangeSelected));
        OnPropertyChanged(nameof(ShowEnableButton));
        OnPropertyChanged(nameof(DetailHint));
        OnPropertyChanged(nameof(ApplyTooltip));
        SetEnabledCommand.RaiseCanExecuteChanged();
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (Changes is null || !CanChangeSelected || Selected is not { } task) return;
        if (enabled && task.DisableOnly) return;
        if (task.Enabled == enabled)
        {
            SetResult(ResultKind.Info, enabled ? "Задача уже включена." : "Задача уже выключена.", "");
            return;
        }

        var ok = _dialogs.Confirm(new ConfirmContent($"{(enabled ? "Включить" : "Выключить")} задачу «{task.Name}»?",
            $"Что изменится:\n• Задача {task.Path}: {task.EnabledText.ToLowerInvariant()} → {(enabled ? "включена" : "выключена")}\n• Действие: {task.Summary}\n" +
            (task.DisableOnly ? "• Крупное обновление Windows может вернуть задачу; откат после этого может отказать.\n" : "") + "\n" +
            "Откуда вернуть: раздел «Журнал и откат», кнопка «Откатить». Прежнее состояние записывается в журнал до изменения.",
            enabled ? "Включить" : "Выключить", Danger: true));
        if (!ok) return;

        IsBusy = true;
        try
        {
            var outcome = await Changes.SetTaskEnabledAsync(task.Path, enabled);
            ShowOutcome(outcome);
            if (outcome.Changed)
            {
                await ReloadAsync();
                await FilterPending;
                Selected = AllRows.FirstOrDefault(x => x.Path == task.Path);
            }
        }
        finally { IsBusy = false; }
    }
}
