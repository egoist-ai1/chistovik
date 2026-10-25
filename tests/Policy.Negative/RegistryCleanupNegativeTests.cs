using Chistovik.Core.Execution;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>Негативные тесты политики чистки реестра (C3): DESIGN C2C3 §3, пункты 12-21 и 24 на уровне политики и разбора идентификаторов.</summary>
public class RegistryCleanupNegativeTests
{
    const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion";

    static RegKey K(string path)
    {
        Assert.True(RegistryPolicy.TryParse(path, out var k, out var err), err);
        return k;
    }

    static OrphanEvidence Ok() => OrphanEvidence.Ok("uninstall-orphan", [@"C:\Program Files\Foo\unins000.exe"]);

    static OrphanEvidence Bad(string rule = "evidence-uninstaller-exists") => OrphanEvidence.Failed(rule);

    // ---------- разбор идентификаторов ----------

    [Theory]
    [InlineData("arp:machine:x64:Foo", "None")]
    [InlineData("arp:machine:x86:{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", "None")]
    [InlineData("arp:user:x64:Foo", "None")]
    [InlineData("apppath:machine:x64:foo.exe", "None")]
    [InlineData(@"apppath:machine:x64:a\b.exe", "Malformed")]
    [InlineData("apppath:machine:x64:..", "Malformed")]
    [InlineData("arp:machine:x64:..", "Malformed")]
    [InlineData("arp:machine:x64:", "Malformed")]
    [InlineData("arp:machine:arm:Foo", "Malformed")]
    [InlineData("shareddll:x64:zz", "Malformed")]
    [InlineData("startupfolder:user:Foo.lnk", "Unknown")]
    [InlineData("service:Foo", "Unknown")]
    [InlineData("", "Malformed")]
    public void Ids_are_parsed_strictly(string text, string expected)
    {
        var ok = OrphanId.TryParse(text, out _, out var error);
        Assert.Equal(expected == "None", ok);
        Assert.Equal(expected, error.ToString());
    }

    [Fact]
    public void Valid_ids_round_trip_into_locations_the_agent_builds_itself()
    {
        Assert.True(OrphanId.TryParse("arp:machine:x86:Foo", out var arp, out _));
        var l = arp.Location(Sid);
        Assert.Equal(RegRoot.Machine, l.Root);
        Assert.Equal(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Foo", l.KeyPath);

        Assert.True(OrphanId.TryParse("arp:user:x64:Foo", out var user, out _));
        var u = user.Location(Sid);
        Assert.Equal(RegRoot.User, u.Root);
        Assert.Equal(Sid, u.UserSid);

        var hash = OrphanId.SharedDllHash(@"C:\Gone\a.dll");
        Assert.True(OrphanId.TryParse("shareddll:x64:" + hash, out var sd, out _));
        Assert.Equal(OrphanClass.SharedDll, sd.Class);
        Assert.True(OrphanId.TryParse("run:hklm64:Foo", out var run, out _));
        Assert.Equal(OrphanClass.Run, run.Class);
        Assert.True(OrphanId.TryParse("runonce:hklm64:Foo", out var once, out _));
        Assert.Equal(OrphanClass.RunOnce, once.Class);
    }

    // ---------- 12, 16: удаление ключа ----------

    [Theory]
    [InlineData(Cv + @"\Uninstall\Foo")]
    [InlineData(Cv + @"\App Paths\foo.exe")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Foo")]
    public void Orphan_program_entry_with_evidence_is_allowed(string key)
    {
        var d = RegistryPolicy.CheckOrphanKeyDelete(K(key), Ok(), 0, false, Sid);
        Assert.True(d.IsAllowed, d.Rule);
    }

    [Fact]
    public void User_hive_entry_of_the_client_is_allowed_and_foreign_or_hkcu_is_denied()
    {
        var own = RegistryPolicy.CheckOrphanKeyDelete(K($@"HKU\{Sid}\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"), Ok(), 0, false, Sid);
        Assert.True(own.IsAllowed, own.Rule);
        var foreign = RegistryPolicy.CheckOrphanKeyDelete(K($@"HKU\{OtherSid}\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"), Ok(), 0, false, Sid);
        Assert.True(foreign.IsDenied);
        Assert.Equal("reg-foreign-user", foreign.Rule);
        var hkcu = RegistryPolicy.CheckOrphanKeyDelete(K(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"), Ok(), 0, false, Sid);
        Assert.True(hkcu.IsDenied);
        Assert.Equal("reg-agent-no-hkcu", hkcu.Rule);
    }

    [Fact]
    public void Failed_evidence_denies_with_its_own_rule()
    {
        var d = RegistryPolicy.CheckOrphanKeyDelete(K(Cv + @"\Uninstall\Foo"), Bad("evidence-install-dir-exists"), 0, false, Sid);
        Assert.True(d.IsDenied);
        Assert.Equal("evidence-install-dir-exists", d.Rule);
    }

    [Fact]
    public void System_component_evidence_is_never_verified()
    {
        var e = OrphanEvidence.Forbidden("arp-system-component", "Foo");
        Assert.False(e.Verified);
        Assert.True(e.Protected);
        Assert.True(RegistryPolicy.CheckOrphanKeyDelete(K(Cv + @"\Uninstall\Foo"), e, 0, false, Sid).IsDenied);
    }

    [Theory]
    [InlineData(Cv + @"\Uninstall")]
    [InlineData(Cv + @"\App Paths")]
    [InlineData(Cv + @"\Run")]
    [InlineData(Cv + @"\Uninstall\Foo\Sub")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Foo")]
    [InlineData(@"HKLM\SOFTWARE\Chistovik\Quarantine")]
    public void Anything_but_a_direct_child_of_uninstall_or_app_paths_is_denied(string key)
    {
        var d = RegistryPolicy.CheckOrphanKeyDelete(K(key), Ok(), 0, false, Sid);
        Assert.True(d.IsDenied, key);
    }

    [Fact]
    public void The_uninstall_node_itself_is_a_structural_node()
    {
        var d = RegistryPolicy.CheckOrphanKeyDelete(K(Cv + @"\Uninstall"), Ok(), 0, false, Sid);
        Assert.True(d.IsDenied);
        Assert.Equal("reg-structure", d.Rule);
    }

    // ---------- 15: размер ----------

    [Theory]
    [InlineData(0, false, true, "reg-program-entry")]
    [InlineData(50, false, true, "reg-program-entry")]
    [InlineData(51, false, false, "reg-tree-too-large")]
    [InlineData(51, true, true, "reg-tree-expert")]
    [InlineData(500, true, true, "reg-tree-expert")]
    [InlineData(500, false, false, "reg-tree-too-large")]
    [InlineData(501, true, false, "reg-tree-too-large")]
    [InlineData(100000, true, false, "reg-tree-too-large")]
    [InlineData(-1, true, false, "reg-bad-count")]
    public void Subkey_limits(int count, bool expert, bool allowed, string rule)
    {
        var d = RegistryPolicy.CheckOrphanKeyDelete(K(Cv + @"\Uninstall\Foo"), Ok(), count, expert, Sid);
        Assert.Equal(allowed, d.IsAllowed);
        Assert.Equal(rule, d.Rule);
    }

    // ---------- 17: адрес копии ----------

    [Fact]
    public void Quarantine_destination_must_be_exactly_the_current_operation_slot()
    {
        var op = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var other = Guid.Parse("99999999-2222-3333-4444-555555555555");
        var good = $@"HKLM\SOFTWARE\Chistovik\Quarantine\{op:N}\3";
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good), op, 3).IsAllowed);

        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good), other, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good), op, 4).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good), op, 0).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good), Guid.Empty, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K($@"HKLM\SOFTWARE\Chistovik\Quarantine\{op:N}"), op, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K(good + @"\Sub"), op, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K($@"HKLM\SOFTWARE\Other\Quarantine\{op:N}\3"), op, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K($@"HKLM\SOFTWARE\Chistovik\Quarantine\{op}\3"), op, 3).IsDenied);
        Assert.True(RegistryPolicy.CheckQuarantineDestination(K($@"HKU\{Sid}\Software\Chistovik\Quarantine\{op:N}\3"), op, 3).IsDenied);
        var d = RegistryPolicy.CheckQuarantineDestination(K(good + @"\Sub"), op, 3);
        Assert.Equal("reg-quarantine-dest", d.Rule);
    }

    // ---------- 19: Run / RunOnce ----------

    [Theory]
    [InlineData(Cv + @"\Run")]
    [InlineData(Cv + @"\RunOnce")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")]
    public void Run_value_with_evidence_is_removable(string key)
    {
        var d = RegistryPolicy.CheckStartupRemove(K(key), "Foo", OrphanEvidence.Ok("command-unresolved", [@"C:\Gone\foo.exe"]), Sid);
        Assert.True(d.IsAllowed, d.Rule);
        Assert.Equal("reg-run-orphan", d.Rule);
    }

    [Fact]
    public void Run_value_of_the_client_in_hku_is_removable_and_foreign_or_hkcu_is_denied()
    {
        var ev = OrphanEvidence.Ok("command-unresolved", [@"C:\Gone\foo.exe"]);
        Assert.True(RegistryPolicy.CheckStartupRemove(K($@"HKU\{Sid}\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", ev, Sid).IsAllowed);
        var foreign = RegistryPolicy.CheckStartupRemove(K($@"HKU\{OtherSid}\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", ev, Sid);
        Assert.Equal("reg-foreign-user", foreign.Rule);
        var hkcu = RegistryPolicy.CheckStartupRemove(K(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", ev, Sid);
        Assert.Equal("reg-agent-no-hkcu", hkcu.Rule);
        var system = RegistryPolicy.CheckStartupRemove(K(@"HKU\.DEFAULT\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", ev, Sid);
        Assert.True(system.IsDenied);
    }

    [Theory]
    [InlineData(Cv + @"\RunOnceEx")]
    [InlineData(Cv + @"\RunServices")]
    [InlineData(Cv + @"\Policies\Explorer\Run")]
    [InlineData(Cv + @"\Uninstall")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    public void Only_run_and_runonce_are_run_parents(string key)
    {
        var d = RegistryPolicy.CheckStartupRemove(K(key), "Foo", OrphanEvidence.Ok("command-unresolved", [@"C:\Gone\foo.exe"]), Sid);
        Assert.True(d.IsDenied, key);
    }

    [Fact]
    public void Run_removal_requires_evidence_and_a_sane_name_and_spares_security_health()
    {
        var run = K(Cv + @"\Run");
        var ok = OrphanEvidence.Ok("command-unresolved", [@"C:\Gone\foo.exe"]);
        var failed = RegistryPolicy.CheckStartupRemove(run, "Foo", Bad("evidence-command-exists"), Sid);
        Assert.Equal("evidence-command-exists", failed.Rule);
        Assert.True(RegistryPolicy.CheckStartupRemove(run, "SecurityHealth", ok, Sid).IsDenied);
        Assert.True(RegistryPolicy.CheckStartupRemove(run, "", ok, Sid).IsDenied);
        Assert.True(RegistryPolicy.CheckStartupRemove(run, "Fo\no", ok, Sid).IsDenied);
    }

    [Fact]
    public void Generic_set_value_on_run_still_returns_reg_run_keys()
    {
        var d = RegistryPolicy.CheckSetValue(K(Cv + @"\Run"), "Foo", Sid);
        Assert.True(d.IsDenied);
        Assert.Equal("reg-run-keys", d.Rule);
        var once = RegistryPolicy.CheckSetValue(K(Cv + @"\RunOnce"), "Foo", Sid);
        Assert.Equal("reg-run-keys", once.Rule);
    }

    [Fact]
    public void Generic_writes_into_the_agent_own_node_stay_protected()
    {
        var d = RegistryPolicy.CheckSetValue(K(@"HKLM\SOFTWARE\Chistovik"), "Foo", Sid);
        Assert.True(d.IsDenied);
        Assert.Equal("reg-protected-node", d.Rule);
        Assert.True(RegistryPolicy.CheckDeleteKey(K(@"HKLM\SOFTWARE\Chistovik"), Sid).IsDenied);
    }

    // ---------- откат Run ----------

    [Theory]
    [InlineData(RegType.String, 10, true)]
    [InlineData(RegType.ExpandString, 2048, true)]
    [InlineData(RegType.String, 2049, false)]
    [InlineData(RegType.Binary, 10, false)]
    [InlineData(RegType.DWord, 1, false)]
    [InlineData(RegType.MultiString, 10, false)]
    [InlineData(RegType.String, -1, false)]
    public void Run_restore_accepts_only_short_strings(RegType type, int chars, bool allowed)
    {
        var d = RegistryPolicy.CheckStartupRestore(K(Cv + @"\Run"), "Foo", type, Sid, chars);
        Assert.Equal(allowed, d.IsAllowed);
        if (!allowed) Assert.Equal("reg-bad-value-type", d.Rule);
    }

    [Fact]
    public void Run_restore_stays_inside_run_parents_of_the_client()
    {
        Assert.True(RegistryPolicy.CheckStartupRestore(K(Cv + @"\Uninstall"), "Foo", RegType.String, Sid, 5).IsDenied);
        Assert.Equal("reg-foreign-user", RegistryPolicy.CheckStartupRestore(K($@"HKU\{OtherSid}\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", RegType.String, Sid, 5).Rule);
        Assert.Equal("reg-agent-no-hkcu", RegistryPolicy.CheckStartupRestore(K(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"), "Foo", RegType.String, Sid, 5).Rule);
    }

    // ---------- 20: SharedDLLs ----------

    [Fact]
    public void SharedDll_value_with_evidence_is_removable_but_only_in_hklm_shareddlls()
    {
        var ev = OrphanEvidence.Ok("file-missing", [@"C:\Gone\a.dll"]);
        var d = RegistryPolicy.CheckSharedDllRemove(K(Cv + @"\SharedDLLs"), @"C:\Gone\a.dll", ev);
        Assert.True(d.IsAllowed, d.Rule);
        Assert.True(RegistryPolicy.CheckSharedDllRemove(K(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"), @"C:\Gone\a.dll", ev).IsAllowed);
        Assert.Equal("reg-shareddll-hive", RegistryPolicy.CheckSharedDllRemove(K($@"HKU\{Sid}\Software\Microsoft\Windows\CurrentVersion\SharedDLLs"), "x", ev).Rule);
        Assert.True(RegistryPolicy.CheckSharedDllRemove(K(Cv + @"\Run"), @"C:\Gone\a.dll", ev).IsDenied);
        Assert.True(RegistryPolicy.CheckSharedDllRemove(K(Cv + @"\SharedDLLs\Sub"), @"C:\Gone\a.dll", ev).IsDenied);
        Assert.True(RegistryPolicy.CheckSharedDllRemove(K(Cv + @"\SharedDLLs"), "", ev).IsDenied);
    }

    [Fact]
    public void SharedDll_removal_requires_evidence()
    {
        var d = RegistryPolicy.CheckSharedDllRemove(K(Cv + @"\SharedDLLs"), @"C:\Here\a.dll", Bad("evidence-file-exists"));
        Assert.True(d.IsDenied);
        Assert.Equal("evidence-file-exists", d.Rule);
    }

    [Fact]
    public void Deleting_the_shareddlls_key_itself_is_structural()
    {
        var d = RegistryPolicy.CheckDeleteKey(K(Cv + @"\SharedDLLs"), Sid);
        Assert.True(d.IsDenied);
        Assert.Equal("reg-structure", d.Rule);
        var wow = RegistryPolicy.CheckDeleteKey(K(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs"), Sid);
        Assert.True(wow.IsDenied);
    }

    // ---------- 21: откат ключа ----------

    [Fact]
    public void Key_restore_is_limited_to_the_same_two_parents()
    {
        Assert.True(RegistryPolicy.CheckOrphanKeyRestore(K(Cv + @"\Uninstall\Foo"), Sid).IsAllowed);
        Assert.True(RegistryPolicy.CheckOrphanKeyRestore(K(Cv + @"\App Paths\foo.exe"), Sid).IsAllowed);
        Assert.True(RegistryPolicy.CheckOrphanKeyRestore(K(Cv + @"\Run"), Sid).IsDenied);
        Assert.True(RegistryPolicy.CheckOrphanKeyRestore(K(Cv + @"\Uninstall"), Sid).IsDenied);
        Assert.True(RegistryPolicy.CheckOrphanKeyRestore(K(Cv + @"\Uninstall\Foo\Sub"), Sid).IsDenied);
        Assert.Equal("reg-foreign-user", RegistryPolicy.CheckOrphanKeyRestore(K($@"HKU\{OtherSid}\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"), Sid).Rule);
        Assert.Equal("reg-agent-no-hkcu", RegistryPolicy.CheckOrphanKeyRestore(K(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"), Sid).Rule);
    }
}
