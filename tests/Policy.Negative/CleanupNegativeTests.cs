using Chistovik.Core.Catalog;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>Негативные тесты политики очистки диска (C2) на уровне фактов: DESIGN C2C3 §3, пункты 1-6 и 8-11.</summary>
public class CleanupNegativeTests
{
    static readonly CleanupPolicy Policy = new(PolicyContext.Fixed);
    static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    const string RootTemp = @"C:\Windows\Temp";
    const string RootGuid = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp";
    const string ClientSid = "S-1-5-21-1-2-3-1001";
    const string OtherSid = "S-1-5-21-1-2-3-1002";

    static CleanupCategory Cat(
        string scope = "machine", string @base = "windir", string sub = "Temp", int age = 24,
        string[]? keep = null, string[]? patterns = null, string mech = "files", string? skip = null)
        => new("x", "t", "d", scope, mech == "files" ? new[] { new CleanupRoot(@base, sub) } : Array.Empty<CleanupRoot>(), age, keep, patterns, mech, "low", true, skip, "none");

    static DirEntryFacts Entry(string name = "a.tmp", bool dir = false, bool reparse = false, double ageHours = 100, double createdHours = 100)
        => new(name, dir, reparse, 10, Now.AddHours(-createdHours), Now.AddHours(-ageHours));

    static FileFacts Root(string? finalPath = RootGuid, bool exists = true, bool reparse = false, string? requestedVolume = VolumeC)
        => new(exists, reparse, finalPath, "S-1-5-18", [], requestedVolume);

    static DirFacts Profile(bool exists = true, bool reparse = false, string? owner = ClientSid)
        => new(exists, reparse, owner, []);

    // ---------- 1, 2: ссылки ----------

    [Fact]
    public void Junction_directory_and_symlink_file_entries_are_skipped_as_reparse()
    {
        var c = Cat();
        var j = Policy.CheckEntry(RootGuid, Entry("J-System32", dir: true, reparse: true), Now, c);
        var l = Policy.CheckEntry(RootGuid, Entry("L-notepad", reparse: true), Now, c);
        Assert.True(j.IsDenied); Assert.Equal("cleanup-reparse", j.Rule);
        Assert.True(l.IsDenied); Assert.Equal("cleanup-reparse", l.Rule);
    }

    [Fact]
    public void Reparse_wins_even_for_old_entries_that_match_patterns()
    {
        var c = Cat(patterns: ["*.db"]);
        Assert.Equal("cleanup-reparse", Policy.CheckEntry(RootGuid, Entry("x.db", reparse: true, ageHours: 9999, createdHours: 9999), Now, c).Rule);
    }

    [Fact]
    public void Opened_handle_that_is_a_reparse_point_is_refused()
    {
        var d = Policy.CheckOpened(RootGuid, RootGuid + @"\L-notepad", isReparse: true);
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-reparse", d.Rule);
    }

    // ---------- 3: подмена после перечисления ----------

    [Theory]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\System32\config\SAM")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp2\a.tmp")]        // общий префикс имени
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp")]               // сам корень
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp\..\System32\x")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp\a.tmp:evil")]
    [InlineData(@"\\?\Volume{99999999-2222-3333-4444-555555555555}\Windows\Temp\a.tmp")]        // другой том
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\")]
    [InlineData(@"\\?\UNC\server\share\Windows\Temp\a.tmp")]
    [InlineData(@"\Device\HarddiskVolume3\Windows\Temp\a.tmp")]
    [InlineData("")]
    public void Opened_handle_outside_the_root_is_refused(string entryFinal)
    {
        var d = Policy.CheckOpened(RootGuid, entryFinal, isReparse: false);
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-not-under-root", d.Rule);
    }

    [Fact]
    public void Opened_handle_inside_the_root_is_allowed_case_insensitively()
    {
        Assert.True(Policy.CheckOpened(RootGuid, RootGuid.ToUpperInvariant() + @"\SUB\a.tmp", false).IsAllowed);
        Assert.True(Policy.CheckOpened(RootGuid + @"\", RootGuid + @"\a.tmp", false).IsAllowed);
    }

    [Fact]
    public void Opened_check_with_a_volume_root_is_refused()
    {
        var vol = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
        Assert.Equal("cleanup-not-under-root", Policy.CheckOpened(vol, vol + @"Windows\a", false).Rule);
    }

    // ---------- 4: корень ----------

    [Fact]
    public void Root_that_is_a_reparse_point_is_denied()
    {
        var d = Policy.CheckRoot(RootTemp, Root(reparse: true));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-root-reparse", d.Rule);
    }

    [Fact]
    public void Missing_root_is_a_skip_not_an_allow()
    {
        var d = Policy.CheckRoot(RootTemp, Root(exists: false, finalPath: null));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-root-missing", d.Rule);
    }

    [Theory]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\System32")]   // подмена на System32
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows")]
    [InlineData(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\Windows\Temp\x")]
    [InlineData(@"\\?\D:\Windows\Temp")]                                                  // другой том (буква)
    [InlineData(@"\\?\UNC\srv\sh\Windows\Temp")]
    [InlineData("")]
    [InlineData(null)]
    public void Root_whose_final_path_differs_is_moved(string? final)
    {
        var d = Policy.CheckRoot(RootTemp, Root(final));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-root-moved", d.Rule);
    }

    const string VolumeC = @"\\?\Volume{11111111-2222-3333-4444-555555555555}";
    const string VolumeOther = @"\\?\Volume{99999999-8888-7777-6666-555555555555}";

    [Theory]
    [InlineData(VolumeOther + @"\Windows\Temp")]    // тот же хвост, но другой том: точка монтирования/ссылка на другой диск
    [InlineData(@"\\?\C:\Windows\Temp")]            // том не GUID-формы при заданном томе буквы
    [InlineData(@"D:\Windows\Temp")]
    public void Root_on_another_volume_than_the_requested_letter_is_moved(string final)
    {
        var facts = new FileFacts(true, false, final, "S-1-5-18", [], VolumeC);
        var d = Policy.CheckRoot(RootTemp, facts);
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-root-moved", d.Rule);
    }

    [Theory]
    [InlineData(VolumeC)]
    [InlineData(VolumeC + @"\")]
    public void Root_on_the_requested_volume_is_allowed(string requestedVolume)
    {
        var facts = new FileFacts(true, false, RootGuid, "S-1-5-18", [], requestedVolume);
        var d = Policy.CheckRoot(RootTemp, facts);
        Assert.True(d.IsAllowed, d.Rule);
        Assert.Equal(RootGuid, d.Target);
    }

    [Fact]
    public void Root_with_unknown_requested_volume_is_denied_fail_closed()
    {
        // subst, сетевой или виртуальный диск: том буквы не определился, финальный путь при этом выглядит правдоподобно.
        foreach (var final in new[] { RootGuid, @"\\?\C:\Windows\Temp", @"C:\Windows\Temp" })
        {
            var d = Policy.CheckRoot(RootTemp, Root(final, requestedVolume: null));
            Assert.True(d.IsDenied, final);
            Assert.Equal("cleanup-root-volume-unknown", d.Rule);
        }
    }

    [Theory]
    [InlineData(RootGuid)]
    [InlineData(VolumeC + @"\windows\TEMP\")]
    public void Root_with_matching_final_path_is_allowed_and_target_is_final_path(string final)
    {
        var d = Policy.CheckRoot(RootTemp, Root(final));
        Assert.True(d.IsAllowed);
        Assert.Equal(final, d.Target);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Program Files\Foo")]
    [InlineData(@"C:\ProgramData\Microsoft\Crypto")]
    [InlineData(@"C:\Windows\Temp\..\System32")]
    [InlineData(@"C:\Windows\Temp\*")]
    [InlineData(@"D:\Data")]
    [InlineData(@"C:\Users\Egoist\Documents")]
    [InlineData(@"\\srv\share\Temp")]
    public void Root_outside_the_closed_list_is_denied_before_any_facts(string requested)
    {
        var d = Policy.CheckRoot(requested, Root(requested));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-root-outside", d.Rule);
    }

    [Fact]
    public void User_root_must_be_under_appdata_local_even_on_another_drive()
    {
        Assert.True(Policy.CheckRoot(@"D:\Users\Ann\AppData\Local\Temp", Root(VolumeC + @"\Users\Ann\AppData\Local\Temp")).IsAllowed);
        Assert.Equal("cleanup-root-outside", Policy.CheckRoot(@"D:\Users\Ann\AppData\Roaming\Temp", Root(@"\\?\D:\Users\Ann\AppData\Roaming\Temp")).Rule);
        Assert.Equal("cleanup-root-outside", Policy.CheckRoot(@"D:\Users\Ann\AppData\Local", Root(@"\\?\D:\Users\Ann\AppData\Local")).Rule);
    }

    // ---------- 5: профиль ----------

    [Fact]
    public void Profile_that_is_a_junction_is_denied()
    {
        var d = Policy.CheckProfileDir(ClientSid, @"C:\Users\Ann", Profile(reparse: true));
        Assert.Equal("cleanup-profile-reparse", d.Rule);
        Assert.True(d.IsDenied);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1002")]
    [InlineData("S-1-1-0")]
    [InlineData("")]
    [InlineData(null)]
    public void Profile_owned_by_someone_else_is_denied(string? owner)
    {
        var d = Policy.CheckProfileDir(ClientSid, @"C:\Users\Ann", Profile(owner: owner));
        Assert.Equal("cleanup-profile-owner", d.Rule);
    }

    [Theory]
    [InlineData(@"C:\Windows\ServiceProfiles\LocalService")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\ProgramData\x")]
    [InlineData(@"C:\Program Files\x")]
    [InlineData(@"C:\Program Files (x86)\x")]
    [InlineData(@"C:\")]
    [InlineData(@"\\srv\share\Ann")]
    [InlineData(@"C:\Users\..\Windows\x")]
    public void Profile_in_a_system_location_is_denied(string dir)
    {
        var d = Policy.CheckProfileDir(ClientSid, dir, Profile());
        Assert.Equal("cleanup-profile-location", d.Rule);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-18")]
    public void Profile_owned_by_client_admins_or_system_is_allowed(string owner)
    {
        Assert.True(Policy.CheckProfileDir(ClientSid, @"D:\Users\Ann", Profile(owner: owner)).IsAllowed);
    }

    [Fact]
    public void Profile_check_rejects_a_malformed_client_sid()
    {
        Assert.True(Policy.CheckProfileDir("not-a-sid", @"C:\Users\Ann", Profile(owner: "not-a-sid")).IsDenied);
    }

    [Fact]
    public void Missing_profile_is_a_skip()
    {
        Assert.Equal("cleanup-root-missing", Policy.CheckProfileDir(ClientSid, @"C:\Users\Ann", Profile(exists: false)).Rule);
    }

    // ---------- 6: возраст ----------

    [Fact]
    public void Entry_younger_than_min_age_is_too_fresh_for_files_and_directories()
    {
        var c = Cat();
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry(ageHours: 1, createdHours: 200), Now, c).Rule);
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry("d", dir: true, ageHours: 23.9, createdHours: 23.9), Now, c).Rule);
    }

    [Fact]
    public void Age_is_the_newer_of_created_and_last_write()
    {
        var c = Cat();
        // Старый файл, скопированный сегодня: LastWrite сохранён, Created свежий.
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry(ageHours: 5000, createdHours: 2), Now, c).Rule);
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry(ageHours: 2, createdHours: 5000), Now, c).Rule);
    }

    [Fact]
    public void Timestamps_in_the_future_count_as_fresh()
    {
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry(ageHours: -5, createdHours: 1000), Now, Cat()).Rule);
    }

    [Fact]
    public void Entry_exactly_at_min_age_is_allowed_and_cbs_logs_need_seven_days()
    {
        Assert.True(Policy.CheckEntry(RootGuid, Entry(ageHours: 24, createdHours: 24), Now, Cat()).IsAllowed);
        var cbs = Cat(sub: @"Logs\CBS", age: 168);
        Assert.Equal("cleanup-too-fresh", Policy.CheckEntry(RootGuid, Entry(ageHours: 100, createdHours: 100), Now, cbs).Rule);
        Assert.True(Policy.CheckEntry(RootGuid, Entry(ageHours: 169, createdHours: 169), Now, cbs).IsAllowed);
    }

    // ---------- 8: каталог ----------

    [Theory]
    [InlineData("windir", "System32")]
    [InlineData("windir", @"Temp\..\System32")]
    [InlineData("windir", "..")]
    [InlineData("windir", @"Temp\..")]
    [InlineData("windir", "")]
    [InlineData("windir", @"Temp:stream")]
    [InlineData("windir", @"Temp\\x")]
    [InlineData("windir", @"\Temp")]
    [InlineData("windir", @"Temp\")]
    [InlineData("windir", "Temp/x")]
    [InlineData("windir", "Temp.")]
    [InlineData("windir", "Temp*")]
    [InlineData("windir", @"Temp\*\x")]                       // подстановка допустима только в userlocal
    [InlineData("windir", @"WinSxS")]
    [InlineData("programdata", @"Microsoft\Crypto")]
    [InlineData("programdata", @"Microsoft\Windows\Start Menu")]
    [InlineData("systemdrive", "Windows.old")]
    [InlineData("systemdrive", "Temp")]
    [InlineData("nowhere", "Temp")]
    [InlineData("", "Temp")]
    [InlineData("WINDIR ", "Temp")]
    public void Machine_category_with_a_bad_root_is_refused(string @base, string sub)
    {
        var d = Policy.CheckCatalog(Cat(@base: @base, sub: sub));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-bad-catalog", d.Rule);
    }

    [Theory]
    [InlineData("userlocal", @"Temp\..\..\Roaming")]
    [InlineData("userlocal", @"Packages\Foo")]
    [InlineData("userlocal", @"Microsoft")]
    [InlineData("userlocal", @"Microsoft\Credentials")]
    [InlineData("userlocal", @"Microsoft\Windows\Recent")]
    [InlineData("userlocal", @"Microsoft\Windows")]
    [InlineData("userlocal", @"Foo\*")]                       // подстановка не последним компонентом
    [InlineData("userlocal", @"*\cache2")]                    // и не первым
    [InlineData("userlocal", @"Foo\*\*\cache2")]              // и не дважды
    [InlineData("userlocal", @"Foo\a*b\cache2")]              // и только целым компонентом
    [InlineData("userlocal", @"Foo\%TEMP%")]
    [InlineData("userroaming", @"Temp")]
    public void User_category_with_a_bad_root_is_refused(string @base, string sub)
    {
        var d = Policy.CheckCatalog(Cat(scope: "user", @base: @base, sub: sub));
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-bad-catalog", d.Rule);
    }

    [Fact]
    public void Scope_and_base_must_agree()
    {
        Assert.Equal("cleanup-bad-catalog", Policy.CheckCatalog(Cat(scope: "user", @base: "windir", sub: "Temp")).Rule);
        Assert.Equal("cleanup-bad-catalog", Policy.CheckCatalog(Cat(scope: "machine", @base: "userlocal", sub: "Temp")).Rule);
    }

    [Fact]
    public void Handler_and_recyclebin_categories_cannot_carry_roots_or_filters()
    {
        var withRoot = Cat() with { Mechanism = "handler" };
        Assert.True(Policy.CheckCatalog(withRoot).IsDenied);
        Assert.True(Policy.CheckCatalog(Cat(scope: "machine", mech: "recyclebin")).IsDenied);      // корзина только user
        Assert.True(Policy.CheckCatalog(Cat(scope: "user", mech: "recyclebin", keep: ["a"])).IsDenied);
        Assert.True(Policy.CheckCatalog(Cat(scope: "user", mech: "recyclebin")).IsAllowed);
    }

    [Theory]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a:b")]
    [InlineData("*")]
    public void Keep_names_must_be_plain_names(string name)
        => Assert.True(Policy.CheckCatalog(Cat(keep: [name])).IsDenied);

    [Theory]
    [InlineData("*")]
    [InlineData("..\\x")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Patterns_must_be_plain_name_globs(string pattern)
        => Assert.True(Policy.CheckCatalog(Cat(patterns: [pattern])).IsDenied);

    [Fact]
    public void Catalog_with_a_forbidden_root_fails_to_load()
    {
        static string Json(string @base, string sub) => $$"""
            { "categories": [ { "id":"bad","title":"t","description":"d","scope":"machine",
              "roots":[{"base":"{{@base}}","sub":"{{sub}}"}],"minAgeHours":24,"mechanism":"files","risk":"low","defaultOn":true,"restart":"none" } ] }
            """;
        Assert.Equal("Temp", CleanupCatalog.Parse(Json("windir", "Temp")).Categories[0].Roots[0].Sub);      // контроль: разбор работает
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Json("windir", "System32")));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Json("windir", @"..")));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Json("windir", @"Temp\\\\..\\\\System32")));
        Assert.Throws<InvalidDataException>(() => CleanupCatalog.Parse(Json("c:", "Temp")));
    }

    // ---------- 9: только профиль клиента ----------

    [Fact]
    public void User_roots_are_resolved_only_under_the_given_profile()
    {
        var c = CleanupCatalog.Shared.Find("temp-user")!;
        var d = Policy.ResolveRoots(c, @"C:\Users\Ann", out var roots);
        Assert.True(d.IsAllowed);
        Assert.Equal(new[] { @"C:\Users\Ann\AppData\Local\Temp" }, roots.ToArray());
        Assert.DoesNotContain(roots, r => r.Contains("Bob", StringComparison.OrdinalIgnoreCase));

        Policy.ResolveRoots(c, @"D:\Users\Bob", out var other);
        Assert.Equal(new[] { @"D:\Users\Bob\AppData\Local\Temp" }, other.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(@"\\srv\share\Ann")]
    [InlineData(@"Users\Ann")]
    public void User_category_without_a_usable_profile_has_no_roots(string? profile)
    {
        var d = Policy.ResolveRoots(CleanupCatalog.Shared.Find("temp-user")!, profile, out var roots);
        Assert.True(d.IsDenied);
        Assert.Empty(roots);
    }

    [Fact]
    public void Machine_roots_come_from_the_context_and_ignore_the_profile()
    {
        var d = Policy.ResolveRoots(CleanupCatalog.Shared.Find("wer-system")!, @"C:\Users\Ann", out var roots);
        Assert.True(d.IsAllowed);
        Assert.All(roots, r => Assert.StartsWith(@"C:\ProgramData\Microsoft\Windows\WER\", r));
        Assert.Equal(3, roots.Count);
        Policy.ResolveRoots(CleanupCatalog.Shared.Find("temp-system")!, null, out var t);
        Assert.Equal(new[] { RootTemp }, t.ToArray());
    }

    [Fact]
    public void Resolve_refuses_an_invalid_category_before_building_paths()
    {
        var d = Policy.ResolveRoots(Cat(sub: "System32"), null, out var roots);
        Assert.Equal("cleanup-bad-catalog", d.Rule);
        Assert.Empty(roots);
    }

    [Fact]
    public void Handler_categories_resolve_to_no_roots()
    {
        var d = Policy.ResolveRoots(CleanupCatalog.Shared.Find("wu-cleanup")!, null, out var roots);
        Assert.True(d.IsAllowed);
        Assert.Empty(roots);
    }

    // ---------- Firefox: «*» на один уровень ----------

    [Fact]
    public void Firefox_wildcard_expands_one_level_over_plain_non_reparse_directories_only()
    {
        var c = CleanupCatalog.Shared.Find("browser-firefox")!;
        Policy.ResolveRoots(c, @"C:\Users\Ann", out var roots);
        var template = Assert.Single(roots);
        Assert.Equal(@"C:\Users\Ann\AppData\Local\Mozilla\Firefox\Profiles\*\cache2", template);

        Assert.True(CleanupPolicy.TrySplitWildcard(template, out var parent, out var suffix));
        Assert.Equal(@"C:\Users\Ann\AppData\Local\Mozilla\Firefox\Profiles", parent);
        Assert.Equal(@"\cache2", suffix);

        var expanded = CleanupPolicy.ExpandWildcard(parent, suffix,
        [
            Entry("abc.default-release", dir: true),
            Entry("junction", dir: true, reparse: true),       // ссылка
            Entry("file.txt"),                                  // не каталог
            Entry("..", dir: true),
            Entry("a\\b", dir: true),
            Entry("x:y", dir: true),
            Entry("*", dir: true),
            Entry("trail.", dir: true),
            Entry("", dir: true),
        ]);
        Assert.Equal(new[] { parent + @"\abc.default-release\cache2" }, expanded.ToArray());
    }

    [Fact]
    public void An_unexpanded_template_never_passes_the_root_check()
    {
        var t = @"C:\Users\Ann\AppData\Local\Mozilla\Firefox\Profiles\*\cache2";
        Assert.Equal("cleanup-root-outside", Policy.CheckRoot(t, Root(t)).Rule);
    }

    [Theory]
    [InlineData(@"C:\a\b")]
    [InlineData(@"C:\a\*")]
    [InlineData(@"C:\a\*\b\*\c")]
    [InlineData(@"C:\a\x*\b")]
    [InlineData(@"*\b")]
    public void Wildcard_split_accepts_exactly_one_middle_star(string template)
        => Assert.False(CleanupPolicy.TrySplitWildcard(template, out _, out _));

    // ---------- 10: корзина ----------

    [Fact]
    public void Recycle_bin_with_a_foreign_token_sid_is_denied()
    {
        var d = Policy.CheckRecycleBin(OtherSid, ClientSid);
        Assert.True(d.IsDenied);
        Assert.Equal("recyclebin-foreign-user", d.Rule);
        Assert.Equal("recyclebin-foreign-user", Policy.CheckRecycleBin("", ClientSid).Rule);
        Assert.Equal("recyclebin-foreign-user", Policy.CheckRecycleBin(ClientSid, "").Rule);
        Assert.True(Policy.CheckRecycleBin(ClientSid.ToLowerInvariant(), ClientSid).IsAllowed);
    }

    // ---------- 11: KeepNames, шаблоны, браузер ----------

    [Fact]
    public void Keep_names_survive_even_when_old()
    {
        var c = Cat(sub: @"Logs\CBS", age: 168, keep: ["CBS.log"]);
        var d = Policy.CheckEntry(RootGuid, Entry("CBS.log", ageHours: 9999, createdHours: 9999), Now, c);
        Assert.Equal("cleanup-keep-name", d.Rule);
        Assert.Equal("cleanup-keep-name", Policy.CheckEntry(RootGuid, Entry("cbs.LOG", ageHours: 9999, createdHours: 9999), Now, c).Rule);
        Assert.True(Policy.CheckEntry(RootGuid, Entry("CBS.20260101.log", ageHours: 9999, createdHours: 9999), Now, c).IsAllowed);
    }

    [Theory]
    [InlineData("thumbcache_256.db", true)]
    [InlineData("ICONCACHE_32.DB", true)]
    [InlineData("thumbcache_.db", true)]
    [InlineData("thumbcache.db", false)]
    [InlineData("desktop.ini", false)]
    [InlineData("thumbcache_256.db.bak", false)]
    [InlineData("custom.sub", false)]
    public void Patterns_limit_what_may_be_removed(string name, bool allowed)
    {
        var c = Cat(scope: "user", @base: "userlocal", sub: @"Microsoft\Windows\Explorer", patterns: ["thumbcache_*.db", "iconcache_*.db"]);
        var d = Policy.CheckEntry(RootGuid, Entry(name, ageHours: 500, createdHours: 500), Now, c);
        Assert.Equal(allowed, d.IsAllowed);
        if (!allowed) Assert.Equal("cleanup-pattern", d.Rule);
    }

    [Fact]
    public void Subdirectories_do_not_match_file_patterns()
    {
        var c = Cat(patterns: ["*.db"]);
        Assert.Equal("cleanup-pattern", Policy.CheckEntry(RootGuid, Entry("sub", dir: true, ageHours: 500, createdHours: 500), Now, c).Rule);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a.txt:stream")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void Entry_names_that_are_not_plain_are_refused(string name)
        => Assert.Equal("cleanup-not-under-root", Policy.CheckEntry(RootGuid, Entry(name), Now, Cat()).Rule);

    [Fact]
    public void Entries_of_a_category_without_files_are_refused()
        => Assert.True(Policy.CheckEntry(RootGuid, Entry(), Now, Cat(scope: "user", mech: "recyclebin")).IsDenied);

    [Fact]
    public void Running_browser_skips_the_whole_category()
    {
        var edge = CleanupCatalog.Shared.Find("browser-edge")!;
        var d = Policy.CheckNotRunning(edge, processRunning: true);
        Assert.True(d.IsDenied);
        Assert.Equal("cleanup-browser-running", d.Rule);
        Assert.True(Policy.CheckNotRunning(edge, processRunning: false).IsAllowed);
        Assert.True(Policy.CheckNotRunning(CleanupCatalog.Shared.Find("temp-user")!, processRunning: true).IsAllowed);
    }

    [Fact]
    public void Default_decision_is_deny()
    {
        // Неинициализированное решение никогда не разрешает действие.
        Assert.True(default(PolicyDecision).IsDenied);
    }
}
