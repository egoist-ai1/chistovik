using System.IO;
using System.Windows.Media;
using Chistovik.App.Services;

namespace Chistovik.App.Mocks;

/// <summary>
/// Значки для --demo и снимков: только shell32.dll и imageres.dll из системного каталога, остальное без значка (остаётся общий).
/// Так кадры не зависят от программ, установленных на машине. Ответ готов сразу (без фоновой загрузки), поэтому снимок не ждёт.
/// </summary>
public sealed class DemoIconService : IIconService
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.Ordinal);

    public bool TryGetCached(string? source, IconSize size, out ImageSource? icon)
    {
        icon = Get(source, size);
        return true;
    }

    public Task<ImageSource?> LoadAsync(string? source, IconSize size, CancellationToken ct = default)
        => Task.FromResult(Get(source, size));

    /// <summary>Строка-источник демонстрационного значка из shell32.dll по номеру.</summary>
    public static string Shell32(int index)
        => "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll") + "\"," + index;

    private ImageSource? Get(string? source, IconSize size)
    {
        if (IconSources.Parse(source) is not { } request) return null;
        var name = Path.GetFileName(request.Path);
        if (!name.Equals("shell32.dll", StringComparison.OrdinalIgnoreCase) && !name.Equals("imageres.dll", StringComparison.OrdinalIgnoreCase))
            return null;
        var key = IconService.KeyOf(request, size);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
            var icon = NativeIcons.Extract(request, (int)size);
            _cache[key] = icon;
            return icon;
        }
    }
}
