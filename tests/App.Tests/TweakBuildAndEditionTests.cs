using Chistovik.App;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

public class TweakBuildAndEditionTests
{
    static TweakRowViewModel Row(string id, int build, Action? changed = null, TweakState state = TweakState.Off)
        => new(TweakCatalog.Shared.FindTweak(id)!, state, changed ?? (() => { }), edition: "Pro", build: build);

    [Fact]
    public void Tweak_below_min_build_is_unavailable_with_russian_reason()
    {
        var row = Row("taskbarAl", 19045);

        Assert.True(row.IsUnavailable);
        Assert.False(row.IsAvailable);
        Assert.Equal("Нужна Windows 11 (сборка 22000+)", row.UnavailableText);
    }

    [Fact]
    public void Tweak_on_supported_build_is_available()
    {
        var row = Row("taskbarAl", 22631);

        Assert.False(row.IsUnavailable);
        Assert.Equal("", row.UnavailableText);
    }

    [Fact]
    public void Unknown_build_does_not_block()
        => Assert.False(Row("aiDataMachine", 0).IsUnavailable);

    [Fact]
    public void Unavailable_row_ignores_toggle_and_is_never_pending()
    {
        var changes = 0;
        var row = Row("aiDataMachine", 22631, () => changes++);

        row.IsOn = true;

        Assert.False(row.IsOn);
        Assert.False(row.IsPending);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Max_build_blocks_newer_builds()
    {
        var entry = TweakCatalog.Shared.FindTweak("ext")! with { MaxBuild = 19045 };

        var row = new TweakRowViewModel(entry, TweakState.Off, () => { }, "Pro", 22631);

        Assert.True(row.IsUnavailable);
        Assert.Contains("19045", row.UnavailableText);
    }

    [Fact]
    public void Row_without_limits_is_available_on_any_build()
        => Assert.False(Row("ext", 10240).IsUnavailable);

    [Theory]
    [InlineData("ServerRdsh", "Enterprise")]
    [InlineData("ServerStandard", "Enterprise")]
    [InlineData("ServerDatacenter", "Enterprise")]
    [InlineData("CloudEdition", "Pro")]
    [InlineData("CloudEditionN", "Pro")]
    [InlineData("Core", "Home")]
    [InlineData("Professional", "Pro")]
    [InlineData("EnterpriseS", "Enterprise")]
    [InlineData("Education", "Education")]
    [InlineData("SomethingNew", null)]
    [InlineData("", null)]
    public void Edition_ids_are_normalized(string id, string? expected)
        => Assert.Equal(expected, WindowsEdition.Normalize(id));
}
