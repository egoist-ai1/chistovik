using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Setup;

/// <summary>
/// Факты о файле деинсталлятора: реальный путь после всех ссылок, владелец и права, определение NSIS. Читает через один открытый
/// хэндл (путь и защита относятся к одному объекту). Решение о доверии принимает Core.
/// </summary>
public sealed class WindowsFileSecurity : IFileSecurity
{
    const int MaxScanBytes = 2 * 1024 * 1024;
    static readonly byte[] NsisFirstHeader = [0xEF, 0xBE, 0xAD, 0xDE, .. "NullsoftInst"u8];

    static readonly FileFacts Missing = new(false, false, null, null, []);

    public FileFacts Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FileAttributes own;
        try { own = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Missing; }

        // BACKUP_SEMANTICS нужен для каталогов; ссылки по пути раскрываются (хэндл указывает на реальный объект).
        using var h = Win32Setup.CreateFile(path, Win32Setup.ReadControl | Win32Setup.FileReadAttributes, Win32Setup.FileShareAll,
            IntPtr.Zero, Win32Setup.OpenExisting, Win32Setup.FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            if (err is Win32.ErrorFileNotFound or Win32.ErrorPathNotFound) return Missing;
            throw Win32.Fail(err, "CreateFile");
        }

        var final = FinalPath(h);
        var isDir = (own & FileAttributes.Directory) != 0;
        var reparse = (own & FileAttributes.ReparsePoint) != 0;

        var descriptor = Descriptor(h);
        FileSystemSecurity security = isDir ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor);
        var (owner, aces) = AclReader.Read(security);
        return new FileFacts(true, reparse, final, owner, aces);
    }

    public bool HasNsisMarker(string finalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(finalPath);
        using var s = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var buf = new byte[(int)Math.Min(s.Length, MaxScanBytes)];
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var span = buf.AsSpan(0, read);
        // Только сигнатура первого заголовка NSIS: надпись «Nullsoft Install System» встречается и в посторонних файлах.
        return span.IndexOf(NsisFirstHeader) >= 0;
    }

    static string FinalPath(SafeFileHandle h)
    {
        var buf = new char[512];
        while (true)
        {
            var n = Win32Setup.GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0); // VOLUME_NAME_DOS, FILE_NAME_NORMALIZED
            if (n == 0) throw Win32.Fail(Marshal.GetLastWin32Error(), "GetFinalPathNameByHandle");
            if (n < buf.Length) return StripPrefix(new string(buf, 0, (int)n));
            buf = new char[n + 1];
        }
    }

    static string StripPrefix(string p) => ExtendedPath.Strip(p);

    static byte[] Descriptor(SafeFileHandle h)
    {
        const uint info = Win32Setup.OwnerSecurityInformation | Win32Setup.DaclSecurityInformation;
        Win32Setup.GetKernelObjectSecurity(h, info, null, 0, out var needed);
        if (needed == 0) throw Win32.Fail(Marshal.GetLastWin32Error(), "GetKernelObjectSecurity");
        var bytes = new byte[needed];
        if (!Win32Setup.GetKernelObjectSecurity(h, info, bytes, needed, out _))
            throw Win32.Fail(Marshal.GetLastWin32Error(), "GetKernelObjectSecurity");
        return bytes;
    }
}
