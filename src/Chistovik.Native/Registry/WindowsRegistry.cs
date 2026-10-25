using System.Security.Principal;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Registry;

/// <summary>
/// Реестр Windows: только HKLM и HKU\&lt;SID&gt; (HKCU не открывается вообще), всегда Registry64.
/// Последний компонент пути открывается с REG_OPTION_OPEN_LINK, поэтому символическая ссылка не разыменовывается
/// и видна через <see cref="IRegKeyHandle.IsSymbolicLink"/>. Решения о допустимости принимает Core.
/// </summary>
public sealed class WindowsRegistry : IRegistry
{
    public IRegKeyHandle? Open(RegLocation loc, bool write, bool createKey)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var (root, subKey) = Resolve(loc);

        // Создание ключа подразумевает право записи.
        var effectiveWrite = write || createKey;
        var sam = Win32.KeyQueryValue | Win32.KeyEnumerateSubKeys | Win32.KeyNotify | Win32.KeyWow64_64Key;
        if (effectiveWrite) sam |= Win32.KeySetValue;

        int rc;
        SafeRegistryHandle handle;
        if (createKey)
            rc = Win32.RegCreateKeyEx(root, subKey, 0, null, Win32.RegOptionOpenLink, sam, IntPtr.Zero, out handle, out _);
        else
            rc = Win32.RegOpenKeyEx(root, subKey, Win32.RegOptionOpenLink, sam, out handle);

        if (rc == Win32.ErrorFileNotFound || rc == Win32.ErrorPathNotFound)
        {
            handle.Dispose();
            return null; // порт: «ключа нет»
        }
        if (rc == Win32.ErrorAccessDenied)
        {
            // Отказ в доступе не «ключа нет»: Core превращает UnauthorizedAccessException в код шага access-denied.
            handle.Dispose();
            throw new UnauthorizedAccessException($"Отказано в доступе к ключу реестра {Describe(loc)} (ERROR_ACCESS_DENIED)");
        }
        if (rc != Win32.ErrorSuccess)
        {
            handle.Dispose();
            throw Win32.Fail(rc, $"RegOpenKeyEx {Describe(loc)}");
        }
        return new WindowsRegKeyHandle(handle, loc, effectiveWrite);
    }

    internal static string Describe(RegLocation loc) =>
        loc.Root == RegRoot.Machine ? $"HKLM\\{loc.KeyPath}" : $"HKU\\{loc.UserSid}\\{loc.KeyPath}";

    internal static (IntPtr Root, string SubKey) Resolve(RegLocation loc)
    {
        var path = (loc.KeyPath ?? string.Empty).Trim('\\');
        switch (loc.Root)
        {
            case RegRoot.Machine:
                return (Win32.HkeyLocalMachine, path);
            case RegRoot.User:
                if (string.IsNullOrEmpty(loc.UserSid))
                    throw new ArgumentException("Для RegRoot.User нужен UserSid.", nameof(loc));
                SecurityIdentifier sid;
                try { sid = new SecurityIdentifier(loc.UserSid); }
                catch (ArgumentException e) { throw new ArgumentException("UserSid не является SID.", nameof(loc), e); }
                var sub = sid.Value + (path.Length == 0 ? string.Empty : "\\" + path);
                return (Win32.HkeyUsers, sub);
            default:
                throw new ArgumentOutOfRangeException(nameof(loc), "Неизвестный корень реестра.");
        }
    }
}
