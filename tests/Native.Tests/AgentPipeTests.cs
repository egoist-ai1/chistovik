using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Native.Ipc;
using Xunit;

namespace Chistovik.NativeTests;

/// <summary>Канал агента на живой Windows без повышения прав: сервер и клиент в одном тестовом процессе.</summary>
public class AgentPipeTests
{
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    static ExpectedClient ExpectSelf(Func<ExpectedClient, ExpectedClient>? tweak = null)
    {
        var image = Environment.ProcessPath!;
        var e = new ExpectedClient(Environment.ProcessId, image, ClientVerifier.Sha256OfFile(image),
            ClientProbe.CurrentSession(), Me.Value);
        return tweak?.Invoke(e) ?? e;
    }

    static string NewPipeName() => "Chistovik.test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Verified_client_gets_pong_and_unknown_operation_is_rejected()
    {
        var name = NewPipeName();
        var server = new AgentPipeServer(name, Me, ExpectSelf(), new RequestDispatcher(), TimeSpan.FromSeconds(20));
        var run = server.RunAsync(CancellationToken.None);

        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            var id = Guid.NewGuid();
            await FrameCodec.WriteAsync(client, AgentJson.Serialize(new PingRequest { Id = id }));
            var pong = AgentJson.DeserializeResponse((await FrameCodec.ReadAsync(client))!);
            Assert.True(pong.Ok);
            Assert.Equal(id, pong.Id);

            await FrameCodec.WriteAsync(client, """{"type":"MoveToQuarantine","id":"00000000-0000-0000-0000-000000000001","path":"C:\\Windows"}"""u8.ToArray());
            var rejected = AgentJson.DeserializeResponse((await FrameCodec.ReadAsync(client))!);
            Assert.False(rejected.Ok);
            Assert.Equal("bad-request", rejected.Code);
        }

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, server.RejectedClients);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("path")]
    [InlineData("pid")]
    [InlineData("user")]
    public async Task Client_that_does_not_match_expectation_is_disconnected_without_answer(string what)
    {
        var expected = ExpectSelf(e => what switch
        {
            "hash" => e with { ImageSha256 = new string('0', 64) },
            "path" => e with { ImagePath = @"C:\Windows\System32\notepad.exe" },
            "pid" => e with { Pid = Environment.ProcessId + 100000 },
            _ => e with { UserSid = "S-1-5-18" },
        });

        var name = NewPipeName();
        var server = new AgentPipeServer(name, Me, expected, new RequestDispatcher(), TimeSpan.FromSeconds(3));
        var run = server.RunAsync(CancellationToken.None);

        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            try
            {
                await FrameCodec.WriteAsync(client, AgentJson.Serialize(new PingRequest { Id = Guid.NewGuid() }));
                var answer = await FrameCodec.ReadAsync(client);
                Assert.Null(answer); // сервер закрыл канал, ответа нет
            }
            catch (IOException) { /* разрыв канала тоже отказ */ }
        }

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(15))); // дождался простоя
        Assert.True(server.RejectedClients >= 1);
    }

    [Fact]
    public async Task Oversized_first_frame_is_dropped_before_verification()
    {
        var name = NewPipeName();
        var server = new AgentPipeServer(name, Me, ExpectSelf(), new RequestDispatcher(), TimeSpan.FromSeconds(2));
        var run = server.RunAsync(CancellationToken.None);

        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            try { await FrameCodec.WriteAsync(client, new byte[20 * 1024]); }
            catch (IOException) { }
        }

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(server.RejectedClients >= 1);
    }

    sealed class Recorder : IOperationHandler
    {
        public List<(AgentRequest Request, ClientContext Client)> Calls { get; } = [];

        public Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct)
        {
            lock (Calls) Calls.Add((request, client));
            return Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.Ok));
        }
    }

    [Fact]
    public async Task Handler_receives_the_verified_client_sid_and_not_a_field_from_the_request()
    {
        var recorder = new Recorder();
        var name = NewPipeName();
        var server = new AgentPipeServer(name, Me, ExpectSelf(), new RequestDispatcher(recorder), TimeSpan.FromSeconds(20));
        var run = server.RunAsync(CancellationToken.None);
        var modeId = TweakCatalog.Shared.Modes[0].Id;

        await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);

            // Запрос с чужим SID в теле отвергается как лишнее поле: до обработчика он не доходит.
            var spoofId = Guid.NewGuid();
            var spoof = $$"""{"type":"ApplyMode","id":"{{spoofId}}","modeId":"{{modeId}}","userSid":"S-1-5-18"}""";
            await FrameCodec.WriteAsync(client, System.Text.Encoding.UTF8.GetBytes(spoof));
            var rejected = AgentJson.DeserializeResponse((await FrameCodec.ReadAsync(client))!);
            Assert.False(rejected.Ok);
            Assert.Equal("bad-request", rejected.Code);
            Assert.Empty(recorder.Calls);

            var id = Guid.NewGuid();
            await FrameCodec.WriteAsync(client, AgentJson.Serialize(new ApplyModeRequest { Id = id, ModeId = modeId }));
            var ok = AgentJson.DeserializeResponse((await FrameCodec.ReadAsync(client))!);
            Assert.True(ok.Ok);
        }

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(Me.Value, call.Client.UserSid);
        Assert.Equal(Environment.ProcessId, call.Client.Pid);
        Assert.Equal(ClientProbe.CurrentSession(), call.Client.SessionId);
    }

    [Fact]
    public async Task Idle_server_stops()
    {
        var server = new AgentPipeServer(NewPipeName(), Me, ExpectSelf(), new RequestDispatcher(), TimeSpan.FromMilliseconds(300));
        Assert.False(await server.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Probe_describes_a_real_client_process()
    {
        // Сведения о самом себе совпадают с тем, что вернёт Process: проверка, что P/Invoke работает.
        Assert.Equal(Process.GetCurrentProcess().SessionId, ClientProbe.CurrentSession());
    }
}
