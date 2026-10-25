using System.Diagnostics;
using System.Security.Principal;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Cleanup;

/// <summary>Запущен ли процесс с данным именем образа у данного пользователя. Если владельца узнать нельзя, считаем «запущен» (безопасная сторона).</summary>
public sealed class WindowsProcessList : IProcessList
{
    public bool IsRunning(string imageName, string userSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageName);
        var name = imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;

        Process[] found;
        try { found = Process.GetProcessesByName(name); }
        catch { return true; }

        try
        {
            foreach (var p in found)
            {
                if (string.IsNullOrEmpty(userSid)) return true;
                var owner = OwnerSid(p.Id);
                if (owner == null) return true; // не смогли определить: безопаснее считать запущенным
                if (string.Equals(owner, userSid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            foreach (var p in found) p.Dispose();
        }
    }

    // null: процесс исчез между перечислением и открытием либо доступ отказан. Исчезнувший не мешает, но отличить его
    // от отказа нельзя без повторной проверки, поэтому выше null трактуется как «запущен».
    static string? OwnerSid(int pid)
    {
        var proc = Win32Cleanup.OpenProcess(Win32Cleanup.ProcessQueryLimitedInformation, false, pid);
        if (proc == IntPtr.Zero) return null;
        try
        {
            if (!Win32Cleanup.OpenProcessToken(proc, Win32Cleanup.TokenQuery, out var token)) return null;
            try
            {
                using var id = new WindowsIdentity(token);
                return id.User?.Value;
            }
            catch { return null; }
            finally { Win32Cleanup.CloseHandle(token); }
        }
        finally { Win32Cleanup.CloseHandle(proc); }
    }
}
