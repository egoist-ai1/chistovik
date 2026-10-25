using System.Windows.Media;

namespace Chistovik.App.Services;

/// <summary>Размер значка: строка списка (рисуется 24 DIP) или панель подробностей (40-48 DIP). Число это пиксели исходного значка.</summary>
public enum IconSize
{
    Row = 48,
    Detail = 96,
}

/// <summary>
/// Настоящие значки программ и файлов для списков. Реализация читает только файлы, ничего не запускает; на сбой отвечает
/// null, и строка остаётся с общим значком.
/// </summary>
public interface IIconService
{
    /// <summary>Загружает значок в фоне. null — значка нет или загрузка не удалась. Исключений не бросает (кроме отмены).</summary>
    Task<ImageSource?> LoadAsync(string? source, IconSize size, CancellationToken ct = default);

    /// <summary>
    /// Значок уже в памяти: true и значок (или null, если ранее выяснили, что значка нет). Без обращения к диску,
    /// поэтому строка, которую показывают повторно, получает значок сразу, без мигания.
    /// </summary>
    bool TryGetCached(string? source, IconSize size, out ImageSource? icon)
    {
        icon = null;
        return false;
    }
}

/// <summary>Кэш с вытеснением давно не используемых записей. Потокобезопасный; размер ограничен.</summary>
public sealed class LruCache<TValue>
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TValue Value)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, TValue Value)> _order = new();

    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public bool TryGet(string key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(string key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }
            _map[key] = _order.AddFirst((key, value));
            while (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}

/// <summary>Настройки <see cref="IconService"/>; всё, что касается системы, подменяется в тестах.</summary>
public sealed class IconServiceOptions
{
    public int MaxParallel { get; init; } = 3;
    public int CacheSize { get; init; } = 512;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Можно ли обращаться к файлу: по умолчанию он есть и лежит на фиксированном диске.</summary>
    public Func<string, bool> IsLocalFile { get; init; } = LocalFiles.IsFixedFile;

    /// <summary>Достаёт значок (путь, индекс, пиксели). По умолчанию Win32 (<see cref="NativeIcons"/>).</summary>
    public Func<IconRequest, int, ImageSource?> Extract { get; init; } = NativeIcons.Extract;
}

/// <summary>
/// Загрузчик значков: фоновые потоки с ограничением числа одновременных загрузок, общий кэш по ключу «путь|индекс|размер»
/// и один запрос на один ключ. Файл не запускается; сетевые и съёмные диски, «..», устройства и длинные пути отвергаются
/// ещё при разборе источника. Пути в журнал не пишутся.
/// </summary>
public sealed class IconService : IIconService, IDisposable
{
    private readonly IconServiceOptions _options;
    private readonly LruCache<ImageSource?> _cache;
    private readonly SemaphoreSlim _gate;
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<ImageSource?>> _inFlight = new(StringComparer.Ordinal);

    public IconService(IconServiceOptions? options = null)
    {
        _options = options ?? new IconServiceOptions();
        _cache = new LruCache<ImageSource?>(_options.CacheSize);
        _gate = new SemaphoreSlim(Math.Clamp(_options.MaxParallel, 1, 8));
    }

    public int CachedCount => _cache.Count;

    /// <summary>Ключ кэша: путь без учёта регистра, индекс, размер.</summary>
    public static string KeyOf(IconRequest request, IconSize size)
        => request.Path.ToUpperInvariant() + "|" + (request.Index?.ToString() ?? "-") + "|" + (int)size;

    public bool TryGetCached(string? source, IconSize size, out ImageSource? icon)
    {
        icon = null;
        if (IconSources.Parse(source) is not { } request) return true; // источника нет: ждать нечего
        return _cache.TryGet(KeyOf(request, size), out icon);
    }

    public async Task<ImageSource?> LoadAsync(string? source, IconSize size, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (IconSources.Parse(source) is not { } request) return null;
        var key = KeyOf(request, size);
        if (_cache.TryGet(key, out var cached)) return cached;

        Task<ImageSource?> work;
        lock (_lock)
        {
            // Пока ждали замок, другой запрос мог закончить и положить значок в кэш.
            if (_cache.TryGet(key, out cached)) return cached;
            if (!_inFlight.TryGetValue(key, out work!))
            {
                work = ProduceAsync(request, size, key);
                _inFlight[key] = work;
            }
        }
        return await work.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ImageSource?> ProduceAsync(IconRequest request, IconSize size, string key)
    {
        ImageSource? result = null;
        await Task.Yield(); // не работаем под замком вызывающего
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Зависшее обращение к диску бросаем по таймауту: поток остаётся в фоне, но очередь не встаёт.
                var job = Task.Run(() => Safe(request, size));
                result = await job.WaitAsync(_options.Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException) { result = null; }
            finally { _gate.Release(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = null;
        }
        finally
        {
            _cache.Set(key, result);
            lock (_lock) _inFlight.Remove(key);
        }
        return result;
    }

    private ImageSource? Safe(IconRequest request, IconSize size)
    {
        try
        {
            return _options.IsLocalFile(request.Path) ? _options.Extract(request, (int)size) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // любая ошибка Win32/WPF: значка нет, остаётся общий
        }
    }

    public void Dispose() => _gate.Dispose();
}
