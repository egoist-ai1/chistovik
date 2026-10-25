using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Payload'ы C3 (RegistryKeyDelete, StartupRemove, SharedDllRemove) и канонические ключи целей. Лежит отдельно от StateJson:
/// разбор не доверяет JSON, нарушение формата даёт <see cref="InvalidDataException"/>.
/// </summary>
internal static class RegTreeJson
{
    const int V = JournalJson.CurrentVersion;
    const string QuarantinePrefix = @"SOFTWARE\Chistovik\Quarantine\";

    static string S<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value, info);

    static T Read<T>(string json, JsonTypeInfo<T> info) where T : class
    {
        try { return JsonSerializer.Deserialize(json, info) ?? throw new InvalidDataException("Пустой payload журнала"); }
        catch (JsonException e) { throw new InvalidDataException("Некорректный payload журнала: " + e.Message, e); }
    }

    public static bool IsSha256(string? s)
    {
        if (s is not { Length: 64 }) return false;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    // ---------- расположение ----------

    public static string QuarantineKeyPath(Guid opId, int n) => QuarantinePrefix + opId.ToString("N") + "\\" + n.ToString(CultureInfo.InvariantCulture);

    public static string BackupFile(Guid opId, int n) => opId.ToString("N") + "\\" + n.ToString(CultureInfo.InvariantCulture) + ".reg";

    /// <summary>Разбирает «SOFTWARE\Chistovik\Quarantine\&lt;opId:N&gt;\&lt;n&gt;»; формат строгий (32 hex-символа, n без ведущих нулей).</summary>
    public static bool TryParseBackupKey(string? key, out Guid opId, out int n)
    {
        opId = Guid.Empty;
        n = 0;
        if (key is null || !key.StartsWith(QuarantinePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = key[QuarantinePrefix.Length..].Split('\\');
        if (parts.Length != 2 || parts[0].Length != 32) return false;
        if (!Guid.TryParseExact(parts[0], "N", out opId) || opId == Guid.Empty) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1) return false;
        return n.ToString(CultureInfo.InvariantCulture) == parts[1];
    }

    public static string View(string keyPath)
    {
        foreach (var seg in keyPath.Split('\\'))
            if (seg.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) return "x86";
        return "x64";
    }

    static string RootName(RegLocation l) => l.Root == RegRoot.Machine ? "HKLM" : "HKU";

    public static RegLocation Location(string root, string? sid, string key)
        => root == "HKU" ? new RegLocation(RegRoot.User, sid, key, string.Empty) : new RegLocation(RegRoot.Machine, null, key, string.Empty);

    /// <summary>regkey:{hklm|hku}:{sid|-}:{x64|x86}:&lt;ключ в нижнем регистре&gt;.</summary>
    public static string KeyTargetKey(RegLocation l)
        => $"regkey:{RootName(l).ToLowerInvariant()}:{(l.Root == RegRoot.User ? l.UserSid : "-")}:{View(l.KeyPath)}:{l.KeyPath}".ToLowerInvariant();

    // ---------- ключи ----------

    public static RegKeyDeleteTarget DeleteTarget(RegLocation loc, OrphanEvidenceJson evidence, RegKeyBackup backup, bool expert, string? display)
        => new(V, RootName(loc), loc.Root == RegRoot.User ? loc.UserSid : null, View(loc.KeyPath), loc.KeyPath, evidence, backup, expert, display);

    public static string DeleteTargetJson(RegKeyDeleteTarget t) => S(t, JournalJson.Default.RegKeyDeleteTarget);

    public static RegKeyDeleteTarget ParseDeleteTarget(string json)
    {
        var t = Read(json, JournalJson.Default.RegKeyDeleteTarget);
        if (t.V != V) throw new InvalidDataException("Неподдерживаемая версия payload журнала: " + t.V);
        if (t.Root is not ("HKLM" or "HKU") || string.IsNullOrEmpty(t.Key) || t.Backup is null || t.Evidence is null)
            throw new InvalidDataException("RegKeyDeleteTarget: недопустимые поля");
        return t;
    }

    public static RegLocation LocationOf(RegKeyDeleteTarget t) => Location(t.Root, t.Sid, t.Key);

    public static string KeyStateJson(bool exists, int? subkeys = null, int? values = null, string? sha = null)
        => S(new RegKeyState(V, exists, subkeys, values, sha), JournalJson.Default.RegKeyState);

    public static string KeyAbsentJson => KeyStateJson(false);

    public static RegKeyState ParseKeyState(string json)
    {
        var s = Read(json, JournalJson.Default.RegKeyState);
        if (s.V != V) throw new InvalidDataException("Неподдерживаемая версия payload журнала: " + s.V);
        return s;
    }

    // ---------- значения ----------

    public static string ValueTargetJson(RegLocation loc, OrphanEvidenceJson evidence)
        => S(new ValueRemoveTarget(V, RootName(loc), loc.Root == RegRoot.User ? loc.UserSid : null, View(loc.KeyPath), loc.KeyPath, loc.ValueName, evidence),
            JournalJson.Default.ValueRemoveTarget);

    public static ValueRemoveTarget ParseValueTarget(string json)
    {
        var t = Read(json, JournalJson.Default.ValueRemoveTarget);
        if (t.V != V) throw new InvalidDataException("Неподдерживаемая версия payload журнала: " + t.V);
        if (t.Root is not ("HKLM" or "HKU") || string.IsNullOrEmpty(t.Key) || string.IsNullOrEmpty(t.Name) || t.Evidence is null)
            throw new InvalidDataException("ValueRemoveTarget: недопустимые поля");
        return t;
    }

    public static RegLocation LocationOf(ValueRemoveTarget t)
        => t.Root == "HKU" ? new RegLocation(RegRoot.User, t.Sid, t.Key, t.Name) : new RegLocation(RegRoot.Machine, null, t.Key, t.Name);
}
