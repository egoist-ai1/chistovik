using System.Security.AccessControl;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

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

    public DirFacts Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists) return new DirFacts(false, false, null, Array.Empty<DirAce>());

        var reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var (owner, aces) = AclReader.Read(security);
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
