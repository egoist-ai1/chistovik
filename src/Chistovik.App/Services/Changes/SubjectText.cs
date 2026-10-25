using Chistovik.Core.Catalog;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Changes;

/// <summary>
/// Человекочитаемые имена для журнала: агент пишет в журнал технические идентификаторы
/// («folder:user:Имя.lnk», «startup:hku:S-1-5-…:startupfolder:имя.lnk»), а человеку нужно имя записи или твика.
/// </summary>
public static class SubjectText
{
    /// <summary>Тема операции: имя записи автозагрузки, названия твиков, имя режима; незнакомое остаётся как есть.</summary>
    public static string OfOperation(OperationKind kind, string? subject, string? programName = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        // У отката тема та же, что у исходной операции, а вид другой: пробуем все форматы.
        if (kind is OperationKind.Startup or OperationKind.Undo
            && StartupEntryId.TryParse(subject, out var id, out _))
            return id.Name;

        // Удаление программы: тема это идентификатор записи («arp:machine:x64:ключ»). Человеку нужно имя программы,
        // которое агент записал в цель шага; в старых записях его нет, тогда показываем ключ записи.
        if (kind == OperationKind.Uninstall)
        {
            if (!string.IsNullOrWhiteSpace(programName)) return programName;
            if (ArpId.TryParse(subject, out _, out _, out var arpKey)) return arpKey;
        }

        var catalog = TweakCatalog.Shared;
        if (kind is OperationKind.Tweaks or OperationKind.Undo)
        {
            var titles = subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => catalog.FindTweak(t)?.Title)
                .ToList();
            if (titles.Count > 0 && titles.All(t => t is not null)) return string.Join(", ", titles);
        }
        if (kind is OperationKind.Mode or OperationKind.Undo && catalog.FindMode(subject) is { } mode)
            return mode.Name;
        return subject;
    }

    /// <summary>Цель шага: имя вместо служебного ключа вида «startup:hku:sid:startupfolder:имя», «svc:имя», «task:путь».</summary>
    public static string OfTarget(string? targetKey)
    {
        if (string.IsNullOrWhiteSpace(targetKey)) return "";
        var key = targetKey.Trim();
        if (key.StartsWith("startup:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split(':', 5);
            if (parts.Length == 5 && parts[4].Length > 0) return parts[4];
        }
        if (key.StartsWith("svc:", StringComparison.OrdinalIgnoreCase) && key.Length > 4) return "служба " + key[4..];
        if (key.StartsWith("task:", StringComparison.OrdinalIgnoreCase) && key.Length > 5) return "задача " + key[5..];
        return key;
    }
}
