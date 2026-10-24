using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

public class ChangeServiceTests
{
    static TweakEntry AnyTweak() => TweakCatalog.Shared.Tweaks.First();

    static AgentResponse WithSteps(AgentRequest r, string code, params StepState[] states)
        => new(r.Id, code is ResponseCodes.Ok or ResponseCodes.NoChange, code, null, Guid.NewGuid(),
            states.Select((s, i) => new StepResult(i + 1, i, StepKind.RegistryValue, s, null)).ToList());

    [Fact]
    public async Task Tweaks_request_contains_exactly_the_user_selection()
    {
        var agent = new FakeAgentClient();
        var svc = Make.Changes(agent);
        var ids = TweakCatalog.Shared.Tweaks.Take(2).Select(t => t.Id).ToList();

        var outcome = await svc.ApplyTweaksAsync(new[]
        {
            new TweakChange { TweakId = ids[0], Enabled = true },
            new TweakChange { TweakId = ids[1], Enabled = false },
        });

        var sent = Assert.IsType<ApplyTweaksRequest>(Assert.Single(agent.Sent));
        Assert.Equal(ids, sent.Changes.Select(c => c.TweakId).ToList());
        Assert.Equal(new[] { true, false }, sent.Changes.Select(c => c.Enabled).ToArray());
        Assert.NotEqual(Guid.Empty, sent.Id);
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
    }

    [Fact]
    public async Task Each_operation_builds_the_matching_request()
    {
        var agent = new FakeAgentClient();
        var svc = Make.Changes(agent);
        var mode = TweakCatalog.Shared.Modes.First();

        await svc.ApplyModeAsync(mode.Id);
        await svc.SetStartupAsync("run:hku:Discord", false);
        await svc.SetServiceStartAsync("DemoSvc", StartMode.Manual);
        await svc.SetTaskEnabledAsync(@"\Vendor\Updater", false);
        await svc.SetPowerSchemeAsync(PowerPlan.High);
        await svc.SetPowerSettingAsync("display", 600, null);

        Assert.Equal(mode.Id, Assert.IsType<ApplyModeRequest>(agent.Sent[0]).ModeId);
        var st = Assert.IsType<SetStartupEntryRequest>(agent.Sent[1]);
        Assert.Equal("run:hku:Discord", st.EntryId);
        Assert.False(st.Enabled);
        var sv = Assert.IsType<SetServiceStartRequest>(agent.Sent[2]);
        Assert.Equal("DemoSvc", sv.ServiceName);
        Assert.Equal(StartMode.Manual, sv.Start);
        var tk = Assert.IsType<SetTaskEnabledRequest>(agent.Sent[3]);
        Assert.Equal(@"\Vendor\Updater", tk.TaskPath);
        Assert.False(tk.Enabled);
        Assert.Equal(PowerPlan.High, Assert.IsType<SetPowerSchemeRequest>(agent.Sent[4]).Plan);
        var ps = Assert.IsType<SetPowerSettingRequest>(agent.Sent[5]);
        Assert.Equal(600u, ps.Ac);
        Assert.Null(ps.Dc);
        Assert.Equal(6, agent.Sent.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task View_only_never_sends_a_request()
    {
        var agent = new FakeAgentClient { Access = AgentAccess.ViewOnly };
        var svc = Make.Changes(agent);
        var seen = new List<ChangeOutcome>();
        svc.Completed += seen.Add;

        var o1 = await svc.ApplyTweaksAsync(new[] { new TweakChange { TweakId = AnyTweak().Id, Enabled = true } });
        var o2 = await svc.SetServiceStartAsync("DemoSvc", StartMode.Disabled);
        var o3 = await svc.UndoAsync(Guid.NewGuid(), null);

        Assert.Empty(agent.Sent);
        Assert.All(new[] { o1, o2, o3 }, o => Assert.Equal(OutcomeKind.ViewOnly, o.Kind));
        Assert.False(svc.CanApply);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Invalid_request_is_denied_without_sending()
    {
        var agent = new FakeAgentClient();
        var svc = Make.Changes(agent);

        var o = await svc.ApplyTweaksAsync(Array.Empty<TweakChange>());

        Assert.Equal(OutcomeKind.Denied, o.Kind);
        Assert.Empty(agent.Sent);
    }

    [Fact]
    public async Task Partial_result_reports_three_of_four()
    {
        var agent = new FakeAgentClient
        {
            Handler = r => WithSteps(r, ResponseCodes.Partial, StepState.Done, StepState.Done, StepState.Done, StepState.Failed),
        };
        var svc = Make.Changes(agent);

        var o = await svc.UndoAsync(Guid.NewGuid(), null);

        Assert.Equal(OutcomeKind.Partial, o.Kind);
        Assert.Equal(3, o.StepsDone);
        Assert.Equal(4, o.StepsTotal);
        Assert.Contains("3 из 4", o.Message);
        Assert.True(o.Changed);
    }

    [Fact]
    public async Task No_change_is_not_a_change()
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, true, ResponseCodes.NoChange, null, Guid.NewGuid()) };
        var effects = new FakeEffects();
        var svc = Make.Changes(agent, effects);

        var o = await svc.ApplyTweaksAsync(new[] { new TweakChange { TweakId = AnyTweak().Id, Enabled = true } });

        Assert.Equal(OutcomeKind.NoChange, o.Kind);
        Assert.False(o.Changed);
        Assert.Empty(effects.Applied);
    }

    [Theory]
    [InlineData(ResponseCodes.PolicyDenied, OutcomeKind.Denied)]
    [InlineData(ResponseCodes.NotFound, OutcomeKind.Denied)]
    [InlineData(ResponseCodes.Busy, OutcomeKind.Unavailable)]
    [InlineData(ResponseCodes.JournalUnavailable, OutcomeKind.Unavailable)]
    [InlineData(ClientCodes.Timeout, OutcomeKind.Unavailable)]
    [InlineData(ClientCodes.Declined, OutcomeKind.ViewOnly)]
    [InlineData(ResponseCodes.Conflict, OutcomeKind.Conflict)]
    public async Task Response_codes_map_to_outcomes(string code, OutcomeKind expected)
    {
        var agent = new FakeAgentClient { Handler = r => new AgentResponse(r.Id, false, code) };
        var svc = Make.Changes(agent);

        var o = await svc.SetServiceStartAsync("DemoSvc", StartMode.Manual);

        Assert.Equal(expected, o.Kind);
        Assert.False(string.IsNullOrWhiteSpace(o.Message));
    }

    [Fact]
    public async Task Effects_and_restart_hint_follow_the_catalog_after_success()
    {
        var tweak = TweakCatalog.Shared.Tweaks.FirstOrDefault(t => t.Effect != "none");
        if (tweak is null) return; // каталог без эффектов: проверять нечего
        var agent = new FakeAgentClient();
        var effects = new FakeEffects();
        var svc = Make.Changes(agent, effects);

        var o = await svc.ApplyTweaksAsync(new[] { new TweakChange { TweakId = tweak.Id, Enabled = true } });

        Assert.Contains(tweak.Effect, effects.Applied);
        if (tweak.Restart == "explorer") Assert.Contains("Проводник", o.RestartHint);
    }

    [Fact]
    public async Task Cancellation_becomes_cancelled_outcome()
    {
        var agent = new CancellingClient();
        var svc = Make.Changes(new FakeAgentClient());
        var cancelling = new ChangeService(agent, new FakeEffects());

        var o = await cancelling.SetStartupAsync("run:hku:X", true);

        Assert.Equal(OutcomeKind.Cancelled, o.Kind);
        _ = svc;
    }

    sealed class CancellingClient : IAgentClient
    {
        public AgentAccess Access => AgentAccess.Connected;
        public bool IsElevated => true;
        public event EventHandler? AccessChanged { add { } remove { } }
        public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default) => throw new OperationCanceledException();
        public void AllowElevationRetry() { }
    }
}

public class UndoFlowTests
{
    static AgentResponse Conflict(AgentRequest r) => new(r.Id, false, ResponseCodes.Conflict, null, null, null,
        new[]
        {
            new ConflictInfo(10, StepKind.RegistryValue, "1", null),
            new ConflictInfo(11, StepKind.ServiceStart, "Manual", Guid.NewGuid()),
        });

    [Fact]
    public async Task Without_conflicts_the_dialog_is_not_shown()
    {
        var agent = new FakeAgentClient();
        var dialogs = new FakeDialogs();
        var flow = new UndoFlow(Make.Changes(agent), dialogs);

        var o = await flow.RunAsync(Guid.NewGuid());

        Assert.Equal(OutcomeKind.Success, o.Kind);
        Assert.Equal(0, dialogs.ConflictCalls);
        Assert.Single(agent.Sent);
    }

    [Fact]
    public async Task Conflicts_are_resolved_and_sent_in_second_request()
    {
        var agent = new FakeAgentClient();
        agent.Handler = r => r is UndoRequest { Resolutions: null } ? Conflict(r) : FakeAgentHost.DefaultReply(r);
        var dialogs = new FakeDialogs
        {
            ConflictAnswer = c => new[] { new UndoResolution { StepId = 10, Action = ConflictAction.Overwrite } },
        };
        var opId = Guid.NewGuid();

        var o = await new UndoFlow(Make.Changes(agent), dialogs).RunAsync(opId);

        Assert.Equal(OutcomeKind.Success, o.Kind);
        Assert.Equal(1, dialogs.ConflictCalls);
        Assert.Equal(2, agent.Sent.Count);
        var second = Assert.IsType<UndoRequest>(agent.Sent[1]);
        Assert.Equal(opId, second.OperationId);
        Assert.NotNull(second.Resolutions);
        Assert.Equal(ConflictAction.Overwrite, second.Resolutions!.Single(x => x.StepId == 10).Action);
        // Конфликт без ответа пользователя считается отказом: Skip.
        Assert.Equal(ConflictAction.Skip, second.Resolutions!.Single(x => x.StepId == 11).Action);
    }

    [Fact]
    public async Task Cancelling_the_dialog_writes_nothing()
    {
        var agent = new FakeAgentClient { Handler = Conflict };
        var dialogs = new FakeDialogs { ConflictAnswer = _ => null };

        var o = await new UndoFlow(Make.Changes(agent), dialogs).RunAsync(Guid.NewGuid());

        Assert.Equal(OutcomeKind.Cancelled, o.Kind);
        Assert.Single(agent.Sent);
    }
}
