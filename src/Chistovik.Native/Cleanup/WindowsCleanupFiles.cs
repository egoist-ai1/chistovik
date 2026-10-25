using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;
using Microsoft.Win32.SafeHandles;

namespace Chistovik.Native.Cleanup;

/// <summary>
/// Файловые операции очистки диска. Удаление только по открытому хэндлу (FILE_FLAG_OPEN_REPARSE_POINT): ссылка не разыменовывается,
/// <c>File.Delete</c>/<c>Directory.Delete</c> не используются. Решения о допустимости принимает Core.
/// </summary>
public sealed class WindowsCleanupFiles : ICleanupFiles
{
    static readonly FileFacts Missing = new(false, false, null, null, []);

    public FileFacts Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Один хэндл на всё: атрибуты, признак ссылки, финальный путь и ACL читаются с одного объекта, поэтому между проверками
        // нельзя подменить путь на junction. OPEN_REPARSE_POINT: ссылка на самом path открывается как ссылка (IsReparse=true, а её цель
        // не раскрывается); ссылки в родительских компонентах пути раскрываются, и FinalPath показывает реальное место, которое Core
        // сверяет с запрошенным.
        // Префикс «\\?\» (пути длиннее 260 символов) добавляется только здесь, у вызова Win32; `..` и `/` убраны до него.
        using var h = Win32Cleanup.CreateFile(ExtendedPath.Extend(path), Win32Setup.ReadControl | Win32Cleanup.FileReadAttributes, Win32Cleanup.FileShareAll,
            IntPtr.Zero, Win32Cleanup.OpenExisting, Win32Cleanup.FileFlagBackupSemantics | Win32Cleanup.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            if (err is Win32.ErrorFileNotFound or Win32.ErrorPathNotFound) return Missing;
            throw Win32.Fail(err, "CreateFile");
        }

        if (!Win32Cleanup.GetFileInformationByHandleEx(h, Win32Cleanup.FileAttributeTagInfoClass, out var tag,
                (uint)Marshal.SizeOf<Win32Cleanup.FileAttributeTagInfo>()))
            throw Win32.Fail(Marshal.GetLastWin32Error(), $"GetFileInformationByHandleEx {path}");
        var own = (FileAttributes)tag.FileAttributes;
        var isDir = (own & FileAttributes.Directory) != 0;
        var reparse = (own & FileAttributes.ReparsePoint) != 0;
        var final = FinalPath(h);
        var volume = DriveVolume(path);

        string? owner = null;
        IReadOnlyList<DirAce> aces = [];
        var descriptor = Descriptor(h);
        if (descriptor != null)
        {
            FileSystemSecurity security = isDir ? new DirectorySecurity() : new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor);
            (owner, aces) = AclReader.Read(security);
        }
        return new FileFacts(true, reparse, final, owner, aces, volume);
    }

    /// <summary>
    /// Том буквы диска запрошенного пути: «\\?\Volume{guid}» (GetVolumePathName + GetVolumeNameForVolumeMountPoint для корня «X:\»).
    /// Берётся том именно буквы, а не точки монтирования глубже по пути: если путь ведёт на другой том, Core отвергнет корень (cleanup-root-moved).
    /// null: путь не на букве диска или том не определился (subst, сеть), такой корень и так не пройдёт сверку хвоста пути.
    /// </summary>
    internal static string? DriveVolume(string path)
    {
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (root.Length != 3 || !(root[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || root[1] != ':' || root[2] != '\\') return null;

        var mount = new char[8];
        if (!Win32Cleanup.GetVolumePathName(root, mount, (uint)mount.Length)) return null;
        var name = new char[64];
        if (!Win32Cleanup.GetVolumeNameForVolumeMountPoint(new string(mount).TrimEnd('\0'), name, (uint)name.Length)) return null;
        var volume = new string(name).TrimEnd('\0').TrimEnd('\\');
        return volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ? volume : null;
    }

    public IEnumerable<DirEntryFacts> Enumerate(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        // Каталог мог исчезнуть между проверкой и обходом: это пустой результат, а не сбой.
        var it = TryStart(directory, options);
        if (it == null) yield break;
        using (it)
        {
            while (TryNext(it)) yield return it.Current;
        }
    }

    static IEnumerator<DirEntryFacts>? TryStart(string directory, EnumerationOptions options)
    {
        try { return new FileSystemEnumerable<DirEntryFacts>(directory, ToFacts, options).GetEnumerator(); }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException) { return null; }
    }

    static bool TryNext(IEnumerator<DirEntryFacts> it)
    {
        try { return it.MoveNext(); }
        catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException) { return false; }
    }

    static DirEntryFacts ToFacts(ref FileSystemEntry e) => new(
        e.FileName.ToString(),
        e.IsDirectory,
        (e.Attributes & FileAttributes.ReparsePoint) != 0,
        e.IsDirectory ? 0 : e.Length,
        e.CreationTimeUtc,
        e.LastWriteTimeUtc);

    public IDeletableHandle? OpenForDelete(string path, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // BACKUP_SEMANTICS нужен для каталогов и безвреден для файлов. OPEN_REPARSE_POINT: ссылка открывается сама, а не её цель.
        // Префикс «\\?\» добавляется только здесь, у вызова Win32; проверки Core идут над обычным путём и FinalPath хэндла.
        var h = Win32Cleanup.CreateFile(ExtendedPath.Extend(path), Win32Cleanup.Delete | Win32Cleanup.FileReadAttributes, Win32Cleanup.FileShareAll, IntPtr.Zero,
            Win32Cleanup.OpenExisting, Win32Cleanup.FileFlagBackupSemantics | Win32Cleanup.FileFlagOpenReparsePoint, IntPtr.Zero);
        if (!h.IsInvalid) return new Handle(h, path);

        var err = Marshal.GetLastWin32Error();
        h.Dispose();
        switch (err)
        {
            case Win32.ErrorFileNotFound:
            case Win32.ErrorPathNotFound:
            case Win32Cleanup.ErrorDeletePending:
                return null;
            case Win32Cleanup.ErrorSharingViolation:
                throw new SharingViolationException($"Объект занят: {path} (ERROR_SHARING_VIOLATION)");
            case Win32.ErrorAccessDenied:
                throw new UnauthorizedAccessException($"Отказано в доступе: {path} (ERROR_ACCESS_DENIED)");
            default:
                throw Win32.Fail(err, $"CreateFile {path}");
        }
    }

    internal static string FinalPath(SafeFileHandle h)
    {
        var buf = new char[512];
        while (true)
        {
            var n = Win32Cleanup.GetFinalPathNameByHandle(h, buf, (uint)buf.Length, Win32Cleanup.VolumeNameGuid); // + FILE_NAME_NORMALIZED (0)
            if (n == 0) throw Win32.Fail(Marshal.GetLastWin32Error(), "GetFinalPathNameByHandle");
            if (n < buf.Length) return new string(buf, 0, (int)n);
            buf = new char[n + 1];
        }
    }

    static byte[]? Descriptor(SafeFileHandle h)
    {
        const uint info = Win32Setup.OwnerSecurityInformation | Win32Setup.DaclSecurityInformation;
        Win32Setup.GetKernelObjectSecurity(h, info, null, 0, out var needed);
        if (needed == 0) return null;
        var bytes = new byte[needed];
        return Win32Setup.GetKernelObjectSecurity(h, info, bytes, needed, out _) ? bytes : null;
    }

    sealed class Handle(SafeFileHandle handle, string path) : IDeletableHandle
    {
        string? _final;
        bool? _reparse;

        public string FinalPath => _final ??= WindowsCleanupFiles.FinalPath(handle);

        public bool IsReparse => _reparse ??= QueryReparse();

        bool QueryReparse()
        {
            if (!Win32Cleanup.GetFileInformationByHandleEx(handle, Win32Cleanup.FileAttributeTagInfoClass, out var info,
                    (uint)Marshal.SizeOf<Win32Cleanup.FileAttributeTagInfo>()))
                throw Win32.Fail(Marshal.GetLastWin32Error(), $"GetFileInformationByHandleEx {path}");
            return (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
        }

        public void Delete()
        {
            var ex = new Win32Cleanup.FileDispositionInfoEx
            {
                Flags = Win32Cleanup.DispositionDelete | Win32Cleanup.DispositionPosixSemantics | Win32Cleanup.DispositionIgnoreReadonly,
            };
            if (Win32Cleanup.SetFileInformationByHandle(handle, Win32Cleanup.FileDispositionInfoExClass, ref ex,
                    (uint)Marshal.SizeOf<Win32Cleanup.FileDispositionInfoEx>()))
                return;

            var err = Marshal.GetLastWin32Error();
            if (err is Win32.ErrorInvalidParameter or Win32.ErrorInvalidLevel or Win32Cleanup.ErrorNotSupported or Win32Cleanup.ErrorInvalidFunction)
            {
                // Файловая система без POSIX-удаления (не NTFS): обычная отметка «удалить при закрытии» на этом же хэндле.
                var plain = new Win32Cleanup.FileDispositionInfo { DeleteFile = 1 };
                if (Win32Cleanup.SetFileInformationByHandle(handle, Win32Cleanup.FileDispositionInfoClass, ref plain,
                        (uint)Marshal.SizeOf<Win32Cleanup.FileDispositionInfo>()))
                    return;
                err = Marshal.GetLastWin32Error();
            }
            throw Fail(err);
        }

        Exception Fail(int err) => err switch
        {
            // Каталог не пуст: Core по HResult отличает это от прочих сбоев и пропускает с причиной.
            Win32Cleanup.ErrorDirNotEmpty => new IOException($"Каталог не пуст: {path} (ERROR_DIR_NOT_EMPTY)", Win32Cleanup.HResultDirNotEmpty),
            Win32Cleanup.ErrorSharingViolation or Win32Cleanup.ErrorUserMappedFile =>
                new SharingViolationException($"Объект занят: {path} (код {err})"),
            Win32.ErrorAccessDenied => new UnauthorizedAccessException($"Отказано в доступе: {path} (ERROR_ACCESS_DENIED)"),
            _ => Win32.Fail(err, $"SetFileInformationByHandle {path}"),
        };

        public void Dispose() => handle.Dispose();
    }
}
