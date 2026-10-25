using Chistovik.Core.Policy;
using Chistovik.Native.Journal;

namespace Chistovik.Native.Cleanup;

/// <summary>
/// Перед записью в каталог журналов и копий очистки сверяет его владельца и ACL по той же политике, что каталог журнала
/// (JournalAclPolicy): если права ослабили после создания, запись отклоняется, а не делается в каталог, куда может писать не-администратор.
/// </summary>
internal static class StoreDirectoryGuard
{
    public static void Require(string directory)
    {
        var acl = JournalAclPolicy.Evaluate(new WindowsJournalDirectory().Inspect(directory));
        if (!acl.IsTrusted) throw new IOException("Каталог небезопасен для записи: " + acl.Rule);
    }
}
