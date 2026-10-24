using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Chistovik.Core.Ipc;

namespace Chistovik.Native.Ipc;

/// <summary>
/// Канал агента. Доступ по ACL только SID пользователя и SYSTEM, сетевым клиентам отказ. После подключения клиент
/// проверяется (PID, путь образа, SHA-256, сеанс, SID); не прошедший проверку отключается без ответа.
/// Один клиент за раз; после ухода проверенного клиента или простоя сервер завершается.
/// </summary>
public sealed class AgentPipeServer(
    string pipeName,
    SecurityIdentifier userSid,
    ExpectedClient expected,
    RequestDispatcher dispatcher,
    TimeSpan idleTimeout,
    Action<string>? log = null)
{
    const int PreAuthMaxFrame = 16 * 1024;

    public int RejectedClients { get; private set; }

    PipeSecurity BuildSecurity()
    {
        var ps = new PipeSecurity();
        // Запрет сетевым клиентам должен идти раньше разрешений.
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    /// <summary>Возвращает true, если был проверенный клиент и он отключился штатно; false при простое.</summary>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleTimeout);

        while (!idle.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, BuildSecurity());

            try { await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }

            try
            {
                // Имперсонация клиента (проверка SID) возможна только после первого прочтения из канала,
                // поэтому читаем первый кадр с малым пределом, но не обрабатываем его до проверки.
                var frame = await FrameCodec.ReadAsync(pipe, idle.Token, PreAuthMaxFrame).ConfigureAwait(false);
                if (frame is null) continue;

                var (ok, reason) = ClientVerifier.Verify(expected, ClientProbe.Probe(pipe));
                if (!ok)
                {
                    RejectedClients++;
                    log?.Invoke("client-rejected:" + reason);
                    continue; // using закрывает канал без ответа; ждём следующего
                }

                log?.Invoke("client-accepted");
                while (true)
                {
                    var response = await dispatcher.HandleAsync(frame).ConfigureAwait(false);
                    await FrameCodec.WriteAsync(pipe, AgentJson.Serialize(response), idle.Token).ConfigureAwait(false);

                    idle.CancelAfter(idleTimeout);
                    var next = await FrameCodec.ReadAsync(pipe, idle.Token).ConfigureAwait(false);
                    if (next is null) return true;
                    frame = next;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (InvalidDataException e) { RejectedClients++; log?.Invoke("bad-frame:" + e.Message); }
            catch (IOException e) { log?.Invoke("io:" + e.Message); }
            // повреждённый кадр или обрыв: сессия закрыта, ждём нового клиента до конца таймера
        }
        return false;
    }
}
