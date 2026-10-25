using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Обрыв процесса между AddPending, записью и Complete для каждого вида шага и восстановление при старте (DESIGN-0.1 §4).</summary>
public class RecoveryTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    sealed record Scenario(
        Action<TestEnv> Setup,
        Func<TestEnv, AgentResponse> Act,
        Func<TestEnv, bool> Applied,
        Func<TestEnv, bool> Original,
        Action<TestEnv>? External);

    static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        ["registry"] = new(
            t => t.SetTweakDword("ext", 1),
            t => t.Tweaks(("ext", true)),
            t => t.TweakValue("ext") == TestEnv.Dword(0),
            t => t.TweakValue("ext") == TestEnv.Dword(1),
            t => t.SetTweakDword("ext", 5)),
        ["startup"] = new(
            t => t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", "\"C:\\Program Files\\Foo\\foo.exe\""),
            t => t.Startup("run:hku:FooTray", false),
            t => t.Registry.Get(FakeRegistry.HkuPath(TestEnv.Sid, ApprovedRun), "FooTray").Exists,
            t => !t.Registry.Get(FakeRegistry.HkuPath(TestEnv.Sid, ApprovedRun), "FooTray").Exists,
            null),
        ["service"] = new(
            t => t.AddThirdPartyService("FooSvc", 2),
            t => t.Service("FooSvc", StartMode.Disabled),
            t => t.Services.Get("FooSvc").StartType == 4,
            t => t.Services.Get("FooSvc").StartType == 2,
            t => t.Services.Mutate("FooSvc", s => s with { StartType = 3 })),
        ["task"] = new(
            t => t.AddUserTask(@"\Foo\Update"),
            t => t.Task(@"\Foo\Update", false),
            t => !t.Tasks.Get(@"\Foo\Update").Enabled,
            t => t.Tasks.Get(@"\Foo\Update").Enabled,
            null),
        ["power-active"] = new(
            _ => { },
            t => t.Plan(PowerPlan.High),
            t => t.Power.Active == TestEnv.High,
            t => t.Power.Active == TestEnv.Balanced,
            t => t.Power.Active = TestEnv.Saver),
        ["power-setting"] = new(
            t =>
            {
                var s = Chistovik.Core.Catalog.PowerCatalog.Shared.FindSetting("min")!;
                t.Power.SetValue(TestEnv.Balanced, s.Subgroup, s.Setting, 5, 5);
            },
            t => t.PowerSetting("min", 50, null),
            t => Min(t) == (50u, 5u),
            t => Min(t) == (5u, 5u),
            t =>
            {
                var s = Chistovik.Core.Catalog.PowerCatalog.Shared.FindSetting("min")!;
                t.Power.SetValue(TestEnv.Balanced, s.Subgroup, s.Setting, 77, 5);
            }),
        ["power-create"] = new(
            _ => { },
            t => t.Plan(PowerPlan.Ult),
            t => t.Power.DuplicateCount == 1,
            t => t.Power.DuplicateCount == 0,
            null),
    };

    static (uint, uint) Min(TestEnv t)
    {
        var s = Chistovik.Core.Catalog.PowerCatalog.Shared.FindSetting("min")!;
        return t.Power.Value(TestEnv.Balanced, s.Subgroup, s.Setting);
    }

    public static TheoryData<string> Names => new(Scenarios.Keys);

    public static TheoryData<string> NamesWithExternal => new(Scenarios.Where(s => s.Value.External is not null).Select(s => s.Key));

    static int TotalWrites(TestEnv t) => t.Registry.WriteCount + t.Services.WriteCount + t.Tasks.WriteCount + t.Power.WriteCount;

    /// <summary>Выполняет действие и обрывает процесс в n-й точке с данной меткой; после этого «перезапускает агента».</summary>
    static void Crash(TestEnv t, string label, Action act, int nth = 1)
    {
        var hits = 0;
        t.Journal.Probe = l =>
        {
            if (l == label && ++hits == nth) throw new SimulatedCrash(l);
        };
        Assert.Throws<SimulatedCrash>(act);
        t.Journal.Probe = null;
        t.Restart();
    }

    static RecoveryReport Recover(TestEnv t) => new RecoveryService(t.Env).Run();

    static StepRecord OnlyPendingFirst(TestEnv t) => t.Journal.AllSteps.First();

    [Theory]
    [MemberData(nameof(Names))]
    public void Crash_after_AddPending_before_write_is_recovered_as_not_applied(string name)
    {
        var sc = Scenarios[name];
        var t = new TestEnv();
        sc.Setup(t);
        Crash(t, "AddPending:after", () => sc.Act(t));
        Assert.True(sc.Original(t));
        Assert.Single(t.Journal.GetPending());
        var writes = TotalWrites(t);

        var rep = Recover(t);

        Assert.Equal(1, rep.NotApplied);
        Assert.Equal(0, rep.RecoveredDone);
        Assert.Equal(0, rep.Failed);
        Assert.Equal(1, rep.InterruptedOperations);
        var step = OnlyPendingFirst(t);
        Assert.Equal(StepState.Skipped, step.State);
        Assert.Equal(StepCodes.RecoveredNotApplied, step.Code);
        Assert.Equal(OperationState.Interrupted, t.Journal.AllOperations.Single().State);
        Assert.Equal(writes, TotalWrites(t));
        Assert.True(sc.Original(t));
        Assert.Empty(t.Journal.GetPending());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Crash_after_write_before_Complete_is_recovered_as_done(string name)
    {
        var sc = Scenarios[name];
        var t = new TestEnv();
        sc.Setup(t);
        Crash(t, "Complete:before", () => sc.Act(t));
        Assert.True(sc.Applied(t));
        var writes = TotalWrites(t);

        var rep = Recover(t);

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Equal(0, rep.NotApplied);
        Assert.Equal(0, rep.Failed);
        var step = OnlyPendingFirst(t);
        Assert.Equal(StepState.Done, step.State);
        Assert.Equal(StepCodes.RecoveredDone, step.Code);
        Assert.Equal(OperationState.Interrupted, t.Journal.AllOperations.Single().State);
        Assert.Equal(writes, TotalWrites(t));
        Assert.True(sc.Applied(t));
    }

    [Theory]
    [MemberData(nameof(NamesWithExternal))]
    public void Crash_then_external_change_is_failed_as_external_change(string name)
    {
        var sc = Scenarios[name];
        var t = new TestEnv();
        sc.Setup(t);
        Crash(t, "AddPending:after", () => sc.Act(t));
        sc.External!(t);
        var writes = TotalWrites(t);

        var rep = Recover(t);

        Assert.Equal(1, rep.Failed);
        var step = OnlyPendingFirst(t);
        Assert.Equal(StepState.Failed, step.State);
        Assert.Equal(StepCodes.ExternalChange, step.Code);
        Assert.Equal(writes, TotalWrites(t));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Recovery_is_idempotent(string name)
    {
        var sc = Scenarios[name];
        var t = new TestEnv();
        sc.Setup(t);
        Crash(t, "Complete:before", () => sc.Act(t));
        Recover(t);
        var again = Recover(t);
        Assert.Equal(new RecoveryReport(0, 0, 0, 0), again);
    }

    [Fact]
    public void Crash_right_after_BeginOrGet_leaves_a_running_operation_closed_as_interrupted()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        Crash(t, "BeginOrGet:after", () => t.Tweaks(("ext", true)));
        Assert.Equal(OperationState.Running, t.Journal.AllOperations.Single().State);

        var rep = Recover(t);

        Assert.Equal(1, rep.InterruptedOperations);
        Assert.Equal(OperationState.Interrupted, t.Journal.AllOperations.Single().State);
        Assert.Empty(t.Journal.AllSteps);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    [Fact]
    public void Crash_before_Finish_leaves_done_steps_and_an_interrupted_operation()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        Crash(t, "Finish:before", () => t.Tweaks(("ext", true)));

        var rep = Recover(t);

        Assert.Equal(new RecoveryReport(0, 0, 0, 1), rep);
        Assert.Equal(StepState.Done, t.Journal.AllSteps.Single().State);
        Assert.Equal(OperationState.Interrupted, t.Journal.AllOperations.Single().State);
    }

    [Fact]
    public void Crash_in_the_second_step_keeps_the_first_done_and_recovers_the_second()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.SetTweakDword("hidden", 2);
        Crash(t, "Complete:before", () => t.Tweaks(("ext", true), ("hidden", true)), nth: 2);

        var rep = Recover(t);

        Assert.Equal(1, rep.RecoveredDone);
        var steps = t.Journal.AllSteps;
        Assert.Equal([StepState.Done, StepState.Done], steps.Select(s => s.State));
        Assert.Null(steps[0].Code);
        Assert.Equal(StepCodes.RecoveredDone, steps[1].Code);
    }

    [Fact]
    public void Crash_with_a_partially_applied_mode_recovers_without_continuing()
    {
        var t = new TestEnv();
        Crash(t, "AddPending:after", () => t.Mode("game"), nth: 3);
        var writes = TotalWrites(t);

        var rep = Recover(t);

        Assert.Equal(1, rep.NotApplied);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(3, t.Journal.AllSteps.Count);
    }

    // ---------- обрыв во время отмены ----------

    [Fact]
    public void Crash_during_undo_after_write_completes_the_revert_atomically()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var r = t.Tweaks(("ext", true));
        var op = r.OperationId!.Value;
        Crash(t, "CompleteRevert:before", () => t.Undo(op));
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));

        var rep = Recover(t);

        Assert.Equal(1, rep.RecoveredDone);
        var orig = t.Journal.Get(op)!;
        Assert.Equal(StepState.Undone, orig.Steps.Single().State);
        var undoOp = t.Journal.AllOperations.Single(o => o.Kind == OperationKind.Undo);
        var undoStep = t.Journal.Get(undoOp.Id)!.Steps.Single();
        Assert.Equal(StepState.Done, undoStep.State);
        Assert.Equal(StepCodes.RecoveredDone, undoStep.Code);
        Assert.Equal(orig.Steps.Single().Id, undoStep.RevertsStepId);
        Assert.Equal(OperationState.Interrupted, undoOp.State);
    }

    [Fact]
    public void Crash_during_undo_before_write_leaves_the_original_step_done()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var r = t.Tweaks(("ext", true));
        var op = r.OperationId!.Value;
        Crash(t, "AddPending:after", () => t.Undo(op));
        Assert.Equal(TestEnv.Dword(0), t.TweakValue("ext"));

        var rep = Recover(t);

        Assert.Equal(1, rep.NotApplied);
        Assert.Equal(StepState.Done, t.Journal.Get(op)!.Steps.Single().State);
        Assert.Equal(TestEnv.Dword(0), t.TweakValue("ext"));
    }

    [Fact]
    public void Undo_can_be_repeated_after_recovery_of_an_interrupted_undo()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        Crash(t, "AddPending:after", () => t.Undo(op));
        Recover(t);

        var u = t.Undo(op);

        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
        Assert.Equal(StepState.Undone, t.Journal.Get(op)!.Steps.Single().State);
    }

    // ---------- отказы при восстановлении ----------

    [Fact]
    public void Recovery_refuses_a_tampered_journal_target_and_writes_nothing()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        Crash(t, "AddPending:after", () => t.Tweaks(("ext", true)));
        t.Journal.Tamper(t.Journal.AllSteps.Single().Id, targetKey: "reg:HKLM\\SYSTEM\\CurrentControlSet\\Services\\WinDefend\\Start");
        var writes = TotalWrites(t);
        var opens = t.Registry.OpenCount;

        var rep = Recover(t);

        Assert.Equal(1, rep.Failed);
        Assert.Equal(StepState.Failed, t.Journal.AllSteps.Single().State);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(opens, t.Registry.OpenCount);
    }

    [Fact]
    public void Recovery_does_not_hold_handles()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        Crash(t, "Complete:before", () => t.Tweaks(("ext", true)));
        Recover(t);
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    // ---------- чистка реестра (C3): три новых вида шагов ----------

    const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion";

    sealed record RegScenario(Action<TestEnv> Setup, string Id, Func<TestEnv, bool> Exists, StepKind Kind);

    static readonly Dictionary<string, RegScenario> RegScenarios = new()
    {
        ["key"] = new(
            t =>
            {
                var p = Cv + @"\Uninstall\Foo";
                t.Registry.SetString(p, "DisplayName", "Foo App");
                t.Registry.SetString(p, "UninstallString", "\"C:\\Program Files\\Foo\\unins000.exe\" /S");
                t.Registry.SetString(p + @"\S0", "v", "1");
            },
            "arp:machine:x64:Foo",
            t => t.Registry.KeyExists(Cv + @"\Uninstall\Foo"),
            StepKind.RegistryKeyDelete),
        ["run"] = new(
            t => t.Registry.SetString(Cv + @"\Run", "Foo", "\"C:\\Gone\\foo.exe\""),
            "run:hklm64:Foo",
            t => t.Registry.Get(Cv + @"\Run", "Foo").Exists,
            StepKind.StartupRemove),
        ["shareddll"] = new(
            t => t.Registry.SetDword(Cv + @"\SharedDLLs", @"C:\Gone\a.dll", 1),
            "shareddll:x64:" + OrphanId.SharedDllHash(@"C:\Gone\a.dll"),
            t => t.Registry.Get(Cv + @"\SharedDLLs", @"C:\Gone\a.dll").Exists,
            StepKind.SharedDllRemove),
    };

    public static TheoryData<string> RegNames => new(RegScenarios.Keys);

    static TestEnv RegEnv(RegScenario sc)
    {
        var t = new TestEnv();
        t.Security.Trust(@"C:\");
        sc.Setup(t);
        return t;
    }

    [Theory]
    [MemberData(nameof(RegNames))]
    public void Registry_cleanup_crash_before_write_is_recovered_as_not_applied(string name)
    {
        var sc = RegScenarios[name];
        var t = RegEnv(sc);
        Crash(t, "AddPending:after", () => FindingsHarness.Run(t, sc.Id));
        Assert.True(sc.Exists(t));
        Assert.Single(t.Journal.GetPending());
        Assert.Equal(sc.Kind, t.Journal.GetPending()[0].Kind);

        var rep = Recover(t);

        Assert.Equal(1, rep.NotApplied);
        Assert.Equal(0, rep.RecoveredDone);
        Assert.Equal(0, rep.Failed);
        Assert.Equal(StepCodes.RecoveredNotApplied, OnlyPendingFirst(t).Code);
        Assert.True(sc.Exists(t));
        Assert.Empty(t.Journal.GetPending());
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Theory]
    [MemberData(nameof(RegNames))]
    public void Registry_cleanup_crash_after_write_is_recovered_as_done(string name)
    {
        var sc = RegScenarios[name];
        var t = RegEnv(sc);
        Crash(t, "Complete:before", () => FindingsHarness.Run(t, sc.Id));
        Assert.False(sc.Exists(t));
        Assert.Single(t.Journal.GetPending());

        var rep = Recover(t);

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Equal(0, rep.NotApplied);
        Assert.Equal(0, rep.Failed);
        Assert.Equal(StepState.Done, OnlyPendingFirst(t).State);
        Assert.Equal(StepCodes.RecoveredDone, OnlyPendingFirst(t).Code);
        Assert.False(sc.Exists(t));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Registry_key_recovery_never_writes_to_the_registry()
    {
        var sc = RegScenarios["key"];
        var t = RegEnv(sc);
        Crash(t, "AddPending:after", () => FindingsHarness.Run(t, sc.Id));
        var created = t.RegistryTree.Created;
        var deleted = t.RegistryTree.Deleted;
        var writes = t.Registry.WriteCount;

        Recover(t);

        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(deleted, t.RegistryTree.Deleted);
        Assert.Equal(writes, t.Registry.WriteCount);
    }
}
