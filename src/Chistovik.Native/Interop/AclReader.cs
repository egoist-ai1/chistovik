using System.Security.AccessControl;
using System.Security.Principal;
using Chistovik.Core.Ports;

namespace Chistovik.Native.Interop;

/// <summary>Сырые факты о владельце и правах из дескриптора безопасности файла или каталога. Решение о доверии принимает Core.</summary>
internal static class AclReader
{
    const FileSystemRights WriteMask =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    public static (string? Owner, List<DirAce> Aces) Read(FileSystemSecurity security)
    {
        var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
        var aces = new List<DirAce>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var allow = rule.AccessControlType == AccessControlType.Allow;
            var mask = (int)rule.FileSystemRights;
            var grantsWrite = allow && (((rule.FileSystemRights & WriteMask) != 0) || (mask & (GenericAll | GenericWrite)) != 0);
            aces.Add(new DirAce(rule.IdentityReference.Value, allow, grantsWrite));
        }
        return (owner, aces);
    }
}
