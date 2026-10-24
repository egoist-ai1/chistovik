using System.Runtime.InteropServices;
using System.Security.Principal;
using Chistovik.Core.Ports;

namespace Chistovik.Native.Env;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class GuidGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}

/// <summary>Перечисление файлов. Несуществующий каталог даёт пустой список (папки автозагрузки могут отсутствовать).</summary>
public sealed class WindowsFileSystem : IFileSystem
{
    public IReadOnlyList<string> ListFiles(string dir, string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(dir);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Папки автозагрузки. Ограничение: для SID пользователя текущего процесса используется SHGetKnownFolderPath (учитывает
/// перенаправление папок). Для любого другого SID берётся профиль из HKLM\...\ProfileList и стандартный подпуть
/// AppData\Roaming\...\Startup: перенаправленная папка автозагрузки чужого профиля не распознаётся. Профиль не найден — null.
/// </summary>
public sealed class WindowsUserFolders : IUserFolders
{
    private const string StartupSubPath = @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    private static readonly Guid FolderStartup = new("B97D20BB-F46A-4C97-BA10-5E3608430854");
    private static readonly Guid FolderCommonStartup = new("82A5EA35-D9CD-47C5-9629-E15D2F714E6E");

    public string? StartupFolder(string userSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(userSid);
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(userSid); }
        catch (ArgumentException e) { throw new ArgumentException("userSid не является SID.", nameof(userSid), e); }

        using var me = WindowsIdentity.GetCurrent();
        if (me.User is { } current && current.Equals(sid)) return KnownFolder(FolderStartup);

        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid.Value}", writable: false);
        var profile = key?.GetValue("ProfileImagePath", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(profile)) return null;
        return Path.Combine(Environment.ExpandEnvironmentVariables(profile), StartupSubPath);
    }

    public string? CommonStartupFolder() => KnownFolder(FolderCommonStartup);

    private static string? KnownFolder(Guid id)
    {
        var hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p);
        try { return hr == 0 ? Marshal.PtrToStringUni(p) : null; }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
