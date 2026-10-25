using Chistovik.Core.Catalog;
using Xunit;

namespace Chistovik.Core.Tests.Catalog;

public sealed class ModeCatalogTests
{
    /// <summary>
    /// Режимы применяют <c>mode.Tweaks</c> целиком и не смотрят на привязку твика к сборке Windows (в отличие от строки твика в интерфейсе).
    /// Пока такой проверки в режимах нет, ни один режим не должен ссылаться на твик с minBuild или maxBuild.
    /// </summary>
    [Fact]
    public void No_mode_references_a_tweak_limited_by_windows_build()
    {
        var cat = TweakCatalog.Shared;
        Assert.NotEmpty(cat.Modes);

        var offenders = cat.Modes
            .SelectMany(m => m.Tweaks.Keys.Select(id => (Mode: m.Id, Tweak: cat.FindTweak(id))))
            .Where(x => x.Tweak is { } t && (t.MinBuild is not null || t.MaxBuild is not null))
            .Select(x => x.Mode + " -> " + x.Tweak!.Id)
            .ToList();

        Assert.True(offenders.Count == 0, "Режим ссылается на твик с minBuild/maxBuild: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_tweak_a_mode_references_exists()
    {
        var cat = TweakCatalog.Shared;
        foreach (var m in cat.Modes)
            foreach (var id in m.Tweaks.Keys)
                Assert.NotNull(cat.FindTweak(id));
    }
}
