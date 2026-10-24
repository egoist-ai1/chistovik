using Chistovik.Core.Catalog;
using Xunit;

namespace Chistovik.Core.Tests.Catalog;

public sealed class PowerCatalogTests
{
    static readonly PowerCatalog Cat = PowerCatalog.Load();

    [Fact]
    public void Schemes_map_to_windows_guids()
    {
        // В Windows saver это SCHEME_MAX, а high это SCHEME_MIN (имена в powercfg обратные смыслу).
        Assert.Equal(Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a"), Cat.FindScheme("saver")!.Guid);
        Assert.Equal(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), Cat.FindScheme("bal")!.Guid);
        Assert.Equal(Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), Cat.FindScheme("high")!.Guid);
        Assert.Equal(Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), Cat.FindScheme("ult")!.Guid);
    }

    [Fact]
    public void Only_ultimate_is_created_from_template()
    {
        foreach (var s in Cat.Schemes)
            Assert.Equal(s.Id == "ult", s.CreateFromTemplate is not null);
        Assert.False(Cat.FindScheme("ult")!.BuiltIn);
    }

    [Fact]
    public void Every_mode_power_plan_has_a_scheme()
    {
        foreach (var m in TweakCatalog.Shared.Modes)
            Assert.NotNull(Cat.FindScheme(m.PowerPlan));
    }

    [Fact]
    public void Settings_are_the_seven_known_ones_with_sane_ranges()
    {
        Assert.Equal(["aspm", "boost", "display", "max", "min", "sleep", "usb"], Cat.Settings.Select(s => s.Id).Order().ToArray());
        foreach (var s in Cat.Settings)
        {
            Assert.True(s.Min <= s.Max, s.Id);
            if (s.Unit == "percent") Assert.Equal(100u, s.Max);
        }
        Assert.Equal(6u, Cat.FindSetting("boost")!.Max);
        Assert.Equal(1u, Cat.FindSetting("usb")!.Max);
    }

    [Fact]
    public void Guids_are_unique_and_parameters_share_known_subgroups()
    {
        var all = Cat.Settings.Select(s => s.Setting).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(Cat.FindSetting("min")!.Subgroup, Cat.FindSetting("boost")!.Subgroup);
    }

    [Theory]
    [InlineData("""{"schemes":[],"settings":[]}""")]
    [InlineData("""{"schemes":[],"settings":[],"extra":1}""")]
    [InlineData("не json")]
    public void Malformed_catalog_is_rejected(string json)
        => Assert.Throws<InvalidDataException>(() => PowerCatalog.Parse(json));
}

public sealed class TweakEffectTests
{
    [Fact]
    public void Every_tweak_has_a_known_effect()
    {
        foreach (var t in TweakCatalog.Shared.Tweaks)
            Assert.Contains(t.Effect, TweakCatalog.Effects);
    }

    [Theory]
    [InlineData("minanim", "spi-animation")]
    [InlineData("dragfull", "spi-dragfull")]
    [InlineData("mouseacc", "spi-mouse")]
    [InlineData("transp", "broadcast")]
    public void Session_effects_are_assigned(string id, string effect)
        => Assert.Equal(effect, TweakCatalog.Shared.FindTweak(id)!.Effect);
}
