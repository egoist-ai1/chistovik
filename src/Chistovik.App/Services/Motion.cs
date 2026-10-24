using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Chistovik.App.Services;

/// <summary>
/// Анимации и контраст по настройкам Windows. Если в системе отключены анимации (reduced motion), все наши анимации
/// сводятся к мгновенной смене состояния. В режиме высокого контраста цвета берутся из системной палитры.
/// </summary>
public static class Motion
{
    /// <summary>Снимки экранов выключают анимации, чтобы кадр не поймал середину перехода.</summary>
    public static bool ForceOff { get; set; }

    public static bool Enabled => !ForceOff && SystemParameters.ClientAreaAnimation;

    /// <summary>Длительность перехода: токен, а при выключенных анимациях ноль.</summary>
    public static Duration Ms(int ms) => Enabled ? new Duration(TimeSpan.FromMilliseconds(ms)) : new Duration(TimeSpan.Zero);

    /// <summary>
    /// Переопределяет токены на уровне приложения: DynamicResource-кисти подхватываются сразу, длительности
    /// (StaticResource в шаблонах) — при создании шаблона, поэтому вызывать надо до показа окон.
    /// </summary>
    public static void Apply(Application app)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            var zero = new Duration(TimeSpan.Zero);
            app.Resources["DurFast"] = zero;
            app.Resources["DurBase"] = zero;
            app.Resources["DurSwitch"] = zero;
            app.Resources["DurSkeleton"] = new Duration(TimeSpan.FromMilliseconds(1));
            app.Resources["SkeletonRepeat"] = new RepeatBehavior(1);
        }
        if (SystemParameters.HighContrast) ApplyHighContrast(app);
    }

    /// <summary>Подписка на смену режима высокого контраста во время работы.</summary>
    public static void Watch(Application app)
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                app.Dispatcher.Invoke(() => { if (SystemParameters.HighContrast) ApplyHighContrast(app); else ResetHighContrast(app); });
        };
    }

    private static readonly string[] BrushKeys =
    {
        "BgBrush", "SurfaceBrush", "Surface2Brush", "Surface3Brush", "BorderBrush", "BorderStrongBrush", "TextBrush",
        "TextMutedBrush", "AccentBrush", "AccentHoverBrush", "OnAccentBrush", "DangerBrush", "WarnBrush", "AccentTintBrush",
        "DangerTintBrush", "WarnTintBrush", "AccentLineBrush", "WarnLineBrush", "DangerLineBrush", "SkeletonBrush",
    };

    private static void ApplyHighContrast(Application app)
    {
        static Brush Sys(object key) => (Brush)Application.Current.FindResource(key);
        var window = Sys(SystemColors.WindowBrushKey);
        var text = Sys(SystemColors.WindowTextBrushKey);
        var accent = Sys(SystemColors.HotTrackBrushKey);
        var highlight = Sys(SystemColors.HighlightBrushKey);
        var onHighlight = Sys(SystemColors.HighlightTextBrushKey);
        var gray = Sys(SystemColors.GrayTextBrushKey);
        var control = Sys(SystemColors.ControlBrushKey);
        var map = new Dictionary<string, Brush>
        {
            ["BgBrush"] = window, ["SurfaceBrush"] = window, ["Surface2Brush"] = control, ["Surface3Brush"] = control,
            ["BorderBrush"] = text, ["BorderStrongBrush"] = text, ["TextBrush"] = text, ["TextMutedBrush"] = gray,
            ["AccentBrush"] = accent, ["AccentHoverBrush"] = highlight, ["OnAccentBrush"] = onHighlight,
            ["DangerBrush"] = text, ["WarnBrush"] = text, ["AccentTintBrush"] = window, ["DangerTintBrush"] = window,
            ["WarnTintBrush"] = window, ["AccentLineBrush"] = text, ["WarnLineBrush"] = text, ["DangerLineBrush"] = text,
            ["SkeletonBrush"] = gray,
        };
        foreach (var key in BrushKeys) app.Resources[key] = map[key];
    }

    private static void ResetHighContrast(Application app)
    {
        foreach (var key in BrushKeys) app.Resources.Remove(key);
    }

}
