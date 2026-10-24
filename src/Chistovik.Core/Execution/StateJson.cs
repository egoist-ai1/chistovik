using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>
/// Преобразование сырых фактов портов в payload'ы журнала (DESIGN-0.1 §1) и обратно, а также канонические ключи целей.
/// Разбор не доверяет JSON: неверный формат, версия и типы дают <see cref="InvalidDataException"/>, а не «пустое» состояние.
/// </summary>
public static class StateJson
{
    const int V = JournalJson.CurrentVersion;

    // ---------- общее ----------

    static string S<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value, info);

    static T Read<T>(string json, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ?? throw new InvalidDataException("Пустой payload журнала");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Некорректный payload журнала: " + e.Message, e);
        }
    }

    static void CheckV(int v)
    {
        if (v != V) throw new InvalidDataException("Неподдерживаемая версия payload журнала: " + v);
    }

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ---------- реестр ----------

    public static RegistryState ToRegistryState(RegValueState s)
    {
        if (!s.Exists) return new RegistryState(V, false, null, null);
        JsonElement? data = s.Type switch
        {
            RegType.DWord => JsonSerializer.SerializeToElement(ToUInt(s.Data)),
            RegType.QWord => JsonSerializer.SerializeToElement(ToULong(s.Data).ToString(CultureInfo.InvariantCulture)),
            RegType.String or RegType.ExpandString => JsonSerializer.SerializeToElement((string?)s.Data ?? string.Empty),
            RegType.Binary => JsonSerializer.SerializeToElement(Convert.ToBase64String((byte[]?)s.Data ?? [])),
            RegType.MultiString => JsonSerializer.SerializeToElement((string[]?)s.Data ?? []),
            _ => null,
        };
        return new RegistryState(V, true, s.Type.ToString(), data);
    }

    public static string RegistryJson(RegValueState s) => S(ToRegistryState(s), JournalJson.Default.RegistryState);

    /// <exception cref="InvalidDataException">Любое нарушение формата.</exception>
    public static RegValueState ParseRegistry(string json)
    {
        var st = Read(json, JournalJson.Default.RegistryState);
        CheckV(st.V);
        if (!st.Exists) return new RegValueState(false, RegType.None, null);
        if (st.Type is null || st.Type.Length == 0 || char.IsDigit(st.Type[0])
            || !Enum.TryParse<RegType>(st.Type, false, out var type) || !Enum.IsDefined(type))
            throw new InvalidDataException("RegistryState: неизвестный тип");
        try
        {
            var d = st.Data;
            switch (type)
            {
                case RegType.DWord:
                    return new RegValueState(true, type, d is { ValueKind: JsonValueKind.Number } n && n.TryGetUInt32(out var u)
                        ? u : throw new InvalidDataException("RegistryState: DWord не число"));
                case RegType.QWord:
                    return new RegValueState(true, type, d is { ValueKind: JsonValueKind.String } q
                        && ulong.TryParse(q.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ul)
                        ? ul : throw new InvalidDataException("RegistryState: QWord не строка-число"));
                case RegType.String or RegType.ExpandString:
                    return new RegValueState(true, type, d is { ValueKind: JsonValueKind.String } s
                        ? s.GetString() : throw new InvalidDataException("RegistryState: ожидалась строка"));
                case RegType.Binary:
                    return new RegValueState(true, type, d is { ValueKind: JsonValueKind.String } b
                        ? Convert.FromBase64String(b.GetString()!) : throw new InvalidDataException("RegistryState: ожидался base64"));
                case RegType.MultiString:
                {
                    if (d is not { ValueKind: JsonValueKind.Array } arr) throw new InvalidDataException("RegistryState: ожидался массив");
                    var list = new List<string>();
                    foreach (var e in arr.EnumerateArray())
                        list.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("RegistryState: элемент не строка"));
                    return new RegValueState(true, type, list.ToArray());
                }
                default:
                    return new RegValueState(true, type, null);
            }
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("RegistryState: неверный base64", e);
        }
    }

    static uint ToUInt(object? o) => o switch
    {
        uint u => u,
        int i when i >= 0 => (uint)i,
        long l when l is >= 0 and <= uint.MaxValue => (uint)l,
        ulong ul when ul <= uint.MaxValue => (uint)ul,
        _ => throw new InvalidDataException("DWord: недопустимое значение"),
    };

    static ulong ToULong(object? o) => o switch
    {
        ulong u => u,
        uint u => u,
        int i when i >= 0 => (ulong)i,
        long l when l >= 0 => (ulong)l,
        _ => throw new InvalidDataException("QWord: недопустимое значение"),
    };

    // ---------- автозагрузка (StartupApproved) ----------

    public static string StartupJson(RegValueState s)
        => s is { Exists: true, Type: RegType.Binary, Data: byte[] b }
            ? StartupJson(true, b)
            : StartupJson(s.Exists, null);

    public static string StartupJson(bool exists, byte[]? bytes)
        => S(new StartupState(V, exists, bytes is null ? null : Convert.ToBase64String(bytes)), JournalJson.Default.StartupState);

    public static (bool Exists, byte[]? Bytes) ParseStartup(string json)
    {
        var st = Read(json, JournalJson.Default.StartupState);
        CheckV(st.V);
        if (!st.Exists) return (false, null);
        if (st.Bytes is null) return (true, null);
        try { return (true, Convert.FromBase64String(st.Bytes)); }
        catch (FormatException e) { throw new InvalidDataException("StartupState: неверный base64", e); }
    }

    // ---------- службы ----------

    public static string ServiceJson(ServiceRaw r) => ServiceJson(r.StartType, r.Delayed, r.TriggerCount > 0, r.Running);

    public static string ServiceJson(uint start, bool delayed, bool trigger, bool running)
        => S(new ServiceState(V, start, delayed, trigger, running), JournalJson.Default.ServiceState);

    public static ServiceState ParseService(string json)
    {
        var st = Read(json, JournalJson.Default.ServiceState);
        CheckV(st.V);
        return st;
    }

    // ---------- задачи ----------

    public const int MaxXmlBytes = 64 * 1024;

    public static string TaskJson(TaskRaw r)
    {
        var xml = r.Xml ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(xml);
        return S(new TaskState(V, true, r.Enabled, Sha256Hex(xml), bytes <= MaxXmlBytes ? xml : null), JournalJson.Default.TaskState);
    }

    public static string TaskJson(bool exists, bool enabled)
        => S(new TaskState(V, exists, enabled, null, null), JournalJson.Default.TaskState);

    public static TaskState ParseTask(string json)
    {
        var st = Read(json, JournalJson.Default.TaskState);
        CheckV(st.V);
        return st;
    }

    // ---------- питание ----------

    public static string PowerActiveJson(Guid scheme) => S(new PowerActiveState(V, scheme), JournalJson.Default.PowerActiveState);

    public static PowerActiveState ParsePowerActive(string json)
    {
        var st = Read(json, JournalJson.Default.PowerActiveState);
        CheckV(st.V);
        return st;
    }

    public static string PowerSettingJson(uint ac, uint dc) => S(new PowerSettingState(V, ac, dc), JournalJson.Default.PowerSettingState);

    public static PowerSettingState ParsePowerSetting(string json)
    {
        var st = Read(json, JournalJson.Default.PowerSettingState);
        CheckV(st.V);
        return st;
    }

    public static string PowerSchemeJson(bool exists) => S(new PowerSchemeState(V, exists), JournalJson.Default.PowerSchemeState);

    public static PowerSchemeState ParsePowerScheme(string json)
    {
        var st = Read(json, JournalJson.Default.PowerSchemeState);
        CheckV(st.V);
        return st;
    }

    // ---------- цели (target_json) ----------

    public static string RegistryTargetJson(RegLocation l)
        => S(new RegistryTarget(l.Root == RegRoot.Machine ? "HKLM" : "HKU", l.Root == RegRoot.User ? l.UserSid : null, "64", l.KeyPath, l.ValueName),
            JournalJson.Default.RegistryTarget);

    public static RegistryTarget ParseRegistryTarget(string json) => Read(json, JournalJson.Default.RegistryTarget);

    public static string StartupTargetJson(StartupTarget t) => S(t, JournalJson.Default.StartupTarget);
    public static StartupTarget ParseStartupTarget(string json) => Read(json, JournalJson.Default.StartupTarget);

    public static string ServiceTargetJson(string name) => S(new ServiceTarget(name), JournalJson.Default.ServiceTarget);
    public static ServiceTarget ParseServiceTarget(string json) => Read(json, JournalJson.Default.ServiceTarget);

    public static string TaskTargetJson(string path) => S(new TaskTarget(path), JournalJson.Default.TaskTarget);
    public static TaskTarget ParseTaskTarget(string json) => Read(json, JournalJson.Default.TaskTarget);

    public static string PowerActiveTargetJson() => S(new PowerActiveTarget(), JournalJson.Default.PowerActiveTarget);
    public static PowerActiveTarget ParsePowerActiveTarget(string json) => Read(json, JournalJson.Default.PowerActiveTarget);

    public static string PowerSettingTargetJson(Guid scheme, Guid sub, Guid setting)
        => S(new PowerSettingTarget(scheme, sub, setting), JournalJson.Default.PowerSettingTarget);
    public static PowerSettingTarget ParsePowerSettingTarget(string json) => Read(json, JournalJson.Default.PowerSettingTarget);

    public static string PowerSchemeTargetJson(Guid template, Guid scheme)
        => S(new PowerSchemeTarget(template, scheme), JournalJson.Default.PowerSchemeTarget);
    public static PowerSchemeTarget ParsePowerSchemeTarget(string json) => Read(json, JournalJson.Default.PowerSchemeTarget);

    // ---------- канонические ключи целей (target_key; регистр не различается) ----------

    public static string RegistryKey(RegLocation l)
        => $"reg:{(l.Root == RegRoot.Machine ? "hklm" : "hku")}:{(l.Root == RegRoot.User ? l.UserSid : "")}:{l.KeyPath}:{l.ValueName}".ToLowerInvariant();

    public static string StartupKey(string root, string? sid, string approved, string name)
        => $"startup:{root}:{sid}:{approved}:{name}".ToLowerInvariant();

    public static string ServiceKey(string name) => "svc:" + name.ToLowerInvariant();
    public static string TaskKey(string path) => "task:" + path.ToLowerInvariant();
    public const string PowerActiveKey = "power-active";
    public static string PowerSettingKey(Guid scheme, Guid sub, Guid setting) => $"power-setting:{scheme:D}:{sub:D}:{setting:D}";
    public static string PowerCreateKey(Guid template) => $"power-create:{template:D}";
}
