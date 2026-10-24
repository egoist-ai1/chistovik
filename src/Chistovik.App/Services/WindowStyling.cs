using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Chistovik.App.Services;

/// <summary>Общее оформление окон: тёмный заголовок, иконка, плавное появление диалога.</summary>
internal static class WindowStyling
{
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static readonly Uri IconUri = new("pack://application:,,,/Assets/chistovik.ico", UriKind.Absolute);

    /// <summary>Подключает тёмную рамку заголовка (Windows 10 20H1+ и 11). Сбой игнорируется: это только оформление.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var on = 1;
                _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    /// <summary>Иконка окна из ресурсов сборки; если не загрузилась, остаётся иконка exe.</summary>
    public static void UseAppIcon(Window window)
    {
        try { window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(IconUri); }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or InvalidOperationException) { }
    }

    /// <summary>Появление содержимого диалога: прозрачность и лёгкий подъём, 160 мс. Без анимаций в системе — сразу.</summary>
    public static void FadeIn(FrameworkElement content)
    {
        if (!Motion.Enabled) return;
        var shift = new TranslateTransform(0, 8);
        content.RenderTransform = shift;
        content.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = Motion.Ms(160);
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = ease });
    }
}
