using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Chistovik.App.Controls;

/// <summary>
/// Векторный значок: геометрия 24×24 из словаря Themes/Icons.xaml по имени (<see cref="Kind"/>), линия цвета Foreground.
/// Рисуется как вектор и масштабируется без размытия на любом DPI. Для программ чтения с экрана значок
/// декоративный: смысл несёт соседний текст, поэтому он скрыт из дерева автоматизации.
/// </summary>
public sealed class Icon : Control
{
    private const double Grid = 24;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        // Кисть значка берётся из Foreground: при смене цвета нужна перерисовка.
        // Inherits оставляем: значок берёт цвет у родителя, пока Foreground не задан явно.
        ForegroundProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(
            null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new UIPropertyMetadata(false));
    }

    public Icon()
    {
        SnapsToDevicePixels = false;
        UseLayoutRounding = false;
    }

    /// <summary>Имя значка без префикса «Ico.», например «home».</summary>
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Сторона квадрата в единицах интерфейса.</summary>
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Толщина линии в координатах сетки 24×24.</summary>
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;

    protected override Size MeasureOverride(Size constraint) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Kind) || Foreground is not { } brush) return;
        if (TryFindResource("Ico." + Kind) is not Geometry geometry) return;

        var scale = Size / Grid;
        var pen = new Pen(brush, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }
}
