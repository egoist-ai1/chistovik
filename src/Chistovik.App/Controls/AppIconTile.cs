using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Chistovik.App.ViewModels;

namespace Chistovik.App.Controls;

/// <summary>Картинка без узла в дереве автоматизации: значок программы декоративный, смысл несёт соседний текст.</summary>
internal sealed class DecorativeImage : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

/// <summary>
/// Плитка значка строки списка: настоящий значок программы или файла (<see cref="Slot"/>), а пока его нет или он не
/// загрузился, прежний общий векторный значок <see cref="Glyph"/>. Фон и скругление задаются как у обычного Border.
/// Значок ставится в плитку с отступом; изображение рисуется с высоким качеством масштабирования на любом DPI.
/// </summary>
public sealed class AppIconTile : Border
{
    public static readonly DependencyProperty SlotProperty = DependencyProperty.Register(
        nameof(Slot), typeof(IconSlot), typeof(AppIconTile), new PropertyMetadata(null, OnSlotChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(AppIconTile), new PropertyMetadata("", (d, e) => ((AppIconTile)d)._glyph.Kind = (string)e.NewValue));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(AppIconTile), new PropertyMetadata(18.0, (d, e) => ((AppIconTile)d)._glyph.Size = (double)e.NewValue));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(AppIconTile), new PropertyMetadata(null, (d, e) => ((AppIconTile)d)._glyph.Foreground = (Brush?)e.NewValue));

    public static readonly DependencyProperty ImageSizeProperty = DependencyProperty.Register(
        nameof(ImageSize), typeof(double), typeof(AppIconTile), new PropertyMetadata(24.0, (d, e) =>
        {
            var pic = ((AppIconTile)d)._picture;
            pic.Width = pic.Height = (double)e.NewValue;
        }));

    private readonly Icon _glyph = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DecorativeImage _picture = new()
    {
        Width = 24,
        Height = 24,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
        SnapsToDevicePixels = true,
    };

    private IconSlot? _subscribed;

    public AppIconTile()
    {
        // HighQuality: значок 48 или 96 px ужимается до 24 или 48 DIP, на 100-250 % DPI без ступенек.
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _glyph.Size = 18;
        var grid = new Grid();
        grid.Children.Add(_glyph);
        grid.Children.Add(_picture);
        Child = grid;
        Focusable = false;
        IsHitTestVisible = false;
        Unloaded += (_, _) => Unsubscribe();
        Loaded += (_, _) => { Subscribe(); Refresh(); };
    }

    /// <summary>Слот настоящего значка строки; null или пустой слот оставляют общий значок.</summary>
    public IconSlot? Slot { get => (IconSlot?)GetValue(SlotProperty); set => SetValue(SlotProperty, value); }

    /// <summary>Имя общего значка из Themes/Icons.xaml: запасной вариант.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }
    public Brush? GlyphBrush { get => (Brush?)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }

    /// <summary>Сторона настоящего значка в единицах интерфейса (24 в строках, 40-48 в подробностях).</summary>
    public double ImageSize { get => (double)GetValue(ImageSizeProperty); set => SetValue(ImageSizeProperty, value); }

    protected override AutomationPeer? OnCreateAutomationPeer() => null;

    private static void OnSlotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (AppIconTile)d;
        tile.Unsubscribe();
        if (tile.IsLoaded) tile.Subscribe();
        tile.Refresh();
    }

    private void Subscribe()
    {
        if (_subscribed is not null || Slot is not { } slot) return;
        _subscribed = slot;
        slot.PropertyChanged += OnSlotPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null) return;
        _subscribed.PropertyChanged -= OnSlotPropertyChanged;
        _subscribed = null;
    }

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Загрузка заканчивается в фоне: изображение меняем в потоке интерфейса.
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>Читает Image слота (это и запускает ленивую загрузку) и переключает значок.</summary>
    private void Refresh()
    {
        var image = Slot?.Image;
        _picture.Source = image;
        var has = image is not null;
        _picture.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _glyph.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }
}
