using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Chistovik.Core.Ipc;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 7. Настоящий Chistovik.Agent.exe (путь в --agent) и клиент на Core.Ipc (FrameCodec, AgentJson) поверх
/// именованного канала. Без GUI и без окна UAC: в Sandbox пользователь уже администратор, агент запускается как обычный
/// дочерний процесс. Проверяется: ACL канала, отказ чужому процессу (клиент проверяется по PID/образу/хешу), Ping,
/// ApplyTweaks с откатом, отказ неизвестному типу запроса, выход агента при простое (код 4) либо после ухода клиента (код 0).
/// </summary>
static class S7Agent
{
    const string Sc = "S7 агент";

    public static async Task Run(LiveContext c)
    {
        if (c.AgentPath is null || !File.Exists(c.AgentPath))
        {
            c.Report.Skip(Sc, "агент", "не найден Chistovik.Agent.exe (параметр --agent)");
            return;
        }

        var t = Tweaks.SimpleUserDword();
        var orig = t is null ? null : Tweaks.Read(c, t);
        var keyExisted = t is not null && Reg.KeyExists(true, c.Sid, t.Key);
        var pipe = "Chistovik.live" + Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo(c.AgentPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(c.AgentPath)!,
        };
        foreach (var a in new[] { "--pipe", pipe, "--parent-pid", Environment.ProcessId.ToString(), "--user-sid", c.Sid, "--app-path", c.SelfExe }) psi.ArgumentList.Add(a);

        Process agent;
        try { agent = Process.Start(psi) ?? throw new InvalidOperationException("процесс не создан"); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            c.Report.Fail(Sc, "запуск агента", e.Message + " (стенд должен быть запущен с правами администратора)");
            return;
        }

        var stderr = new StringBuilder();
        agent.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        agent.BeginErrorReadLine();

        try
        {
            await AclCheck(c, pipe);
            await ForeignClient(c, pipe);

            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(30000);

            var ping = await Exchange(client, new PingRequest { Id = Guid.NewGuid() });
            c.Report.Check(Sc, "Ping", ping is { Ok: true, Code: ResponseCodes.Pong }, "pong", "ответ: " + Fmt(ping));

            if (t is not null)
            {
                Tweaks.Force(c, t, Tweaks.Expect(t, false));
                var apply = await Exchange(client, Tweaks.Request(t.Id, true));
                var now = Tweaks.Read(c, t);
                c.Report.Check(Sc, $"ApplyTweaks {t.Id}", apply is { Ok: true } && Tweaks.Expect(t, true).Matches(now), $"реестр {now}", $"ответ: {Fmt(apply)}; реестр {now}");
                if (apply?.OperationId is { } op)
                {
                    var undo = await Exchange(client, new UndoRequest { Id = Guid.NewGuid(), OperationId = op });
                    var back = Tweaks.Read(c, t);
                    c.Report.Check(Sc, $"Undo {t.Id}", undo is { Ok: true } && Tweaks.Expect(t, false).Matches(back), $"реестр {back}", $"ответ: {Fmt(undo)}; реестр {back}");
                }
            }
            else c.Report.Skip(Sc, "ApplyTweaks", "нет подходящего твика");

            // неизвестный тип запроса: сырой кадр с типом вне белого списка
            var raw = Encoding.UTF8.GetBytes($"{{\"type\":\"FormatDisk\",\"id\":\"{Guid.NewGuid()}\"}}");
            await FrameCodec.WriteAsync(client, raw);
            var frame = await FrameCodec.ReadAsync(client);
            var rej = frame is null ? null : AgentJson.DeserializeResponse(frame);
            c.Report.Check(Sc, "неизвестный тип запроса отвергается", rej is { Ok: false, Code: ResponseCodes.BadRequest }, "bad-request", "ответ: " + Fmt(rej));

            // простой / уход клиента
            if (c.SkipIdle)
            {
                await client.DisposeAsync();
                var exited = agent.WaitForExit(15000);
                c.Report.Check(Sc, "выход агента после ухода клиента", exited && agent.ExitCode == 0, "код 0", $"вышел: {exited}, код {(exited ? agent.ExitCode.ToString() : "-")}");
            }
            else
            {
                var sw = Stopwatch.StartNew();
                var exited = await Task.Run(() => agent.WaitForExit(TimeSpan.FromMinutes(6.5)));
                c.Report.Check(Sc, "выход агента по простою (5 мин)", exited && agent.ExitCode == 4,
                    $"код 4 через {sw.Elapsed.TotalSeconds:F0} с", $"вышел: {exited}, код {(exited ? agent.ExitCode.ToString() : "-")}, прошло {sw.Elapsed.TotalSeconds:F0} с");
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException or System.Text.Json.JsonException)
        {
            c.Report.Fail(Sc, "обмен с агентом", e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            try { if (!agent.HasExited) agent.Kill(true); } catch (InvalidOperationException) { }
            string log;
            lock (stderr) log = stderr.ToString().Trim();
            if (log.Length > 0) c.Report.Pass(Sc, "журнал stderr агента (справка)", log.Length > 1500 ? log[^1500..] : log);
            agent.Dispose();
            if (t is not null && orig is not null)
            {
                Reg.Restore(true, c.Sid, t.Key, t.Value, orig);
                if (!keyExisted) Reg.DeleteTree(true, c.Sid, t.Key);
            }
        }
    }

    static string Fmt(AgentResponse? r) => r is null ? "<нет ответа>" : $"{r.Code} {r.Message}";

    static async Task<AgentResponse?> Exchange(Stream s, AgentRequest request)
    {
        await FrameCodec.WriteAsync(s, AgentJson.Serialize(request));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var frame = await FrameCodec.ReadAsync(s, cts.Token);
        return frame is null ? null : AgentJson.DeserializeResponse(frame);
    }

    /// <summary>ACL канала: запрет сети, доступ пользователю и SYSTEM, никому больше.</summary>
    static async Task AclCheck(LiveContext c, string pipe)
    {
        PipeSecurity sec;
        await using (var probe = new NamedPipeClientStream(".", pipe, PipeAccessRights.ReadPermissions | PipeAccessRights.ReadData | PipeAccessRights.WriteData,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification, HandleInheritability.None))
        {
            await probe.ConnectAsync(30000);
            sec = probe.GetAccessControl();
        }

        var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        string Who(PipeAccessRule r) => ((SecurityIdentifier)r.IdentityReference).Value;
        var denyNetwork = rules.Any(r => r.AccessControlType == AccessControlType.Deny && Who(r) == "S-1-5-2");
        var allowUser = rules.Any(r => r.AccessControlType == AccessControlType.Allow && string.Equals(Who(r), c.Sid, StringComparison.OrdinalIgnoreCase));
        var allowSystem = rules.Any(r => r.AccessControlType == AccessControlType.Allow && Who(r) == "S-1-5-18");
        string[] broad = ["S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4"];
        var extra = rules.Where(r => r.AccessControlType == AccessControlType.Allow && broad.Contains(Who(r))).Select(Who).ToArray();
        var text = string.Join("; ", rules.Select(r => $"{r.AccessControlType} {Who(r)} {r.PipeAccessRights}"));
        c.Report.Check(Sc, "ACL канала: запрет сети, доступ пользователю и SYSTEM, без широких групп",
            denyNetwork && allowUser && allowSystem && extra.Length == 0, text,
            $"deny-сеть: {denyNetwork}, пользователь: {allowUser}, SYSTEM: {allowSystem}, широкие группы: {string.Join(",", extra)}; правила: {text}");
    }

    /// <summary>Чужой процесс (не тот PID и образ, что агент получил в --parent-pid/--app-path) должен быть отключён без ответа.</summary>
    static async Task ForeignClient(LiveContext c, string pipe)
    {
        var outFile = Path.Combine(c.WorkDir, "foreign-" + Guid.NewGuid().ToString("N") + ".txt");
        var psi = new ProcessStartInfo(c.SelfExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--child-client", "--pipe", pipe, "--out", outFile, "--token", c.Token }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("чужой клиент не запущен");
        if (!p.WaitForExit(60000)) { p.Kill(true); c.Report.Fail(Sc, "проверка клиента: чужой процесс", "дочерний клиент завис"); return; }
        var text = File.Exists(outFile) ? File.ReadAllText(outFile) : "<нет файла результата>";
        c.Report.Check(Sc, "проверка клиента: чужому процессу отказано без ответа", text.StartsWith("refused", StringComparison.Ordinal), text, "чужой процесс не был отключён: " + text);
    }
}
