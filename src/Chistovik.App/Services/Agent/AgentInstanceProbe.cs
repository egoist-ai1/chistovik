using System.Runtime.InteropServices;

namespace Chistovik.App.Services.Agent;

/// <summary>
/// Узнаёт без прав администратора, держит ли кто-то машинный мьютекс агента. За интерфейсом, чтобы подставлять в тестах.
/// </summary>
public interface IAgentInstanceProbe
{
    /// <summary>true — мьютекс агента существует (агент ещё живёт или имя занято); false — его нет.</summary>
    bool IsHeld();
}

/// <summary>
/// OpenMutexW(SYNCHRONIZE) на <c>Global\Chistovik.Agent</c>. DACL мьютекса даёт доступ только администраторам и SYSTEM, поэтому
/// приложение без повышения получает ERROR_ACCESS_DENIED: это и значит «мьютекс есть». ERROR_FILE_NOT_FOUND — мьютекса нет.
/// Любая другая ошибка считается «есть»: лучше показать сообщение, чем без нужды запрашивать UAC второй раз.
/// </summary>
public sealed class MutexAgentInstanceProbe(string name = Chistovik.Native.Ipc.AgentInstanceLock.MachineName) : IAgentInstanceProbe
{
    const uint Synchronize = 0x00100000;
    const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenMutexW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public bool IsHeld()
    {
        var handle = OpenMutexW(Synchronize, false, name);
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastWin32Error() != ErrorFileNotFound;
    }
}
