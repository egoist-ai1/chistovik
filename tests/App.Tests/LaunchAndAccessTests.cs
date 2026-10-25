using System.IO;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;
using Xunit;

namespace App.Tests;

/// <summary>Сообщения кодов запуска, код выхода агента 3 и режим просмотра для обычного пользователя.</summary>
public class LaunchAndAccessTests
{
    sealed class FakeEnv(TokenElevationKind type, bool admin) : IElevationEnvironment
    {
        public TokenElevationKind ElevationType => type;
        public bool IsInAdministratorsRole => admin;
    }

    /// <summary>Мьютекс агента на фейковом порту: <c>held(n)</c> отвечает на n-й опрос (с 1).</summary>
    sealed class FakeProbe(Func<int, bool> held) : IAgentInstanceProbe
    {
        public int Polls { get; private set; }
        public bool IsHeld() => held(++Polls);
    }

    static AgentClientOptions Opts(bool canElevate = true, IAgentInstanceProbe? probe = null) => new()
    {
        CanElevate = canElevate,
        UserSid = "S-1-5-21-test",
        ParentPid = 1,
        AppPath = @"C:\app\Chistovik.App.exe",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReconnectTimeout = TimeSpan.FromMilliseconds(100),
        MutexPollInterval = TimeSpan.FromMilliseconds(5),
        MutexPollTimeout = TimeSpan.FromMilliseconds(100),
        InstanceProbe = probe ?? new FakeProbe(_ => false),
        HeartbeatInterval = TimeSpan.FromMinutes(10),
    };

    static SetPowerSchemeRequest Change() => new() { Id = Guid.NewGuid(), Plan = PowerPlan.Bal };

    // ----- П.20: коды ShellExecute -----

    [Fact]
    public void Access_denied_5_gets_a_russian_message()
    {
        var r = ProcessAgentLauncher.FromNativeError(5, "Access is denied");

        Assert.Equal(AgentLaunchStatus.Failed, r.Status);
        Assert.Equal("Windows отклонила запрос прав администратора (возможно, действует политика организации).", r.Message);
    }

    [Fact]
    public void Elevation_required_740_gets_a_russian_message()
    {
        var r = ProcessAgentLauncher.FromNativeError(740, "The requested operation requires elevation");

        Assert.Equal(AgentLaunchStatus.Failed, r.Status);
        Assert.Equal("Для запуска нужны права администратора.", r.Message);
    }

    [Fact]
    public void Cancelled_1223_stays_a_decline()
    {
        Assert.Equal(AgentLaunchStatus.Declined, ProcessAgentLauncher.FromNativeError(1223, "cancelled").Status);
    }

    [Fact]
    public void Unknown_native_error_keeps_system_text()
    {
        var r = ProcessAgentLauncher.FromNativeError(2, "Не найден файл");

        Assert.Equal(AgentLaunchStatus.Failed, r.Status);
        Assert.Equal("Не найден файл", r.Message);
    }

    // ----- П.3: код выхода 3 -----

    [Fact]
    public void Exit_code_3_has_the_agreed_message()
    {
        Assert.Equal("Служба Чистовика уже запущена, возможно в другом сеансе Windows. Закройте её там или подождите несколько секунд и повторите.",
            LaunchMessages.ForAgentExit(3));
        Assert.Null(LaunchMessages.ForAgentExit(0));
        Assert.Null(LaunchMessages.ForAgentExit(4));
    }

    [Fact]
    public void Exit_codes_have_specific_or_generic_messages()
    {
        Assert.Equal("Имя службы Чистовика занято другой программой. Перезапустите компьютер или обратитесь к администратору.",
            LaunchMessages.ForAgentExit(6));
        // Коды, после которых файла журнала гарантированно нет, на журнал не ссылаются.
        Assert.DoesNotContain("журнал", LaunchMessages.ForAgentExit(7));
        Assert.DoesNotContain("журнал", LaunchMessages.ForAgentExit(5));
        Assert.DoesNotContain("журнал", LaunchMessages.ForAgentExit(6));
        Assert.DoesNotContain("журнал", LaunchMessages.ForAgentExit(2));
        Assert.DoesNotContain("журнал", LaunchMessages.ForAgentExit(3));
        Assert.Contains("(код 42)", LaunchMessages.ForAgentExit(42));
        Assert.Equal("Служба Чистовика завершилась с ошибкой (код 42). Подробности в журнале агента.", LaunchMessages.ForAgentExit(42));
    }

    [Fact]
    public async Task Agent_exiting_with_3_gives_already_running_without_waiting_for_the_pipe()
    {
        var launcher = new FakeLauncher { Exited = Task.FromResult(3) };
        var host = new FakeAgentHost { FailConnect = _ => true };
        using var client = new AgentClient(launcher, host, Opts(), () => "Chistovik.test");

        var r = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.AlreadyRunning, r.Code);
        Assert.Equal(LaunchMessages.AlreadyRunning, r.Message);
        Assert.Equal(0, host.Count);
        Assert.NotEqual(AgentAccess.ViewOnly, client.Access); // не режим просмотра: после закрытия другого сеанса можно повторить
    }

    [Fact]
    public async Task Exit_code_3_is_retried_exactly_once_then_reported()
    {
        var launcher = new FakeLauncher { Exited = Task.FromResult(3) };
        var host = new FakeAgentHost { FailConnect = _ => true };
        using var client = new AgentClient(launcher, host, Opts(), () => "Chistovik.test");

        var r = await client.SendAsync(Change());

        Assert.Equal(ClientCodes.AlreadyRunning, r.Code);
        Assert.Equal(2, launcher.Calls.Count); // один повтор, не бесконечный
    }

    [Fact]
    public async Task Exit_code_3_then_a_healthy_agent_connects_on_the_single_retry()
    {
        var launcher = new FakeLauncher();
        launcher.ExitedSequence.Enqueue(Task.FromResult(3));
        launcher.ExitedSequence.Enqueue(null); // второй запуск: процесс жив, канал доступен
        var host = new FakeAgentHost { FailConnect = attempt => attempt == 1 };
        using var client = new AgentClient(launcher, host, Opts(), () => "Chistovik.test");

        var r = await client.SendAsync(Change());

        Assert.True(r.Ok);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal(AgentAccess.Connected, client.Access);
    }

    [Fact]
    public async Task Exit_code_3_relaunches_once_after_the_mutex_disappears_on_the_second_poll()
    {
        var probe = new FakeProbe(n => n < 2);
        var launcher = new FakeLauncher();
        launcher.ExitedSequence.Enqueue(Task.FromResult(3));
        launcher.ExitedSequence.Enqueue(null);
        var host = new FakeAgentHost { FailConnect = attempt => attempt == 1 };
        using var client = new AgentClient(launcher, host, Opts(probe: probe), () => "Chistovik.test");

        var r = await client.SendAsync(Change());

        Assert.True(r.Ok);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal(2, probe.Polls);
    }

    [Fact]
    public async Task Exit_code_3_with_a_mutex_that_never_goes_away_shows_the_message_without_a_second_launch()
    {
        var probe = new FakeProbe(_ => true);
        var launcher = new FakeLauncher { Exited = Task.FromResult(3) };
        var host = new FakeAgentHost { FailConnect = _ => true };
        using var client = new AgentClient(launcher, host, Opts(probe: probe), () => "Chistovik.test");

        var r = await client.SendAsync(Change()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientCodes.AlreadyRunning, r.Code);
        Assert.Equal(LaunchMessages.AlreadyRunning, r.Message);
        Assert.Single(launcher.Calls);
        Assert.True(probe.Polls >= 2); // опрашивали, а не сдались сразу
    }

    [Fact]
    public async Task Second_exit_code_3_after_the_relaunch_does_not_loop()
    {
        var probe = new FakeProbe(_ => false);
        var launcher = new FakeLauncher { Exited = Task.FromResult(3) };
        var host = new FakeAgentHost { FailConnect = _ => true };
        using var client = new AgentClient(launcher, host, Opts(probe: probe), () => "Chistovik.test");

        var r = await client.SendAsync(Change()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientCodes.AlreadyRunning, r.Code);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.Equal(1, probe.Polls); // второй раз опроса нет: повтор только один
    }

    /// <summary>Транспорт, который не подключается, пока его не отменят (процесс агента уже вышел, канала нет).</summary>
    sealed class NeverConnects : IAgentTransport
    {
        public async Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(99)]
    public async Task Agent_exit_with_error_code_stops_waiting_and_is_translated(int exitCode)
    {
        var launcher = new FakeLauncher { Exited = Task.FromResult(exitCode) };
        using var client = new AgentClient(launcher, new NeverConnects(), Opts(), () => "Chistovik.test");

        var r = await client.SendAsync(Change()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.LaunchFailed, r.Code);
        Assert.Equal(LaunchMessages.ForAgentExit(exitCode), r.Message);
        Assert.Single(launcher.Calls); // повтор только для кода 3
    }

    [Fact]
    public async Task Unknown_exit_code_message_names_the_code_and_the_agent_journal()
    {
        var launcher = new FakeLauncher { Exited = Task.FromResult(99) };
        using var client = new AgentClient(launcher, new NeverConnects(), Opts(), () => "Chistovik.test");

        var r = await client.SendAsync(Change()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Служба Чистовика завершилась с ошибкой (код 99). Подробности в журнале агента.", r.Message);
    }

    [Fact]
    public void Already_running_is_shown_to_the_user_as_unavailable_with_the_message()
    {
        var o = ResponseText.ForApply(new AgentResponse(Guid.NewGuid(), false, ClientCodes.AlreadyRunning, "x"));

        Assert.Equal(OutcomeKind.Unavailable, o.Kind);
        Assert.Contains("другом сеансе Windows", o.Message);
    }

    // ----- П.4: обычный пользователь -----

    [Theory]
    [InlineData(TokenElevationKind.Limited, false, true)]   // администратор с включённым UAC
    [InlineData(TokenElevationKind.Full, true, true)]       // приложение уже повышено
    [InlineData(TokenElevationKind.Default, true, true)]    // администратор при выключенном UAC
    [InlineData(TokenElevationKind.Default, false, false)]  // обычный пользователь
    [InlineData(TokenElevationKind.Unknown, false, true)]   // токен не прочитан: запрет не вводим
    public void Admin_check_distinguishes_standard_users(TokenElevationKind type, bool admin, bool expected)
    {
        Assert.Equal(expected, new AdminAccountCheck(new FakeEnv(type, admin)).CanElevate());
    }

    [Fact]
    public async Task Standard_user_is_view_only_from_the_start_and_never_launches_runas()
    {
        var launcher = new FakeLauncher();
        var host = new FakeAgentHost();
        using var client = new AgentClient(launcher, host, Opts(canElevate: false), () => "Chistovik.test");

        Assert.Equal(AgentAccess.ViewOnly, client.Access);
        Assert.Equal(AgentViewOnlyReason.NoAdminAccount, client.ViewOnlyReason);

        var r = await client.SendAsync(Change());

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.NoAdminAccount, r.Code);
        Assert.Equal("Только просмотр: для изменений нужна учётная запись администратора", r.Message);
        Assert.Empty(launcher.Calls);
        Assert.Equal(0, host.Count);
    }

    [Fact]
    public void Standard_user_cannot_lift_view_only_by_retry()
    {
        using var client = new AgentClient(new FakeLauncher(), new FakeAgentHost(), Opts(canElevate: false), () => "Chistovik.test");

        client.AllowElevationRetry();

        Assert.Equal(AgentAccess.ViewOnly, client.Access);
        Assert.Equal(AgentViewOnlyReason.NoAdminAccount, client.ViewOnlyReason);
    }

    [Fact]
    public async Task Declined_uac_reports_declined_reason_and_retry_clears_it()
    {
        var launcher = new FakeLauncher { Status = AgentLaunchStatus.Declined };
        using var client = new AgentClient(launcher, new FakeAgentHost(), Opts(), () => "Chistovik.test");

        await client.SendAsync(Change());
        Assert.Equal(AgentViewOnlyReason.Declined, client.ViewOnlyReason);

        client.AllowElevationRetry();
        Assert.Equal(AgentViewOnlyReason.None, client.ViewOnlyReason);
    }

    [Fact]
    public void View_only_text_for_standard_user_is_specific()
    {
        var o = ResponseText.ViewOnly(AgentViewOnlyReason.NoAdminAccount);

        Assert.Equal(OutcomeKind.ViewOnly, o.Kind);
        Assert.Contains("нужна учётная запись администратора", o.Message);
        Assert.DoesNotContain("Запросить права", o.Message);
    }
}
