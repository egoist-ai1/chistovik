using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Исправления по независимому ревью: Purge и своя схема питания, имя схемы, команда автозагрузки, internal, Recovery.</summary>
public class ReviewFixTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string OldCmd = "\"C:\\Program Files\\Foo\\foo.exe\"";
    const string NewCmd = "\"C:\\Program Files\\Foo\\foo-new.exe\"";

    static Guid Created(TestEnv t) => Assert.Single(t.Power.ListSchemes().Except([TestEnv.Saver, TestEnv.Balanced, TestEnv.High]));

    // ---------- 1. Purge ----------

    [Fact]
    public void Purge_keeps_operation_that_created_our_scheme_so_it_is_neither_duplicated_nor_forgotten()
    {
        var t = new TestEnv();
        var op1 = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var ours = Created(t);
        t.Clock.Advance(TimeSpan.FromDays(91));
        var op2 = t.Plan(PowerPlan.High).OperationId!.Value; // before = наша схема
        t.Power.Names.Remove(ours); // доказательство только журнал, не имя

        var removed = t.Journal.Purge(t.Clock.UtcNow.AddDays(-90));

        Assert.Equal(0, removed);
        Assert.NotNull(t.Journal.Get(op1));
        Assert.NotNull(t.Journal.LatestEffective(StateJson.PowerCreateKey(TestEnv.UltTemplate)));
        var u = t.Undo(op2);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(ours, t.Power.Active);
        Assert.True(t.Plan(PowerPlan.High).Ok);
        var again = t.Plan(PowerPlan.Ult);
        Assert.True(again.Ok, again.Code);
        Assert.Equal(1, t.Power.DuplicateCount);
        Assert.Equal(ours, t.Power.Active);
    }

    [Fact]
    public void Purge_removes_operation_whose_scheme_was_rolled_back()
    {
        var t = new TestEnv();
        var op1 = t.Plan(PowerPlan.Ult).OperationId!.Value;
        Assert.True(t.Undo(op1).Ok);
        t.Clock.Advance(TimeSpan.FromDays(91));

        var removed = t.Journal.Purge(t.Clock.UtcNow.AddDays(-90));

        Assert.Equal(2, removed); // создание (шаги undone) и откат (шаг удаления схемы не считается)
        Assert.Null(t.Journal.Get(op1));
        Assert.Empty(t.Journal.AllOperations);
    }

    // ---------- 2. схема по имени ----------

    [Fact]
    public void Scheme_with_exactly_our_name_but_no_journal_record_is_not_ours()
    {
        var t = new TestEnv();
        var named = Guid.Parse("11111111-2222-3333-4444-555555555555");
        t.Power.AddScheme(named, "Chistovik Ultimate Performance"); // в журнале записей нет

        var r = t.Plan(PowerPlan.Ult);

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(1, t.Power.DuplicateCount);
        Assert.NotEqual(named, t.Power.Active);
        Assert.Contains(named, t.Power.ListSchemes());
        Assert.Equal(0, t.Power.DeleteCount);
    }

    [Fact]
    public void Setting_in_active_scheme_with_our_name_but_no_journal_record_is_denied()
    {
        var t = new TestEnv();
        var named = Guid.Parse("11111111-2222-3333-4444-555555555555");
        t.Power.AddScheme(named, "Chistovik Ultimate Performance");
        t.Power.Active = named;
        var writes = t.Power.WriteCount;

        var r = t.PowerSetting("min", 50, null);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Equal(writes, t.Power.WriteCount);
    }

    [Fact]
    public void Purge_keeps_only_the_latest_live_creation_of_the_same_scheme()
    {
        var t = new TestEnv();
        var op1 = t.Plan(PowerPlan.Ult).OperationId!.Value;
        t.Power.RemoveScheme(Created(t)); // схему удалили снаружи, журнал о ней помнит
        var op2 = t.Plan(PowerPlan.Ult).OperationId!.Value;
        t.Clock.Advance(TimeSpan.FromDays(91));

        var removed = t.Journal.Purge(t.Clock.UtcNow.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Null(t.Journal.Get(op1));
        Assert.NotNull(t.Journal.Get(op2));
    }

    [Fact]
    public void Recovery_does_not_throw_when_rebuilding_a_step_hits_a_port_error()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", OldCmd);
        t.Journal.Probe = l => { if (l == "AddPending:after") throw new SimulatedCrash(l); };
        Assert.Throws<SimulatedCrash>(() => t.Startup("run:hku:FooTray", false));
        t.Journal.Probe = null;
        t.Restart();
        t.Registry.OnOpen = _ => throw new IOException("port unavailable");

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.Failed);
        Assert.Equal(StepState.Failed, t.Journal.AllSteps.Single().State);
        Assert.Empty(t.Journal.GetPending());
    }

    [Fact]
    public void Undo_rejects_with_internal_when_rebuilding_a_step_hits_a_port_error()
    {
        var (t, op, _) = StartupOff();
        t.Registry.OnOpen = _ => throw new IOException("port unavailable");

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.Internal, u.Code);
    }

    [Fact]
    public void Scheme_with_a_similar_but_not_equal_name_is_not_ours()
    {
        var t = new TestEnv();
        t.Power.AddScheme(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Chistovik Ultimate Performance ");
        Assert.True(t.Plan(PowerPlan.Ult).Ok);
        Assert.Equal(1, t.Power.DuplicateCount);
    }

    // ---------- 3. команда автозагрузки ----------

    static (TestEnv T, Guid Op, long StepId) StartupOff()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", OldCmd);
        var r = t.Startup("run:hku:FooTray", false);
        Assert.True(r.Ok, r.Code + r.Message);
        return (t, r.OperationId!.Value, t.Journal.Get(r.OperationId!.Value)!.Steps.Single().Id);
    }

    static bool Disabled(TestEnv t) => t.Registry.Get(FakeRegistry.HkuPath(TestEnv.Sid, ApprovedRun), "FooTray").Exists;

    [Fact]
    public void Changed_command_in_program_files_is_a_conflict_and_overwrite_reverts()
    {
        var (t, op, stepId) = StartupOff();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", NewCmd);
        var writes = t.Registry.WriteCount;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.Conflict, u.Code);
        Assert.Equal(stepId, Assert.Single(u.Conflicts!).StepId);
        Assert.Equal(writes, t.Registry.WriteCount);

        var ok = t.Undo(op, (stepId, ConflictAction.Overwrite));
        Assert.True(ok.Ok, ok.Code + ok.Message);
        Assert.Equal(OperationState.Undone, t.Journal.Get(op)!.Summary.State);
    }

    [Fact]
    public void Changed_command_with_skip_leaves_the_state_alone()
    {
        var (t, op, stepId) = StartupOff();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", NewCmd);
        var writes = t.Registry.WriteCount;

        var u = t.Undo(op, (stepId, ConflictAction.Skip));

        Assert.Equal(writes, t.Registry.WriteCount);
        Assert.True(Disabled(t));
        Assert.Equal(StepState.Skipped, t.Journal.Get(u.OperationId!.Value)!.Steps.Single().State);
    }

    [Fact]
    public void Changed_command_pointing_into_windows_stays_denied_even_with_overwrite()
    {
        var (t, op, stepId) = StartupOff();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", "C:\\Windows\\System32\\evil.exe");
        var writes = t.Registry.WriteCount;

        var plain = t.Undo(op);
        var forced = t.Undo(op, (stepId, ConflictAction.Overwrite));

        foreach (var u in new[] { plain, forced })
        {
            Assert.False(u.Ok);
            Assert.Equal(ResponseCodes.PolicyDenied, u.Code);
        }
        Assert.Equal(writes, t.Registry.WriteCount);
        Assert.True(Disabled(t));
    }

    // ---------- 4. задача от SYSTEM через весь стек ----------

    [Fact]
    public void System_task_can_be_disabled_directly_but_enabling_is_denied_until_it_is_a_revert()
    {
        var t = new TestEnv();
        t.Tasks.Add(new TaskRaw(@"\Foo\Sys", true, "<Task/>", "SYSTEM", "LeastPrivilege", "ServiceAccount",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)], "S-1-5-18"));
        t.Tasks.Add(new TaskRaw(@"\Foo\SysOff", false, "<Task/>", "SYSTEM", "LeastPrivilege", "ServiceAccount",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)], "S-1-5-18"));

        var off = t.Task(@"\Foo\Sys", false);
        Assert.True(off.Ok, off.Code + off.Message);
        Assert.False(t.Tasks.Get(@"\Foo\Sys").Enabled);

        var on = t.Task(@"\Foo\SysOff", true);
        Assert.False(on.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, on.Code);
        Assert.False(t.Tasks.Get(@"\Foo\SysOff").Enabled);

        var undo = t.Undo(off.OperationId!.Value); // включение откатом собственного шага разрешено
        Assert.True(undo.Ok, undo.Code + undo.Message);
        Assert.True(t.Tasks.Get(@"\Foo\Sys").Enabled);
    }

    // ---------- 5. непредвиденное исключение порта ----------

    [Fact]
    public void Unexpected_port_exception_in_planner_fails_the_operation_and_repeat_returns_it_not_busy()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "FooTray", OldCmd);
        t.Registry.OnOpen = _ => throw new InvalidOperationException("порт сломан");
        var req = new SetStartupEntryRequest { Id = t.NextRequestId(), EntryId = "run:hku:FooTray", Enabled = false };

        var first = t.Run(req);
        var second = t.Run(req);

        Assert.False(first.Ok);
        Assert.Equal(ResponseCodes.Internal, first.Code);
        Assert.Equal(ResponseCodes.Internal, second.Code);
        Assert.Equal(first.OperationId, second.OperationId);
        var op = Assert.Single(t.Journal.AllOperations);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(ResponseCodes.Internal, op.Code);
    }

    [Fact]
    public void Repeated_request_id_from_another_user_gets_unknown_operation()
    {
        var t = new TestEnv();
        var req = new SetPowerSchemeRequest { Id = t.NextRequestId(), Plan = PowerPlan.High };
        Assert.True(t.Run(req).Ok);

        t.Client = new ClientContext(TestEnv.OtherSid, 4321, 1);
        var other = t.Run(req);

        Assert.False(other.Ok);
        Assert.Equal(ResponseCodes.UnknownOperation, other.Code);
        Assert.Null(other.Steps);
    }

    // ---------- 7. Recovery: плохая запись не отключает агент ----------

    [Fact]
    public void Recovery_closes_undo_step_as_failed_when_original_is_no_longer_done()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;

        void CrashUndo()
        {
            t.Journal.Probe = l => { if (l == "CompleteRevert:before") throw new SimulatedCrash(l); };
            Assert.Throws<SimulatedCrash>(() => t.Undo(op));
            t.Journal.Probe = null;
        }

        CrashUndo(); // запись отката сделана, шаг отмены pending
        t.SetTweakDword("ext", 0); // снова «после», чтобы второй откат тоже дошёл до записи
        CrashUndo();
        t.Restart();
        Assert.Equal(2, t.Journal.GetPending().Count);

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Equal(1, rep.Failed);
        Assert.Empty(t.Journal.GetPending());
        Assert.Contains(t.Journal.AllSteps, s => s.State == StepState.Failed && s.Code == StepCodes.ExternalChange);
        Assert.Equal(StepState.Undone, t.Journal.Get(op)!.Steps.Single().State);
    }
}
