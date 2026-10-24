using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Тёмная рамка заголовка (Windows 10 20H1+ / 11). Ошибку игнорируем: это только оформление.
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmChanged;
        if (e.NewValue is MainViewModel vm) vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Current)) PlayTransition();
    }

    /// <summary>Короткая смена раздела: прозрачность и сдвиг. Без анимаций в системе — мгновенно.</summary>
    private void PlayTransition()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var dur = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        HostShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, dur) { EasingFunction = ease });
    }
}
