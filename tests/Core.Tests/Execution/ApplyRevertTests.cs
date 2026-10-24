using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Применение и откат каждого вида шага на подставных портах (DESIGN-0.1 §4).</summary>
public class ApplyRevertTests
{
    static IReadOnlyList<StepRecord> Steps(TestEnv t, AgentResponse r) => t.Journal.Get(r.OperationId!.Value)!.Steps;

    static OperationState State(TestEnv t, AgentResponse r) => t.Journal.Get(r.OperationId!.Value)!.Summary.State;

    static void AssertUndone(TestEnv t, Guid op)
    {
        var v = t.Journal.Get(op)!;
        Assert.Equal(OperationState.Undone, v.Summary.State);
        Assert.All(v.Steps, s => Assert.Equal(StepState.Undone, s.State));
    }

    // ---------- реестр ----------

    [Fact]
    public void Registry_existing_value_is_changed_and_restored()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var r = t.Tweaks(("ext", true));

        Assert.True(r.Ok);
        Assert.Equal(ResponseCodes.Ok, r.Code);
        Assert.Equal(TestEnv.Dword(0), t.TweakValue("ext"));
        var step = Assert.Single(Steps(t, r));
        Assert.Equal(StepState.Done, step.State);
        Assert.Equal(StepKind.RegistryValue, step.Kind);
        Assert.Equal(1, t.Registry.WriteCount);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
        AssertUndone(t, r.OperationId.Value);
        var undoStep = Assert.Single(Steps(t, u));
        Assert.Equal(StepState.Done, undoStep.State);
        Assert.Equal(step.Id, undoStep.RevertsStepId);
        Assert.Equal(r.OperationId, t.Journal.Get(u.OperationId!.Value)!.RevertsOpId);
    }

    [Fact]
    public void Registry_absent_value_is_created_and_deleted_by_undo()
    {
        var t = new TestEnv();
        var r = t.Tweaks(("hidden", true));
        Assert.True(r.Ok);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("hidden"));

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code);
        Assert.False(t.TweakValue("hidden").Exists);
    }

    [Fact]
    public void Registry_value_is_deleted_when_off_value_is_null_and_restored_by_undo()
    {
        var t = new TestEnv();
        t.SetTweakDword("bing", 1);
        var r = t.Tweaks(("bing", false));
        Assert.True(r.Ok);
        Assert.False(t.TweakValue("bing").Exists);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("bing"));
    }

    [Fact]
    public void Registry_machine_string_tweak_round_trip()
    {
        var t = new TestEnv();
        t.SetTweakValue("killsvc", TestEnv.Str("5000"));
        var r = t.Tweaks(("killsvc", true));
        Assert.True(r.Ok);
        Assert.Equal(TestEnv.Str("2000"), t.TweakValue("killsvc"));

        t.Undo(r.OperationId!.Value);
        Assert.Equal(TestEnv.Str("5000"), t.TweakValue("killsvc"));
    }

    [Fact]
    public void Registry_steps_run_in_request_order_and_undo_in_reverse()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.SetTweakDword("hidden", 2);
        var r = t.Tweaks(("ext", true), ("hidden", true));
        Assert.Equal(["ext", "hidden"], t.Registry.Writes.Select(w => w.Name).Select(n => n == t.Loc("ext").ValueName ? "ext" : "hidden"));

        var u = t.Undo(r.OperationId!.Value);
        var undoSteps = Steps(t, u);
        Assert.Equal(2, undoSteps.Count);
        Assert.Equal(t.Journal.Get(r.OperationId.Value)!.Steps[1].Id, undoSteps[0].RevertsStepId);
        Assert.Equal(t.Journal.Get(r.OperationId.Value)!.Steps[0].Id, undoSteps[1].RevertsStepId);
    }

    [Fact]
    public void Registry_value_already_equal_is_skipped_as_no_change()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 0);
        var r = t.Tweaks(("ext", true));
        Assert.True(r.Ok);
        Assert.Equal(ResponseCodes.NoChange, r.Code);
        var s = Assert.Single(Steps(t, r));
        Assert.Equal(StepState.Skipped, s.State);
        Assert.Equal(StepCodes.NoChange, s.Code);
        Assert.Equal(0, t.Registry.WriteCount);
        Assert.Empty(t.Journal.GetPending());
    }

    [Fact]
    public void Registry_write_that_changes_nothing_is_failed_with_access_denied()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.Registry.DropWrites = true;
        var r = t.Tweaks(("ext", true));
        Assert.False(r.Ok);
        var s = Assert.Single(Steps(t, r));
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.AccessDenied, s.Code);
        Assert.Equal(OperationState.Failed, State(t, r));
    }

    [Fact]
    public void Registry_write_error_without_effect_is_failed_with_the_error_code()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.Registry.FailOnWrite(1);
        var r = t.Tweaks(("ext", true));
        Assert.False(r.Ok);
        var s = Assert.Single(Steps(t, r));
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.AccessDenied, s.Code);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
    }

    [Fact]
    public void Registry_write_error_after_the_value_changed_counts_as_done()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.Registry.FailOnWrite(1, new IOException("сбой после записи"), applyFirst: true);
        var r = t.Tweaks(("ext", true));
        Assert.True(r.Ok);
        Assert.Equal(StepState.Done, Assert.Single(Steps(t, r)).State);
    }

    [Fact]
    public void Second_step_failure_stops_the_operation_as_partial_and_does_not_roll_back()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        t.SetTweakDword("hidden", 2);
        t.SetTweakDword("thispc", 2);
        t.Registry.FailOnWrite(2);
        var r = t.Tweaks(("ext", true), ("hidden", true), ("thispc", true));

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.Partial, r.Code);
        var steps = Steps(t, r);
        Assert.Equal([StepState.Done, StepState.Failed], steps.Select(s => s.State));
        Assert.Equal(2, steps.Count);
        Assert.Equal(OperationState.Partial, State(t, r));
        Assert.Equal(TestEnv.Dword(0), t.TweakValue("ext"));
        Assert.Equal(TestEnv.Dword(2), t.TweakValue("hidden"));
        Assert.Equal(TestEnv.Dword(2), t.TweakValue("thispc"));
        Assert.Equal(2, t.Registry.WriteCount);
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    [Fact]
    public void Handles_are_released_after_every_operation()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var r = t.Tweaks(("ext", true), ("hidden", true));
        t.Undo(r.OperationId!.Value);
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    // ---------- автозагрузка ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static string HkuRun => FakeRegistry.HkuPath(TestEnv.Sid, RunKey);
    static string HkuApproved => FakeRegistry.HkuPath(TestEnv.Sid, ApprovedRun);

    static byte[] Flag(byte first)
    {
        var b = new byte[12];
        b[0] = first;
        return b;
    }

    [Fact]
    public void Startup_hku_run_entry_is_disabled_and_restored()
    {
        var t = new TestEnv();
        t.Registry.SetString(HkuRun, "FooTray", "\"C:\\Program Files\\Foo\\foo.exe\" --tray");
        t.Trust.SetSigned(@"C:\Program Files\Foo\foo.exe");

        var r = t.Startup("run:hku:FooTray", false);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(Flag(3), (byte[]?)t.Registry.Get(HkuApproved, "FooTray").Data);
        var step = Assert.Single(Steps(t, r));
        Assert.Equal(StepKind.StartupApproved, step.Kind);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.False(t.Registry.Get(HkuApproved, "FooTray").Exists);
    }

    [Fact]
    public void Startup_disabled_entry_is_enabled_and_original_bytes_are_restored()
    {
        var t = new TestEnv();
        t.Registry.SetString(HkuRun, "FooTray", "\"C:\\Program Files\\Foo\\foo.exe\"");
        var original = new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        t.Registry.SetBinary(HkuApproved, "FooTray", original);

        var r = t.Startup("run:hku:FooTray", true);
        Assert.True(r.Ok, r.Code);
        Assert.Equal(Flag(2), (byte[]?)t.Registry.Get(HkuApproved, "FooTray").Data);

        t.Undo(r.OperationId!.Value);
        Assert.Equal(original, (byte[]?)t.Registry.Get(HkuApproved, "FooTray").Data);
    }

    [Fact]
    public void Startup_enable_of_an_entry_without_a_flag_is_no_change()
    {
        var t = new TestEnv();
        t.Registry.SetString(HkuRun, "FooTray", "\"C:\\Program Files\\Foo\\foo.exe\"");
        var r = t.Startup("run:hku:FooTray", true);
        Assert.Equal(ResponseCodes.NoChange, r.Code);
        Assert.Equal(0, t.Registry.WriteCount);
    }

    [Fact]
    public void Startup_hklm_entries_use_their_own_approved_keys()
    {
        var t = new TestEnv();
        t.Registry.SetString(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Tool64", "\"C:\\Program Files\\Foo\\a.exe\"");
        t.Registry.SetString(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Tool32", "\"C:\\Program Files (x86)\\Foo\\b.exe\"");

        Assert.True(t.Startup("run:hklm64:Tool64", false).Ok);
        Assert.True(t.Startup("run:hklm32:Tool32", false).Ok);

        Assert.Equal(Flag(3), (byte[]?)t.Registry.Get(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Tool64").Data);
        Assert.Equal(Flag(3), (byte[]?)t.Registry.Get(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "Tool32").Data);
    }

    [Fact]
    public void Startup_folder_entries_user_and_common()
    {
        var t = new TestEnv();
        var user = @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
        var common = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
        t.Files.Add(user + @"\desktop.ini");
        t.Files.Add(user + @"\Foo.lnk");
        t.Files.Add(common + @"\Bar.lnk");

        var r1 = t.Startup("folder:user:Foo.lnk", false);
        Assert.True(r1.Ok, r1.Code + r1.Message);
        Assert.Equal(Flag(3), (byte[]?)t.Registry.Get(
            FakeRegistry.HkuPath(TestEnv.Sid, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"), "Foo.lnk").Data);

        var r2 = t.Startup("folder:common:Bar.lnk", false);
        Assert.True(r2.Ok, r2.Code + r2.Message);
        Assert.Equal(Flag(3), (byte[]?)t.Registry.Get(
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "Bar.lnk").Data);
    }

    // ---------- службы ----------

    [Fact]
    public void Service_start_type_is_changed_and_restored_including_delayed()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", start: 2, delayed: true);

        var r = t.Service("FooSvc", StartMode.Disabled);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(4u, t.Services.Get("FooSvc").StartType);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(2u, t.Services.Get("FooSvc").StartType);
        Assert.True(t.Services.Get("FooSvc").Delayed);
        Assert.Equal(0, t.Services.OpenHandles);
    }

    [Fact]
    public void Service_same_start_is_no_change()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", start: 3);
        var r = t.Service("FooSvc", StartMode.Manual);
        Assert.Equal(ResponseCodes.NoChange, r.Code);
        Assert.Equal(0, t.Services.WriteCount);
    }

    [Fact]
    public void Service_elevation_inside_program_files_is_allowed_directly()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", start: 4);
        var r = t.Service("FooSvc", StartMode.Manual);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(3u, t.Services.Get("FooSvc").StartType);
    }

    // ---------- задачи ----------

    [Fact]
    public void Task_is_disabled_and_restored()
    {
        var t = new TestEnv();
        t.AddUserTask(@"\Foo\Update");
        var r = t.Task(@"\Foo\Update", false);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.False(t.Tasks.Get(@"\Foo\Update").Enabled);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.True(t.Tasks.Get(@"\Foo\Update").Enabled);
        Assert.Equal(0, t.Tasks.OpenHandles);
    }

    [Fact]
    public void Task_same_state_is_no_change()
    {
        var t = new TestEnv();
        t.AddUserTask(@"\Foo\Update", enabled: false);
        Assert.Equal(ResponseCodes.NoChange, t.Task(@"\Foo\Update", false).Code);
        Assert.Equal(0, t.Tasks.WriteCount);
    }

    // ---------- питание ----------

    [Fact]
    public void Power_scheme_builtin_is_activated_and_previous_restored()
    {
        var t = new TestEnv();
        var r = t.Plan(PowerPlan.High);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(TestEnv.High, t.Power.Active);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
    }

    [Fact]
    public void Power_scheme_already_active_is_no_change()
    {
        var t = new TestEnv();
        Assert.Equal(ResponseCodes.NoChange, t.Plan(PowerPlan.Bal).Code);
        Assert.Equal(0, t.Power.WriteCount);
    }

    [Fact]
    public void Power_scheme_missing_builtin_is_skipped_not_denied()
    {
        var t2 = new TestEnv();
        // Схемы «Экономия энергии» нет на устройстве (Modern Standby): шаг пропускается.
        t2.Power.RemoveScheme(TestEnv.Saver);
        var r = t2.Plan(PowerPlan.Saver);
        Assert.True(r.Ok, r.Code + r.Message);
        var s = Assert.Single(Steps(t2, r));
        Assert.Equal(StepState.Skipped, s.State);
        Assert.Equal(StepCodes.NotFound, s.Code);
        Assert.Equal(TestEnv.Balanced, t2.Power.Active);
    }

    [Fact]
    public void Power_ultimate_creates_our_copy_activates_it_and_undo_deletes_it()
    {
        var t = new TestEnv();
        var r = t.Plan(PowerPlan.Ult);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(1, t.Power.DuplicateCount);
        var created = Assert.Single(t.Power.ListSchemes().Except([TestEnv.Saver, TestEnv.Balanced, TestEnv.High]));
        Assert.Equal(created, t.Power.Active);
        Assert.StartsWith("Chistovik ", t.Power.Names[created]);
        Assert.Equal([StepKind.PowerSchemeCreate, StepKind.PowerActiveScheme], Steps(t, r).Select(s => s.Kind));

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
        Assert.DoesNotContain(created, t.Power.ListSchemes());
        Assert.Equal(1, t.Power.DeleteCount);
    }

    [Fact]
    public void Power_ultimate_second_time_reuses_our_existing_copy()
    {
        var t = new TestEnv();
        Assert.True(t.Plan(PowerPlan.Ult).Ok);
        Assert.True(t.Plan(PowerPlan.High).Ok);
        var r = t.Plan(PowerPlan.Ult);
        Assert.True(r.Ok, r.Code);
        Assert.Equal(1, t.Power.DuplicateCount);
        Assert.Equal([StepKind.PowerActiveScheme], Steps(t, r).Select(s => s.Kind));
    }

    [Fact]
    public void Power_setting_changes_one_side_and_undo_restores()
    {
        var t = new TestEnv();
        var min = Chistovik.Core.Catalog.PowerCatalog.Shared.FindSetting("min")!;
        t.Power.SetValue(TestEnv.Balanced, min.Subgroup, min.Setting, 5, 5);

        var r = t.PowerSetting("min", 50, null);
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal((50u, 5u), t.Power.Value(TestEnv.Balanced, min.Subgroup, min.Setting));

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal((5u, 5u), t.Power.Value(TestEnv.Balanced, min.Subgroup, min.Setting));
    }

    [Fact]
    public void Power_setting_in_a_foreign_scheme_is_denied()
    {
        var t = new TestEnv();
        var foreign = Guid.Parse("11111111-2222-3333-4444-555555555555");
        t.Power.AddScheme(foreign, "Моя схема");
        t.Power.Active = foreign;
        var r = t.PowerSetting("min", 50, 50);
        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Equal(0, t.Power.WriteCount);
    }

    // ---------- режим ----------

    [Fact]
    public void Mode_applies_tweaks_and_power_and_is_undone_as_one()
    {
        var t = new TestEnv();
        foreach (var id in new[] { "gamemode", "gamedvr", "appcapture", "transp", "taskanim" }) t.SetTweakDword(id, 7);
        foreach (var id in new[] { "mouseacc", "minanim" }) t.SetTweakValue(id, TestEnv.Str("9"));

        var r = t.Mode("game");
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(TestEnv.High, t.Power.Active);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("gamemode"));
        Assert.Equal(TestEnv.Str("0"), t.TweakValue("mouseacc"));
        var ops = t.Journal.AllOperations;
        Assert.Single(ops);
        Assert.Equal(OperationKind.Mode, ops[0].Kind);
        Assert.Equal(8, Steps(t, r).Count);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
        Assert.Equal(TestEnv.Dword(7), t.TweakValue("gamemode"));
        Assert.Equal(TestEnv.Str("9"), t.TweakValue("minanim"));
        AssertUndone(t, r.OperationId.Value);
    }

    [Fact]
    public void Mode_with_a_created_power_scheme_undoes_in_reverse_order()
    {
        var t = new TestEnv();
        var r = t.Mode("max");
        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(1, t.Power.DuplicateCount);

        var u = t.Undo(r.OperationId!.Value);
        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
        Assert.Equal(1, t.Power.DeleteCount);
        Assert.Equal(3, t.Power.ListSchemes().Count);
    }
}
