using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Chistovik.App.Controls;

/// <summary>
/// Ключ оттенка раздела («Violet») в кисть из Themes/Tokens.xaml. Параметр: пусто — линия и текст,
/// «Tint» — заливка, «Line» — рамка. Палитра живёт только в XAML, в коде нет ни одного цвета.
/// </summary>
public sealed class HueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrEmpty(key)) key = "Lime";
        var name = "Hue" + key + (parameter as string ?? "") + "Brush";
        return Application.Current?.TryFindResource(name) as Brush
            ?? Application.Current?.TryFindResource("HueLimeBrush") as Brush
            ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
