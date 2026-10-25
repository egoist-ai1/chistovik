using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Откат: конфликты и решения, уже возвращённое, чужое, подмена журнала (DESIGN-0.1 §4, §7).</summary>
public class UndoTests
{
    static int TotalWrites(TestEnv t) => t.Registry.WriteCount + t.Services.WriteCount + t.Tasks.WriteCount + t.Power.WriteCount;

    [Fact]
    public void External_change_without_resolution_gives_conflict_and_writes_nothing()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.SetTweakDword("ext", 9);
        var writes = TotalWrites(t);
        var opsBefore = t.Journal.AllOperations.Count;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.Conflict, u.Code);
        var c = Assert.Single(u.Conflicts!);
        Assert.Equal(t.Journal.Get(op)!.Steps.Single().Id, c.StepId);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(opsBefore, t.Journal.AllOperations.Count);
        Assert.Equal(TestEnv.Dword(9), t.TweakValue("ext"));
    }

    [Fact]
    public void Conflict_with_overwrite_restores_before_value()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.SetTweakDword("ext", 9);
        var stepId = t.Journal.Get(op)!.Steps.Single().Id;

        var u = t.Undo(op, (stepId, ConflictAction.Overwrite));

        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
        Assert.Equal(StepState.Undone, t.Journal.Get(op)!.Steps.Single().State);
    }

    [Fact]
    public void Conflict_with_skip_leaves_the_value_and_the_step_done()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.SetTweakDword("ext", 9);
        var stepId = t.Journal.Get(op)!.Steps.Single().Id;
        var writes = TotalWrites(t);

        var u = t.Undo(op, (stepId, ConflictAction.Skip));

        Assert.True(u.Ok, u.Code);
        Assert.Equal(ResponseCodes.NoChange, u.Code);
        Assert.Equal(TestEnv.Dword(9), t.TweakValue("ext"));
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(StepState.Done, t.Journal.Get(op)!.Steps.Single().State);
    }

    [Fact]
    public void Conflict_in_one_step_of_two_requires_resolution_for_that_step_only()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.SetTweakDword("hidden", 2);
        var op = t.Tweaks(("ext", true), ("hidden", true)).OperationId!.Value;
        t.SetTweakDword("hidden", 9);
        var steps = t.Journal.Get(op)!.Steps;

        var first = t.Undo(op);
        Assert.Equal(ResponseCodes.Conflict, first.Code);
        Assert.Equal(steps[1].Id, Assert.Single(first.Conflicts!).StepId);

        var u = t.Undo(op, (steps[1].Id, ConflictAction.Skip));
        Assert.True(u.Ok, u.Code);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
        Assert.Equal(TestEnv.Dword(9), t.TweakValue("hidden"));
        Assert.Equal(OperationState.PartiallyUndone, t.Journal.Get(op)!.Summary.State);
    }

    [Fact]
    public void Already_reverted_value_is_closed_without_a_write()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.SetTweakDword("ext", 1);
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.True(u.Ok, u.Code);
        Assert.Equal(writes, TotalWrites(t));
        var undoStep = Assert.Single(t.Journal.Get(u.OperationId!.Value)!.Steps);
        Assert.Equal(StepState.Done, undoStep.State);
        Assert.Equal(StepCodes.AlreadyReverted, undoStep.Code);
        Assert.Equal(StepState.Undone, t.Journal.Get(op)!.Steps.Single().State);
    }

    [Fact]
    public void Second_undo_of_the_same_operation_is_a_no_change_without_writes()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        Assert.True(t.Undo(op).Ok);
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.True(u.Ok);
        Assert.Equal(ResponseCodes.NoChange, u.Code);
        Assert.Equal(writes, TotalWrites(t));
    }

    [Fact]
    public void Undo_of_an_undo_is_rejected()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        var undo = t.Undo(op).OperationId!.Value;
        var writes = TotalWrites(t);

        var u = t.Undo(undo);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.BadRequest, u.Code);
        Assert.Equal(writes, TotalWrites(t));
    }

    [Fact]
    public void Unknown_operation_is_unknown()
    {
        var t = new TestEnv();
        var u = t.Undo(Guid.NewGuid());
        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.UnknownOperation, u.Code);
    }

    [Fact]
    public void Operation_of_another_user_looks_unknown()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.Client = new ClientContext(TestEnv.OtherSid, 4321, 2);
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.UnknownOperation, u.Code);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(StepState.Done, t.Journal.Get(op)!.Steps.Single().State);
    }

    [Fact]
    public void Resolution_for_a_foreign_step_id_is_bad_request()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        t.SetTweakDword("hidden", 3);
        var other = t.Tweaks(("hidden", true)).OperationId!.Value;
        var foreign = t.Journal.Get(other)!.Steps.Single().Id;
        var writes = TotalWrites(t);

        var u = t.Undo(op, (foreign, ConflictAction.Overwrite));

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.BadRequest, u.Code);
        Assert.Equal(writes, TotalWrites(t));
    }

    [Fact]
    public void Undo_target_that_disappeared_is_skipped_as_not_found()
    {
        var t = new TestEnv();
        t.AddUserTask(@"\Foo\Update");
        var op = t.Task(@"\Foo\Update", false).OperationId!.Value;
        t.Tasks.Remove(@"\Foo\Update");

        var u = t.Undo(op);

        Assert.True(u.Ok, u.Code + u.Message);
        var s = Assert.Single(t.Journal.Get(u.OperationId!.Value)!.Steps);
        Assert.Equal(StepState.Skipped, s.State);
        Assert.Equal(StepCodes.NotFound, s.Code);
    }

    [Fact]
    public void Failure_in_the_middle_of_undo_gives_partial_and_partially_undone()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.SetTweakDword("hidden", 2);
        var op = t.Tweaks(("ext", true), ("hidden", true)).OperationId!.Value;
        t.Registry.FailOnWrite(t.Registry.WriteCount + 2);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.Partial, u.Code);
        var steps = t.Journal.Get(op)!.Steps;
        Assert.Equal([StepState.Done, StepState.Undone], steps.Select(s => s.State));
        Assert.Equal(OperationState.PartiallyUndone, t.Journal.Get(op)!.Summary.State);
    }

    // ---------- журнал не доверенный ----------

    [Fact]
    public void Tampered_target_json_is_rejected_and_nothing_is_written()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        var step = t.Journal.Get(op)!.Steps.Single();
        t.Journal.Tamper(step.Id, targetJson: step.TargetJson.Replace("ext", "evil", StringComparison.Ordinal)
            .Replace("Explorer", "Windows Defender", StringComparison.Ordinal));
        var writes = TotalWrites(t);
        var opens = t.Registry.OpenCount;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, u.Code);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(StepState.Done, t.Journal.Get(op)!.Steps.Single().State);
        Assert.DoesNotContain(t.Journal.AllOperations, o => o.Kind == OperationKind.Undo);
        Assert.True(opens >= 0);
    }

    [Fact]
    public void Tampered_target_key_pointing_to_a_protected_service_is_rejected()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", 2);
        t.AddThirdPartyService("WinDefend", 2);
        var op = t.Service("FooSvc", StartMode.Disabled).OperationId!.Value;
        var step = t.Journal.Get(op)!.Steps.Single();
        t.Journal.Tamper(step.Id, targetKey: step.TargetKey.Replace("FooSvc", "WinDefend", StringComparison.Ordinal),
            targetJson: step.TargetJson.Replace("FooSvc", "WinDefend", StringComparison.Ordinal));
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, u.Code);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(2u, t.Services.Get("WinDefend").StartType);
        Assert.Equal(4u, t.Services.Get("FooSvc").StartType);
    }

    [Fact]
    public void Tampered_before_value_outside_the_catalog_is_rejected()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var op = t.Tweaks(("ext", true)).OperationId!.Value;
        var step = t.Journal.Get(op)!.Steps.Single();
        t.Journal.Tamper(step.Id, beforeJson: "{\"junk\":true}");
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, u.Code);
        Assert.Equal(writes, TotalWrites(t));
    }

    [Fact]
    public void Tampered_target_json_for_power_scheme_is_rejected_before_any_write()
    {
        var t = new TestEnv();
        var op = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var created = t.Journal.Get(op)!.Steps[0];
        t.Journal.Tamper(created.Id, targetJson: created.TargetJson.Replace(created.TargetJson, "{}", StringComparison.Ordinal));
        var writes = TotalWrites(t);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, u.Code);
        Assert.Equal(writes, TotalWrites(t));
        Assert.Equal(0, t.Power.DeleteCount);
    }

    // ---------- чистка диска и реестра (C2/C3) ----------

    const string Cv = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion";
    const string CleanTemp = @"C:\Users\Test\AppData\Local\Temp";

    static TestEnv RegEnv(out string key)
    {
        var t = new TestEnv();
        t.Security.Trust(@"C:\");
        key = Cv + @"\Uninstall\Foo";
        t.Registry.SetString(key, "DisplayName", "Foo App");
        t.Registry.SetString(key, "UninstallString", "\"C:\\Program Files\\Foo\\unins000.exe\" /S");
        t.Registry.SetString(key + @"\S0", "v", "1");
        t.Registry.SetString(Cv + @"\Run", "Foo", "\"C:\\Gone\\foo.exe\"");
        t.Registry.SetDword(Cv + @"\SharedDLLs", @"C:\Gone\a.dll", 3);
        return t;
    }

    static readonly string[] Findings =
    [
        "arp:machine:x64:Foo", "run:hklm64:Foo", "shareddll:x64:" + OrphanId.SharedDllHash(@"C:\Gone\a.dll"),
    ];

    [Fact]
    public void Registry_operation_is_reverted_whole()
    {
        var t = RegEnv(out var key);
        var op = t.Findings(Findings).OperationId!.Value;
        Assert.False(t.Registry.KeyExists(key));
        Assert.False(t.Registry.Get(Cv + @"\Run", "Foo").Exists);
        Assert.False(t.Registry.Get(Cv + @"\SharedDLLs", @"C:\Gone\a.dll").Exists);

        var u = t.Undo(op);

        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal("Foo App", t.Registry.Get(key, "DisplayName").Data);
        Assert.True(t.Registry.KeyExists(key + @"\S0"));
        Assert.Equal("\"C:\\Gone\\foo.exe\"", t.Registry.Get(Cv + @"\Run", "Foo").Data);
        Assert.Equal(3u, t.Registry.Get(Cv + @"\SharedDLLs", @"C:\Gone\a.dll").Data);
        Assert.All(t.Journal.Get(op)!.Steps, s => Assert.Equal(StepState.Undone, s.State));
        Assert.Equal(0, t.RegistryTree.OpenHandles);
    }

    [Fact]
    public void Registry_operation_second_undo_changes_nothing()
    {
        var t = RegEnv(out _);
        var op = t.Findings(Findings).OperationId!.Value;
        Assert.True(t.Undo(op).Ok);
        var created = t.RegistryTree.Created;
        var sets = t.RegistryTree.SetRawCount;
        var writes = t.Registry.WriteCount;

        var u = t.Undo(op);

        Assert.True(u.Ok, u.Code);
        Assert.Equal(ResponseCodes.NoChange, u.Code);
        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(sets, t.RegistryTree.SetRawCount);
        Assert.Equal(writes, t.Registry.WriteCount);
    }

    [Fact]
    public void Recreated_key_with_other_content_is_a_conflict_and_nothing_is_written()
    {
        var t = RegEnv(out var key);
        var op = t.Findings("arp:machine:x64:Foo").OperationId!.Value;
        t.Registry.SetString(key, "DisplayName", "Reinstalled");
        var created = t.RegistryTree.Created;
        var sets = t.RegistryTree.SetRawCount;
        var writes = t.Registry.WriteCount;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.Conflict, u.Code);
        var c = Assert.Single(u.Conflicts!);
        Assert.Equal(t.Journal.Get(op)!.Steps.Single().Id, c.StepId);
        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(sets, t.RegistryTree.SetRawCount);
        Assert.Equal(writes, t.Registry.WriteCount);
        Assert.Equal("Reinstalled", t.Registry.Get(key, "DisplayName").Data);
    }

    [Fact]
    public void Key_conflict_with_skip_writes_nothing_and_keeps_the_new_key()
    {
        var t = RegEnv(out var key);
        var op = t.Findings("arp:machine:x64:Foo").OperationId!.Value;
        t.Registry.SetString(key, "DisplayName", "Reinstalled");
        var stepId = t.Journal.Get(op)!.Steps.Single().Id;
        var created = t.RegistryTree.Created;
        var sets = t.RegistryTree.SetRawCount;

        var u = t.Undo(op, (stepId, ConflictAction.Skip));

        Assert.True(u.Ok, u.Code);
        Assert.Equal(ResponseCodes.NoChange, u.Code);
        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(sets, t.RegistryTree.SetRawCount);
        Assert.Equal("Reinstalled", t.Registry.Get(key, "DisplayName").Data);
    }

    [Fact]
    public void Key_conflict_with_overwrite_is_a_bad_request_and_writes_nothing()
    {
        var t = RegEnv(out var key);
        var op = t.Findings("arp:machine:x64:Foo").OperationId!.Value;
        t.Registry.SetString(key, "DisplayName", "Reinstalled");
        var stepId = t.Journal.Get(op)!.Steps.Single().Id;
        var created = t.RegistryTree.Created;
        var sets = t.RegistryTree.SetRawCount;
        var deleted = t.RegistryTree.Deleted;
        var opsBefore = t.Journal.AllOperations.Count;

        var u = t.Undo(op, (stepId, ConflictAction.Overwrite));

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.BadRequest, u.Code);
        Assert.Equal(created, t.RegistryTree.Created);
        Assert.Equal(sets, t.RegistryTree.SetRawCount);
        Assert.Equal(deleted, t.RegistryTree.Deleted);
        Assert.Equal(opsBefore, t.Journal.AllOperations.Count);
        Assert.Equal("Reinstalled", t.Registry.Get(key, "DisplayName").Data);
    }

    [Fact]
    public void Disk_cleanup_operation_is_not_revertible()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(CleanTemp + @"\old.tmp", ageHours: 100, length: 1000);
        var op = t.Clean("temp-user").OperationId!.Value;
        var opsBefore = t.Journal.AllOperations.Count;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.NotRevertible, u.Code);
        Assert.Equal(opsBefore, t.Journal.AllOperations.Count);
    }

    [Fact]
    public void Recycle_bin_operation_is_not_revertible()
    {
        var t = new TestEnv();
        t.RecycleBin.Bytes = 500;
        var op = t.Clean("recycle-bin").OperationId!.Value;
        var emptied = t.RecycleBin.EmptyCalls;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.NotRevertible, u.Code);
        Assert.Equal(emptied, t.RecycleBin.EmptyCalls);
    }
}
