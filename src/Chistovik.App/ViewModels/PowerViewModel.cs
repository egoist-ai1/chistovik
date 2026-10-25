using Chistovik.App.Services.Changes;
using System.Globalization;
using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;

namespace Chistovik.App.ViewModels;

public sealed class PowerSchemeRowViewModel : ObservableObject
{
    private bool _isActive;

    public PowerSchemeRowViewModel(PowerSchemeEntry entry, bool isActive, RelayCommand apply, bool isAvailable = true)
    {
        Entry = entry;
        _isActive = isActive;
        ApplyCommand = apply;
        IsAvailable = isAvailable;
    }

    /// <summary>false: схемы нет на этом устройстве (Modern Standby и т. п.), выбрать её нельзя.</summary>
    public bool IsAvailable { get; }
    public bool IsMissing => !IsAvailable;

    public PowerSchemeEntry Entry { get; }
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public bool IsUnverified => !Entry.GuidVerified;
    public bool CreatesCopy => !Entry.BuiltIn;
    public RelayCommand ApplyCommand { get; }
    public override string ToString() => Title;

    public bool IsActive
    {
        get => _isActive;
        set { if (SetProperty(ref _isActive, value)) OnPropertyChanged(nameof(CanChoose)); }
    }

    public bool CanChoose => !IsActive && IsAvailable;
}

public sealed class PowerSettingRowViewModel : ObservableObject
{
    private string _ac;
    private string _dc;
    private string _error = "";

    public PowerSettingRowViewModel(PowerSettingEntry entry, (uint Ac, uint Dc)? current, RelayCommand apply)
    {
        Entry = entry;
        Current = current;
        _ac = current?.Ac.ToString(CultureInfo.InvariantCulture) ?? "";
        _dc = current?.Dc.ToString(CultureInfo.InvariantCulture) ?? "";
        ApplyCommand = apply;
    }

    public PowerSettingEntry Entry { get; }
    public (uint Ac, uint Dc)? Current { get; }
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public RelayCommand ApplyCommand { get; }
    public override string ToString() => Title;

    public string UnitText => Entry.Unit switch
    {
        "percent" => $"проценты, {Entry.Min}–{Entry.Max}",
        "seconds" => $"секунды, {Entry.Min}–{Entry.Max}; 0 — никогда",
        _ => $"код значения, {Entry.Min}–{Entry.Max}",
    };

    public string CurrentText => Current is { } c ? $"сейчас: сеть {c.Ac}, батарея {c.Dc}" : "текущее значение не прочитано";

    public string AcText { get => _ac; set { if (SetProperty(ref _ac, value)) Error = ""; } }
    public string DcText { get => _dc; set { if (SetProperty(ref _dc, value)) Error = ""; } }

    public string Error
    {
        get => _error;
        set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error.Length > 0;

    /// <summary>Разбор и проверка ввода. Пустое поле — «не менять». Возвращает false с текстом ошибки.</summary>
    public bool TryRead(out uint? ac, out uint? dc)
    {
        ac = dc = null;
        if (!TryField(AcText, "от сети", out ac)) return false;
        if (!TryField(DcText, "от батареи", out dc)) return false;
        // Не отправляем то, что уже стоит.
        if (Current is { } c)
        {
            if (ac == c.Ac) ac = null;
            if (dc == c.Dc) dc = null;
        }
        return true;
    }

    private bool TryField(string text, string name, out uint? value)
    {
        value = null;
        var t = text.Trim();
        if (t.Length == 0) return true;
        if (!uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            Error = $"Значение {name}: введите целое число без знака.";
            return false;
        }
        if (v < Entry.Min || v > Entry.Max)
        {
            Error = $"Значение {name} должно быть от {Entry.Min} до {Entry.Max}.";
            return false;
        }
        value = v;
        return true;
    }
}

public sealed class PowerViewModel : StatefulPageViewModel
{
    private readonly PowerCatalog _catalog;
    private readonly IPowerSource _source;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<PowerSchemeRowViewModel> _schemes = Array.Empty<PowerSchemeRowViewModel>();
    private IReadOnlyList<PowerSettingRowViewModel> _settings = Array.Empty<PowerSettingRowViewModel>();
    private string _problem = "";

    public PowerViewModel(PowerCatalog catalog, IPowerSource source, IChangeService? changes, IDialogService dialogs,
        INavigator? navigator = null)
        : base("Питание", "Схемы электропитания и их параметры. Текущие значения читаются без прав, смена идёт через агента и попадает в журнал.", changes, navigator)
    {
        _catalog = catalog;
        _source = source;
        _dialogs = dialogs;
        UndoDialogs = dialogs;
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastAction { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<PowerSchemeRowViewModel> Schemes { get => _schemes; private set => SetProperty(ref _schemes, value); }
    public IReadOnlyList<PowerSettingRowViewModel> Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public string Problem { get => _problem; private set { if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
    public bool HasProblem => Problem.Length > 0;

    public override Task ReloadAsync() => LoadAsync(async () =>
    {
        var snap = await _source.ReadAsync();
        Problem = snap.Problem ?? "";

        Schemes = _catalog.Schemes.Select(s =>
        {
            PowerSchemeRowViewModel? row = null;
            var cmd = new RelayCommand(() => LastAction = ApplySchemeAsync(row!.Entry), () => CanApply && row is { IsActive: false, IsAvailable: true });
            row = new PowerSchemeRowViewModel(s, snap.ActiveScheme == s.Guid, cmd, IsSchemeAvailable(s, snap));
            return row;
        }).ToList();

        Settings = _catalog.Settings.Select(s =>
        {
            PowerSettingRowViewModel? row = null;
            var cmd = new RelayCommand(() => LastAction = ApplySettingAsync(row!), () => CanApply);
            row = new PowerSettingRowViewModel(s, snap.Settings.TryGetValue(s.Id, out var v) ? v : null, cmd);
            return row;
        }).ToList();
        return Schemes.Count + Settings.Count;
    }, "Каталог питания пуст.");

    /// <summary>
    /// Встроенная схема доступна, если она есть в системе; схема из шаблона (ultimate) не создаётся на Modern Standby.
    /// Список схем не прочитан (null): считаем доступной, окончательно решает агент.
    /// </summary>
    internal static bool IsSchemeAvailable(PowerSchemeEntry s, PowerSnapshot snap)
    {
        if (s.CreateFromTemplate is not null) return !snap.ModernStandby;
        return snap.InstalledSchemes is null || snap.InstalledSchemes.Contains(s.Guid);
    }

    protected override void OnCanApplyChanged()
    {
        foreach (var s in Schemes) s.ApplyCommand.RaiseCanExecuteChanged();
        foreach (var s in Settings) s.ApplyCommand.RaiseCanExecuteChanged();
    }

    private static PowerPlan ToPlan(string id) => id switch
    {
        "saver" => PowerPlan.Saver,
        "bal" => PowerPlan.Bal,
        "high" => PowerPlan.High,
        _ => PowerPlan.Ult,
    };

    public async Task ApplySchemeAsync(PowerSchemeEntry scheme)
    {
        if (Changes is null || !CanApply) return;
        if (Schemes.FirstOrDefault(r => r.Entry.Id == scheme.Id) is { IsAvailable: false }) return;
        if (!scheme.BuiltIn)
        {
            var ok = _dialogs.Confirm(new ConfirmContent($"Включить «{scheme.Title}»?",
                scheme.Description + "\n\nЕсли нашей копии схемы ещё нет, агент создаст её. Откат вернёт прежнюю активную схему и удалит только нашу копию.\n" +
                "Откуда вернуть: раздел «Журнал и откат», кнопка «Откатить».", "Включить"));
            if (!ok) return;
        }

        IsBusy = true;
        try
        {
            var outcome = await Changes.SetPowerSchemeAsync(ToPlan(scheme.Id));
            ShowOutcome(outcome);
            if (outcome.Changed) await ReloadAsync();
        }
        finally { IsBusy = false; }
    }

    public async Task ApplySettingAsync(PowerSettingRowViewModel row)
    {
        if (Changes is null || !CanApply) return;
        if (!row.TryRead(out var ac, out var dc)) return;
        if (ac is null && dc is null)
        {
            row.Error = "Нечего менять: введите новое значение.";
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await Changes.SetPowerSettingAsync(row.Entry.Id, ac, dc);
            ShowOutcome(outcome);
            if (outcome.Changed) await ReloadAsync();
        }
        finally { IsBusy = false; }
    }
}
