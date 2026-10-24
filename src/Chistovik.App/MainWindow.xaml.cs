using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class MainWindow : Window
{
    private const double MinContentWidth = 900;
    private const double MinContentHeight = 600;

    public MainWindow()
    {
        InitializeComponent();
        WindowStyling.UseDarkTitleBar(this);
        WindowStyling.UseAppIcon(this);
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
    }

    /// <summary>Снимки экранов: без анимации смены раздела, чтобы кадр не поймал её середину.</summary>
    internal bool SkipTransitions { get; set; }

    /// <summary>Снимки: не подставлять сохранённый размер и позицию, их задаёт раннер.</summary>
    internal bool KeepBounds { get; set; }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!KeepBounds) ApplySavedBounds();
    }

    // ----- размер и позиция между запусками -----

    /// <summary>
    /// Размер и позиция из ui.json, но так, чтобы окно целиком помещалось в рабочую область: на 1280×800 и при
    /// масштабе 200 % стартовые 1320×820 не годятся. Если сохранённая позиция вне экранов (отключили монитор) — по центру.
    /// </summary>
    private void ApplySavedBounds()
    {
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinContentWidth, work.Width);
        MinHeight = Math.Min(MinContentHeight, work.Height);

        var saved = Vm?.UiStore.Load();
        var width = saved?.Width ?? Width;
        var height = saved?.Height ?? Height;
        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, work.Width));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, work.Height));
        Width = width;
        Height = height;

        if (saved?.Left is { } left && saved.Top is { } top && IsVisibleOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft,
                Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width));
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop,
                Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Left = work.Left + (work.Width - width) / 2;
            Top = work.Top + (work.Height - height) / 2;
        }

        if (saved?.Maximized == true) WindowState = WindowState.Maximized;
    }

    /// <summary>Хотя бы заголовок окна (его левая часть) попадает в область виртуального экрана.</summary>
    private static bool IsVisibleOnScreen(double left, double top, double width, double height)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        var visibleWidth = Math.Min(left + width, vr) - Math.Max(left, vl);
        return visibleWidth >= 160 && top >= vt - 1 && top <= vb - 60;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Vm is not { } vm) return;
        var s = vm.UiStore.Load();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        s.Left = bounds.Left;
        s.Top = bounds.Top;
        s.Width = bounds.Width;
        s.Height = bounds.Height;
        s.Maximized = WindowState == WindowState.Maximized;
        s.LastSection = vm.SelectedTitle;
        vm.UiStore.Save(s);
    }

    // ----- горячие клавиши, которым нужен визуальный поиск -----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (FindSearchBox(ContentHost) is { } box)
            {
                box.Focus();
                box.SelectAll();
            }
            e.Handled = true;
        }
    }

    private TextBox? FindSearchBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { IsVisible: true } tb && ReferenceEquals(tb.Style, TryFindResource("SearchBox"))) return tb;
            if (FindSearchBox(child) is { } found) return found;
        }
        return null;
    }

    // ----- смена раздела -----

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel vm) vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Current)) PlayTransition();
    }

    /// <summary>Короткая смена раздела: прозрачность и сдвиг, 160 мс. Без анимаций в системе — мгновенно.</summary>
    private void PlayTransition()
    {
        if (SkipTransitions || !Motion.Enabled) return;
        var dur = Motion.Ms(160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        HostShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }
}
