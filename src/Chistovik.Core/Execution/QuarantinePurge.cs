using System.Globalization;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Очистка копий удалённых ключей реестра (C3, DESIGN C2C3 §1.2 Purge). После Journal.Purge(90 суток) удаляются
/// HKLM\SOFTWARE\Chistovik\Quarantine\&lt;opId&gt; и каталог .reg-копий &lt;opId&gt; для операций, которых в журнале больше нет.
/// Имена берутся только из списков самих хранилищ и разбираются как GUID формата N: произвольный путь до Win32 не доходит.
/// </summary>
public static class QuarantinePurge
{
    const string QuarantineKey = @"SOFTWARE\Chistovik\Quarantine";

    static readonly RegLocation Root = new(RegRoot.Machine, null, QuarantineKey, string.Empty);

    /// <summary>Возвращает число уникальных операций, чьи копии удалены полностью или частично. Сбой одной копии не останавливает остальные.</summary>
    public static int Run(IRegistryTree tree, IQuarantineStore? files, IJournalWriter journal)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(journal);
        var alive = new HashSet<Guid>(journal.ExistingOperationIds());
        var purged = new HashSet<Guid>();

        PurgeRegistry(tree, alive, purged);
        if (files is not null) PurgeFiles(files, alive, purged);
        return purged.Count;
    }

    static bool TryOpId(string name, out Guid id)
    {
        id = Guid.Empty;
        return name.Length == 32 && Guid.TryParseExact(name, "N", out id) && id != Guid.Empty;
    }

    static void PurgeRegistry(IRegistryTree tree, HashSet<Guid> alive, HashSet<Guid> purged)
    {
        var stale = new List<string>();
        using (var h = tree.OpenTree(Root, false))
        {
            if (h is null || h.IsSymbolicLink) return;
            foreach (var name in h.SubKeyNames())
                if (TryOpId(name, out var id) && !alive.Contains(id)) stale.Add(name);
        }
        if (stale.Count == 0) return;

        using var root = tree.OpenTree(Root, true);
        if (root is null || root.IsSymbolicLink) return;
        foreach (var name in stale)
        {
            try
            {
                using var sub = root.OpenSub(name, true);
                if (sub is null) continue;
                if (sub.IsSymbolicLink) continue;
                RegTreeOps.DeleteTree(sub);
                TryOpId(name, out var id);
                purged.Add(id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Копия остаётся до следующего запуска агента.
            }
        }
    }

    static void PurgeFiles(IQuarantineStore files, HashSet<Guid> alive, HashSet<Guid> purged)
    {
        foreach (var id in files.ListOperations())
        {
            if (alive.Contains(id)) continue;
            try
            {
                files.DeleteOperation(id);
                purged.Add(id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Каталог копий операции в хранилище: «&lt;opId:N&gt;».</summary>
    public static string FolderName(Guid opId) => opId.ToString("N", CultureInfo.InvariantCulture);
}
