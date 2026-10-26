using Chistovik.App.Services.Agent;
using Chistovik.Core.Ipc;
using Xunit;

namespace App.Tests;

public class AgentClientTests
{
    static AgentClientOptions Opts(TimeSpan? request = null, TimeSpan? heartbeat = null) => new()
    {
        CanElevate = true,
        UserSid = "S-1-5-21-test",
        ParentPid = 4242,
        AppPath = @"C:\app\Chistovik.App.exe",
        RequestTimeout = request ?? TimeSpan.FromSeconds(5),
        HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromSeconds(2),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReconnectTimeout = TimeSpan.FromMilliseconds(200),
    };

    static (AgentClient Client, FakeLauncher Launcher, FakeAgentHost Host) Create(AgentClientOptions? options = null)
    {
        var launcher = new FakeLauncher();
        var host = new FakeAgentHost();
        var client = new AgentClient(launcher, host, options ?? Opts(), () => "Chistovik.test");
        return (client, launcher, host);
    }

    static SetPowerSchemeRequest Change() => new() { Id = Guid.NewGuid(), Plan = PowerPlan.Bal };

    [Fact]
    public async Task Ping_without_connection_does_not_start_the_agent()
    {
        var (client, launcher, host) = Create();

        var r = await client.SendAsync(new PingRequest { Id = Guid.NewGuid() });

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.NotRunning, r.Code);
        Assert.Empty(launcher.Calls);
        Assert.Equal(0, host.Count);
        Assert.Equal(AgentAccess.NotStarted, client.Access);
    }

    [Fact]
    public async Task First_change_starts_agent_once_with_expected_arguments()
    {
        var (client, launcher, host) = Create();

        var first = await client.SendAsync(Change());
        var second = await client.SendAsync(Change());

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal("Chistovik.test", call.PipeName);
        Assert.Equal(4242, call.ParentPid);
        Assert.Equal("S-1-5-21-test", call.UserSid);
        Assert.Equal(@"C:\app\Chistovik.App.exe", call.AppPath);
        Assert.Equal(2, host.Count);
        Assert.True(client.IsElevated);
        Assert.Equal(AgentAccess.Connected, client.Access);
    }

    [Fact]
    public void Launch_arguments_are_built_in_agreed_form()
    {
        var args = ProcessAgentLauncher.BuildArguments(new AgentLaunchArgs("Chistovik.abc", 77, "S-1-5-21-1", @"C:\Program Files\Chistovik\Chistovik.App.exe"));

        Assert.Contains("--pipe \"Chistovik.abc\"", args);
        Assert.Contains("--parent-pid 77", args);
        Assert.Contains("--user-sid \"S-1-5-21-1\"", args);
        Assert.Contains("--app-path", args);
        Assert.Contains("Chistovik.App.exe", args);
    }

    [Fact]
    public async Task Declined_uac_switches_to_view_only_and_blocks_further_requests()
    {
        var (client, launcher, host) = Create();
        launcher.Status = AgentLaunchStatus.Declined;
        var changes = new List<AgentAccess>();
        client.AccessChanged += (_, _) => changes.Add(client.Access);

        var r = await client.SendAsync(Change());
        var again = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.Declined, r.Code);
        Assert.Equal(ClientCodes.Declined, again.Code);
        Assert.Equal(AgentAccess.ViewOnly, client.Access);
        Assert.False(client.IsElevated);
        Assert.Single(launcher.Calls); // повторного UAC нет
        Assert.Equal(0, host.Count);
        Assert.Equal(new[] { AgentAccess.ViewOnly }, changes);
    }

    [Fact]
    public async Task Allow_elevation_retry_shows_uac_again()
    {
        var (client, launcher, _) = Create();
        launcher.Status = AgentLaunchStatus.Declined;
        await client.SendAsync(Change());

        client.AllowElevationRetry();
        Assert.Equal(AgentAccess.NotStarted, client.Access);
        launcher.Status = AgentLaunchStatus.Started;
        var r = await client.SendAsync(Change());

        Assert.True(r.Ok);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal(AgentAccess.Connected, client.Access);
    }

    [Fact]
    public async Task Launch_failure_is_reported_and_is_not_view_only()
    {
        var (client, launcher, host) = Create();
        launcher.Status = AgentLaunchStatus.Failed;

        var r = await client.SendAsync(Change());

        Assert.Equal(ClientCodes.LaunchFailed, r.Code);
        Assert.Equal(AgentAccess.NotStarted, client.Access);
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public async Task Invalid_request_is_rejected_before_any_launch()
    {
        var (client, launcher, _) = Create();

        var r = await client.SendAsync(new ApplyTweaksRequest { Id = Guid.NewGuid(), Changes = Array.Empty<TweakChange>() });

        Assert.Equal(ResponseCodes.BadRequest, r.Code);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Heartbeat_sends_ping_over_live_connection()
    {
        var (client, _, host) = Create();
        await client.SendAsync(Change());

        var alive = await client.HeartbeatOnceAsync();

        Assert.True(alive);
        Assert.Single(host.Of<PingRequest>());
    }

    [Fact]
    public async Task Heartbeat_without_connection_is_false_and_sends_nothing()
    {
        var (client, launcher, host) = Create();

        Assert.False(await client.HeartbeatOnceAsync());
        Assert.Empty(launcher.Calls);
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public async Task Heartbeat_timer_keeps_pinging()
    {
        var (client, _, host) = Create(Opts(heartbeat: TimeSpan.FromMilliseconds(40)));
        await client.SendAsync(Change());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Of<PingRequest>().Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.True(host.Of<PingRequest>().Count >= 3, "Ping по таймеру не приходит");
        client.Dispose();
    }

    [Fact]
    public async Task Failed_heartbeat_marks_connection_lost()
    {
        var (client, _, host) = Create();
        await client.SendAsync(Change());
        host.Handler = (_, _) => null; // агент закрывает канал на Ping

        var alive = await client.HeartbeatOnceAsync();

        Assert.False(alive);
        Assert.Equal(AgentAccess.Lost, client.Access);
        Assert.False(client.IsElevated);
    }

    [Fact]
    public async Task Dropped_connection_is_retried_once_with_the_same_id()
    {
        var (client, launcher, host) = Create();
        host.Handler = (r, i) => i == 0 ? null : FakeAgentHost.DefaultReply(r); // первый обмен обрывается без ответа
        var request = Change();

        var r = await client.SendAsync(request);

        Assert.True(r.Ok);
        Assert.Equal(2, host.Count);
        Assert.All(host.Of<SetPowerSchemeRequest>(), x => Assert.Equal(request.Id, x.Id));
        Assert.Single(launcher.Calls); // тихое переподключение к тому же каналу, без нового UAC
        Assert.Equal(new[] { "Chistovik.test", "Chistovik.test" }, host.Pipes);
        Assert.Equal(AgentAccess.Connected, client.Access);
    }

    [Fact]
    public async Task Retry_relaunches_agent_when_old_pipe_is_gone()
    {
        var (client, launcher, host) = Create();
        host.Handler = (r, i) => i == 0 ? null : FakeAgentHost.DefaultReply(r);
        host.FailConnect = attempt => attempt == 2; // тихая попытка вернуться в старый канал не удаётся

        var r = await client.SendAsync(Change());

        Assert.True(r.Ok);
        Assert.Equal(2, launcher.Calls.Count);
    }

    [Fact]
    public async Task Second_drop_returns_disconnected_without_endless_retry()
    {
        var (client, _, host) = Create();
        host.Handler = (_, _) => null;

        var r = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.Disconnected, r.Code);
        Assert.Equal(2, host.Count);
    }

    [Fact]
    public async Task Timeout_returns_code_and_does_not_resend()
    {
        var (client, _, host) = Create(Opts(request: TimeSpan.FromMilliseconds(150)));
        host.Hang = true;

        var r = await client.SendAsync(Change());

        Assert.Equal(ClientCodes.Timeout, r.Code);
        Assert.Equal(1, host.Count); // результат неизвестен: повторно не отправляем
        Assert.Equal(AgentAccess.Lost, client.Access);
    }

    static CleanCategoriesRequest Clean(bool measureOnly) => new() { Id = Guid.NewGuid(), Categories = ["user-temp"], MeasureOnly = measureOnly };

    static ApplyFindingsRequest Findings() => new()
    {
        Id = Guid.NewGuid(),
        Items = [new FindingAction("apppath:machine:x64:a.exe", FindingActionKind.Remove)],
        DryRun = true,
    };

    [Fact]
    public void Timeout_depends_on_request_type()
    {
        var opt = new AgentClientOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(1),
            UninstallTimeout = TimeSpan.FromSeconds(2),
            CleanupTimeout = TimeSpan.FromSeconds(3),
            FindingsTimeout = TimeSpan.FromSeconds(4),
            UndoTimeout = TimeSpan.FromSeconds(5),
        };
        var client = new AgentClient(new FakeLauncher(), new FakeAgentHost(), opt);

        Assert.Equal(TimeSpan.FromSeconds(5), client.TimeoutFor(new UndoRequest { Id = Guid.NewGuid(), OperationId = Guid.NewGuid() }));
        Assert.Equal(TimeSpan.FromSeconds(3), client.TimeoutFor(Clean(false)));
        Assert.Equal(TimeSpan.FromSeconds(1), client.TimeoutFor(Clean(true)));   // измерение обычный короткий запрос
        Assert.Equal(TimeSpan.FromSeconds(4), client.TimeoutFor(Findings()));
        Assert.Equal(TimeSpan.FromSeconds(2), client.TimeoutFor(new UninstallRequest { Id = Guid.NewGuid(), FindingId = "arp:machine:x64:k" }));
        Assert.Equal(TimeSpan.FromSeconds(1), client.TimeoutFor(Change()));
    }

    [Fact]
    public void Default_timeouts_are_25_minutes_for_cleanup_and_5_for_findings()
    {
        var opt = new AgentClientOptions();
        Assert.Equal(TimeSpan.FromMinutes(25), opt.CleanupTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), opt.FindingsTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), opt.UndoTimeout);
    }

    [Fact]
    public async Task Cleanup_waits_longer_than_an_ordinary_request_while_measure_does_not()
    {
        var opt = new AgentClientOptions
        {
            CanElevate = true, UserSid = "S-1-5-21-test", ParentPid = 1,AppPath = @"C:\app\Chistovik.App.exe",
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            CleanupTimeout = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ReconnectTimeout = TimeSpan.FromMilliseconds(200),
        };
        var (client, _, host) = Create(opt);
        host.Hang = true;

        var measured = await client.SendAsync(Clean(true));
        Assert.Equal(ClientCodes.Timeout, measured.Code);

        // Очистка за 100 мс не обрывается: ждёт своего срока, здесь её останавливает только отмена вызывающего.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Clean(false), cts.Token));
    }

    [Fact]
    public async Task Facts_travel_through_the_channel()
    {
        var (client, _, host) = Create();
        host.Handler = (r, _) => new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid(), null, null,
            [new FactEntry("cleanup:user:-:user-temp", "{\"v\":1,\"done\":false,\"bytes\":10,\"files\":2}")]);

        var r = await client.SendAsync(Clean(true));

        Assert.True(r.Ok);
        var fact = Assert.Single(r.Facts!);
        Assert.Equal("cleanup:user:-:user-temp", fact.Key);
    }

    [Fact]
    public async Task Cancellation_throws_and_closes_the_channel()
    {
        var (client, _, host) = Create();
        host.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Change(), cts.Token));

        Assert.Equal(AgentAccess.Lost, client.Access);
    }

    [Fact]
    public async Task Response_with_foreign_id_is_not_accepted()
    {
        var (client, _, host) = Create();
        host.Handler = (_, _) => new AgentResponse(Guid.NewGuid(), true, ResponseCodes.Ok);

        var r = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.Disconnected, r.Code);
    }

    [Fact]
    public async Task Requests_after_dispose_are_refused()
    {
        var (client, launcher, _) = Create();
        client.Dispose();

        var r = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Empty(launcher.Calls);
    }
}
