using Chistovik.Core.Catalog;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

public class NewPowerPolicyTests
{
    static readonly PowerPolicy Policy = new();

    static readonly Guid Saver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    static readonly Guid High = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    static readonly Guid UltTemplate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");
    static readonly Guid Ours = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly Guid Foreign = Guid.Parse("99999999-8888-7777-6666-555555555555");

    static readonly Guid CpuSub = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid CpuMin = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");
    static readonly Guid CpuMax = Guid.Parse("bc5038f7-23e0-4960-96da-33abaf5935ec");
    static readonly Guid DisplaySub = Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99");
    static readonly Guid DisplayIdle = Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    [Fact]
    public void Embedded_catalog_has_the_four_schemes_this_policy_trusts()
    {
        var guids = PowerCatalog.Shared.Schemes.Select(s => s.Guid).ToHashSet();
        Assert.Equal(new[] { Saver, Balanced, High, UltTemplate }.ToHashSet(), guids);
    }

    // ---------- удаление ----------

    [Theory]
    [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a")]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("e9a42b02-d5df-448d-aa00-03f14749eb61")]
    public void Builtin_schemes_are_never_deleted_even_if_journal_claims_ownership(string guid)
        => Assert.True(Policy.CheckDelete(Guid.Parse(guid), Ours, createdByUs: true).IsDenied);

    [Fact]
    public void Foreign_scheme_is_not_deleted()
        => Assert.True(Policy.CheckDelete(Foreign, Ours, createdByUs: false).IsDenied);

    [Fact]
    public void Active_scheme_is_not_deleted_even_if_ours()
        => Assert.True(Policy.CheckDelete(Ours, Ours, createdByUs: true).IsDenied);

    [Fact]
    public void Empty_guid_is_not_deleted()
        => Assert.True(Policy.CheckDelete(Guid.Empty, Ours, createdByUs: true).IsDenied);

    [Fact]
    public void Own_inactive_scheme_can_be_deleted()
        => Assert.True(Policy.CheckDelete(Ours, High, createdByUs: true).IsAllowed);

    // ---------- активация ----------

    [Theory]
    [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a")]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("e9a42b02-d5df-448d-aa00-03f14749eb61")]
    public void Catalog_schemes_can_be_activated(string guid)
        => Assert.True(Policy.CheckSetActive(Guid.Parse(guid), createdByUs: false).IsAllowed);

    [Fact]
    public void Own_scheme_can_be_activated_and_foreign_or_empty_cannot()
    {
        Assert.True(Policy.CheckSetActive(Ours, createdByUs: true).IsAllowed);
        Assert.True(Policy.CheckSetActive(Foreign, createdByUs: false).IsDenied);
        Assert.True(Policy.CheckSetActive(Guid.Empty, createdByUs: true).IsDenied);
    }

    // ---------- создание копии ----------

    [Fact]
    public void Duplicate_only_from_catalog_template_with_free_guid_and_own_name()
    {
        var none = Array.Empty<Guid>();
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "Chistovik Ultimate", none).IsAllowed);
        // шаблон не из каталога или встроенная схема без CreateFromTemplate
        Assert.True(Policy.CheckDuplicate(Foreign, Ours, "Chistovik X", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(High, Ours, "Chistovik X", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(Guid.Empty, Ours, "Chistovik X", none).IsDenied);
        // новый GUID: пустой, из каталога, уже установлен
        Assert.True(Policy.CheckDuplicate(UltTemplate, Guid.Empty, "Chistovik X", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, High, "Chistovik X", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "Chistovik X", [Ours]).IsDenied);
        // имя
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, null, none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "Chistovik", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "chistovik X", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "My plan", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "Chistovik X\n", none).IsDenied);
        Assert.True(Policy.CheckDuplicate(UltTemplate, Ours, "Chistovik " + new string('x', 60), none).IsDenied);
    }

    // ---------- параметры ----------

    [Fact]
    public void Every_catalog_setting_accepts_its_bounds_and_rejects_one_past()
    {
        foreach (var s in PowerCatalog.Shared.Settings)
        {
            Assert.True(Policy.CheckWriteSetting(High, false, s.Subgroup, s.Setting, s.Min, s.Max).IsAllowed, s.Id);
            Assert.True(Policy.CheckWriteSetting(High, false, s.Subgroup, s.Setting, s.Max, s.Min).IsAllowed, s.Id);
            Assert.True(Policy.CheckWriteSetting(High, false, s.Subgroup, s.Setting, s.Max + 1, s.Min).IsDenied, s.Id + " ac");
            Assert.True(Policy.CheckWriteSetting(High, false, s.Subgroup, s.Setting, s.Min, s.Max + 1).IsDenied, s.Id + " dc");
            Assert.True(Policy.CheckWriteSetting(High, false, s.Subgroup, s.Setting, uint.MaxValue, uint.MaxValue).IsDenied, s.Id + " max");
        }
    }

    [Fact]
    public void Processor_min_ac_101_is_denied_and_100_allowed()
    {
        Assert.True(Policy.CheckWriteSetting(Balanced, false, CpuSub, CpuMin, 101, 50).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Balanced, false, CpuSub, CpuMin, 100, 50).IsAllowed);
    }

    [Fact]
    public void Unknown_or_mismatched_guids_are_denied()
    {
        Assert.True(Policy.CheckWriteSetting(Balanced, false, CpuSub, Foreign, 1, 1).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Balanced, false, Foreign, CpuMin, 1, 1).IsDenied);
        // известный параметр под чужой подгруппой и наоборот
        Assert.True(Policy.CheckWriteSetting(Balanced, false, DisplaySub, CpuMin, 1, 1).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Balanced, false, CpuSub, DisplayIdle, 1, 1).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Balanced, false, Guid.Empty, Guid.Empty, 0, 0).IsDenied);
    }

    [Fact]
    public void Setting_in_foreign_scheme_is_denied_but_own_scheme_is_fine()
    {
        Assert.True(Policy.CheckWriteSetting(Foreign, false, CpuSub, CpuMax, 100, 100).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Guid.Empty, true, CpuSub, CpuMax, 100, 100).IsDenied);
        Assert.True(Policy.CheckWriteSetting(Ours, true, CpuSub, CpuMax, 100, 100).IsAllowed);
    }

    [Fact]
    public void Display_timeout_bounds()
    {
        Assert.True(Policy.CheckWriteSetting(Balanced, false, DisplaySub, DisplayIdle, 86400, 0).IsAllowed);
        Assert.True(Policy.CheckWriteSetting(Balanced, false, DisplaySub, DisplayIdle, 86401, 0).IsDenied);
    }
}

public class NewJournalAclPolicyTests
{
    const string Admins = "S-1-5-32-544";
    const string SystemSid = "S-1-5-18";
    const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    static DirFacts Good(string owner = Admins, params DirAce[] extra)
    {
        var aces = new List<DirAce>
        {
            new(Admins, true, true), new(SystemSid, true, true), new("S-1-5-32-545", true, false), new("S-1-5-11", true, false),
        };
        aces.AddRange(extra);
        return new DirFacts(true, false, owner, aces);
    }

    [Theory]
    [InlineData(Admins)]
    [InlineData(SystemSid)]
    [InlineData(TrustedInstaller)]
    public void Protected_directory_with_trusted_owner_is_trusted(string owner)
    {
        var r = JournalAclPolicy.Evaluate(Good(owner));
        Assert.True(r.IsTrusted, r.Reason);
    }

    [Fact]
    public void Read_only_aces_for_broad_groups_are_fine()
        => Assert.True(JournalAclPolicy.Evaluate(Good(Admins, new DirAce("S-1-1-0", true, false))).IsTrusted);

    [Fact]
    public void Deny_aces_never_make_a_directory_untrusted()
        => Assert.True(JournalAclPolicy.Evaluate(Good(Admins, new DirAce("S-1-5-32-545", false, true))).IsTrusted);

    [Fact]
    public void Missing_directory_is_untrusted()
        => Assert.False(JournalAclPolicy.Evaluate(new DirFacts(false, false, Admins, [])).IsTrusted);

    [Fact]
    public void Reparse_directory_is_untrusted_even_with_perfect_acl()
    {
        var r = JournalAclPolicy.Evaluate(Good() with { IsReparse = true });
        Assert.False(r.IsTrusted);
        Assert.Equal("journal-dir-reparse", r.Rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-4")]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-32-544X")]
    public void Untrusted_owner_is_rejected(string? owner)
    {
        var r = JournalAclPolicy.Evaluate(Good(owner!));
        Assert.False(r.IsTrusted);
        Assert.Equal("journal-dir-owner", r.Rule);
    }

    [Theory]
    [InlineData("S-1-5-32-545")]   // Users
    [InlineData("S-1-5-11")]       // Authenticated Users
    [InlineData("S-1-1-0")]        // Everyone
    [InlineData("S-1-5-4")]        // INTERACTIVE
    [InlineData("s-1-5-32-545")]
    [InlineData(" S-1-1-0 ")]
    public void Write_for_broad_groups_is_untrusted(string sid)
    {
        var r = JournalAclPolicy.Evaluate(Good(Admins, new DirAce(sid, true, true)));
        Assert.False(r.IsTrusted);
        Assert.Equal("journal-acl-broad-write", r.Rule);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-32-551")]
    [InlineData("")]
    [InlineData(null)]
    public void Write_for_any_other_subject_is_untrusted(string? sid)
        => Assert.False(JournalAclPolicy.Evaluate(Good(Admins, new DirAce(sid!, true, true))).IsTrusted);

    [Fact]
    public void Missing_ace_list_is_untrusted()
        => Assert.False(JournalAclPolicy.Evaluate(new DirFacts(true, false, Admins, null!)).IsTrusted);

    [Fact]
    public void Default_result_is_untrusted()
        => Assert.False(default(JournalAclResult).IsTrusted);
}
