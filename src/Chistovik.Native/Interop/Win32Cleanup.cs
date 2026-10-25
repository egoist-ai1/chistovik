using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Interop;

/// <summary>Объявления Win32 для очистки диска (удаление по хэндлу, корзина, токен процесса) и обхода реестра по хэндлам. Только P/Invoke.</summary>
internal static class Win32Cleanup
{
    // ---------- Коды ошибок ----------
    public const int ErrorInvalidFunction = 1;
    public const int ErrorNotSupported = 50;
    public const int ErrorSharingViolation = 32;
    public const int ErrorDirNotEmpty = 145;
    public const int ErrorDeletePending = 303;
    public const int ErrorUserMappedFile = 1224;

    /// <summary>HRESULT_FROM_WIN32(ERROR_DIR_NOT_EMPTY): по нему Core узнаёт «каталог не пуст» в IOException.</summary>
    public const int HResultDirNotEmpty = unchecked((int)0x80070091);

    // ---------- Файлы ----------
    public const uint Delete = 0x00010000;
    public const uint FileReadAttributes = 0x0080;
    public const uint FileShareAll = 7;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint VolumeNameGuid = 0x1; // FILE_NAME_NORMALIZED = 0

    public const int FileDispositionInfoClass = 4;
    public const int FileAttributeTagInfoClass = 9;
    public const int FileDispositionInfoExClass = 21;

    public const uint DispositionDelete = 0x1, DispositionPosixSemantics = 0x2, DispositionIgnoreReadonly = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct FileDispositionInfoEx { public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileDispositionInfo { public byte DeleteFile; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTagInfo { public uint FileAttributes, ReparseTag; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDispositionInfoEx info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDispositionInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW")]
    public static extern bool GetVolumePathName(string fileName, char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    public static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char[] volumeName, uint bufferLength);

    // ---------- Корзина ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct ShQueryRbInfo
    {
        public uint Size;
        public long SizeBytes, NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    public static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    // flags 7 = SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    public static extern int SHEmptyRecycleBin(IntPtr window, string? rootPath, uint flags);

    // ---------- Процессы ----------
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---------- Реестр: обход по хэндлам ----------
    public const uint KeyRead = 0x20019;
    public const uint KeyWrite = 0x20006;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    public static extern int RegOpenKeyExSub(SafeRegistryHandle parent, string subKey, uint options, uint sam, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    public static extern int RegCreateKeyExSub(SafeRegistryHandle parent, string subKey, uint reserved, string? cls, uint options, uint sam,
        IntPtr security, out SafeRegistryHandle result, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
    public static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, char[] name, ref uint nameLen, IntPtr reserved,
        IntPtr cls, IntPtr classLen, IntPtr lastWrite);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    public static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr cls, IntPtr classLen, IntPtr reserved, out uint subKeys,
        IntPtr maxSubKeyLen, IntPtr maxClassLen, out uint values, IntPtr maxValueNameLen, IntPtr maxValueLen, IntPtr securityDescriptor, IntPtr lastWrite);

    [DllImport("ntdll.dll")]
    public static extern int NtDeleteKey(SafeRegistryHandle key);
}
