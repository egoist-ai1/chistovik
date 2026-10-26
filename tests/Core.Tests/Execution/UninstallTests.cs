using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Удаление программ через агента: применение, отказы, коды установщика, обрыв процесса, запрет отката.</summary>
public class UninstallTests
{
    const string Guid1 = "{11111111-2222-3333-4444-555555555555}";
    const string Exe = @"C:\Program Files\Foo\uninst.exe";
    static readonly string MsiId = ArpId(Guid1);
    static readonly string ExeId = ArpId("FooApp");

    static string ArpId(string key) => Chistovik.Core.Policy.ArpId.Make("x64", key);

    static string KeyPath(string key, string arch = "x64") => FakeRegistry.Path(UninstallProbe.Location(arch, key));

    static void AddMsi(TestEnv t, string name = "Foo Tool")
    {
        var p = KeyPath(Guid1);
        t.Registry.SetString(p, "DisplayName", name);
        t.Registry.SetDword(p, "WindowsInstaller", 1);
        t.Uninstaller.MsiStates[Guid1] = 3;
    }

    // exe-программа: ключ ARP исчезает, когда деинсталлятор отработал
    static void AddExe(TestEnv t, string name = "Foo App")
    {
        var p = KeyPath("FooApp");
        t.Registry.SetString(p, "DisplayName", name);
        t.Registry.SetString(p, "QuietUninstallString", "\"" + Exe + "\" /quiet");
        t.Security.TrustFileAndDir(Exe);
        t.Uninstaller.Effect = _ => t.Registry.RemoveKey(p);
    }

    static StepRecord OnlyStep(TestEnv t, AgentResponse r) => t.Journal.Get(r.OperationId!.Value)!.Steps.Single();

    [Fact]
    public void Msi_is_uninstalled_by_product_code()
    {
        var t = new TestEnv();
        AddMsi(t);

        var r = t.Uninstall(MsiId);

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(["msi:" + Guid1], t.Uninstaller.Calls);
        Assert.Equal(TimeSpan.FromMinutes(10), t.Uninstaller.LastTimeout);
        var s = OnlyStep(t, r);
        Assert.Equal(StepKind.UninstallRun, s.Kind);
        Assert.Equal(StepState.Done, s.State);
        Assert.False(s.Revertible);
        Assert.Equal(OperationKind.Uninstall, t.Journal.Get(r.OperationId!.Value)!.Summary.Kind);
    }

    [Fact]
    public void Exe_with_quiet_string_is_run_with_its_arguments()
    {
        var t = new TestEnv();
        AddExe(t);

        var r = t.Uninstall(ExeId);

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(["exe:" + Exe + " /quiet"], t.Uninstaller.Calls);
        Assert.Equal(StepState.Done, OnlyStep(t, r).State);
    }

    [Fact]
    public void Repeated_request_after_success_changes_nothing()
    {
        var t = new TestEnv();
        AddMsi(t);
        Assert.True(t.Uninstall(MsiId).Ok);

        var again = t.Uninstall(MsiId);

        Assert.Single(t.Uninstaller.Calls);
        Assert.Equal(ResponseCodes.NoChange, again.Code);
        Assert.Equal(StepState.Skipped, OnlyStep(t, again).State);
    }

    // ---------- отказы: ничего не запускается ----------

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)")]
    [InlineData("Microsoft Edge")]
    public void Runtimes_and_windows_components_are_denied_before_run(string name)
    {
        var t = new TestEnv();
        AddMsi(t, name);

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Uninstaller.Calls);
        var s = OnlyStep(t, r);
        Assert.Equal(StepState.Denied, s.State);
        Assert.NotNull(s.PolicyRule);
    }

    [Fact]
    public void System_component_and_child_entries_are_denied()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Registry.SetDword(KeyPath(Guid1), "SystemComponent", 1);
        Assert.Equal(ResponseCodes.PolicyDenied, t.Uninstall(MsiId).Code);

        var t2 = new TestEnv();
        AddMsi(t2);
        t2.Registry.SetString(KeyPath(Guid1), "ParentKeyName", "Parent");
        Assert.Equal(ResponseCodes.PolicyDenied, t2.Uninstall(MsiId).Code);

        Assert.Empty(t.Uninstaller.Calls);
        Assert.Empty(t2.Uninstaller.Calls);
    }

    [Theory]
    [InlineData("NoRemove", "dword:1", true)]
    [InlineData("NoRemove", "dword:2", true)]
    [InlineData("NoRemove", "sz:1", true)]
    [InlineData("NoRemove", "sz:0", false)]
    [InlineData("NoRemove", "none", false)]
    [InlineData("SystemComponent", "dword:1", true)]
    [InlineData("SystemComponent", "dword:2", true)]
    [InlineData("SystemComponent", "sz:1", true)]
    [InlineData("SystemComponent", "sz:0", false)]
    [InlineData("SystemComponent", "none", false)]
    public void Forbid_flags_are_read_as_dword_or_numeric_string(string flag, string value, bool denied)
    {
        var t = new TestEnv();
        AddMsi(t);
        var p = KeyPath(Guid1);
        if (value.StartsWith("dword:", StringComparison.Ordinal)) t.Registry.SetDword(p, flag, uint.Parse(value[6..]));
        else if (value.StartsWith("sz:", StringComparison.Ordinal)) t.Registry.SetString(p, flag, value[3..]);

        var r = t.Uninstall(MsiId);

        if (denied)
        {
            Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
            Assert.Empty(t.Uninstaller.Calls);
        }
        else Assert.True(r.Ok, r.Code + r.Message);
    }

    [Fact]
    public void Exe_without_silent_mode_is_denied()
    {
        var t = new TestEnv();
        var p = KeyPath("FooApp");
        t.Registry.SetString(p, "DisplayName", "Foo App");
        t.Registry.SetString(p, "UninstallString", "\"" + Exe + "\"");
        t.Security.TrustFileAndDir(Exe);

        var r = t.Uninstall(ExeId);

        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Uninstaller.Calls);
    }

    [Fact]
    public void Exe_in_users_writable_directory_is_denied()
    {
        var t = new TestEnv();
        AddExe(t);
        t.Security.UsersWritable(@"C:\Program Files\Foo");

        var r = t.Uninstall(ExeId);

        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Uninstaller.Calls);
    }

    [Fact]
    public void Missing_program_is_not_found()
    {
        var t = new TestEnv();

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Empty(t.Uninstaller.Calls);
    }

    [Theory]
    [InlineData("arp:user:x64:Foo")]
    [InlineData("arp:machine:arm64:Foo")]
    [InlineData("not-an-id")]
    public void User_scope_and_garbage_ids_fail_protocol_validation_and_planner(string id)
    {
        // Протокол отклоняет такой запрос ещё при разборе (bad-request); планировщик — вторая линия защиты.
        Assert.NotNull(new UninstallRequest { Id = Guid.NewGuid(), FindingId = id }.Validate());

        var t = new TestEnv();
        AddMsi(t);

        var r = t.Uninstall(id);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Uninstaller.Calls);
        Assert.Empty(t.Journal.AllSteps);
    }

    [Fact]
    public void Registry_symlink_on_arp_key_is_denied()
    {
        var t = new TestEnv();
        AddMsi(t);
        var link = KeyPath("Linked");
        t.Registry.AddSymlink(link, KeyPath(Guid1), openAsLink: true);
        t.Registry.SetDword(link, "WindowsInstaller", 1);

        var r = t.Uninstall(ArpId("Linked"));

        Assert.False(r.Ok);
        Assert.Empty(t.Uninstaller.Calls);
    }

    // ---------- результат установщика ----------

    [Fact]
    public void Exit_3010_is_success_with_reboot_note()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Outcome = new UninstallOutcome(3010, false);

        var r = t.Uninstall(MsiId);

        Assert.True(r.Ok, r.Code + r.Message);
        var s = OnlyStep(t, r);
        Assert.Equal(StepState.Done, s.State);
        Assert.Equal("win32:3010", s.Code);
    }

    [Fact]
    public void Exit_1618_means_installer_busy()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Outcome = new UninstallOutcome(1618, false);

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        var s = OnlyStep(t, r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.InUse, s.Code);
    }

    [Fact]
    public void Timeout_is_failure_with_timeout_code()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Outcome = new UninstallOutcome(0, true);

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        Assert.Equal(StepCodes.Timeout, OnlyStep(t, r).Code);
    }

    [Fact]
    public void Other_exit_codes_are_recorded_as_win32()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Outcome = new UninstallOutcome(1603, false);

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        Assert.Equal("win32:1603", OnlyStep(t, r).Code);
    }

    [Fact]
    public void Success_code_with_program_still_installed_is_verify_mismatch()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Effect = _ => t.Uninstaller.MsiStates[Guid1] = 3;

        var r = t.Uninstall(MsiId);

        Assert.False(r.Ok);
        Assert.Equal(StepCodes.VerifyMismatch, OnlyStep(t, r).Code);
    }

    // ---------- откат ----------

    [Fact]
    public void Undo_of_uninstall_is_refused_and_runs_nothing()
    {
        var t = new TestEnv();
        AddMsi(t);
        var op = t.Uninstall(MsiId).OperationId!.Value;
        var calls = t.Uninstaller.Calls.Count;
        var ops = t.Journal.AllOperations.Count;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.NotRevertible, u.Code);
        Assert.Equal(calls, t.Uninstaller.Calls.Count);
        Assert.Equal(ops, t.Journal.AllOperations.Count);
        Assert.Equal(StepState.Done, t.Journal.Get(op)!.Steps.Single().State);
    }

    [Fact]
    public void Undo_of_failed_uninstall_is_not_revertible_too()
    {
        var t = new TestEnv();
        AddMsi(t);
        t.Uninstaller.Outcome = new UninstallOutcome(1603, false);
        var op = t.Uninstall(MsiId).OperationId!.Value;
        var ops = t.Journal.AllOperations.Count;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.NotRevertible, u.Code);
        Assert.Equal(ops, t.Journal.AllOperations.Count);
    }

    // ---------- обрыв процесса ----------

    static void Crash(TestEnv t, string label, Action act)
    {
        t.Journal.Probe = l =>
        {
            if (l == label) throw new SimulatedCrash(l);
        };
        Assert.Throws<SimulatedCrash>(act);
        t.Journal.Probe = null;
        t.Restart();
    }

    [Fact]
    public void Crash_before_run_is_recovered_as_not_applied_and_never_reruns()
    {
        var t = new TestEnv();
        AddMsi(t);
        Crash(t, "AddPending:after", () => t.Uninstall(MsiId));
        Assert.Empty(t.Uninstaller.Calls);

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.NotApplied);
        Assert.Equal(0, rep.RecoveredDone);
        Assert.Empty(t.Uninstaller.Calls);
        Assert.Equal(StepState.Skipped, t.Journal.AllSteps.Single().State);
    }

    [Fact]
    public void Crash_after_msi_run_is_recovered_as_done_without_second_run()
    {
        var t = new TestEnv();
        AddMsi(t);
        Crash(t, "Complete:before", () => t.Uninstall(MsiId));
        Assert.Single(t.Uninstaller.Calls);

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Equal(0, rep.Failed);
        Assert.Single(t.Uninstaller.Calls);
        var s = t.Journal.AllSteps.Single();
        Assert.Equal(StepState.Done, s.State);
        Assert.Equal(StepCodes.RecoveredDone, s.Code);
        Assert.False(s.Revertible);
    }

    [Fact]
    public void Crash_after_exe_run_is_recovered_as_done_by_registry_key()
    {
        var t = new TestEnv();
        AddExe(t);
        Crash(t, "Complete:before", () => t.Uninstall(ExeId));

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Single(t.Uninstaller.Calls);
    }

    [Fact]
    public void Recovery_never_starts_an_uninstaller()
    {
        var t = new TestEnv();
        AddMsi(t);
        Crash(t, "AddPending:after", () => t.Uninstall(MsiId));
        t.Uninstaller.Calls.Clear();

        _ = new RecoveryService(t.Env).Run();
        _ = new RecoveryService(t.Env).Run();

        Assert.Empty(t.Uninstaller.Calls);
    }
}
