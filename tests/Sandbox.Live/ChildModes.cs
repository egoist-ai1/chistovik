using System.IO.Pipes;
using Chistovik.Agent;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.Native.Ipc;

namespace Sandbox.Live;

/// <summary>Режимы, в которых стенд запускает самого себя дочерним процессом.</summary>
static class ChildModes
{
    /// <summary>--reader БД --out ФАЙЛ: читает журнал без прав записи (его запускают через runas /trustlevel).</summary>
    public static int Reader(Options o)
    {
        var db = o.Get("--reader")!;
        var outFile = o.Get("--out")!;
        string text;
        try
        {
            using var r = new SqliteJournalReader(db);
            var list = r.List(0, 20);
            text = $"ok;admin={Sids.IsAdmin()};count={list.Count};states={string.Join(",", list.Select(x => x.State))}";
        }
        catch (Exception e)
        {
            text = $"error;admin={Sids.IsAdmin()};{e.GetType().Name}: {e.Message}";
        }
        File.WriteAllText(outFile, text);
        return 0;
    }

    /// <summary>
    /// --child-hang КАТАЛОГ --tweak ID --enable 1|0 --signal ФАЙЛ, режим в переменной окружения CHISTOVIK_LIVE_HANG
    /// (after-write | before-write): применяет твик через настоящий обработчик и «зависает» вокруг первой записи реестра.
    /// Родитель убивает процесс; журнал остаётся с pending-шагом.
    /// </summary>
    public static int Hang(Options o)
    {
        var mode = Environment.GetEnvironmentVariable("CHISTOVIK_LIVE_HANG");
        if (mode is not ("after-write" or "before-write")) return 2;
        var dir = o.Get("--child-hang")!;
        var signal = o.Get("--signal")!;
        var ports = AgentPorts.Windows();
        ports = ports with { Registry = new HangingRegistry(ports.Registry, mode, signal) };
        using var comp = AgentComposition.Build(ports, dir, PolicyContext.FromEnvironment());
        if (comp.Mode != JournalMode.Ready) return 5;
        var request = Tweaks.Request(o.Get("--tweak")!, o.Get("--enable") == "1");
        var client = new ClientContext(Sids.Current(), Environment.ProcessId, ClientProbe.CurrentSession());
        comp.Handler.HandleAsync(request, client, CancellationToken.None).GetAwaiter().GetResult();
        return 0; // сюда доходит только если «зависания» не случилось
    }

    /// <summary>--child-client --pipe ИМЯ --out ФАЙЛ: чужой процесс подключается к каналу агента и шлёт Ping; ждём отказ.</summary>
    public static async Task<int> ForeignClient(Options o)
    {
        var pipe = o.Get("--pipe")!;
        var outFile = o.Get("--out")!;
        // --wait-file: подключаться только когда родитель создаст файл (агент к этому времени уже открыл канал).
        var waitFile = o.Get("--wait-file");
        if (waitFile is not null)
            for (var i = 0; i < 600 && !File.Exists(waitFile); i++) await Task.Delay(100);
        string text;
        try
        {
            await using var s = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await s.ConnectAsync(20000);
            await FrameCodec.WriteAsync(s, AgentJson.Serialize(new PingRequest { Id = Guid.NewGuid() }));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var frame = await FrameCodec.ReadAsync(s, cts.Token);
            text = frame is null
                ? "refused:eof"
                : "ACCEPTED:" + AgentJson.DeserializeResponse(frame).Code;
        }
        catch (IOException e) { text = "refused:io:" + e.Message; }
        catch (InvalidDataException e) { text = "refused:data:" + e.Message; }
        catch (OperationCanceledException) { text = "no-answer:timeout"; }
        catch (TimeoutException) { text = "no-connect:timeout"; }
        File.WriteAllText(outFile, text);
        return 0;
    }
}

/// <summary>Реестр, который после (или перед) первой записью замирает навсегда: так имитируется гибель процесса посреди шага.</summary>
sealed class HangingRegistry(IRegistry inner, string mode, string signal) : IRegistry
{
    public IRegKeyHandle? Open(RegLocation loc, bool write, bool createKey)
    {
        var h = inner.Open(loc, write, createKey);
        return h is null ? null : new HangingHandle(h, mode, signal);
    }
}

sealed class HangingHandle(IRegKeyHandle inner, string mode, string signal) : IRegKeyHandle
{
    public string ResolvedPath => inner.ResolvedPath;
    public bool IsSymbolicLink => inner.IsSymbolicLink;
    public RegValueState Read(string name) => inner.Read(name);
    public IReadOnlyList<string> ValueNames() => inner.ValueNames();

    public void Write(ApprovedRegistryWrite w)
    {
        if (mode == "before-write") Hang();
        inner.Write(w);
        if (mode == "after-write") Hang();
    }

    void Hang()
    {
        File.WriteAllText(signal, "hang");
        Thread.Sleep(Timeout.Infinite);
    }

    public void Dispose() => inner.Dispose();
}
