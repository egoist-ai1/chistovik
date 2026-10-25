using System.Security.AccessControl;
using System.Security.Principal;

namespace Chistovik.Native.Ipc;

/// <summary>Коды выхода Chistovik.Agent.exe. Приложение по ним понимает, почему агент не запустился.</summary>
public static class AgentExitCodes
{
    public const int Ok = 0;
    public const int BadArgs = 2;
    /// <summary>Другой агент уже работает на этом компьютере (возможно, в другом сеансе Windows).</summary>
    public const int AlreadyRunning = 3;
    public const int Idle = 4;
    public const int BadEnvironment = 5;
}

/// <summary>
/// Один агент на весь компьютер: именованный мьютекс в глобальном пространстве имён. Второй агент (другой пользователь при быстром
/// переключении, второй сеанс) обязан выйти до восстановления журнала и до любых записей, иначе он закроет чужую идущую операцию
/// как прерванную и оба процесса будут писать в HKLM и журнал одновременно.
/// Доступ к мьютексу только у администраторов и SYSTEM: агент всегда запущен с повышением, обычному пользователю открывать его не нужно,
/// а лишние права (SYNCHRONIZE) позволили бы ему занять мьютекс и удерживать его. Если мьютекс уже есть, но открыть его нельзя
/// (чужой DACL), это считается «занято»: безопаснее не стартовать.
/// </summary>
public sealed class AgentInstanceLock : IDisposable
{
    public const string MachineName = @"Global\Chistovik.Agent";

    readonly Mutex _mutex;

    AgentInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>Захватывает «место» агента. null — уже занято другим процессом.</summary>
    public static AgentInstanceLock? TryAcquire(string name = MachineName)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        try
        {
            // Владение не берём: мьютекс служит именем-замком, а не блокировкой потока (Main агента асинхронный).
            var mutex = MutexAcl.Create(false, name, out var created, security);
            if (created) return new AgentInstanceLock(mutex);
            mutex.Dispose();
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    public void Dispose() => _mutex.Dispose();
}
