using System.Text;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Исполнение чистки реестра (C3): ключи-сироты с копией, Run/RunOnce, SharedDLLs, откат и защитные отказы.</summary>
public class RegistryCleanupTests
{
    const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion";
    const string Uninst = Cv + @"\Uninstall";
    const string AppPaths = Cv + @"\App Paths";
    const string Run = Cv + @"\Run";
    const string SharedDlls = Cv + @"\SharedDLLs";
    const string Exe = "\"C:\\Program Files\\Foo\\unins000.exe\" /S";

    static TestEnv Env()
    {
        var t = new TestEnv();
        t.Security.Trust(@"C:\");
        return t;
    }

    static string Arp(TestEnv t, string name, int subkeys = 0, string? uninstall = Exe)
    {
        var p = Uninst + "\\" + name;
        t.Registry.SetString(p, "DisplayName", "Foo App");
        if (uninstall is not null) t.Registry.SetString(p, "UninstallString", uninstall);
        t.Registry.SetDword(p, "EstimatedSize", 42);
        for (var i = 0; i < subkeys; i++) t.Registry.SetString(p + "\\S" + i, "v", "1");
        return p;
    }

    static string CopyPath(Guid op, int n = 1) => $@"HKLM\SOFTWARE\Chistovik\Quarantine\{op:N}\{n}";

    static StepRecord Only(FindingsResult r) => Assert.Single(r.Steps);

    static void AssertUntouched(TestEnv t)
    {
        Assert.Equal(0, t.RegistryTree.Created);
        Assert.Equal(0, t.RegistryTree.Deleted);
        Assert.Equal(0, t.Quarantine.WriteCount);
        Assert.Equal(0, t.RegistryTree.OpenHandles);
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    static StepOutcome Revert(TestEnv t, Guid op, StepRecord s, int ord = 100)
    {
        PlannedStep step = s.Kind switch
        {
            StepKind.RegistryKeyDelete => new RegistryKeyRestoreStep(RegTreeJson.ParseDeleteTarget(s.TargetJson), s.BeforeJson!, TestEnv.Sid),
            StepKind.StartupRemove => StartupRemoveStep.ForRestore(RegTreeJson.ParseValueTarget(s.TargetJson), s.BeforeJson!, TestEnv.Sid),
            _ => SharedDllRemoveStep.ForRestore(RegTreeJson.ParseValueTarget(s.TargetJson), s.BeforeJson!, TestEnv.Sid),
        };
        return new StepRunner(t.Env).Execute(op, ord, step, new RevertContext(s.Id, s.AfterJson, false, StepRunner.RevertOkCode));
    }

    // ---------- R1: Uninstall ----------

    [Fact]
    public void Orphan_uninstall_key_is_copied_hashed_exported_and_deleted()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Done, r.State);
        var s = Only(r);
        Assert.Equal(StepKind.RegistryKeyDelete, s.Kind);
        Assert.Equal(StepState.Done, s.State);
        Assert.StartsWith("regkey:hklm:", s.TargetKey);
        Assert.False(t.Registry.KeyExists(p));
        Assert.False(t.Registry.KeyExists(p + @"\S0"));

        var copy = CopyPath(r.OpId);
        Assert.Equal("Foo App", t.Registry.Get(copy, "DisplayName").Data);
        Assert.Equal(42u, t.Registry.Get(copy, "EstimatedSize").Data);
        Assert.True(t.Registry.KeyExists(copy + @"\S1"));

        var before = RegTreeJson.ParseKeyState(s.BeforeJson!);
        Assert.True(before.Exists);
        Assert.Equal(2, before.Subkeys);
        Assert.Equal(5, before.Values);
        Assert.True(RegTreeJson.IsSha256(before.TreeSha256));
        Assert.False(RegTreeJson.ParseKeyState(s.AfterJson).Exists);

        var target = RegTreeJson.ParseDeleteTarget(s.TargetJson);
        Assert.Equal(RegTreeJson.BackupFile(r.OpId, 1), target.Backup.File);
        Assert.Equal(RegTreeJson.QuarantineKeyPath(r.OpId, 1), target.Backup.Key);

        var bytes = t.Quarantine.Files[target.Backup.File];
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        var text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n", text);
        Assert.Contains(@"Uninstall\Foo]", text);
        Assert.Contains("\"DisplayName\"=\"Foo App\"", text);
        Assert.Contains("\"EstimatedSize\"=dword:0000002a", text);
        Assert.Contains(@"Uninstall\Foo\S1]", text);
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Missing_finding_is_skipped_as_gone()
    {
        var t = Env();
        var r = FindingsHarness.Run(t, "arp:machine:x64:Nothing");
        var s = Only(r);
        Assert.Equal(StepState.Skipped, s.State);
        Assert.NotEqual(OperationState.Denied, r.State);
        AssertUntouched(t);
    }

    [Fact]
    public void Dry_run_writes_nothing_to_registry_store_or_journal()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 3);
        var writes = t.Registry.WriteCount;

        var facts = FindingsHarness.DryRun(t, "arp:machine:x64:Foo");

        var f = Assert.Single(facts);
        Assert.StartsWith("regkey:hklm:", f.Key);
        Assert.Contains("\"display\":\"Foo App\"", f.Json);
        Assert.Contains("\"subkeys\":3", f.Json);
        Assert.Contains("\"rule\":null", f.Json);
        Assert.True(t.Registry.KeyExists(p));
        Assert.Equal(writes, t.Registry.WriteCount);
        AssertUntouched(t);
        Assert.Empty(t.Journal.AllOperations);
        Assert.Empty(t.Journal.AllSteps);
    }

    [Fact]
    public void Dry_run_reports_the_refusal_rule_without_writing()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Registry.SetDword(p, "SystemComponent", 1);

        var f = Assert.Single(FindingsHarness.DryRun(t, "arp:machine:x64:Foo"));

        Assert.Contains("\"rule\":\"arp-system-component\"", f.Json);
        AssertUntouched(t);
        Assert.Empty(t.Journal.AllOperations);
    }

    [Fact]
    public void Existing_install_location_means_the_program_is_still_there()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Registry.SetString(p, "InstallLocation", @"C:\Program Files\Foo");
        t.Security.Trust(@"C:\Program Files\Foo");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
    }

    [Fact]
    public void Existing_uninstaller_file_blocks_removal()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Security.Trust(@"C:\Program Files\Foo\unins000.exe");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
    }

    [Fact]
    public void Missing_drive_is_unknown_not_orphan()
    {
        var t = Env();
        var p = Arp(t, "Foo", uninstall: "\"D:\\Tools\\unins000.exe\" /S");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
    }

    [Fact]
    public void Msi_product_still_installed_blocks_removal()
    {
        var t = Env();
        const string code = "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}";
        var p = Arp(t, code, uninstall: "MsiExec.exe /X" + code);
        t.Registry.SetDword(p, "WindowsInstaller", 1);
        t.Uninstaller.MsiStates[code] = 5;

        var r = FindingsHarness.Run(t, "arp:machine:x64:" + code);

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
    }

    [Fact]
    public void Msi_product_unknown_to_windows_installer_is_an_orphan()
    {
        var t = Env();
        const string code = "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}";
        var p = Arp(t, code, uninstall: "MsiExec.exe /X" + code);
        t.Registry.SetDword(p, "WindowsInstaller", 1);

        var r = FindingsHarness.Run(t, "arp:machine:x64:" + code);

        Assert.Equal(OperationState.Done, r.State);
        Assert.False(t.Registry.KeyExists(p));
    }

    [Fact]
    public void System_component_and_windowsapps_location_are_denied_by_policy()
    {
        var t = Env();
        var a = Arp(t, "Sys");
        t.Registry.SetDword(a, "SystemComponent", 1);
        var b = Arp(t, "Store");
        t.Registry.SetString(b, "InstallLocation", @"C:\Program Files\WindowsApps\Foo_1.0");

        var r1 = FindingsHarness.Run(t, "arp:machine:x64:Sys");
        var r2 = FindingsHarness.Run(t, "arp:machine:x64:Store");

        Assert.Equal(StepCodes.PolicyDenied, Only(r1).Code);
        Assert.Equal(StepCodes.PolicyDenied, Only(r2).Code);
        Assert.True(t.Registry.KeyExists(a));
        Assert.True(t.Registry.KeyExists(b));
        AssertUntouched(t);
    }

    // ---------- ссылки и пути ----------

    [Fact]
    public void Symlink_key_is_refused_and_left_alone()
    {
        var t = Env();
        Arp(t, "Real");
        t.Registry.AddSymlink(Uninst + @"\Foo", Uninst + @"\Real", openAsLink: true);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        Assert.Equal("reg-symlink", Only(r).PolicyRule);
        Assert.True(t.Registry.KeyExists(Uninst + @"\Real"));
        AssertUntouched(t);
    }

    [Fact]
    public void Link_that_resolves_elsewhere_is_a_resolved_path_mismatch()
    {
        var t = Env();
        // Узел Uninstall подменён ссылкой на другое место: ключ Foo открывается там, но разрешённый путь не тот.
        const string elsewhere = @"HKLM\SOFTWARE\Elsewhere";
        t.Registry.AddSymlink(Uninst, elsewhere, openAsLink: false);
        t.Registry.SetString(elsewhere + @"\Foo", "DisplayName", "Foo App");
        t.Registry.SetString(elsewhere + @"\Foo", "UninstallString", Exe);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        Assert.Equal("reg-resolved-mismatch", Only(r).PolicyRule);
        Assert.True(t.Registry.KeyExists(elsewhere + @"\Foo"));
        AssertUntouched(t);
    }

    [Fact]
    public void Link_inside_the_tree_stops_everything_before_any_copy()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        t.Registry.AddSymlink(p + @"\Link", @"HKLM\SOFTWARE\Microsoft\Windows NT", openAsLink: true);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        var s = Only(r);
        Assert.Equal(StepCodes.RegTreeHasLink, s.Code);
        Assert.Equal("reg-tree-has-link", s.PolicyRule);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
        Assert.False(t.Registry.KeyExists(CopyPath(r.OpId)));
    }

    [Fact]
    public void Malformed_and_unknown_ids_never_reach_the_registry()
    {
        var t = Env();
        Arp(t, "Foo");

        var bad = FindingsHarness.Run(t, @"apppath:machine:x64:a\b.exe");
        var unknown = FindingsHarness.Run(t, "startupfolder:user:Foo.lnk");

        Assert.Equal(OperationState.Denied, bad.State);
        Assert.Equal(ResponseCodes.BadRequest, bad.Code);
        Assert.Equal(OperationState.Denied, unknown.State);
        Assert.Equal(StepCodes.PolicyDenied, unknown.Code);
        AssertUntouched(t);
    }

    [Fact]
    public void Expert_confirmation_outside_items_is_a_bad_request()
    {
        var t = Env();
        var req = FindingsHarness.Request(t, ["arp:machine:x64:Foo"], ["arp:machine:x64:Other"]);
        Assert.NotNull(req.Validate());
        var ok = FindingsHarness.Request(t, ["arp:machine:x64:Foo"], ["arp:machine:x64:Foo"]);
        Assert.Null(ok.Validate());
    }

    // ---------- размер ----------

    [Theory]
    [InlineData(50, false, true)]
    [InlineData(51, false, false)]
    [InlineData(51, true, true)]
    [InlineData(500, true, true)]
    [InlineData(501, true, false)]
    public void Large_keys_need_expert_confirmation_and_never_exceed_500(int subkeys, bool expert, bool removed)
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys);
        var id = "arp:machine:x64:Foo";

        var r = FindingsHarness.Run(t, [id], expert ? [id] : null);

        if (removed)
        {
            Assert.Equal(OperationState.Done, r.State);
            Assert.False(t.Registry.KeyExists(p));
        }
        else
        {
            Assert.Equal(OperationState.Denied, r.State);
            Assert.Equal(StepCodes.RegTreeTooLarge, Only(r).Code);
            Assert.True(t.Registry.KeyExists(p));
            AssertUntouched(t);
        }
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    // ---------- сбои копии ----------

    [Fact]
    public void Incomplete_copy_is_dropped_and_the_original_survives()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        t.RegistryTree.DropSetRawUnder = @"HKLM\SOFTWARE\Chistovik";

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        var s = Only(r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.BackupMismatch, s.Code);
        Assert.True(t.Registry.KeyExists(p));
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.Equal("Foo App", t.Registry.Get(p, "DisplayName").Data);
        Assert.False(t.Registry.KeyExists(CopyPath(r.OpId)));
        Assert.False(t.Registry.KeyExists(CopyPath(r.OpId) + @"\S0"));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Source_changed_after_the_check_is_external_change_and_nothing_is_copied()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        var plan = FindingsPlanner.Plan(t.Env, FindingsHarness.Request(t, ["arp:machine:x64:Foo"]), TestEnv.Sid, Guid.NewGuid());
        var open = plan.Steps[0].Open(t.Env, null, true);
        Assert.Null(open.Refusal);
        using var session = open.Session!;

        t.Registry.SetString(p, "Late", "value");

        var e = Assert.Throws<StepFailureException>(session.Write);
        Assert.Equal(StepCodes.ExternalChange, e.Code);
        Assert.Equal(0, t.RegistryTree.Created);
        Assert.Equal(0, t.RegistryTree.Deleted);
        Assert.True(t.Registry.KeyExists(p));
    }

    [Fact]
    public void Quarantine_store_failure_leaves_the_original_in_place()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Quarantine.FailWrites = true;

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(StepState.Failed, Only(r).State);
        Assert.True(t.Registry.KeyExists(p));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Interrupted_delete_leaves_the_copy_for_recovery()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        t.RegistryTree.FailDeleteAt(p);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(StepState.Failed, Only(r).State);
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId)));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    // ---------- откат ----------

    [Fact]
    public void Revert_restores_the_key_from_the_copy()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        Assert.False(t.Registry.KeyExists(p));

        var o = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Done, o.End);
        Assert.Null(o.Code);
        Assert.Equal("Foo App", t.Registry.Get(p, "DisplayName").Data);
        Assert.Equal(42u, t.Registry.Get(p, "EstimatedSize").Data);
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.True(t.Registry.KeyExists(p + @"\S1"));
        using var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", string.Empty), false)!;
        Assert.Equal(RegTreeJson.ParseKeyState(s.BeforeJson!).TreeSha256, RegistryTreeHasher.Compute(h).Sha256);
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId)));
    }

    [Fact]
    public void Revert_is_idempotent_and_never_overwrites_an_existing_original()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        // Программу поставили заново: ключ с другим содержимым уже на месте.
        t.Registry.SetString(p, "DisplayName", "Reinstalled");
        var created = t.RegistryTree.Created;
        var sets = t.RegistryTree.SetRawCount;

        var again = Revert(t, r.OpId, s);

        // Ключ на месте, но с другим содержимым (хэш дерева не тот): это чужое изменение, а не «уже возвращено».
        Assert.NotEqual(StepEnd.Done, again.End);
        Assert.Equal(StepCodes.ExternalChange, again.Code);
        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(sets, t.RegistryTree.SetRawCount);
        Assert.Equal("Reinstalled", t.Registry.Get(p, "DisplayName").Data);
    }

    [Fact]
    public void Revert_without_the_copy_is_backup_missing()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        t.Registry.RemoveKey(CopyPath(r.OpId));

        var o = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Failed, o.End);
        Assert.Equal(StepCodes.BackupMissing, o.Code);
        Assert.False(t.Registry.KeyExists(p));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Revert_with_a_modified_copy_is_backup_mismatch_and_creates_nothing()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        t.Registry.SetString(CopyPath(r.OpId), "Planted", "evil");

        var o = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Failed, o.End);
        Assert.Equal(StepCodes.BackupMismatch, o.Code);
        Assert.False(t.Registry.KeyExists(p));
    }

    [Fact]
    public void Revert_cannot_be_pointed_at_a_foreign_backup()
    {
        var t = Env();
        Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        var target = RegTreeJson.ParseDeleteTarget(s.TargetJson);
        var forged = target with { Backup = new RegKeyBackup(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Other", target.Backup.File) };

        var step = new RegistryKeyRestoreStep(forged, s.BeforeJson!, TestEnv.Sid);
        var o = new StepRunner(t.Env).Execute(r.OpId, 100, step, new RevertContext(s.Id, s.AfterJson, false, StepRunner.RevertOkCode));

        Assert.NotEqual(StepEnd.Done, o.End);
        Assert.False(t.Registry.KeyExists(Uninst + @"\Foo"));
    }

    // ---------- R2: App Paths ----------

    [Fact]
    public void App_path_with_missing_target_is_removed_and_restorable()
    {
        var t = Env();
        var p = AppPaths + @"\gone.exe";
        t.Registry.SetString(p, "", @"C:\Gone\gone.exe");

        var r = FindingsHarness.Run(t, "apppath:machine:x64:gone.exe");

        Assert.Equal(OperationState.Done, r.State);
        Assert.False(t.Registry.KeyExists(p));
        Assert.Equal(StepEnd.Done, Revert(t, r.OpId, Only(r)).End);
        Assert.Equal(@"C:\Gone\gone.exe", t.Registry.Get(p, "").Data);
    }

    [Fact]
    public void App_path_whose_file_exists_is_kept()
    {
        var t = Env();
        var p = AppPaths + @"\here.exe";
        t.Registry.SetString(p, "", @"C:\Here\here.exe");
        t.Security.Trust(@"C:\Here\here.exe");

        var r = FindingsHarness.Run(t, "apppath:machine:x64:here.exe");

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
    }

    // ---------- R3: Run / RunOnce ----------

    [Fact]
    public void Run_value_whose_command_is_gone_is_removed_and_returned_by_revert()
    {
        var t = Env();
        t.Registry.SetString(Run, "Foo", "\"C:\\Gone\\foo.exe\" --tray");
        t.Registry.SetString(Run, "Keep", "\"C:\\Gone\\keep.exe\"");

        var r = FindingsHarness.Run(t, "run:hklm64:Foo");

        Assert.Equal(OperationState.Done, r.State);
        var s = Only(r);
        Assert.Equal(StepKind.StartupRemove, s.Kind);
        Assert.False(t.Registry.Get(Run, "Foo").Exists);
        Assert.True(t.Registry.Get(Run, "Keep").Exists);

        var o = Revert(t, r.OpId, s);
        Assert.Equal(StepEnd.Done, o.End);
        Assert.Equal("\"C:\\Gone\\foo.exe\" --tray", t.Registry.Get(Run, "Foo").Data);
    }

    [Fact]
    public void Run_once_value_is_handled_the_same_way()
    {
        var t = Env();
        t.Registry.SetString(Cv + @"\RunOnce", "Foo", "\"C:\\Gone\\foo.exe\"");

        var r = FindingsHarness.Run(t, "runonce:hklm64:Foo");

        Assert.Equal(OperationState.Done, r.State);
        Assert.False(t.Registry.Get(Cv + @"\RunOnce", "Foo").Exists);
    }

    [Fact]
    public void Run_value_of_the_client_in_hku_is_removed()
    {
        var t = Env();
        var key = FakeRegistry.HkuPath(TestEnv.Sid, @"Software\Microsoft\Windows\CurrentVersion\Run");
        t.Registry.SetString(key, "Foo", "\"C:\\Gone\\foo.exe\"");

        var r = FindingsHarness.Run(t, "run:hku:Foo");

        Assert.Equal(OperationState.Done, r.State);
        Assert.False(t.Registry.Get(key, "Foo").Exists);
    }

    [Theory]
    [InlineData("cmd.exe /c start x")]
    [InlineData("rundll32.exe foo.dll,Start")]
    [InlineData("\"D:\\Tools\\foo.exe\"")]
    public void Run_value_without_clear_evidence_is_kept(string command)
    {
        var t = Env();
        t.Registry.SetString(Run, "Foo", command);

        var r = FindingsHarness.Run(t, "run:hklm64:Foo");

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.Get(Run, "Foo").Exists);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    [Fact]
    public void Run_value_whose_command_resolves_to_an_existing_file_is_kept()
    {
        var t = Env();
        t.Registry.SetString(Run, "Foo", "\"C:\\Here\\foo.exe\"");
        t.Security.Trust(@"C:\Here\foo.exe");

        var r = FindingsHarness.Run(t, "run:hklm64:Foo");

        Assert.Equal(StepCodes.EvidenceFailed, Only(r).Code);
        Assert.True(t.Registry.Get(Run, "Foo").Exists);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    [Fact]
    public void Security_health_entry_is_never_removed()
    {
        var t = Env();
        t.Registry.SetString(Run, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");

        var r = FindingsHarness.Run(t, "run:hklm64:SecurityHealth");

        Assert.Equal(OperationState.Denied, r.State);
        Assert.True(t.Registry.Get(Run, "SecurityHealth").Exists);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    // ---------- R4: SharedDLLs ----------

    [Fact]
    public void SharedDll_counter_for_a_missing_file_is_removed()
    {
        var t = Env();
        const string dll = @"C:\Gone\a.dll";
        t.Registry.SetDword(SharedDlls, dll, 1);
        t.Registry.SetDword(SharedDlls, @"C:\Gone\b.dll", 2);

        var r = FindingsHarness.Run(t, "shareddll:x64:" + OrphanId.SharedDllHash(dll));

        Assert.Equal(OperationState.Done, r.State);
        Assert.Equal(StepKind.SharedDllRemove, Only(r).Kind);
        Assert.False(t.Registry.Get(SharedDlls, dll).Exists);
        Assert.True(t.Registry.Get(SharedDlls, @"C:\Gone\b.dll").Exists);
    }

    [Fact]
    public void SharedDll_counter_is_returned_by_revert()
    {
        var t = Env();
        const string dll = @"C:\Gone\a.dll";
        t.Registry.SetDword(SharedDlls, dll, 3);
        var r = FindingsHarness.Run(t, "shareddll:x64:" + OrphanId.SharedDllHash(dll));

        var o = Revert(t, r.OpId, Only(r));

        Assert.Equal(StepEnd.Done, o.End);
        Assert.Equal(3u, t.Registry.Get(SharedDlls, dll).Data);
    }

    [Fact]
    public void SharedDll_counter_for_an_existing_file_or_missing_drive_is_kept()
    {
        var t = Env();
        t.Registry.SetDword(SharedDlls, @"C:\Here\a.dll", 1);
        t.Registry.SetDword(SharedDlls, @"D:\Tools\b.dll", 1);
        t.Security.Trust(@"C:\Here\a.dll");

        var here = FindingsHarness.Run(t, "shareddll:x64:" + OrphanId.SharedDllHash(@"C:\Here\a.dll"));
        var drive = FindingsHarness.Run(t, "shareddll:x64:" + OrphanId.SharedDllHash(@"D:\Tools\b.dll"));

        Assert.Equal(StepCodes.EvidenceFailed, Only(here).Code);
        Assert.Equal(StepCodes.EvidenceFailed, Only(drive).Code);
        Assert.True(t.Registry.Get(SharedDlls, @"C:\Here\a.dll").Exists);
        Assert.True(t.Registry.Get(SharedDlls, @"D:\Tools\b.dll").Exists);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    [Fact]
    public void SharedDll_unknown_hash_is_gone()
    {
        var t = Env();
        t.Registry.SetDword(SharedDlls, @"C:\Gone\a.dll", 1);

        var r = FindingsHarness.Run(t, "shareddll:x64:" + OrphanId.SharedDllHash(@"C:\Gone\other.dll"));

        Assert.Equal(StepState.Skipped, Only(r).State);
        Assert.True(t.Registry.Get(SharedDlls, @"C:\Gone\a.dll").Exists);
    }

    // ---------- несколько находок ----------

    [Fact]
    public void One_refusal_denies_the_whole_batch_before_anything_is_written()
    {
        var t = Env();
        var good = Arp(t, "Foo");
        var bad = Arp(t, "Sys");
        t.Registry.SetDword(bad, "SystemComponent", 1);

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo", "arp:machine:x64:Sys");

        Assert.Equal(OperationState.Denied, r.State);
        Assert.True(t.Registry.KeyExists(good));
        Assert.True(t.Registry.KeyExists(bad));
        AssertUntouched(t);
    }

    [Fact]
    public void Each_finding_gets_its_own_copy_slot()
    {
        var t = Env();
        Arp(t, "Foo");
        Arp(t, "Bar");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo", "arp:machine:x64:Bar");

        Assert.Equal(OperationState.Done, r.State);
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId, 1)));
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId, 2)));
        Assert.Equal(2, t.Quarantine.Files.Count);
    }

    // ---------- формат .reg ----------

    static (string Path, string Name, RegType Type, byte[] Raw)[] ParseReg(byte[] file)
    {
        Assert.Equal(0xFF, file[0]);
        Assert.Equal(0xFE, file[1]);
        var lines = Encoding.Unicode.GetString(file, 2, file.Length - 2).Split("\r\n");
        Assert.Equal("Windows Registry Editor Version 5.00", lines[0]);
        var result = new List<(string, string, RegType, byte[])>();
        string path = string.Empty;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                path = line[1..^1];
                continue;
            }
            while (line.EndsWith('\\'))
                line = line[..^1] + lines[++i].TrimStart();

            string name;
            int pos;
            if (line.StartsWith("@=", StringComparison.Ordinal))
            {
                name = string.Empty;
                pos = 2;
            }
            else
            {
                (name, pos) = Unquote(line, 0);
                Assert.Equal('=', line[pos]);
                pos++;
            }
            var rest = line[pos..];
            if (rest[0] == '"')
            {
                var (text, _) = Unquote(rest, 0);
                result.Add((path, name, RegType.String, Encoding.Unicode.GetBytes(text + "\0")));
            }
            else if (rest.StartsWith("dword:", StringComparison.Ordinal))
            {
                result.Add((path, name, RegType.DWord, BitConverter.GetBytes(Convert.ToUInt32(rest[6..], 16))));
            }
            else
            {
                var colon = rest.IndexOf(':');
                var head = rest[..colon];
                var data = rest[(colon + 1)..];
                var type = head switch
                {
                    "hex" => RegType.Binary,
                    "hex(0)" => RegType.None,
                    "hex(1)" => RegType.String,
                    "hex(2)" => RegType.ExpandString,
                    "hex(3)" => RegType.Binary,
                    "hex(4)" => RegType.DWord,
                    "hex(7)" => RegType.MultiString,
                    "hex(b)" => RegType.QWord,
                    _ => throw new InvalidDataException(head),
                };
                var raw = data.Length == 0 ? [] : data.Split(',').Select(x => Convert.ToByte(x, 16)).ToArray();
                result.Add((path, name, type, raw));
            }
        }
        return result.ToArray();
    }

    static (string Text, int End) Unquote(string s, int start)
    {
        Assert.Equal('"', s[start]);
        var sb = new StringBuilder();
        var i = start + 1;
        for (; i < s.Length; i++)
        {
            if (s[i] == '\\') sb.Append(s[++i]);
            else if (s[i] == '"') break;
            else sb.Append(s[i]);
        }
        return (sb.ToString(), i + 1);
    }

    [Fact]
    public void Reg_export_round_trips_every_value_type_through_a_reader()
    {
        var t = Env();
        const string root = @"HKLM\SOFTWARE\Test\Root";
        t.Registry.EnsureKey(root);
        t.Registry.EnsureKey(root + @"\Sub");
        var longBytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var utf = (string s) => Encoding.Unicode.GetBytes(s);
        var values = new List<(string Key, string Name, RegType Type, byte[] Raw)>
        {
            (root, "", RegType.String, utf("default text\0")),
            (root, "plain", RegType.String, utf("hello\0")),
            (root, "quote\"and\\slash", RegType.String, utf("a \"b\" c\\d\\\\e\0")),
            (root, "no-nul", RegType.String, utf("abc")),
            (root, "empty-raw", RegType.String, []),
            (root, "control", RegType.String, utf("line1\r\nline2\0")),
            (root, "double-nul", RegType.String, utf("x\0\0")),
            (root, "expand", RegType.ExpandString, utf("%windir%\\x\0")),
            (root, "bin0", RegType.Binary, []),
            (root, "bin300", RegType.Binary, longBytes),
            (root, "dword", RegType.DWord, BitConverter.GetBytes(0xDEADBEEF)),
            (root, "dword-zero", RegType.DWord, BitConverter.GetBytes(0u)),
            (root, "dword3", RegType.DWord, [1, 2, 3]),
            (root, "qword", RegType.QWord, BitConverter.GetBytes(0x0123456789ABCDEFul)),
            (root, "multi", RegType.MultiString, utf("one\0two\0\0")),
            (root, "multi-empty", RegType.MultiString, utf("\0")),
            (root, "none0", RegType.None, []),
            (root, "none5", RegType.None, [9, 8, 7, 6, 5]),
            (root + @"\Sub", "child", RegType.String, utf("c\0")),
            (root + @"\Sub", "child-bin", RegType.Binary, [0, 255]),
        };
        using (var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Test\Root", string.Empty), true)!)
        {
            using var sub = h.OpenSub("Sub", true)!;
            foreach (var v in values)
                (v.Key == root ? h : sub).SetRaw(v.Name, v.Type, v.Raw);
        }

        byte[] file;
        using (var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Test\Root", string.Empty), false)!)
            file = RegExport.ToRegFile(@"HKEY_LOCAL_MACHINE\SOFTWARE\Test\Root", h);

        var parsed = ParseReg(file);
        Assert.Equal(values.Count, parsed.Length);
        foreach (var v in values)
        {
            var fullPath = v.Key == root ? @"HKEY_LOCAL_MACHINE\SOFTWARE\Test\Root" : @"HKEY_LOCAL_MACHINE\SOFTWARE\Test\Root\Sub";
            var got = Assert.Single(parsed, p => p.Path == fullPath && p.Name == v.Name);
            Assert.Equal(v.Type, got.Type);
            Assert.Equal(v.Raw, got.Raw);
        }
        var text = Encoding.Unicode.GetString(file, 2, file.Length - 2);
        Assert.All(text.Split("\r\n"), l => Assert.True(l.Length <= 90, l));
    }

    // ---------- StepFactory.Rebuild для новых видов ----------

    static PlannedStep Rebuild(TestEnv t, StepRecord s, bool isRevert, string? sid = null, string? key = null, string? target = null)
        => StepFactory.Rebuild(t.Env, sid ?? TestEnv.Sid, s.Kind, key ?? s.TargetKey, target ?? s.TargetJson, isRevert ? s.BeforeJson : s.AfterJson, isRevert);

    [Fact]
    public void Rebuild_key_delete_forward_is_recovery_only_and_revert_is_a_restore()
    {
        var t = Env();
        Arp(t, "Foo");
        var s = Only(FindingsHarness.Run(t, "arp:machine:x64:Foo"));

        var fwd = Assert.IsType<RegistryKeyDeleteStep>(Rebuild(t, s, false));
        var back = Assert.IsType<RegistryKeyRestoreStep>(Rebuild(t, s, true));

        Assert.True(fwd.RecoveryOnly);
        Assert.Null(fwd.PresetRefusal);
        Assert.Null(back.PresetRefusal);
        Assert.True(back.IsRevert);
    }

    [Fact]
    public void Rebuild_value_removals_forward_and_revert()
    {
        var t = Env();
        t.Registry.SetString(Run, "Foo", "\"C:\\Gone\\foo.exe\"");
        const string dll = @"C:\Gone\a.dll";
        t.Registry.SetDword(SharedDlls, dll, 1);
        var r = FindingsHarness.Run(t, "run:hklm64:Foo", "shareddll:x64:" + OrphanId.SharedDllHash(dll));
        var run = r.Steps.Single(x => x.Kind == StepKind.StartupRemove);
        var shared = r.Steps.Single(x => x.Kind == StepKind.SharedDllRemove);

        Assert.True(Assert.IsType<StartupRemoveStep>(Rebuild(t, run, false)).RecoveryOnly);
        Assert.True(Assert.IsType<SharedDllRemoveStep>(Rebuild(t, shared, false)).RecoveryOnly);
        Assert.Null(Rebuild(t, run, true).PresetRefusal);
        Assert.Null(Rebuild(t, shared, true).PresetRefusal);
    }

    [Fact]
    public void Rebuild_refuses_a_foreign_sid_in_hku()
    {
        var t = Env();
        var key = FakeRegistry.HkuPath(TestEnv.Sid, @"Software\Microsoft\Windows\CurrentVersion\Run");
        t.Registry.SetString(key, "Foo", "\"C:\\Gone\\foo.exe\"");
        var s = Only(FindingsHarness.Run(t, "run:hku:Foo"));

        var step = Rebuild(t, s, false, sid: TestEnv.OtherSid);

        Assert.Equal("journal-foreign-sid", step.PresetRefusal!.Rule);
    }

    [Fact]
    public void Rebuild_refuses_a_target_key_that_does_not_match_the_target_json()
    {
        var t = Env();
        Arp(t, "Foo");
        t.Registry.SetString(Run, "Foo", "\"C:\\Gone\\foo.exe\"");
        var key = Only(FindingsHarness.Run(t, "arp:machine:x64:Foo"));
        var run = Only(FindingsHarness.Run(t, "run:hklm64:Foo"));

        Assert.Equal("journal-target-key-mismatch", Rebuild(t, key, true, key: "regkey:hklm:other").PresetRefusal!.Rule);
        Assert.Equal("journal-target-key-mismatch", Rebuild(t, run, false, key: "run:hklm64:Other").PresetRefusal!.Rule);
    }

    [Fact]
    public void Rebuild_refuses_a_forged_key_outside_the_allowed_branches()
    {
        var t = Env();
        Arp(t, "Foo");
        var s = Only(FindingsHarness.Run(t, "arp:machine:x64:Foo"));
        var target = s.TargetJson.Replace("Uninstall", "Services", StringComparison.Ordinal);
        var key = s.TargetKey.Replace("uninstall", "services", StringComparison.OrdinalIgnoreCase);

        var back = Rebuild(t, s, true, key: key, target: target);
        var fwd = Rebuild(t, s, false, key: key, target: target);

        Assert.NotNull(back.PresetRefusal);
        Assert.NotNull(fwd.PresetRefusal);
        Assert.IsNotType<RegistryKeyRestoreStep>(back);
        Assert.IsNotType<RegistryKeyDeleteStep>(fwd);
    }

    [Fact]
    public void Rebuild_cleanup_forward_is_recovery_only_and_revert_is_not_revertible()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(@"C:\Users\Test\AppData\Local\Temp\old.tmp", ageHours: 100, length: 1000);
        t.RecycleBin.Bytes = 500;
        var op = t.Clean("temp-user", "recycle-bin").OperationId!.Value;
        var steps = t.Journal.Get(op)!.Steps;
        var disk = steps.Single(x => x.Kind == StepKind.CleanupCategory);
        var bin = steps.Single(x => x.Kind == StepKind.RecycleBinEmpty);

        Assert.True(Assert.IsType<CleanupStep>(Rebuild(t, disk, false)).RecoveryOnly);
        Assert.True(Assert.IsType<RecycleBinStep>(Rebuild(t, bin, false)).RecoveryOnly);
        foreach (var s in new[] { disk, bin })
        {
            var refused = Rebuild(t, s, true);
            Assert.Equal(StepCodes.NotRevertible, refused.PresetRefusal!.Code);
        }
    }

    [Fact]
    public void Rebuild_cleanup_refuses_foreign_sid_and_unknown_category()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(@"C:\Users\Test\AppData\Local\Temp\old.tmp", ageHours: 100, length: 1000);
        t.RecycleBin.Bytes = 500;
        var op = t.Clean("temp-user", "recycle-bin").OperationId!.Value;
        var steps = t.Journal.Get(op)!.Steps;
        var disk = steps.Single(x => x.Kind == StepKind.CleanupCategory);
        var bin = steps.Single(x => x.Kind == StepKind.RecycleBinEmpty);

        Assert.Equal("journal-foreign-sid", Rebuild(t, disk, false, sid: TestEnv.OtherSid).PresetRefusal!.Rule);
        Assert.Equal("journal-foreign-sid", Rebuild(t, bin, false, sid: TestEnv.OtherSid).PresetRefusal!.Rule);
        var unknown = disk.TargetJson.Replace("temp-user", "no-such-category", StringComparison.Ordinal);
        Assert.NotNull(Rebuild(t, disk, false, target: unknown).PresetRefusal);
    }

    // ---------- имена и типы, которые нельзя безопасно записать в .reg ----------

    [Theory]
    [InlineData("bad]name")]
    [InlineData("bad\u0001name")]
    [InlineData("bad\nname")]
    [InlineData("bad\u007Fname")]
    public void Tree_with_an_unsafe_subkey_name_is_denied_before_any_copy_or_write(string badName)
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Registry.SetString(p + "\\" + badName, "v", "1");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        var s = Only(r);
        Assert.Equal(StepCodes.RegTreeBadName, s.Code);
        Assert.Equal("reg-tree-bad-name", s.PolicyRule);
        Assert.True(t.Registry.KeyExists(p));
        Assert.True(t.Registry.KeyExists(p + "\\" + badName));
        AssertUntouched(t);
        Assert.False(t.Registry.KeyExists(CopyPath(r.OpId)));
    }

    [Fact]
    public void Reg_export_itself_refuses_an_unsafe_key_name()
    {
        var t = Env();
        const string root = @"HKLM\SOFTWARE\Test\Root";
        t.Registry.SetString(root + @"\a]" + "\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil", "v", "1");
        t.Registry.EnsureKey(root + @"\ok]x");
        using var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Test\Root", string.Empty), false)!;

        var e = Assert.Throws<StepFailureException>(() => RegExport.ToRegFile(@"HKEY_LOCAL_MACHINE\SOFTWARE\Test\Root", h));
        Assert.Equal(StepCodes.RegTreeBadName, e.Code);
    }

    [Fact]
    public void Tree_with_a_value_type_outside_the_known_list_is_refused_not_downgraded_to_binary()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        using (var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", string.Empty), true)!)
            h.SetRaw("odd", RegType.Other, [1, 2, 3]);
        var writes = t.RegistryTree.SetRawCount;

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        var s = Only(r);
        Assert.Equal(StepCodes.RegTreeBadType, s.Code);
        Assert.Equal("reg-tree-bad-type", s.PolicyRule);
        Assert.True(t.Registry.KeyExists(p));
        Assert.Equal(0, t.RegistryTree.Deleted);
        Assert.Equal(0, t.Quarantine.WriteCount);
        Assert.Equal(writes, t.RegistryTree.SetRawCount);
    }

    // ---------- удаление дерева: право на подключи, обрыв посередине, сравнение состояния ----------

    static string TreeHash(TestEnv t, string subPath)
    {
        using var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, subPath, string.Empty), false)!;
        return RegistryTreeHasher.Compute(h).Sha256;
    }

    const string FooSub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo";

    [Fact]
    public void Subkey_without_delete_right_is_refused_before_the_copy()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);
        t.RegistryTree.DenyDeleteAt(p + @"\S1");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(OperationState.Denied, r.State);
        var s = Only(r);
        Assert.Equal(StepCodes.AccessDenied, s.Code);
        Assert.Equal("reg-tree-not-deletable", s.PolicyRule);
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.True(t.Registry.KeyExists(p + @"\S1"));
        AssertUntouched(t);
        Assert.False(t.Registry.KeyExists(CopyPath(r.OpId)));
    }

    [Fact]
    public void Delete_interrupted_in_the_middle_is_healed_from_the_verified_copy()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);
        var before = TreeHash(t, FooSub);
        t.RegistryTree.FailDeleteAt(p + @"\S1");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.Equal(StepState.Failed, Only(r).State);
        Assert.NotEqual(StepCodes.PartialDelete, Only(r).Code);
        // Дерево на месте целиком и совпадает с исходным по хэшу: половинчатого состояния не осталось.
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.True(t.Registry.KeyExists(p + @"\S1"));
        Assert.Equal("Foo App", t.Registry.Get(p, "DisplayName").Data);
        Assert.Equal(before, TreeHash(t, FooSub));
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId)));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Delete_interrupted_and_not_healable_is_failed_with_partial_delete_and_keeps_the_copy()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);
        t.RegistryTree.FailDeleteAt(p + @"\S1");
        t.RegistryTree.DropSetRawUnder = p; // лечение не может вернуть значения: хэш не сойдётся

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        var s = Only(r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.PartialDelete, s.Code);
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId)));
        Assert.True(t.Quarantine.Files.ContainsKey(RegTreeJson.BackupFile(r.OpId, 1)));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Key_state_comparer_compares_the_tree_hash_when_both_keys_exist()
    {
        var cmp = new RegKeyStateComparer();
        var a = new string('a', 64);
        var b = new string('b', 64);
        var exists = (string? sha) => RegTreeJson.KeyStateJson(true, 1, 1, sha);

        Assert.True(cmp.IsSame(exists(a), exists(a.ToUpperInvariant())));
        Assert.False(cmp.IsSame(exists(a), exists(b)));
        Assert.False(cmp.IsSame(exists(a), exists(null)));
        Assert.False(cmp.IsSame(exists(null), exists(null)));
        Assert.False(cmp.IsSame(exists(a), RegTreeJson.KeyAbsentJson));
        Assert.True(cmp.IsSame(RegTreeJson.KeyAbsentJson, RegTreeJson.KeyAbsentJson));
        Assert.False(cmp.IsNoChange(exists(a), exists(b)));
    }

    [Fact]
    public void Revert_over_the_same_content_is_already_reverted()
    {
        var t = Env();
        Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        Arp(t, "Foo"); // точно такой же ключ поставили заново: хэш дерева равен записанному до удаления

        var again = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Done, again.End);
        Assert.Equal(StepCodes.AlreadyReverted, again.Code);
    }

    // ---------- восстановление: ключ появился между проверкой и созданием ----------

    [Fact]
    public void Restore_does_not_touch_a_key_that_appeared_between_the_check_and_the_creation()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        var step = new RegistryKeyRestoreStep(RegTreeJson.ParseDeleteTarget(s.TargetJson), s.BeforeJson!, TestEnv.Sid);
        var open = step.Open(t.Env, null, true);
        Assert.Null(open.Refusal);
        using var session = open.Session!;
        var created = t.RegistryTree.Created;

        // Проверка «оригинала нет» уже прошла; ключ появляется при открытии копии, то есть до CreateSub.
        t.RegistryTree.OnOpen = path =>
        {
            if (path.EndsWith(@"\1", StringComparison.Ordinal) && path.Contains("Quarantine", StringComparison.OrdinalIgnoreCase))
                t.Registry.SetString(p, "Foreign", "x");
        };

        var e = Assert.Throws<StepFailureException>(session.Write);

        Assert.Equal(StepCodes.ExternalChange, e.Code);
        Assert.Equal("x", t.Registry.Get(p, "Foreign").Data);
        Assert.False(t.Registry.Get(p, "DisplayName").Exists);
        Assert.Equal(created, t.RegistryTree.Created);
    }

    // ---------- приёмник копирования: ссылка и чужой ключ на месте подключа ----------

    /// <summary>Ключ HKLM\…\Run с одним значением: цель «ссылки», в которую агент не должен ничего писать.</summary>
    static void ArmRunTarget(TestEnv t) => t.Registry.SetString(Run, "Keep", "1");

    static void AssertRunUntouched(TestEnv t)
    {
        Assert.False(t.Registry.KeyExists(Run + @"\S0"), "агент создал подключ в цели ссылки");
        Assert.False(t.Registry.Get(Run, "v").Exists, "агент записал значение в цель ссылки");
        Assert.False(t.Registry.Get(Run, "DisplayName").Exists, "агент записал значение в цель ссылки");
        Assert.Equal("1", t.Registry.Get(Run, "Keep").Data);
    }

    [Fact]
    public void Quarantine_refuses_a_symlink_planted_in_place_of_a_destination_subkey()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        ArmRunTarget(t);
        t.RegistryTree.OnCreate = child =>
        {
            if (child.EndsWith(@"\1\S0", StringComparison.Ordinal) && child.Contains("Quarantine", StringComparison.OrdinalIgnoreCase))
                t.Registry.AddSymlink(child, Run, openAsLink: true);
        };

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        var s = Only(r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.PolicyDenied, s.Code);
        AssertRunUntouched(t);
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.Equal("Foo App", t.Registry.Get(p, "DisplayName").Data);
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Quarantine_refuses_a_foreign_key_that_already_exists_in_place_of_a_destination_subkey()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        t.RegistryTree.OnCreate = child =>
        {
            if (child.EndsWith(@"\1\S0", StringComparison.Ordinal) && child.Contains("Quarantine", StringComparison.OrdinalIgnoreCase))
                t.Registry.SetString(child, "Planted", "evil");
        };

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        var s = Only(r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.ExternalChange, s.Code);
        // Записаны только три значения корня копии; в подложенный подключ не записано ничего.
        Assert.Equal(3, t.RegistryTree.SetRawCount);
        Assert.True(t.Registry.KeyExists(p + @"\S0"));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Restore_refuses_a_symlink_planted_in_place_of_a_subkey_of_the_restored_key()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        Assert.False(t.Registry.KeyExists(p));
        ArmRunTarget(t);
        t.RegistryTree.OnCreate = child =>
        {
            if (child.Equals(p + @"\S0", StringComparison.OrdinalIgnoreCase)) t.Registry.AddSymlink(child, Run, openAsLink: true);
        };

        var o = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Failed, o.End);
        // Подложенную ссылку DeleteTree не удаляет (по замыслу), поэтому недовосстановленный ключ остаётся и сверка состояния
        // после сбоя даёт verify-mismatch; главное свойство: в цель ссылки не записано ничего.
        Assert.Contains(o.Code, new[] { StepCodes.PolicyDenied, StepCodes.VerifyMismatch });
        AssertRunUntouched(t);
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Restore_refuses_a_foreign_key_that_already_exists_in_place_of_a_subkey()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 1);
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");
        var s = Only(r);
        var sets = t.RegistryTree.SetRawCount;
        t.RegistryTree.OnCreate = child =>
        {
            if (child.Equals(p + @"\S0", StringComparison.OrdinalIgnoreCase)) t.Registry.SetString(child, "Planted", "evil");
        };

        var o = Revert(t, r.OpId, s);

        Assert.Equal(StepEnd.Failed, o.End);
        Assert.Equal(StepCodes.ExternalChange, o.Code);
        Assert.Equal(sets + 3, t.RegistryTree.SetRawCount); // только значения корня, подключ чужого ключа не тронут
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Heal_refuses_a_symlink_planted_in_place_of_a_deleted_subkey_and_reports_partial_delete()
    {
        var t = Env();
        var p = Arp(t, "Foo", subkeys: 2);
        ArmRunTarget(t);
        t.RegistryTree.FailDeleteAt(p + @"\S1"); // S0 уже удалён, S1 не удаляется: запускается лечение
        t.RegistryTree.OnCreate = child =>
        {
            if (child.Equals(p + @"\S0", StringComparison.OrdinalIgnoreCase)) t.Registry.AddSymlink(child, Run, openAsLink: true);
        };

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        var s = Only(r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.PartialDelete, s.Code);
        AssertRunUntouched(t);
        Assert.True(t.Registry.KeyExists(CopyPath(r.OpId)));
        Assert.True(t.Quarantine.Files.ContainsKey(RegTreeJson.BackupFile(r.OpId, 1)));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    // ---------- имена с непарными суррогатами ----------

    [Fact]
    public void Name_check_rejects_unpaired_surrogates_and_accepts_real_pairs()
    {
        Assert.False(RegistryTreeHasher.IsSafeName("a" + (char)0xD800));
        Assert.False(RegistryTreeHasher.IsSafeName("a" + (char)0xDC00 + "b"));
        Assert.False(RegistryTreeHasher.IsSafeName("" + (char)0xD800 + 'x'));
        Assert.False(RegistryTreeHasher.IsSafeName("" + (char)0xDC00 + (char)0xD800)); // перевёрнутая пара
        Assert.True(RegistryTreeHasher.IsSafeName("a\U0001F600b"));
        Assert.True(RegistryTreeHasher.IsSafeName("Привет"));
    }

    [Fact]
    public void Tree_with_an_unpaired_surrogate_in_a_subkey_or_value_name_is_denied_before_any_write()
    {
        foreach (var inValue in new[] { false, true })
        {
            var t = Env();
            var p = Arp(t, "Foo");
            var bad = "bad" + (char)0xD800 + "name";
            if (inValue) t.Registry.SetString(p, bad, "1");
            else t.Registry.SetString(p + "\\" + bad, "v", "1");

            var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

            Assert.Equal(OperationState.Denied, r.State);
            var s = Only(r);
            Assert.Equal(StepCodes.RegTreeBadName, s.Code);
            Assert.Equal("reg-tree-bad-name", s.PolicyRule);
            Assert.True(t.Registry.KeyExists(p));
            AssertUntouched(t);
        }
    }

    // ---------- размер значения проверяется до чтения ----------

    [Fact]
    public void Oversized_value_is_refused_by_size_before_its_data_is_read()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Registry.SetBinary(p, "Blob", new byte[3 << 20]);

        using (var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, FooSub, string.Empty), false)!)
        {
            var stats = RegistryTreeHasher.Compute(h);
            Assert.True(stats.TooLarge);
            Assert.False(stats.Usable);
            Assert.Equal(string.Empty, stats.Sha256);
        }
        Assert.True(t.RegistryTree.RawBytesMaterialized < (1 << 20), "гигантское значение прочитано целиком");

        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo");

        Assert.NotEqual(OperationState.Done, r.State);
        Assert.True(t.Registry.KeyExists(p));
        AssertUntouched(t);
        Assert.True(t.RegistryTree.RawBytesMaterialized < (1 << 20), "гигантское значение прочитано целиком");
    }

    [Fact]
    public void Values_that_fit_one_by_one_but_not_together_are_too_large()
    {
        var t = Env();
        var p = Arp(t, "Foo");
        t.Registry.SetBinary(p, "A", new byte[700_000]);
        t.Registry.SetBinary(p, "B", new byte[700_000]);
        using var h = t.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, FooSub, string.Empty), false)!;

        Assert.True(RegistryTreeHasher.Compute(h).TooLarge);
        Assert.Throws<RegValueTooLargeException>(() => h.RawValues(RegistryTreeHasher.MaxBytes));
        Assert.True(h.RawValues(2_000_000).Count >= 2);
    }

    // ---------- StateDisplay ----------

    [Fact]
    public void State_display_has_short_russian_text_for_the_new_kinds()
    {
        var t = Env();
        Arp(t, "Foo", subkeys: 2);
        t.Registry.SetString(Run, "Foo", "\"C:\\Gone\\foo.exe\"");
        t.Cleanup.AddFile(@"C:\Users\Test\AppData\Local\Temp\old.tmp", ageHours: 100, length: 1000);
        var r = FindingsHarness.Run(t, "arp:machine:x64:Foo", "run:hklm64:Foo");
        var key = r.Steps.Single(x => x.Kind == StepKind.RegistryKeyDelete);
        var run = r.Steps.Single(x => x.Kind == StepKind.StartupRemove);
        var clean = t.Journal.Get(t.Clean("temp-user").OperationId!.Value)!.Steps.Single();

        Assert.Equal("ключ есть, подключей: 2", StateDisplay.Of(StepKind.RegistryKeyDelete, key.BeforeJson!));
        Assert.Equal("ключа нет", StateDisplay.Of(StepKind.RegistryKeyDelete, key.AfterJson));
        Assert.StartsWith("строка", StateDisplay.Of(StepKind.StartupRemove, run.BeforeJson!));
        Assert.Equal("значения нет", StateDisplay.Of(StepKind.StartupRemove, run.AfterJson));
        Assert.NotEqual("неизвестно", StateDisplay.Of(StepKind.CleanupCategory, clean.BeforeJson!));
        Assert.Equal("очищено", StateDisplay.Of(StepKind.CleanupCategory, clean.AfterJson));
        Assert.Equal("неизвестно", StateDisplay.Of(StepKind.RegistryKeyDelete, "not json"));
    }

    // ---------- Purge копий ----------

    [Fact]
    public void Purge_removes_copies_of_operations_that_left_the_journal_and_keeps_live_ones()
    {
        var t = Env();
        Arp(t, "Foo");
        var live = FindingsHarness.Run(t, "arp:machine:x64:Foo").OpId;
        var dead = Guid.NewGuid();
        t.Registry.SetString(CopyPath(dead), "DisplayName", "Old");
        t.Registry.SetString(CopyPath(dead) + @"\S0", "v", "1");
        t.Quarantine.WriteFile(RegTreeJson.BackupFile(dead, 1), [1, 2, 3]);
        t.Registry.SetString(@"HKLM\SOFTWARE\Chistovik\Quarantine\not-a-guid", "v", "1");

        var n = QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal);

        Assert.Equal(1, n);
        Assert.False(t.Registry.KeyExists(CopyPath(dead)));
        Assert.False(t.Registry.KeyExists(CopyPath(dead) + @"\S0"));
        Assert.False(t.Quarantine.Files.ContainsKey(RegTreeJson.BackupFile(dead, 1)));
        Assert.True(t.Registry.KeyExists(CopyPath(live)));
        Assert.True(t.Quarantine.Files.ContainsKey(RegTreeJson.BackupFile(live, 1)));
        Assert.True(t.Registry.KeyExists(@"HKLM\SOFTWARE\Chistovik\Quarantine\not-a-guid"));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Purge_after_journal_retention_removes_the_copies_of_the_expired_operation()
    {
        var t = Env();
        Arp(t, "Foo");
        var op = FindingsHarness.Run(t, "arp:machine:x64:Foo").OpId;
        Assert.Equal(0, QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal));
        t.Clock.Advance(TimeSpan.FromDays(91));

        Assert.Equal(1, t.Journal.Purge(t.Clock.UtcNow - TimeSpan.FromDays(90)));
        var n = QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal);

        Assert.Equal(1, n);
        Assert.False(t.Registry.KeyExists(CopyPath(op)));
        Assert.Empty(t.Quarantine.Files);
        Assert.Equal(0, QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal));
    }

    [Fact]
    public void Purge_survives_a_failing_file_store_and_a_missing_one()
    {
        var t = Env();
        var dead = Guid.NewGuid();
        t.Registry.SetString(CopyPath(dead), "v", "1");
        t.Quarantine.WriteFile(RegTreeJson.BackupFile(dead, 1), [1]);
        t.Quarantine.FailDeletes = true;

        var n = QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal);

        Assert.Equal(1, n);
        Assert.False(t.Registry.KeyExists(CopyPath(dead)));
        Assert.True(t.Quarantine.Files.ContainsKey(RegTreeJson.BackupFile(dead, 1)));
        t.Quarantine.FailDeletes = false;
        Assert.Equal(0, QuarantinePurge.Run(t.RegistryTree, null, t.Journal));
        Assert.Equal(1, QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal));
        Assert.Empty(t.Quarantine.Files);
    }

    [Fact]
    public void Purge_never_follows_a_link_inside_the_copy()
    {
        var t = Env();
        var dead = Guid.NewGuid();
        t.Registry.SetString(CopyPath(dead), "v", "1");
        t.Registry.SetString(@"HKLM\SOFTWARE\Keep", "v", "1");
        t.Registry.AddSymlink(CopyPath(dead) + @"\Link", @"HKLM\SOFTWARE\Keep", openAsLink: true);

        QuarantinePurge.Run(t.RegistryTree, t.Quarantine, t.Journal);

        Assert.True(t.Registry.KeyExists(@"HKLM\SOFTWARE\Keep"));
        Assert.Equal("1", t.Registry.Get(@"HKLM\SOFTWARE\Keep", "v").Data);
    }
}
