using System.Windows;
using System.Windows.Controls;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Views;

public partial class OverviewView : UserControl { public OverviewView() { InitializeComponent(); } }
public partial class ProgramsView : UserControl { public ProgramsView() { InitializeComponent(); } }
public partial class StartupView : UserControl
{
    public StartupView() { InitializeComponent(); }

    /// <summary>Enter и пробел на строке списка переключают запись, если её можно менять.</summary>
    private void List_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)) return;
        if (e.OriginalSource is not ListBoxItem { DataContext: Chistovik.App.ViewModels.StartupRowViewModel row }) return;
        row.Toggle();
        e.Handled = true;
    }
}
public partial class JournalView : UserControl { public JournalView() { InitializeComponent(); } }
public partial class PlaceholderView : UserControl { public PlaceholderView() { InitializeComponent(); } }
public partial class TweaksView : UserControl { public TweaksView() { InitializeComponent(); } }
public partial class ModesView : UserControl { public ModesView() { InitializeComponent(); } }
public partial class PowerView : UserControl { public PowerView() { InitializeComponent(); } }
public partial class ServicesView : UserControl { public ServicesView() { InitializeComponent(); } }
public partial class TasksView : UserControl { public TasksView() { InitializeComponent(); } }
public partial class PageBanner : UserControl { public PageBanner() { InitializeComponent(); } }

public partial class StubDialog : Window
{
    public StubDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Подтверждение изменения: что изменится и откуда вернуть. Результат — DialogResult.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmContent content)
    {
        InitializeComponent();
        Title = content.Title;
        TitleText.Text = content.Title;
        MessageText.Text = content.Message;
        OkButton.Content = content.ConfirmText;
        OkButton.SetResourceReference(StyleProperty, content.Danger ? "DangerButton" : "PrimaryButton");
        OkButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, content.ConfirmText + ": " + content.Title);
        // Для опасных действий по умолчанию стоит «Отмена»: случайный Enter ничего не меняет.
        if (content.Danger)
        {
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>Строка диалога конфликтов: переключатель «Перезаписать» (по умолчанию выключен, значит Skip).</summary>
public sealed class ConflictRowViewModel : ObservableObject
{
    private bool _overwrite;

    public ConflictRowViewModel(ConflictInfo info)
    {
        Info = info;
        Title = ResponseText.StepKindName(info.Kind);
        Current = "Сейчас: " + info.CurrentDisplay;
        Cause = info.ChangedByOperationId is { } id
            ? "Изменено операцией " + id.ToString("D")[..8]
            : "Изменено не через Чистовик";
    }

    public ConflictInfo Info { get; }
    public string Title { get; }
    public string Current { get; }
    public string Cause { get; }

    public bool Overwrite
    {
        get => _overwrite;
        set { if (SetProperty(ref _overwrite, value)) OnPropertyChanged(nameof(ChoiceText)); }
    }

    public string ChoiceText => Overwrite ? "перезаписать" : "оставить как есть";

    public UndoResolution ToResolution() => new()
    {
        StepId = Info.StepId,
        Action = Overwrite ? ConflictAction.Overwrite : ConflictAction.Skip,
    };
}

public partial class ConflictsDialog : Window
{
    private readonly IReadOnlyList<ConflictRowViewModel> _rows;

    public ConflictsDialog(IReadOnlyList<ConflictInfo> conflicts)
    {
        InitializeComponent();
        _rows = conflicts.Select(c => new ConflictRowViewModel(c)).ToList();
        Rows.ItemsSource = _rows;
    }

    /// <summary>Решения по каждому конфликту после закрытия окна кнопкой «Продолжить».</summary>
    public IReadOnlyList<UndoResolution> Resolutions => _rows.Select(r => r.ToResolution()).ToList();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
