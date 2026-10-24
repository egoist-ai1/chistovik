using System.IO;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;

namespace App.Tests;

/// <summary>Байтовый канал в памяти с отменой и EOF: заменяет именованный канал в тестах.</summary>
public sealed class ByteChannel
{
    readonly object _lock = new();
    readonly Queue<byte> _queue = new();
    bool _closed;
    TaskCompletionSource? _signal;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_closed) throw new IOException("канал закрыт");
            foreach (var b in data) _queue.Enqueue(b);
            Wake();
        }
    }

    public void Close()
    {
        lock (_lock) { _closed = true; Wake(); }
    }

    void Wake()
    {
        _signal?.TrySetResult();
        _signal = null;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> dst, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    var n = Math.Min(dst.Length, _queue.Count);
                    for (var i = 0; i < n; i++) dst.Span[i] = _queue.Dequeue();
                    return n;
                }
                if (_closed) return 0;
                _signal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _signal.Task;
            }
            await wait.WaitAsync(ct);
        }
    }
}

public sealed class DuplexStream(ByteChannel read, ByteChannel write) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        write.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { write.Close(); read.Close(); }
        base.Dispose(disposing);
    }

    public static (DuplexStream Client, DuplexStream Agent) CreatePair()
    {
        var a = new ByteChannel();
        var b = new ByteChannel();
        return (new DuplexStream(b, a), new DuplexStream(a, b));
    }
}

public sealed class FakeLauncher : IAgentLauncher
{
    public AgentLaunchStatus Status { get; set; } = AgentLaunchStatus.Started;
    public List<AgentLaunchArgs> Calls { get; } = new();

    public Task<AgentLaunchResult> LaunchAsync(AgentLaunchArgs args, CancellationToken ct)
    {
        Calls.Add(args);
        return Task.FromResult(new AgentLaunchResult(Status, Status == AgentLaunchStatus.Failed ? "не запустился" : null));
    }
}

/// <summary>Транспорт-подставка с «агентом» на другом конце: отвечает по обработчику, записывает запросы.</summary>
public sealed class FakeAgentHost : IAgentTransport
{
    readonly object _lock = new();
    int _attempts;

    public List<AgentRequest> Received { get; } = new();
    public List<string> Pipes { get; } = new();

    /// <summary>Номер попытки подключения (с 1) → true, если подключение должно упасть.</summary>
    public Func<int, bool> FailConnect { get; set; } = _ => false;

    /// <summary>Ответ на запрос (индекс с 0); null — «агент» закрывает канал, ничего не ответив.</summary>
    public Func<AgentRequest, int, AgentResponse?> Handler { get; set; } = (r, _) => DefaultReply(r);

    /// <summary>true — агент читает запрос, но не отвечает (проверка таймаута и отмены).</summary>
    public bool Hang { get; set; }

    public int Count { get { lock (_lock) return Received.Count; } }

    public List<T> Of<T>() where T : AgentRequest
    {
        lock (_lock) return Received.OfType<T>().ToList();
    }

    public static AgentResponse DefaultReply(AgentRequest r) => r is PingRequest
        ? new AgentResponse(r.Id, true, ResponseCodes.Pong)
        : new AgentResponse(r.Id, true, ResponseCodes.Ok, null, Guid.NewGuid());

    public Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        int attempt;
        lock (_lock) { attempt = ++_attempts; Pipes.Add(pipeName); }
        if (FailConnect(attempt)) throw new IOException("канал недоступен");

        var (client, agent) = DuplexStream.CreatePair();
        _ = Task.Run(() => RunAgentAsync(agent));
        return Task.FromResult<Stream>(client);
    }

    async Task RunAgentAsync(DuplexStream agent)
    {
        try
        {
            while (true)
            {
                var frame = await FrameCodec.ReadAsync(agent);
                if (frame is null) return;
                var request = AgentJson.DeserializeRequest(frame);
                int index;
                lock (_lock) { index = Received.Count; Received.Add(request); }
                if (Hang) { await Task.Delay(Timeout.Infinite); }
                var reply = Handler(request, index);
                if (reply is null) { agent.Dispose(); return; }
                await FrameCodec.WriteAsync(agent, AgentJson.Serialize(reply));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}

/// <summary>Агент-клиент без процесса: фиксирует запросы, отвечает по обработчику.</summary>
public sealed class FakeAgentClient : IAgentClient
{
    public AgentAccess Access { get; set; } = AgentAccess.Connected;
    public bool IsElevated => Access == AgentAccess.Connected;
    public List<AgentRequest> Sent { get; } = new();
    public Func<AgentRequest, AgentResponse> Handler { get; set; } = FakeAgentHostReply;
    public int RetryCalls { get; private set; }

    public event EventHandler? AccessChanged;

    static AgentResponse FakeAgentHostReply(AgentRequest r) => FakeAgentHost.DefaultReply(r);

    public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default)
    {
        Sent.Add(request);
        return Task.FromResult(Handler(request));
    }

    public void AllowElevationRetry()
    {
        RetryCalls++;
        SetAccess(AgentAccess.NotStarted);
    }

    public void SetAccess(AgentAccess a)
    {
        Access = a;
        AccessChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FakeEffects : IShellEffects
{
    public List<string> Applied { get; } = new();
    public int Restarts { get; private set; }
    public void Apply(string effect) => Applied.Add(effect);
    public void RestartExplorer() => Restarts++;
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<ConfirmContent> Confirms { get; } = new();
    public Func<IReadOnlyList<ConflictInfo>, IReadOnlyList<UndoResolution>?>? ConflictAnswer { get; set; }
    public int ConflictCalls { get; private set; }

    public void ShowStub(string title, string message) { }

    public bool Confirm(ConfirmContent content)
    {
        Confirms.Add(content);
        return ConfirmAnswer;
    }

    public IReadOnlyList<UndoResolution>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts)
    {
        ConflictCalls++;
        return ConflictAnswer?.Invoke(conflicts);
    }
}

public sealed class FakeNavigator : INavigator
{
    public List<string> Opened { get; } = new();
    public void Open(string title) => Opened.Add(title);
}

public static class Make
{
    public static ChangeService Changes(FakeAgentClient agent, FakeEffects? effects = null)
        => new(agent, effects ?? new FakeEffects());
}
