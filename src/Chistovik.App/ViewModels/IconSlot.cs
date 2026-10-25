using System.Windows.Media;
using Chistovik.App.Mvvm;
using Chistovik.App.Services;

namespace Chistovik.App.ViewModels;

/// <summary>
/// Значок строки списка. Загрузка начинается лениво, при первом обращении привязки к Image/HasImage: ListBox с виртуализацией
/// читает свойства только у созданных строк, поэтому значки грузятся лишь для видимых. Значок из кэша подставляется сразу,
/// без мигания; иначе загрузка идёт в фоне. Нет службы, нет источника или сбой: Image остаётся null, строка показывает общий значок.
/// </summary>
public sealed class IconSlot : ObservableObject
{
    private readonly IIconService? _service;
    private readonly string? _source;
    private readonly IconSize _size;
    private ImageSource? _image;
    private bool _requested;

    public IconSlot(IIconService? service, string? source, IconSize size = IconSize.Row)
    {
        _service = service;
        _source = source;
        _size = size;
    }

    /// <summary>Идущая загрузка (для тестов и ожидания); завершена, если значок не запрашивался или уже в кэше.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    public ImageSource? Image
    {
        get { Request(); return _image; }
    }

    public bool HasImage
    {
        get { Request(); return _image is not null; }
    }

    /// <summary>Значок декоративный: в имя элемента для чтения с экрана он ничего не добавляет.</summary>
    public override string ToString() => "";

    private void Request()
    {
        if (_requested) return;
        _requested = true;
        if (_service is null || string.IsNullOrWhiteSpace(_source)) return;
        if (_service.TryGetCached(_source, _size, out var cached))
        {
            _image = cached;
            return;
        }
        Loading = LoadAsync();
    }

    private async Task LoadAsync()
    {
        ImageSource? icon = null;
        try
        {
            await Task.Yield(); // не меняем свойства внутри чтения привязки
            icon = await _service!.LoadAsync(_source, _size);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // значка нет: остаётся общий
        }
        if (icon is null) return;
        _image = icon;
        OnPropertyChanged(nameof(Image));
        OnPropertyChanged(nameof(HasImage));
    }
}
