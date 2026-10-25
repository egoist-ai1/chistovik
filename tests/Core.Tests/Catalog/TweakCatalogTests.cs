using Chistovik.Core.Catalog;
using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.Core.Tests.Catalog;

public sealed class TweakCatalogTests
{
    static readonly TweakCatalog Cat = TweakCatalog.Load();

    // Имена значений защит, которые каталог не вправе писать (в дополнение к проверке политикой).
    static readonly string[] ForbiddenValues =
    [
        "EnableLUA", "ConsentPromptBehaviorAdmin", "ConsentPromptBehaviorUser", "PromptOnSecureDesktop",
        "EnableSmartScreen", "SmartScreenEnabled", "ShellSmartScreenLevel",
        "DisableAntiSpyware", "DisableRealtimeMonitoring", "TamperProtection",
    ];

    static readonly string[] ForbiddenKeyParts =
    [
        @"\Control\Lsa", @"\Control\CI", @"\Control\DeviceGuard", "Windows Defender", "SmartScreen",
        @"CurrentVersion\Policies\System",
    ];

    public static IEnumerable<object[]> AllTweaks() => Cat.Tweaks.Select(t => new object[] { t.Id });

    [Fact]
    public void Catalog_is_not_empty()
    {
        Assert.NotEmpty(Cat.Tweaks);
        Assert.NotEmpty(Cat.Modes);
    }

    [Theory]
    [MemberData(nameof(AllTweaks))]
    public void Policy_allows_every_tweak(string id)
    {
        var t = Cat.FindTweak(id)!;
        var d = RegistryPolicy.CheckSetValue(t.FullKey, t.Value);
        Assert.True(d.Verdict == Verdict.Allow, $"{id}: {d.Verdict} {d.Rule} {d.Reason}");
    }

    [Fact]
    public void No_tweak_writes_forbidden_values_or_keys()
    {
        foreach (var t in Cat.Tweaks)
        {
            Assert.DoesNotContain(t.Value, ForbiddenValues, StringComparer.OrdinalIgnoreCase);
            foreach (var part in ForbiddenKeyParts)
                Assert.DoesNotContain(part, t.FullKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Hklm_tweaks_require_admin()
    {
        foreach (var t in Cat.Tweaks.Where(t => t.Hive == "HKLM"))
            Assert.True(t.RequiresAdmin, t.Id);
    }

    [Fact]
    public void Ids_are_unique_and_modes_reference_existing_tweaks()
    {
        Assert.Equal(Cat.Tweaks.Count, Cat.Tweaks.Select(t => t.Id).Distinct().Count());
        foreach (var m in Cat.Modes)
            foreach (var id in m.Tweaks.Keys)
                Assert.NotNull(Cat.FindTweak(id));
    }

    [Fact]
    public void Every_tweak_has_scope_matching_hive()
    {
        foreach (var t in Cat.Tweaks)
            Assert.Equal(t.Hive == "HKCU" ? "user" : "machine", t.Scope);
    }

    [Fact]
    public void Editions_and_reverted_by_use_closed_sets()
    {
        foreach (var t in Cat.Tweaks)
        {
            if (t.Editions is not null) Assert.All(t.Editions, e => Assert.Contains(e, TweakCatalog.Editions));
            if (t.RevertedBy is not null) Assert.Contains(t.RevertedBy, TweakCatalog.RevertedByValues);
        }
    }

    [Fact]
    public void Content_delivery_tweaks_are_reverted_by_feature_update_and_policies_are_not()
    {
        foreach (var t in Cat.Tweaks.Where(t => t.Key.EndsWith("ContentDeliveryManager", StringComparison.OrdinalIgnoreCase)))
            Assert.Equal("feature-update", t.RevertedBy);
        foreach (var t in Cat.Tweaks.Where(t => t.Key.Contains(@"Policies\Microsoft", StringComparison.OrdinalIgnoreCase)))
            Assert.True(t.RevertedBy is null or "none", t.Id);
    }

    [Fact]
    public void Tweaks_never_touch_delivery_optimization_or_windows_update()
    {
        foreach (var t in Cat.Tweaks)
        {
            Assert.DoesNotContain("DeliveryOptimization", t.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WindowsUpdate", t.Key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Consent_store_tweaks_restore_allow()
    {
        foreach (var t in Cat.Tweaks.Where(t => t.Key.Contains("ConsentStore", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal("careful", t.Risk);
            Assert.Equal("Deny", t.OnValue.GetString());
            Assert.Equal("Allow", t.OffValue!.Value.GetString());
        }
    }

    [Theory]
    [InlineData("\"editions\":[\"Server\"]")]
    [InlineData("\"editions\":[]")]
    [InlineData("\"revertedBy\":\"sometimes\"")]
    [InlineData("\"scope\":\"machine\"")]
    [InlineData("\"scope\":\"all\"")]
    public void Bad_v2_fields_are_rejected(string extra)
        => Fails("[" + Tweak("a", "," + extra) + "]");

    [Fact]
    public void Valid_v2_fields_parse()
    {
        var c = TweakCatalog.Parse("[" + Tweak("a", ",\"editions\":[\"Pro\",\"Enterprise\"],\"revertedBy\":\"none\",\"scope\":\"user\"") + "]", GoodMode);
        Assert.Equal("Pro,Enterprise", string.Join(",", c.Tweaks[0].Editions!));
    }

    // ---------- валидация на испорченных данных ----------

    const string GoodMode = """[{"id":"m","name":"M","tag":"t","description":"d","powerPlan":"bal","requiresAdmin":false,"tweaks":{}}]""";

    static string Tweak(string id = "a", string extra = "") =>
        $$"""
        {"id":"{{id}}","title":"T","description":"D","area":"tw","risk":"safe","hive":"HKCU","key":"Software\\X","value":"V",
         "valueType":"DWord","onValue":1,"offValue":0,"restart":"none","effect":"none","requiresAdmin":false,"source":"known-setting","verified":false{{extra}}}
        """;

    static InvalidDataException Fails(string tweaks, string modes = GoodMode)
        => Assert.Throws<InvalidDataException>(() => TweakCatalog.Parse(tweaks, modes));

    [Fact]
    public void Valid_minimal_catalog_parses()
    {
        var c = TweakCatalog.Parse("[" + Tweak() + "]", GoodMode);
        Assert.Single(c.Tweaks);
    }

    [Fact]
    public void Duplicate_tweak_ids_are_rejected()
        => Assert.Contains("повторяющийся", Fails("[" + Tweak("a") + "," + Tweak("a") + "]").Message);

    [Fact]
    public void Mode_with_unknown_tweak_is_rejected()
    {
        var modes = GoodMode.Replace("\"tweaks\":{}", "\"tweaks\":{\"ghost\":true}");
        Assert.Contains("несуществующий твик", Fails("[" + Tweak() + "]", modes).Message);
    }

    [Fact]
    public void Duplicate_mode_ids_are_rejected()
    {
        var m = GoodMode.Trim('[', ']');
        Assert.Contains("повторяющийся", Fails("[" + Tweak() + "]", "[" + m + "," + m + "]").Message);
    }

    [Theory]
    [InlineData("\"area\":\"tw\"", "\"area\":\"zz\"")]
    [InlineData("\"risk\":\"safe\"", "\"risk\":\"wild\"")]
    [InlineData("\"hive\":\"HKCU\"", "\"hive\":\"HKCR\"")]
    [InlineData("\"valueType\":\"DWord\"", "\"valueType\":\"Binary\"")]
    [InlineData("\"restart\":\"none\"", "\"restart\":\"sometime\"")]
    [InlineData("\"effect\":\"none\"", "\"effect\":\"unknown\"")]
    [InlineData("\"title\":\"T\"", "\"title\":\"\"")]
    [InlineData("\"description\":\"D\"", "\"description\":\" \"")]
    [InlineData("\"onValue\":1", "\"onValue\":\"text\"")]
    public void Bad_field_values_are_rejected(string from, string to)
        => Fails("[" + Tweak().Replace(from, to) + "]");

    [Fact]
    public void Unknown_power_plan_is_rejected()
        => Fails("[" + Tweak() + "]", GoodMode.Replace("\"bal\"", "\"turbo\""));

    [Fact]
    public void Mode_with_admin_tweak_must_be_marked_admin()
    {
        var admin = Tweak("a").Replace("\"hive\":\"HKCU\"", "\"hive\":\"HKLM\"").Replace("\"requiresAdmin\":false", "\"requiresAdmin\":true");
        var modes = GoodMode.Replace("\"tweaks\":{}", "\"tweaks\":{\"a\":true}");
        Assert.Contains("прав администратора", Fails("[" + admin + "]", modes).Message);
    }

    [Fact]
    public void Broken_json_gives_invalid_data()
        => Fails("[{");

    [Fact]
    public void Unknown_json_field_is_rejected()
        => Fails("[" + Tweak("a", ",\"extra\":1") + "]");
}
