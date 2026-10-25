using System.Runtime.InteropServices;
using System.Security.Principal;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Cleanup;

/// <summary>Корзина текущего токена (все тома). Очистка без диалога, звука и прогресса.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public long QueryBytes()
    {
        var info = new Win32Cleanup.ShQueryRbInfo { Size = (uint)Marshal.SizeOf<Win32Cleanup.ShQueryRbInfo>() };
        var hr = Win32Cleanup.SHQueryRecycleBin(null, ref info);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return info.SizeBytes;
    }

    public void Empty()
    {
        var hr = Win32Cleanup.SHEmptyRecycleBin(IntPtr.Zero, null, 7);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public string CurrentTokenSid()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value ?? throw new InvalidOperationException("У токена нет SID пользователя.");
    }
}
