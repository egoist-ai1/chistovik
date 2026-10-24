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
}

public sealed class ServiceRowViewModel(ServiceInfo model)
{
    public ServiceInfo Model { get; } = model;
    public string Name => Model.Name;
    public string DisplayName => Model.DisplayName;
    public string ClassText => ChangeabilityText.Name(Model.Class);
    public string StartText => ChangeabilityText.StartName(Model.Start);
    public Changeability Class => Model.Class;
    public string Reason => Model.Reason;
    public string ImagePath => Model.ImagePath;
    public string AutomationName => $"{DisplayName}, {StartText}, {ClassText}";
}

public sealed class ServicesViewModel : StatefulPageViewModel
{
    private readonly IServiceSource _source;
    private readonly IDialogService _dialogs;
    private List<ServiceRowViewModel> _all = new();
    private IReadOnlyList<ServiceRowViewModel> _items = Array.Empty<ServiceRowViewModel>();
    private ServiceRowViewModel? _selected;
    private string _search = "";
    private string _filter = "all";

    public ServicesViewModel(IServiceSource source, IChangeService? changes, IDialogService dialogs, INavigator? navigator = null)
        : base("Службы", "Тип запуска служб программ. Защищённые и системные службы заблокированы, их можно только смотреть.", changes, navigator)
    {
        _source = source;
        _dialogs = dialogs;
        FilterCommand = new RelayCommand(p => { Filter = p as string ?? "all"; });
        SetStartCommand = new RelayCommand(p => LastAction = SetStartAsync(p as string ?? ""), p => CanChangeSelected);
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public RelayCommand FilterCommand { get; }
    public RelayCommand SetStartCommand { get; }

    public IReadOnlyList<ServiceRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }

    public ServiceRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanChangeSelected));
            OnPropertyChanged(nameof(DetailHint));
            SetStartCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Apply(); } }

    public string Filter
    {
        get => _filter;
        private set { if (SetProperty(ref _filter, value)) { Apply(); OnPropertyChanged(nameof(FilterAll)); OnPropertyChanged(nameof(FilterModifiable)); OnPropertyChanged(nameof(FilterReadOnly)); } }
    }

    public bool FilterAll => Filter == "all";
    public bool FilterModifiable => Filter == "mod";
    public bool FilterReadOnly => Filter == "ro";

    public string StatusText => $"Показано {Items.Count} из {_all.Count}; можно менять: {_all.Count(x => x.Class == Changeability.Modifiable)}";

    public bool CanChangeSelected => CanApply && Selected?.Class == Changeability.Modifiable;

    public string DetailHint => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? "Только чтение: " + Selected.Reason :
        ViewOnly ? "Режим просмотра: изменения отключены" : Selected.Reason;

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var data = await _source.GetServicesAsync();
        _all = data.Select(x => new ServiceRowViewModel(x)).ToList();
        Apply();
        return _all.Count;
    }, "Службы не найдены.");

    protected override void OnCanApplyChanged()
    {
        OnPropertyChanged(nameof(CanChangeSelected));
        OnPropertyChanged(nameof(DetailHint));
        SetStartCommand.RaiseCanExecuteChanged();
    }

    private void Apply()
    {
        IEnumerable<ServiceRowViewModel> q = _all;
        q = _filter switch
        {
            "mod" => q.Where(x => x.Class == Changeability.Modifiable),
            "ro" => q.Where(x => x.Class != Changeability.Modifiable),
            _ => q,
        };
        var s = _search.Trim();
        if (s.Length > 0)
            q = q.Where(x => x.DisplayName.Contains(s, StringComparison.CurrentCultureIgnoreCase)
                          || x.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Items = q.ToList();
        OnPropertyChanged(nameof(StatusText));
    }

    private static bool TryParseStart(string key, out StartMode mode)
        => Enum.TryParse(key, out mode) && Enum.IsDefined(mode);

    public async Task SetStartAsync(string startKey)
    {
        if (Changes is null || !CanChangeSelected || Selected is not { } svc) return;
        if (!TryParseStart(startKey, out var mode)) return;
        if (svc.Model.Start == mode)
        {
            SetResult(ResultKind.Info, "Служба уже в этом режиме запуска.", "");
            return;
        }

        var ok = _dialogs.Confirm(new ConfirmContent($"Изменить запуск службы «{svc.DisplayName}»?",
            $"Что изменится:\n• Служба {svc.Name}: {ChangeabilityText.StartName(svc.Model.Start)} → {ChangeabilityText.StartName(mode)}\n" +
            $"• Файл службы: {(string.IsNullOrEmpty(svc.ImagePath) ? "не определён" : svc.ImagePath)}\n" +
            "• Это изменение касается всего компьютера (HKLM, ветка Services).\n\n" +
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
        Selected = _all.FirstOrDefault(x => x.Name == name);
    }
}

public sealed class TaskRowViewModel(TaskInfo model)
{
    public TaskInfo Model { get; } = model;
    public string Path => Model.Path;
    public string Name => Model.Name;
    public bool Enabled => Model.Enabled;
    public string EnabledText => Model.Enabled ? "Включена" : "Выключена";
    public Changeability Class => Model.Class;
    public string ClassText => ChangeabilityText.Name(Model.Class);
    public string Reason => Model.Reason;
    public string Summary => Model.Summary;
    public string AutomationName => $"{Name}, {EnabledText}, {ClassText}";
}

public sealed class TasksViewModel : StatefulPageViewModel
{
    private readonly ITaskSource _source;
    private readonly IDialogService _dialogs;
    private List<TaskRowViewModel> _all = new();
    private IReadOnlyList<TaskRowViewModel> _items = Array.Empty<TaskRowViewModel>();
    private TaskRowViewModel? _selected;
    private string _search = "";
    private string _filter = "all";

    public TasksViewModel(ITaskSource source, IChangeService? changes, IDialogService dialogs, INavigator? navigator = null)
        : base("Задачи", "Задачи планировщика Windows. Служебные задачи заблокированы; пока правила для остальных не подключены, они доступны только для чтения.", changes, navigator)
    {
        _source = source;
        _dialogs = dialogs;
        FilterCommand = new RelayCommand(p => { Filter = p as string ?? "all"; });
        SetEnabledCommand = new RelayCommand(p => LastAction = SetEnabledAsync(string.Equals(p as string, "on", StringComparison.Ordinal)), p => CanChangeSelected);
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public RelayCommand FilterCommand { get; }
    public RelayCommand SetEnabledCommand { get; }

    public IReadOnlyList<TaskRowViewModel> Items { get => _items; private set => SetProperty(ref _items, value); }

    public TaskRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanChangeSelected));
            OnPropertyChanged(nameof(DetailHint));
            SetEnabledCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Apply(); } }

    public string Filter
    {
        get => _filter;
        private set { if (SetProperty(ref _filter, value)) { Apply(); OnPropertyChanged(nameof(FilterAll)); OnPropertyChanged(nameof(FilterModifiable)); OnPropertyChanged(nameof(FilterReadOnly)); } }
    }

    public bool FilterAll => Filter == "all";
    public bool FilterModifiable => Filter == "mod";
    public bool FilterReadOnly => Filter == "ro";

    public string StatusText => $"Показано {Items.Count} из {_all.Count}; можно менять: {_all.Count(x => x.Class == Changeability.Modifiable)}";

    public bool CanChangeSelected => CanApply && Selected?.Class == Changeability.Modifiable;

    public string DetailHint => Selected is null ? "" :
        Selected.Class != Changeability.Modifiable ? "Только чтение: " + Selected.Reason :
        ViewOnly ? "Режим просмотра: изменения отключены" : Selected.Reason;

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var data = await _source.GetTasksAsync();
        _all = data.Select(x => new TaskRowViewModel(x)).ToList();
        Apply();
        return _all.Count;
    }, "Задачи не найдены или нет доступа к папке задач.");

    protected override void OnCanApplyChanged()
    {
        OnPropertyChanged(nameof(CanChangeSelected));
        OnPropertyChanged(nameof(DetailHint));
        SetEnabledCommand.RaiseCanExecuteChanged();
    }

    private void Apply()
    {
        IEnumerable<TaskRowViewModel> q = _all;
        q = _filter switch
        {
            "mod" => q.Where(x => x.Class == Changeability.Modifiable),
            "ro" => q.Where(x => x.Class != Changeability.Modifiable),
            _ => q,
        };
        var s = _search.Trim();
        if (s.Length > 0) q = q.Where(x => x.Path.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        Items = q.ToList();
        OnPropertyChanged(nameof(StatusText));
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (Changes is null || !CanChangeSelected || Selected is not { } task) return;
        if (task.Enabled == enabled)
        {
            SetResult(ResultKind.Info, enabled ? "Задача уже включена." : "Задача уже выключена.", "");
            return;
        }

        var ok = _dialogs.Confirm(new ConfirmContent($"{(enabled ? "Включить" : "Выключить")} задачу «{task.Name}»?",
            $"Что изменится:\n• Задача {task.Path}: {task.EnabledText.ToLowerInvariant()} → {(enabled ? "включена" : "выключена")}\n• Действие: {task.Summary}\n\n" +
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
                Selected = _all.FirstOrDefault(x => x.Path == task.Path);
            }
        }
        finally { IsBusy = false; }
    }
}
