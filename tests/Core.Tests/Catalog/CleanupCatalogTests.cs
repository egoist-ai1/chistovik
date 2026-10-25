using Chistovik.Core.Catalog;
using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.Core.Tests.Catalog;

public sealed class CleanupCatalogTests
{
    static readonly CleanupCatalog Cat = CleanupCatalog.Load();

    static readonly string[] MachineBases = ["windir", "programdata", "systemdrive"];
    static readonly string[] UserBases = ["userlocal", "userroaming"];

    [Fact]
    public void Catalog_has_the_fifteen_c2_categories()
    {
        var expected = new[]
        {
            "browser-chrome", "browser-edge", "browser-firefox", "d3d-shader-cache", "delivery-optimization", "driver-packages",
            "logs-cbs", "minidump", "recycle-bin", "temp-system", "temp-user", "thumbcache", "wer-system", "wer-user", "wu-cleanup",
        };
        Assert.Equal(expected, Cat.Categories.Select(c => c.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Same(Cat.Categories[0], Cat.Find(Cat.Categories[0].Id));
        Assert.Null(Cat.Find("nope"));
        Assert.NotEmpty(CleanupCatalog.Shared.Categories);
    }

    [Fact]
    public void Ids_are_unique_and_match_the_request_id_rule()
    {
        Assert.Equal(Cat.Categories.Count, Cat.Categories.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var c in Cat.Categories)
            Assert.Matches(@"^[A-Za-z0-9_\-]{1,64}$", c.Id);
    }

    [Fact]
    public void Every_root_base_is_allowed_and_agrees_with_the_scope()
    {
        foreach (var c in Cat.Categories)
        {
            Assert.Contains(c.Scope, new[] { "machine", "user" });
            foreach (var r in c.Roots)
            {
                Assert.Contains(r.Base, CleanupPolicy.AllowedBases);
                Assert.Contains(r.Base, c.Scope == "machine" ? MachineBases : UserBases);
            }
        }
    }

    [Fact]
    public void Handler_and_recyclebin_categories_have_no_roots_and_files_categories_have_some()
    {
        foreach (var c in Cat.Categories)
        {
            if (c.Mechanism == "files") Assert.NotEmpty(c.Roots);
            else Assert.Empty(c.Roots);
        }
        Assert.Equal(["delivery-optimization", "driver-packages", "wu-cleanup"],
            Cat.Categories.Where(c => c.Mechanism == "handler").Select(c => c.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("recyclebin", Cat.Find("recycle-bin")!.Mechanism);
    }

    [Fact]
    public void File_categories_keep_files_for_at_least_a_day()
    {
        foreach (var c in Cat.Categories.Where(c => c.Mechanism == "files"))
            Assert.True(c.MinAgeHours >= 24, c.Id);
        Assert.Equal(168, Cat.Find("logs-cbs")!.MinAgeHours);
        Assert.Equal(168, Cat.Find("minidump")!.MinAgeHours);
    }

    [Fact]
    public void No_sub_path_can_escape_its_base()
    {
        foreach (var r in Cat.Categories.SelectMany(c => c.Roots))
        {
            Assert.DoesNotContain("..", r.Sub);
            Assert.DoesNotContain(":", r.Sub);
            Assert.DoesNotContain(@"\\", r.Sub);
            Assert.DoesNotContain("/", r.Sub);
            Assert.False(r.Sub.StartsWith('\\') || r.Sub.EndsWith('\\'));
            Assert.False(string.IsNullOrWhiteSpace(r.Sub));
        }
    }

    [Fact]
    public void Only_firefox_uses_a_wildcard_and_only_as_one_middle_component()
    {
        var withStar = Cat.Categories.Where(c => c.Roots.Any(r => r.Sub.Contains('*'))).ToList();
        var ff = Assert.Single(withStar);
        Assert.Equal("browser-firefox", ff.Id);
        var root = Assert.Single(ff.Roots);
        Assert.Equal(@"Mozilla\Firefox\Profiles\*\cache2", root.Sub);
    }

    [Fact]
    public void Every_category_passes_the_policy_catalog_check()
    {
        var policy = new CleanupPolicy(PolicyContext.Fixed);
        foreach (var c in Cat.Categories)
            Assert.True(policy.CheckCatalog(c).IsAllowed, c.Id);
    }

    [Fact]
    public void Specific_values_match_the_design_table()
    {
        var cbs = Cat.Find("logs-cbs")!;
        Assert.Equal(["CBS.log"], cbs.KeepNames!.ToArray());
        var th = Cat.Find("thumbcache")!;
        Assert.Equal(["thumbcache_*.db", "iconcache_*.db"], th.Patterns!.ToArray());
        Assert.Equal("explorer", th.Restart);
        Assert.Equal("msedge", Cat.Find("browser-edge")!.SkipIfRunning);
        Assert.Equal("chrome", Cat.Find("browser-chrome")!.SkipIfRunning);
        Assert.Equal("firefox", Cat.Find("browser-firefox")!.SkipIfRunning);
        Assert.False(Cat.Find("recycle-bin")!.DefaultOn);
        Assert.False(Cat.Find("minidump")!.DefaultOn);
        Assert.Equal(3, Cat.Find("wer-system")!.Roots.Count);
        Assert.Equal(2, Cat.Find("wer-user")!.Roots.Count);
        Assert.Equal(3, Cat.Find("browser-edge")!.Roots.Count);
        Assert.Equal("machine", Cat.Find("minidump")!.Scope);
        Assert.Equal("user", Cat.Find("recycle-bin")!.Scope);
        Assert.All(Cat.Categories.Where(c => c.Id != "thumbcache"), c => Assert.Equal("none", c.Restart));
    }

    [Fact]
    public void Catalog_resource_hash_input_is_embedded()
    {
        using var s = typeof(CleanupCatalog).Assembly.GetManifestResourceStream("catalog/cleanup.json");
        Assert.NotNull(s);
    }

    // ---------- плохие каталоги ----------

    const string Good = """
        { "id":"{ID}","title":"t","description":"d","scope":"machine","roots":[{"base":"windir","sub":"Temp"}],
          "minAgeHours":24,"mechanism":"files","risk":"low","defaultOn":true,"restart":"none" }
        """;

    static string Doc(params string[] items) => "{ \"categories\": [" + string.Join(",", items) + "] }";

    [Fact]
    public void Duplicate_ids_are_rejected()
        => Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Doc(Good.Replace("{ID}", "a"), Good.Replace("{ID}", "a"))));

    [Theory]
    [InlineData("bad id")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Bad_id_format_is_rejected(string id)
        => Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Doc(Good.Replace("{ID}", id))));

    [Fact]
    public void Unknown_members_missing_lists_and_bad_json_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Doc(Good.Replace("{ID}", "a").Replace("\"risk\"", "\"extra\":1,\"risk\""))));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse("{ \"categories\": [] }"));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse("{"));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse("{}"));
    }

    [Theory]
    [InlineData("\"risk\":\"low\"", "\"risk\":\"nope\"")]
    [InlineData("\"scope\":\"machine\"", "\"scope\":\"both\"")]
    [InlineData("\"mechanism\":\"files\"", "\"mechanism\":\"magic\"")]
    [InlineData("\"restart\":\"none\"", "\"restart\":\"reboot\"")]
    [InlineData("\"title\":\"t\"", "\"title\":\" \"")]
    [InlineData("\"minAgeHours\":24", "\"minAgeHours\":-1")]
    [InlineData("\"base\":\"windir\"", "\"base\":\"userlocal\"")]
    [InlineData("\"sub\":\"Temp\"", "\"sub\":\"System32\"")]
    [InlineData("\"sub\":\"Temp\"", "\"sub\":\"Temp\\\\..\\\\System32\"")]
    [InlineData("\"roots\":[{\"base\":\"windir\",\"sub\":\"Temp\"}]", "\"roots\":[]")]
    public void Field_violations_are_rejected(string from, string to)
        => Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Doc(Good.Replace("{ID}", "a").Replace(from, to))));

    [Fact]
    public void Control_document_parses()
        => Assert.Equal("a", CleanupCatalog.Parse(Doc(Good.Replace("{ID}", "a"))).Find("a")!.Id);
}
