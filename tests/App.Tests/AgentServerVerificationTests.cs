using System.IO;
using System.IO.Pipes;
using Chistovik.App.Services.Agent;
using Chistovik.Core.Ipc;
using Chistovik.Native.Ipc;
using Xunit;

namespace App.Tests;

/// <summary>Проверка процесса на сервере канала со стороны приложения. Подставные факты о процессе; канал настоящий, но свой, в памяти процесса тестов.</summary>
public sealed class AgentServerVerificationTests : IDisposable
{
    const string Sha = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";
    const string OtherSha = "0000000000000000000000000000000000000000000000000000000000000001";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "chistovik-srv-" + Guid.NewGuid().ToString("N"));
    readonly string _agent;

    public AgentServerVerificationTests()
    {
        Directory.CreateDirectory(_dir);
        _agent = Path.Combine(_dir, "Chistovik.Agent.exe");
        File.WriteAllBytes(_agent, [1, 2, 3, 4, 5, 6, 7, 8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    sealed class FakeProbe(ActualServer actual) : IPipeServerProbe
    {
        public int Calls { get; private set; }

        public ActualServer Probe(NamedPipeClientStream pipe)
        {
            Calls++;
            return actual;
        }
    }

    /// <summary>Транспорт, который сразу отказывает как при чужом сервере; считает попытки.</summary>
    sealed class UntrustedTransport : IAgentTransport
    {
        public int Attempts { get; private set; }

        public Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            Attempts++;
            throw new AgentUntrustedException("image-path");
        }
    }

    ActualServer Own() => new(4321, _agent, Chistovik.Core.Ipc.ClientVerifier.Sha256OfFile(_agent));

    // ---------- ServerVerifier ----------

    [Fact]
    public void Verifier_accepts_own_image()
    {
        var (ok, reason) = ServerVerifier.Verify(new ExpectedServer(@"C:\Program Files\Chistovik\Chistovik.Agent.exe", Sha),
            new ActualServer(10, @"c:\program files\chistovik\CHISTOVIK.AGENT.EXE", Sha.ToLowerInvariant()));
        Assert.True(ok);
        Assert.Equal("ok", reason);
    }

    [Theory]
    [InlineData(@"C:\Users\evil\Chistovik.Agent.exe", Sha, "image-path")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.Agent.exe", OtherSha, "image-hash")]
    [InlineData(null, Sha, "image-path")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.Agent.exe", null, "image-hash")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.Agent.exe", "zz", "image-hash")]
    public void Verifier_rejects_foreign_or_missing_image(string? path, string? hash, string expectedReason)
    {
        var (ok, reason) = ServerVerifier.Verify(new ExpectedServer(@"C:\Program Files\Chistovik\Chistovik.Agent.exe", Sha),
            new ActualServer(10, path, hash));
        Assert.False(ok);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public void Verifier_rejects_unknown_pid()
    {
        var (ok, reason) = ServerVerifier.Verify(new ExpectedServer(@"C:\a\Chistovik.Agent.exe", Sha), new ActualServer(-1, null, null));
        Assert.False(ok);
        Assert.Equal("pid", reason);
    }

    // ---------- NamedPipeAgentTransport ----------

    [Fact]
    public async Task Transport_returns_stream_when_server_is_our_agent()
    {
        var name = "Chistovik.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var probe = new FakeProbe(Own());
        var transport = new NamedPipeAgentTransport(probe, _agent);

        var accept = server.WaitForConnectionAsync();
        await using var stream = await transport.ConnectAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None);
        await accept;

        Assert.Equal(1, probe.Calls);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task Transport_refuses_foreign_image_and_closes_the_pipe()
    {
        var name = "Chistovik.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var foreign = new ActualServer(4321, @"C:\Users\evil\agent.exe", OtherSha);
        var transport = new NamedPipeAgentTransport(new FakeProbe(foreign), _agent);

        var accept = server.WaitForConnectionAsync();
        var ex = await Assert.ThrowsAsync<AgentUntrustedException>(
            () => transport.ConnectAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None));
        await accept;

        Assert.Equal("image-path", ex.Reason);
        // клиентский конец закрыт: сервер видит конец потока и ничего не получил
        var buf = new byte[1];
        Assert.Equal(0, await server.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Transport_refuses_same_path_with_other_hash()
    {
        var name = "Chistovik.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var swapped = new ActualServer(4321, _agent, OtherSha); // тот же путь, файл подменён
        var transport = new NamedPipeAgentTransport(new FakeProbe(swapped), _agent);

        var accept = server.WaitForConnectionAsync();
        var ex = await Assert.ThrowsAsync<AgentUntrustedException>(
            () => transport.ConnectAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None));
        await accept;

        Assert.Equal("image-hash", ex.Reason);
    }

    [Fact]
    public async Task Transport_refuses_when_installed_agent_file_is_missing()
    {
        var name = "Chistovik.test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var transport = new NamedPipeAgentTransport(new FakeProbe(Own()), Path.Combine(_dir, "missing", "Chistovik.Agent.exe"));

        var accept = server.WaitForConnectionAsync();
        var ex = await Assert.ThrowsAsync<AgentUntrustedException>(
            () => transport.ConnectAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None));
        await accept;

        Assert.Equal("agent-missing", ex.Reason);
    }

    // ---------- AgentClient ----------

    [Fact]
    public async Task Client_does_not_send_request_when_server_is_untrusted()
    {
        var launcher = new FakeLauncher();
        var transport = new UntrustedTransport();
        using var client = new AgentClient(launcher, transport, new AgentClientOptions
        {
            UserSid = "S-1-5-21-test",
            ParentPid = 1,
            AppPath = @"C:\app\Chistovik.App.exe",
            ConnectTimeout = TimeSpan.FromSeconds(1),
            ReconnectTimeout = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromMinutes(10),
        }, () => "Chistovik.test");

        var r = await client.SendAsync(new SetPowerSchemeRequest { Id = Guid.NewGuid(), Plan = PowerPlan.Bal });

        Assert.False(r.Ok);
        Assert.Equal(ClientCodes.Untrusted, r.Code);
        Assert.Equal(1, transport.Attempts); // дальше попыток нет: ни тихого переподключения, ни повторов
        Assert.False(client.IsElevated);
        Assert.NotEqual(AgentAccess.Connected, client.Access);
    }

    [Fact]
    public async Task Client_with_trusted_server_sends_request_as_before()
    {
        var host = new FakeAgentHost();
        using var client = new AgentClient(new FakeLauncher(), host, new AgentClientOptions
        {
            UserSid = "S-1-5-21-test",
            ParentPid = 1,
            AppPath = @"C:\app\Chistovik.App.exe",
            HeartbeatInterval = TimeSpan.FromMinutes(10),
        }, () => "Chistovik.test");

        var r = await client.SendAsync(new SetPowerSchemeRequest { Id = Guid.NewGuid(), Plan = PowerPlan.Bal });

        Assert.True(r.Ok);
        Assert.Equal(1, host.Count);
    }
}
