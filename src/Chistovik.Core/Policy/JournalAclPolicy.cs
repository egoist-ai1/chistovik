using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>Итог проверки каталога журнала: Trusted либо Untrusted с причиной и кодом правила. default это Untrusted.</summary>
public readonly record struct JournalAclResult(bool IsTrusted, string Reason, string Rule)
{
    public static JournalAclResult Trusted { get; } = new(true, "Каталог журнала доверенный", "journal-acl-trusted");
    public static JournalAclResult Untrusted(string reason, string rule) => new(false, reason, rule);
}

/// <summary>
/// Доверие к каталогу журнала (DESIGN-0.1 §7). Права сама не исправляет: при отказе агент отвечает кодом journal-untrusted.
/// </summary>
public static class JournalAclPolicy
{
    const string Administrators = "S-1-5-32-544";
    const string SystemSid = "S-1-5-18";
    const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    const string CreatorOwner = "S-1-3-0";

    // Широкие группы, запись для которых недопустима.
    static readonly HashSet<string> BroadSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-32-545",   // Users
        "S-1-5-11",       // Authenticated Users
        "S-1-1-0",        // Everyone
        "S-1-5-4",        // INTERACTIVE
        "S-1-5-32-546",   // Guests
        "S-1-5-32-547",   // Power Users
        "S-1-5-2",        // NETWORK
        "S-1-5-7",        // ANONYMOUS LOGON
        "S-1-5-15",       // This Organization
    };

    static readonly HashSet<string> TrustedOwners = new(StringComparer.OrdinalIgnoreCase) { Administrators, SystemSid, TrustedInstaller };

    // Субъекты, которым разрешающий ACE с записью допустим: администраторы, SYSTEM, TrustedInstaller и CREATOR OWNER
    // (заглушка наследования; настоящий владелец проверяется отдельно).
    static readonly HashSet<string> TrustedWriters = new(StringComparer.OrdinalIgnoreCase) { Administrators, SystemSid, TrustedInstaller, CreatorOwner };

    /// <summary>
    /// Решает, можно ли доверять каталогу журнала. Сигнатура: <c>Evaluate(DirFacts facts)</c>, факты собирает
    /// IJournalDirectory.Inspect. Untrusted, если: каталога нет; он reparse-точка; владелец неизвестен или не Administrators
    /// (S-1-5-32-544), SYSTEM (S-1-5-18), TrustedInstaller; список ACE отсутствует; любой разрешающий ACE с записью
    /// (<c>DirAce.GrantsWrite</c>) выдан Users (S-1-5-32-545), Authenticated Users (S-1-5-11), Everyone (S-1-1-0), INTERACTIVE
    /// (S-1-5-4) и другим широким группам, а также любому субъекту вне {Administrators, SYSTEM, TrustedInstaller, CREATOR OWNER},
    /// в том числе с пустым SID. Запрещающие ACE доверия не добавляют: порядок ACE не учитывается.
    /// </summary>
    public static JournalAclResult Evaluate(DirFacts facts)
    {
        if (facts is null) return JournalAclResult.Untrusted("Нет данных о каталоге журнала", "journal-acl-no-facts");
        if (!facts.Exists) return JournalAclResult.Untrusted("Каталог журнала не существует", "journal-dir-missing");
        if (facts.IsReparse) return JournalAclResult.Untrusted("Каталог журнала это точка повторной обработки (ссылка)", "journal-dir-reparse");

        var owner = facts.OwnerSid?.Trim();
        if (string.IsNullOrEmpty(owner) || !TrustedOwners.Contains(owner))
            return JournalAclResult.Untrusted("Владелец каталога журнала не администратор и не SYSTEM", "journal-dir-owner");

        if (facts.Aces is null) return JournalAclResult.Untrusted("Список прав каталога журнала недоступен", "journal-acl-no-aces");
        foreach (var ace in facts.Aces)
        {
            if (ace is null) return JournalAclResult.Untrusted("Пустая запись списка прав", "journal-acl-bad-ace");
            if (!ace.Allow || !ace.GrantsWrite) continue;
            var sid = ace.Sid?.Trim();
            if (string.IsNullOrEmpty(sid))
                return JournalAclResult.Untrusted("Запись прав без субъекта", "journal-acl-bad-ace");
            if (BroadSids.Contains(sid))
                return JournalAclResult.Untrusted("Запись в каталог разрешена широкой группе (" + sid + ")", "journal-acl-broad-write");
            if (!TrustedWriters.Contains(sid))
                return JournalAclResult.Untrusted("Запись в каталог разрешена постороннему субъекту (" + sid + ")", "journal-acl-foreign-write");
        }
        return JournalAclResult.Trusted;
    }
}
