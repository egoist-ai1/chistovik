using Chistovik.App.Models;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;

namespace Chistovik.App.ViewModels;

/// <summary>
/// Редакция Windows для подсказок «не действует на этой редакции». Только чтение EditionID из реестра;
/// агент редакцию не проверяет и от неё ничего не зависит.
/// </summary>
public static class WindowsEdition
{
    private static readonly Lazy<string?> CurrentLazy = new(ReadCurrent);

    /// <summary>Home | Pro | Enterprise | Education, либо null, если определить не удалось.</summary>
    public static string? Current => CurrentLazy.Value;

    /// <summary>Сводит EditionID к одной из четырёх редакций каталога; неизвестное даёт null.</summary>
    public static string? Normalize(string? editionId)
    {
        if (string.IsNullOrWhiteSpace(editionId)) return null;
        var id = editionId.Trim();
        bool Is(string prefix) => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (Is("ProfessionalEducation") || Is("Education")) return "Education";
        if (Is("Professional")) return "Pro";
        if (Is("Core")) return "Home";
        if (Is("Enterprise") || Is("IoT")) return "Enterprise";
        return null;
    }

    private static string? ReadCurrent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return Normalize(key?.GetValue("EditionID") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }
}

public sealed class TweakRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isOn;
    private readonly string? _edition;

    public TweakRowViewModel(TweakEntry entry, TweakState current, Action changed, string? edition = null)
    {
        _edition = edition ?? WindowsEdition.Current;
        Entry = entry;
        _changed = changed;
        Current = current;
        _isOn = current == TweakState.On;
        AutomationName = $"{entry.Title}, {StateText}";
    }

    public TweakEntry Entry { get; }
    public TweakState Current { get; }
    public string Id => Entry.Id;
    public string Title => Entry.Title;
    public string Description => Entry.Description;
    public bool IsCareful => Entry.Risk == "careful";
    public bool IsUnverified => !Entry.Verified;
    public string UnverifiedText => "не проверено на живой системе";

    /// <summary>Твик привязан к редакциям, и текущая в список не входит (неизвестная редакция не считается несовпадением).</summary>
    public bool IsNotApplicable => Entry.Editions is { Length: > 0 } eds && _edition is not null
        && !eds.Contains(_edition, StringComparer.Ordinal);
    public string NotApplicableText => "не действует на этой редакции Windows";

    public bool RevertsAfterUpdate => Entry.RevertedBy is "feature-update" or "cumulative-update";
    public string RevertText => RevertsAfterUpdate ? "после обновления Windows может вернуться" : "";
    public bool IsMachineWide => Entry.Hive == "HKLM";
    public string RiskText => IsCareful ? "Осторожно" : "Безопасно";
    public string ScopeText => IsMachineWide ? "Для всех пользователей" : "Только для вас";
    public string AutomationName { get; }

    public string StateText => Current switch
    {
        TweakState.On => "сейчас включено",
        TweakState.Off => "сейчас выключено",
        _ => "значение не задано или другое",
    };

    public string RestartText => Entry.Restart switch
    {
        "explorer" => "нужен перезапуск Проводника",
        "logoff" => "нужен выход из системы",
        "reboot" => "нужна перезагрузка",
        _ => "",
    };

    public bool HasRestart => RestartText.Length > 0;

    /// <summary>Желаемое состояние переключателя; применяется кнопкой «Применить».</summary>
    public bool IsOn
    {
        get => _isOn;
        set { if (SetProperty(ref _isOn, value)) { OnPropertyChanged(nameof(IsPending)); _changed(); } }
    }

    public bool IsPending => IsOn != (Current == TweakState.On);

    public void Reset() => IsOn = Current == TweakState.On;
}

public sealed class TweakGroupViewModel(string name, IReadOnlyList<TweakRowViewModel> items)
{
    public string Name { get; } = name;
    public IReadOnlyList<TweakRowViewModel> Items { get; } = items;
}

/// <summary>Твики реестра из каталога (area «tw») или визуальные эффекты (area «fx»): один класс, разный фильтр.</summary>
public sealed class TweaksViewModel : StatefulPageViewModel
{
    private readonly ITweakStateSource _state;
    private readonly IDialogService _dialogs;
    private readonly IShellEffects _effects;
    private readonly TweakCatalog _catalog;
    private readonly string _area;
    private IReadOnlyList<TweakGroupViewModel> _groups = Array.Empty<TweakGroupViewModel>();
    private List<TweakRowViewModel> _rows = new();

    public TweaksViewModel(string title, string subtitle, string area, TweakCatalog catalog, ITweakStateSource state,
        IChangeService? changes, IDialogService dialogs, IShellEffects effects, INavigator? navigator = null)
        : base(title, subtitle, changes, navigator)
    {
        _area = area;
        _catalog = catalog;
        _state = state;
        _dialogs = dialogs;
        UndoDialogs = dialogs;
        _effects = effects;
        ApplyCommand = new RelayCommand(() => LastApply = ApplyAsync(), () => CanApply && PendingCount > 0);
        DiscardCommand = new RelayCommand(Discard, () => PendingCount > 0);
        RestartExplorerCommand = new RelayCommand(RestartExplorer);
        Loaded = ReloadAsync();
    }

    public Task Loaded { get; }
    public Task LastApply { get; private set; } = Task.CompletedTask;

    public RelayCommand ApplyCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand RestartExplorerCommand { get; }

    public IReadOnlyList<TweakGroupViewModel> Groups { get => _groups; private set => SetProperty(ref _groups, value); }

    public int PendingCount => _rows.Count(r => r.IsPending);
    public string ApplyText => PendingCount == 0 ? "Применить" : $"Применить ({PendingCount})";
    public bool NeedsExplorerRestart { get; private set; }

    public override Task ReloadAsync() => LoadAsync(ReadRowsAsync, "Каталог твиков пуст.");

    private async Task<int> ReadRowsAsync()
    {
        var entries = _catalog.Tweaks.Where(t => t.Area == _area).ToList();
        var states = await _state.ReadAsync(entries);
        _rows = entries.Select(e => new TweakRowViewModel(e, states.GetValueOrDefault(e.Id, TweakState.Unknown), OnRowChanged)).ToList();
        Groups = _rows.GroupBy(r => r.Entry.Category ?? "Прочее")
            .Select(g => new TweakGroupViewModel(g.Key, g.ToList())).ToList();
        RefreshPending();
        return _rows.Count;
    }

    protected override void OnCanApplyChanged() => ApplyCommand.RaiseCanExecuteChanged();

    private void OnRowChanged() => RefreshPending();

    private void RefreshPending()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ApplyText));
        ApplyCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }

    private void Discard()
    {
        foreach (var r in _rows) r.Reset();
        ClearResult();
    }

    private void RestartExplorer()
    {
        try
        {
            _effects.RestartExplorer();
            NeedsExplorerRestart = false;
            OnPropertyChanged(nameof(NeedsExplorerRestart));
            SetResult(ResultKind.Info, "Проводник перезапущен.", "");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetResult(ResultKind.Error, "Не удалось перезапустить Проводник: " + e.Message, "");
        }
    }

    public async Task ApplyAsync()
    {
        if (Changes is null || !CanApply) return;
        var pending = _rows.Where(r => r.IsPending).ToList();
        if (pending.Count == 0) return;

        if (pending.Any(NeedsConfirmation))
        {
            var ok = _dialogs.Confirm(new ConfirmContent("Подтвердите изменения", Describe(pending), "Применить",
                Danger: pending.Any(r => r.IsCareful)));
            if (!ok) return;
        }

        IsBusy = true;
        try
        {
            var changes = pending.Select(r => new TweakChange { TweakId = r.Id, Enabled = r.IsOn }).ToList();
            var outcome = await Changes.ApplyTweaksAsync(changes);
            ShowOutcome(outcome);
            NeedsExplorerRestart = outcome.RestartHint?.Contains("Проводник", StringComparison.Ordinal) == true;
            OnPropertyChanged(nameof(NeedsExplorerRestart));
            // Источник истины: что реально стало в реестре, а не что мы отправили.
            if (outcome.Changed || outcome.Kind == OutcomeKind.NoChange) await ReloadKeepResultAsync();
        }
        finally { IsBusy = false; }
    }

    private Task ReloadKeepResultAsync() => ReadRowsAsync();

    private static bool NeedsConfirmation(TweakRowViewModel r) => r.IsCareful || r.IsMachineWide || r.Entry.RequiresAdmin;

    /// <summary>Текст окна подтверждения: что изменится и откуда вернуть.</summary>
    public static string Describe(IReadOnlyList<TweakRowViewModel> rows)
    {
        var sb = new System.Text.StringBuilder("Что изменится:\n");
        foreach (var r in rows)
        {
            var e = r.Entry;
            var target = r.IsOn ? e.OnValue.ToString() : e.OffValue is { ValueKind: not System.Text.Json.JsonValueKind.Null } off ? off.ToString() : "значение будет удалено";
            sb.Append("• ").Append(e.Title).Append(r.IsOn ? " — включить" : " — выключить").Append('\n')
              .Append("   ").Append(e.Hive).Append('\\').Append(e.Key).Append(", ").Append(e.Value).Append(" = ").Append(target).Append('\n');
            if (r.IsMachineWide) sb.Append("   Касается всех пользователей этого компьютера.\n");
            if (r.IsCareful) sb.Append("   ").Append(e.Description).Append('\n');
            if (r.HasRestart) sb.Append("   После применения: ").Append(r.RestartText).Append(".\n");
            if (r.IsNotApplicable) sb.Append("   Внимание: ").Append(r.NotApplicableText).Append(".\n");
            if (r.RevertsAfterUpdate) sb.Append("   Учтите: ").Append(r.RevertText).Append(".\n");
        }
        sb.Append("\nОткуда вернуть: раздел «Журнал и откат», кнопка «Откатить». Прежнее значение записывается в журнал до изменения.");
        if (rows.Any(r => !r.Entry.Verified))
            sb.Append("\nНекоторые твики помечены «не проверено»: они ещё не проверялись на чистой системе.");
        return sb.ToString();
    }
}
