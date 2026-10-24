using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Повтор запроса по request_id и по состоянию; сравнение состояний каждого вида шага.</summary>
public class IdempotencyAndComparerTests
{
    // ---------- повтор запроса ----------

    [Fact]
    public void Same_request_id_returns_the_stored_result_without_a_second_run()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var req = new ApplyTweaksRequest { Id = t.NextRequestId(), Changes = [new TweakChange { TweakId = "ext", Enabled = true }] };

        var first = t.Run(req);
        t.SetTweakDword("ext", 1);
        var writes = t.Registry.WriteCount;
        var second = t.Run(req);

        Assert.True(first.Ok);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(writes, t.Registry.WriteCount);
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
        Assert.Single(t.Journal.AllOperations);
    }

    [Fact]
    public void Same_request_id_after_restart_returns_the_stored_result()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var req = new ApplyTweaksRequest { Id = t.NextRequestId(), Changes = [new TweakChange { TweakId = "ext", Enabled = true }] };
        var first = t.Run(req);
        t.Restart();
        var second = t.Run(req);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(1, t.Registry.WriteCount);
    }

    [Fact]
    public void Same_request_id_of_a_denied_request_stays_denied()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("WinDefend", 2);
        var req = new SetServiceStartRequest { Id = t.NextRequestId(), ServiceName = "WinDefend", Start = StartMode.Disabled };

        var first = t.Run(req);
        var second = t.Run(req);

        Assert.False(first.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, first.Code);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(0, t.Services.WriteCount);
    }

    [Fact]
    public void Same_request_id_after_a_crash_does_not_apply_twice()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var req = new ApplyTweaksRequest { Id = t.NextRequestId(), Changes = [new TweakChange { TweakId = "ext", Enabled = true }] };
        t.Journal.Probe = l =>
        {
            if (l == "Complete:before") throw new SimulatedCrash(l);
        };
        Assert.Throws<SimulatedCrash>(() => t.Run(req));
        t.Journal.Probe = null;
        t.Restart();
        new RecoveryService(t.Env).Run();
        var writes = t.Registry.WriteCount;

        var again = t.Run(req);

        Assert.Equal(writes, t.Registry.WriteCount);
        Assert.Single(t.Journal.AllOperations);
        Assert.Equal(t.Journal.AllOperations[0].Id, again.OperationId);
    }

    [Fact]
    public void New_request_id_with_the_same_change_is_no_change_by_state()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        Assert.True(t.Tweaks(("ext", true)).Ok);
        var writes = t.Registry.WriteCount;

        var r = t.Tweaks(("ext", true));

        Assert.True(r.Ok);
        Assert.Equal(ResponseCodes.NoChange, r.Code);
        Assert.Equal(writes, t.Registry.WriteCount);
    }

    [Fact]
    public void Mode_replayed_by_state_is_no_change()
    {
        var t = new TestEnv();
        Assert.True(t.Mode("game").Ok);
        var writes = t.Registry.WriteCount + t.Power.WriteCount;

        var r = t.Mode("game");

        Assert.Equal(ResponseCodes.NoChange, r.Code);
        Assert.Equal(writes, t.Registry.WriteCount + t.Power.WriteCount);
    }

    // ---------- сравнение состояний ----------

    [Fact]
    public void Registry_comparer_ignores_trailing_nul_and_treats_types_as_different()
    {
        var c = StateComparers.For(StepKind.RegistryValue);
        var a = StateJson.RegistryJson(TestEnv.Str("2000"));
        var b = StateJson.RegistryJson(TestEnv.Str("2000\0"));
        Assert.True(c.IsSame(a, b));
        Assert.False(c.IsSame(a, StateJson.RegistryJson(TestEnv.Dword(2000))));
        Assert.False(c.IsSame(a, StateJson.RegistryJson(TestEnv.Str("2001"))));
        Assert.True(c.IsSame(StateJson.RegistryJson(TestEnv.Absent), StateJson.RegistryJson(TestEnv.Absent)));
        Assert.False(c.IsSame(StateJson.RegistryJson(TestEnv.Absent), StateJson.RegistryJson(TestEnv.Dword(0))));
    }

    [Fact]
    public void Registry_comparer_rejects_garbage_with_InvalidDataException()
    {
        var c = StateComparers.For(StepKind.RegistryValue);
        Assert.Throws<InvalidDataException>(() => c.IsSame("not json", "{}"));
    }

    [Fact]
    public void Startup_comparer_compares_by_enabled_class_not_by_raw_bytes()
    {
        var c = StateComparers.For(StepKind.StartupApproved);
        var enabled02 = StateJson.StartupJson(true, [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var enabled06 = StateJson.StartupJson(true, [6, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8]);
        var disabled = StateJson.StartupJson(true, [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var absent = StateJson.StartupJson(false, null);

        Assert.True(c.IsNoChange(enabled02, enabled06));
        Assert.True(c.IsNoChange(absent, enabled02));
        Assert.False(c.IsNoChange(disabled, enabled02));
        Assert.False(c.IsNoChange(enabled02, disabled));
        Assert.True(c.IsNoChange(disabled, StateJson.StartupJson(true, [3, 0, 0, 0, 9, 9, 9, 9, 9, 9, 9, 9])));
    }

    [Fact]
    public void Service_comparer_compares_start_and_delayed_flag()
    {
        var c = StateComparers.For(StepKind.ServiceStart);
        Assert.True(c.IsSame(StateJson.ServiceJson(3, false, false, false), StateJson.ServiceJson(3, false, false, true)));
        Assert.False(c.IsSame(StateJson.ServiceJson(3, false, false, false), StateJson.ServiceJson(4, false, false, false)));
        Assert.False(c.IsSame(StateJson.ServiceJson(2, true, false, false), StateJson.ServiceJson(2, false, false, false)));
    }

    [Fact]
    public void Task_comparer_compares_enabled()
    {
        var c = StateComparers.For(StepKind.TaskEnabled);
        Assert.True(c.IsSame(StateJson.TaskJson(true, true), StateJson.TaskJson(true, true)));
        Assert.False(c.IsSame(StateJson.TaskJson(true, true), StateJson.TaskJson(true, false)));
    }

    [Fact]
    public void Power_comparers_compare_scheme_values_and_existence()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        Assert.True(StateComparers.For(StepKind.PowerActiveScheme).IsSame(StateJson.PowerActiveJson(g1), StateJson.PowerActiveJson(g1)));
        Assert.False(StateComparers.For(StepKind.PowerActiveScheme).IsSame(StateJson.PowerActiveJson(g1), StateJson.PowerActiveJson(g2)));
        Assert.True(StateComparers.For(StepKind.PowerSetting).IsSame(StateJson.PowerSettingJson(1, 2), StateJson.PowerSettingJson(1, 2)));
        Assert.False(StateComparers.For(StepKind.PowerSetting).IsSame(StateJson.PowerSettingJson(1, 2), StateJson.PowerSettingJson(1, 3)));
        Assert.True(StateComparers.For(StepKind.PowerSchemeCreate).IsSame(StateJson.PowerSchemeJson(true), StateJson.PowerSchemeJson(true)));
        Assert.False(StateComparers.For(StepKind.PowerSchemeCreate).IsSame(StateJson.PowerSchemeJson(true), StateJson.PowerSchemeJson(false)));
    }

    [Fact]
    public void Every_step_kind_has_a_comparer()
    {
        foreach (var k in Enum.GetValues<StepKind>()) Assert.NotNull(StateComparers.For(k));
    }
}
