using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Chistovik.App.Services;

namespace Chistovik.App.Controls;

/// <summary>
/// Появление элемента при показе экрана: прозрачность и подъём на 14 px, с задержкой 45 мс на каждый порядковый номер.
/// Без анимаций в Windows (и в снимках экранов) элемент просто показан. Номер -1 — эффекта нет.
/// </summary>
public static class Reveal
{
    public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
        "Order", typeof(int), typeof(Reveal), new PropertyMetadata(-1, OnOrderChanged));

    public static int GetOrder(DependencyObject d) => (int)d.GetValue(OrderProperty);
    public static void SetOrder(DependencyObject d, int value) => d.SetValue(OrderProperty, value);

    private static void OnOrderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnLoaded;
        if ((int)e.NewValue >= 0) fe.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Loaded -= OnLoaded;
        if (!Motion.Enabled) return;

        var begin = TimeSpan.FromMilliseconds(45 * Math.Max(0, GetOrder(fe)));
        var duration = Motion.Ms(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 14);
        var previous = fe.RenderTransform;
        fe.RenderTransform = shift;
        fe.Opacity = 0;

        var fade = new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            fe.BeginAnimation(UIElement.OpacityProperty, null);
            fe.ClearValue(UIElement.OpacityProperty);
            fe.RenderTransform = previous;
        };
        fe.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, duration) { BeginTime = begin, EasingFunction = ease });
    }
}
