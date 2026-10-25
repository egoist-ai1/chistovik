using System.Security.Cryptography;
using System.Text;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

internal enum OrphanClass { Arp, AppPath, Run, RunOnce, SharedDll }

internal enum OrphanIdError
{
    None,

    /// <summary>Класс идентификатора агенту неизвестен: policy-denied.</summary>
    Unknown,

    /// <summary>Класс известен, формат нарушен: bad-request.</summary>
    Malformed,
}

/// <summary>
/// Разобранный идентификатор находки реестра R1–R4 (docs/C2C3-DESIGN.md §1.2). Пути в идентификаторе нет: агент сам строит
/// расположение из класса, представления и имени. Scope: machine (HKLM) | user (HKU клиента). View: x64 | x86.
/// Name: имя ключа (arp, apppath), имя значения (run, runonce) либо SHA-256 имени значения (shareddll).
/// </summary>
internal sealed record OrphanId(OrphanClass Class, string Scope, string View, string Name)
{
    public const string CurrentVersion = @"Microsoft\Windows\CurrentVersion\";

    public bool IsKey => Class is OrphanClass.Arp or OrphanClass.AppPath;

    /// <summary>Родительский ключ с учётом представления: «SOFTWARE\…» либо «SOFTWARE\WOW6432Node\…» (HKU клиента: «Software\…»).</summary>
    public string ParentKeyPath
    {
        get
        {
            var leaf = Class switch
            {
                OrphanClass.Arp => "Uninstall",
                OrphanClass.AppPath => "App Paths",
                OrphanClass.Run => "Run",
                OrphanClass.RunOnce => "RunOnce",
                _ => "SharedDLLs",
            };
            var root = Scope == "user" ? "Software" : "SOFTWARE";
            return (View == "x86" ? root + @"\WOW6432Node\" : root + @"\") + CurrentVersion + leaf;
        }
    }

    /// <summary>Расположение: для ключей это сам ключ (ValueName пуст), для Run/RunOnce ключ и имя значения, для shareddll родитель.</summary>
    public RegLocation Location(string clientSid)
    {
        var root = Scope == "user" ? RegRoot.User : RegRoot.Machine;
        var sid = Scope == "user" ? clientSid : null;
        return Class switch
        {
            OrphanClass.Arp or OrphanClass.AppPath => new RegLocation(root, sid, ParentKeyPath + "\\" + Name, ""),
            OrphanClass.Run or OrphanClass.RunOnce => new RegLocation(root, sid, ParentKeyPath, Name),
            _ => new RegLocation(root, sid, ParentKeyPath, ""),
        };
    }

    /// <summary>SHA-256 имени значения (нижний регистр, UTF-8) в виде 64 hex-символов нижнего регистра: тело идентификатора shareddll.</summary>
    public static string SharedDllHash(string valueName)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valueName.ToLowerInvariant()))).ToLowerInvariant();

    public static string Make(OrphanClass c, string scope, string view, string name) => c switch
    {
        OrphanClass.Arp => $"arp:{scope}:{view}:{name}",
        OrphanClass.AppPath => $"apppath:{scope}:{view}:{name}",
        OrphanClass.Run => $"run:{RunWhere(scope, view)}:{name}",
        OrphanClass.RunOnce => $"runonce:{RunWhere(scope, view)}:{name}",
        _ => $"shareddll:{view}:{name}",
    };

    static string RunWhere(string scope, string view) => scope == "user" ? "hku" : view == "x86" ? "hklm32" : "hklm64";

    static bool Malformed(out OrphanIdError error)
    {
        error = OrphanIdError.Malformed;
        return false;
    }

    /// <summary>
    /// Разбирает идентификатор. Unknown: неизвестный класс (в том числе папки автозагрузки): policy-denied.
    /// Malformed: известный класс с нарушенным форматом (например «\» в имени App Paths): bad-request.
    /// </summary>
    public static bool TryParse(string? text, out OrphanId id, out OrphanIdError error)
    {
        id = new OrphanId(OrphanClass.Arp, "machine", "x64", string.Empty);
        error = OrphanIdError.None;
        if (string.IsNullOrEmpty(text) || text.Length > ApplyFindingsRequest.MaxFindingIdLength) { error = OrphanIdError.Malformed; return false; }


        if (text.StartsWith("arp:", StringComparison.Ordinal))
        {
            var parts = text.Split(':', 4);
            if (parts.Length == 4 && parts[3] is "." or "..") return Malformed(out error);
            if (Rules.ArpMachineId.IsMatch(text) && parts[1] == "machine") { id = new OrphanId(OrphanClass.Arp, "machine", parts[2], parts[3]); return true; }
            if (Rules.ArpUserId.IsMatch(text) && parts[1] == "user") { id = new OrphanId(OrphanClass.Arp, "user", parts[2], parts[3]); return true; }
            return Malformed(out error);
        }
        if (text.StartsWith("apppath:", StringComparison.Ordinal))
        {
            if (!Rules.AppPathId.IsMatch(text)) return Malformed(out error);
            var parts = text.Split(':', 4);
            if (parts[3] is "." or "..") return Malformed(out error);
            id = new OrphanId(OrphanClass.AppPath, parts[1], parts[2], parts[3]);
            return true;
        }
        if (text.StartsWith("shareddll:", StringComparison.Ordinal))
        {
            if (!Rules.SharedDllId.IsMatch(text)) return Malformed(out error);
            var parts = text.Split(':', 3);
            id = new OrphanId(OrphanClass.SharedDll, "machine", parts[1], parts[2]);
            return true;
        }
        if (text.StartsWith("run:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("runonce:", StringComparison.OrdinalIgnoreCase))
        {
            var once = text.StartsWith("runonce:", StringComparison.OrdinalIgnoreCase);
            var asRun = once ? "run:" + text[8..] : text;
            if (once && !Rules.RunOnceId.IsMatch(text)) return Malformed(out error);
            if (!StartupEntryId.TryParse(asRun, out var sid, out _) || !sid.IsRun) return Malformed(out error);
            var (scope, view) = sid.Location switch
            {
                StartupLocation.Hklm64 => ("machine", "x64"),
                StartupLocation.Hklm32 => ("machine", "x86"),
                _ => ("user", "x64"),
            };
            id = new OrphanId(once ? OrphanClass.RunOnce : OrphanClass.Run, scope, view, sid.Name);
            return true;
        }
        error = OrphanIdError.Unknown;
        return false;
    }
}
