using System.Windows;
using System.Windows.Controls;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Views;

public partial class OverviewView : UserControl { public OverviewView() { InitializeComponent(); } }

/// <summary>Общий приём клавиатуры для списков с флажком: Пробел и Enter на строке переключают отметку.</summary>
internal static class RowKeyToggle
{
    /// <summary>Переключает строку и возвращает true, если нажатие обработано.</summary>
    internal static bool TryToggle(System.Windows.Input.Key key, object? rowContext)
    {
        if (key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)) return false;
        switch (rowContext)
        {
            case Chistovik.App.ViewModels.StartupRowViewModel startup: startup.Toggle(); return true;
            case Chistovik.App.ViewModels.ProgramRowViewModel program: program.Toggle(); return true;
            case Chistovik.App.ViewModels.FindingRowViewModel finding: finding.Toggle(); return true;
            default: return false;
        }
    }

    internal static void Handle(System.Windows.Input.KeyEventArgs e)
    {
        if (e.OriginalSource is ListBoxItem item && TryToggle(e.Key, item.DataContext)) e.Handled = true;
    }
}

public partial class CleanupView : UserControl
{
    public CleanupView() { InitializeComponent(); }
    private void List_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => RowKeyToggle.Handle(e);
}
public partial class ProgramsView : UserControl
{
    public ProgramsView() { InitializeComponent(); }
    private void List_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => RowKeyToggle.Handle(e);
}
public partial class StartupView : UserControl
{
    public StartupView() { InitializeComponent(); }

    /// <summary>Enter и пробел на строке списка переключают запись, если её можно менять.</summary>
    private void List_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => RowKeyToggle.Handle(e);
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
        WindowStyling.UseDarkTitleBar(this);
        WindowStyling.UseAppIcon(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Подтверждение изменения: что изменится и откуда вернуть. Результат — DialogResult.</summary>
public partial class ConfirmDialog : Window
{
    private const string RollbackMarker = "Откуда вернуть:";
    private const string ChangesHeading = "Что изменится:";

    public ConfirmDialog(ConfirmContent content)
    {
        InitializeComponent();
        WindowStyling.UseDarkTitleBar(this);
        WindowStyling.UseAppIcon(this);
        Title = content.Title;
        TitleText.Text = content.Title;
        var (what, rollback) = Split(content.Message);
        MessageText.Text = what;
        if (rollback.Length > 0)
        {
            RollbackText.Text = rollback;
            RollbackBox.Visibility = Visibility.Visible;
        }
        OkButton.Content = content.ConfirmText;
        OkButton.SetResourceReference(StyleProperty, content.Danger ? "DangerButton" : "PrimaryButton");
        OkButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, AccessibleName(content));
        if (content.Danger)
        {
            // Для опасных действий по умолчанию стоит «Отмена»: случайный Enter ничего не меняет.
            OkButton.IsDefault = false;
            CancelButton.IsDefault = true;
            KindText.Text = "Изменение системы";
            Badge.SetResourceReference(Border.BackgroundProperty, "WarnTintBrush");
            BadgeIcon.Kind = "alert";
            BadgeIcon.SetResourceReference(Control.ForegroundProperty, "WarnBrush");
        }
        Loaded += (_, _) => WindowStyling.FadeIn(Root);
    }

    /// <summary>
    /// Имя кнопки подтверждения для программ чтения с экрана. WPF считает «_» в имени кнопки маркером быстрой клавиши
    /// и убирает его («Имя_файла» превращается в «Имяфайла»), поэтому каждый «_» удваивается.
    /// </summary>
    internal static string AccessibleName(ConfirmContent content)
        => (content.ConfirmText + ": " + content.Title).Replace("_", "__");

    /// <summary>
    /// Делит текст на «что произойдёт» и «как вернуть»: всё после «Откуда вернуть:» уходит в отдельную панель.
    /// Заголовок «Что изменится:» в начале убирается, он уже есть в подписи раздела.
    /// </summary>
    internal static (string What, string Rollback) Split(string message)
    {
        var text = message.Trim();
        var rollback = "";
        var i = text.IndexOf(RollbackMarker, StringComparison.Ordinal);
        if (i >= 0)
        {
            rollback = text[(i + RollbackMarker.Length)..].Trim();
            text = text[..i].TrimEnd();
        }
        if (text.StartsWith(ChangesHeading, StringComparison.Ordinal)) text = text[ChangesHeading.Length..].TrimStart();
        return (text, rollback);
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
        // В данных конфликта есть только идентификатор операции (ни названия, ни времени): пользователю он не показывается,
        // а для диагностики лежит в подсказке.
        Cause = info.ChangedByOperationId is not null
            ? "Изменено другой операцией Чистовика"
            : "Изменено не через Чистовик";
        CauseDetail = info.ChangedByOperationId is { } id ? "Операция " + id.ToString("D") : "";
    }

    /// <summary>Технический идентификатор операции: только для подсказки.</summary>
    public string CauseDetail { get; }

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

    public override string ToString() => $"{Title}, {Current}, {ChoiceText}";

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
        WindowStyling.UseDarkTitleBar(this);
        WindowStyling.UseAppIcon(this);
        _rows = conflicts.Select(c => new ConflictRowViewModel(c)).ToList();
        Rows.ItemsSource = _rows;
    }

    /// <summary>Решения по каждому конфликту после закрытия окна кнопкой «Продолжить».</summary>
    public IReadOnlyList<UndoResolution> Resolutions => _rows.Select(r => r.ToResolution()).ToList();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
