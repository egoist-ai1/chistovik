using System.Security.AccessControl;
using System.Security.Principal;
using Chistovik.Core.Ports;

namespace Chistovik.Native.Journal;

/// <summary>
/// Каталог журнала: чтение владельца и ACL (сырые факты) и создание каталога с защищённым ACL.
/// Решение «доверенный ли каталог» принимает Core (JournalAclPolicy).
/// </summary>
public sealed class WindowsJournalDirectory : IJournalDirectory
{
    /// <summary>
    /// Владелец — Administrators; SYSTEM и Administrators — FullControl; Users — ReadAndExecute (0x1200A9); наследование от родителя
    /// отключено (P), права распространяются на вложенные каталоги и файлы (OI/CI).
    /// </summary>
    internal const string ProtectedSddl = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    private const FileSystemRights WriteMask =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    public DirFacts Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists) return new DirFacts(false, false, null, Array.Empty<DirAce>());

        var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;

        var aces = new List<DirAce>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var allow = rule.AccessControlType == AccessControlType.Allow;
            var mask = (int)rule.FileSystemRights;
            var grantsWrite = allow && (((rule.FileSystemRights & WriteMask) != 0) || (mask & (GenericAll | GenericWrite)) != 0);
            aces.Add(new DirAce(rule.IdentityReference.Value, allow, grantsWrite));
        }
        return new DirFacts(true, reparse, owner, aces);
    }

    public void CreateProtected(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Нужен полный путь.", nameof(path));
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var dir = new DirectoryInfo(full);
        if (dir.Exists) throw new IOException($"Каталог уже существует, ACL существующего каталога не меняется: {full}");

        // Недостающие родители тоже создаются защищённо: каталог не должен унаследовать чужой ACL.
        if (dir.Parent is { Exists: false } parent) CreateProtected(parent.FullName);

        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(ProtectedSddl);
        dir.Create(security);
    }
}
