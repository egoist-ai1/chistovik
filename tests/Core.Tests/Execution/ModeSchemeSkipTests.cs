using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Режим на устройстве, где схема питания недоступна (Modern Standby): твики применяются, схема пропускается и сообщается.</summary>
public class ModeSchemeSkipTests
{
    const string SkippedFact = "mode:power-scheme-skipped";

    static List<StepKind> Kinds(TestEnv t, AgentResponse r) => t.Journal.Get(r.OperationId!.Value)!.Steps.Select(s => s.Kind).ToList();

    [Fact]
    public void Template_scheme_on_modern_standby_is_skipped_and_tweaks_are_applied()
    {
        var t = new TestEnv();
        t.Power.ModernStandby = true;

        var r = t.Mode("max");

        Assert.True(r.Ok, r.Code + r.Message);
        var steps = Kinds(t, r);
        Assert.NotEmpty(steps);
        Assert.DoesNotContain(steps, k => k is StepKind.PowerActiveScheme or StepKind.PowerSchemeCreate);
        Assert.Equal(0, t.Power.DuplicateCount);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
        Assert.Contains(r.Facts ?? [], f => f.Key == SkippedFact);
    }

    [Fact]
    public void Skipped_scheme_mode_undoes_only_applied_steps()
    {
        var t = new TestEnv();
        t.Power.ModernStandby = true;
        var r = t.Mode("max");
        Assert.True(r.Ok, r.Code + r.Message);

        var u = t.Undo(r.OperationId!.Value);

        Assert.True(u.Ok, u.Code + u.Message);
        Assert.Equal(TestEnv.Balanced, t.Power.Active);
    }

    [Fact]
    public void Without_modern_standby_scheme_steps_stay_and_no_note_is_added()
    {
        var t = new TestEnv();

        var r = t.Mode("max");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Contains(StepKind.PowerActiveScheme, Kinds(t, r));
        Assert.DoesNotContain(r.Facts ?? [], f => f.Key == SkippedFact);
    }

    [Fact]
    public void Scheme_read_failure_still_refuses_the_whole_mode()
    {
        var t = new TestEnv();
        t.Power.ModernStandby = true;
        t.Power.ListFailure = new InvalidOperationException("сбой чтения схем");

        var r = t.Mode("max");

        Assert.False(r.Ok);
        Assert.DoesNotContain(r.Facts ?? [], f => f.Key == SkippedFact);
    }

    [Fact]
    public void Single_scheme_choice_on_modern_standby_is_still_refused()
    {
        var t = new TestEnv();
        t.Power.ModernStandby = true;

        var r = t.Plan(PowerPlan.Ult);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Null(r.Facts);
    }
}
