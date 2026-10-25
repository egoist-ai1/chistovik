using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Interop;

/// <summary>Объявления Win32 для удаления программ: Windows Installer, запуск процесса в задании, чтение защиты файла. Только P/Invoke.</summary>
internal static class Win32Setup
{
    // ---------- Windows Installer ----------
    public const uint InstallUiLevelNone = 2;
    public const int InstallLevelDefault = 0;
    public const int InstallStateAbsent = 2;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    public static extern int MsiQueryProductState(string product);

    [DllImport("msi.dll", EntryPoint = "MsiSetInternalUI")]
    public static extern uint MsiSetInternalUI(uint uiLevel, IntPtr window);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiConfigureProductExW")]
    public static extern uint MsiConfigureProductEx(string product, int installLevel, int installState, string commandLine);

    // ---------- Процесс и задание ----------
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateUnicodeEnvironment = 0x00000400;
    public const uint CreateNoWindow = 0x08000000;

    public const uint WaitObject0 = 0, WaitTimeout = 0x102, Infinite = 0xFFFFFFFF;

    public const int JobObjectAssociateCompletionPortInformation = 7;

    public const uint JobObjectMsgActiveProcessZero = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public uint Cb;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobAssociateCompletionPort
    {
        public IntPtr CompletionKey, CompletionPort;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    public static extern bool CreateProcess(string? application, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    // hToken = NULL и inherit = false: блок только из системных переменных.
    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobAssociateCompletionPort info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existingPort, UIntPtr key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetQueuedCompletionStatus(IntPtr port, out uint bytes, out UIntPtr key, out IntPtr overlapped, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    // ---------- Защита файла ----------
    public const uint ReadControl = 0x00020000;
    public const uint FileReadAttributes = 0x0080;
    public const uint FileShareAll = 7;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    public const uint OwnerSecurityInformation = 0x1, DaclSecurityInformation = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW")]
    public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] buffer, uint length, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);
}
