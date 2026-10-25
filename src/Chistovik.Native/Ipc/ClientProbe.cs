using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Chistovik.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Ipc;

/// <summary>Собирает сведения о процессе на другом конце именованного канала. Только чтение.</summary>
public static class ClientProbe
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ProcessIdToSessionId(uint pid, out uint session);

    /// <summary>Сведения о клиенте канала; при любой ошибке соответствующие поля остаются пустыми (проверка их отвергнет).</summary>
    public static ActualClient Probe(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            return new ActualClient(-1, null, null, -1, null);

        int session = ProcessIdToSessionId(pid, out var s) ? (int)s : -1;
        var image = ImagePathOf(pid);

        string? hash = null;
        if (image is not null)
        {
            try { hash = ClientVerifier.Sha256OfFile(image); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        string? sid = null;
        try { pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { }

        return new ActualClient((int)pid, image, hash, session, sid);
    }

    internal static string? ImagePathOf(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Путь образа текущего процесса в том виде, в каком его видит ядро (ссылки и соединения папок уже раскрыты):
    /// ровно так агент увидит нас на другом конце канала. Environment.ProcessPath этого не гарантирует.
    /// </summary>
    public static string SelfImagePath()
        => ImagePathOf((uint)Environment.ProcessId) ?? Environment.ProcessPath ?? "";

    /// <summary>Сеанс текущего процесса.</summary>
    public static int CurrentSession()
        => ProcessIdToSessionId((uint)Environment.ProcessId, out var s) ? (int)s : throw new Win32Exception(Marshal.GetLastWin32Error());
}
