using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Interop;

/// <summary>Объявления Win32 для реестра, служб и питания. Только P/Invoke и коды ошибок, без решений.</summary>
internal static class Win32
{
    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorInvalidLevel = 124;
    public const int ErrorMoreData = 234;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorServiceDoesNotExist = 1060;

    public static Win32Exception Fail(int code, string what) =>
        new(code, $"{what}: {new Win32Exception(code).Message} (код {code})");

    // ---------- Реестр ----------
    public static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    public static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));

    public const uint RegOptionOpenLink = 0x00000008;
    public const uint KeyQueryValue = 0x0001;
    public const uint KeySetValue = 0x0002;
    public const uint KeyEnumerateSubKeys = 0x0008;
    public const uint KeyNotify = 0x0010;
    public const uint KeyWow64_64Key = 0x0100;

    public const uint RegNone = 0, RegSz = 1, RegExpandSz = 2, RegBinary = 3, RegDword = 4, RegLink = 6, RegMultiSz = 7, RegQword = 11;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    public static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, uint sam, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    public static extern int RegCreateKeyEx(IntPtr hKey, string subKey, uint reserved, string? cls, uint options, uint sam,
        IntPtr security, out SafeRegistryHandle result, out uint disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    public static extern int RegQueryValueEx(SafeRegistryHandle key, string? name, IntPtr reserved, out uint type, byte[]? data, ref uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    public static extern int RegSetValueEx(SafeRegistryHandle key, string? name, uint reserved, uint type, byte[] data, uint size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegDeleteValueW")]
    public static extern int RegDeleteValue(SafeRegistryHandle key, string? name);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumValueW")]
    public static extern int RegEnumValue(SafeRegistryHandle key, uint index, char[] name, ref uint nameLen, IntPtr reserved,
        IntPtr type, IntPtr data, IntPtr dataLen);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryKey(SafeRegistryHandle key, int infoClass, IntPtr buffer, uint length, out uint resultLength);

    // ---------- Службы ----------
    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerEnumerateService = 0x0004;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceEnumerateDependents = 0x0008;
    public const uint ServiceNoChange = 0xFFFFFFFF;
    public const uint ServiceWin32 = 0x30;
    public const uint ServiceStateAll = 3;
    public const uint ServiceRunning = 4;
    public const int ScEnumProcessInfo = 0;
    public const int ScStatusProcessInfo = 0;
    public const uint ServiceConfigDelayedAutoStartInfo = 3;
    public const uint ServiceConfigTriggerInfo = 8;
    public const uint ServiceConfigLaunchProtected = 12;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    public static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    public static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    public static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer,
        uint size, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    public static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    public static extern bool QueryServiceConfig2(IntPtr service, uint level, IntPtr buffer, uint size, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "QueryServiceStatusEx")]
    public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint size, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "EnumDependentServicesW")]
    public static extern bool EnumDependentServices(IntPtr service, uint state, IntPtr buffer, uint size, out uint bytesNeeded, out uint count);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    public static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    public static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref int info);

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryServiceConfigW
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName, DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    // ---------- Питание ----------
    public const uint AccessScheme = 16;

    [DllImport("powrprof.dll")]
    public static extern uint PowerGetActiveScheme(IntPtr userRoot, out IntPtr schemeGuid);

    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint flags, uint index, IntPtr buffer, ref uint size);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    public static extern uint PowerSetActiveScheme(IntPtr userRoot, ref Guid scheme);

    [DllImport("powrprof.dll")]
    public static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, ref IntPtr destination);

    [DllImport("powrprof.dll")]
    public static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);

    [DllImport("powrprof.dll")]
    public static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buffer, uint size);

    /// <summary>Имя схемы (UTF-16 с завершающим NUL). buffer = IntPtr.Zero: в size вернётся нужный размер в байтах.</summary>
    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);
}
