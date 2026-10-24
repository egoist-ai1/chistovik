using System.Text.Json;

namespace Chistovik.Core.Catalog;

/// <summary>Схема питания из каталога. CreateFromTemplate != null: нашей схемы может не быть, агент создаёт её копированием шаблона.</summary>
public sealed record PowerSchemeEntry(
    string Id,
    string Title,
    string Description,
    Guid Guid,
    bool BuiltIn,
    Guid? CreateFromTemplate,
    bool GuidVerified);

/// <summary>Параметр схемы питания. Unit: percent | seconds | enum. Min и Max включительно.</summary>
public sealed record PowerSettingEntry(
    string Id,
    string Title,
    string Description,
    Guid Subgroup,
    Guid Setting,
    string Unit,
    uint Min,
    uint Max,
    bool GuidVerified);

/// <summary>Каталог питания, зашитый в сборку (power.json). Любое нарушение формата — InvalidDataException.</summary>
public sealed class PowerCatalog
{
    public static readonly IReadOnlySet<string> Units = new HashSet<string> { "percent", "seconds", "enum" };

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    static readonly Lazy<PowerCatalog> SharedLazy = new(Load);

    /// <summary>Общий экземпляр встроенного каталога (для проверки запросов).</summary>
    public static PowerCatalog Shared => SharedLazy.Value;

    sealed record Document(List<PowerSchemeEntry> Schemes, List<PowerSettingEntry> Settings);

    public IReadOnlyList<PowerSchemeEntry> Schemes { get; }
    public IReadOnlyList<PowerSettingEntry> Settings { get; }

    PowerCatalog(IReadOnlyList<PowerSchemeEntry> schemes, IReadOnlyList<PowerSettingEntry> settings)
    {
        Schemes = schemes;
        Settings = settings;
    }

    public PowerSchemeEntry? FindScheme(string id) => Schemes.FirstOrDefault(s => s.Id == id);

    public PowerSettingEntry? FindSetting(string id) => Settings.FirstOrDefault(s => s.Id == id);

    public static PowerCatalog Load()
    {
        var asm = typeof(PowerCatalog).Assembly;
        using var s = asm.GetManifestResourceStream("catalog/power.json")
            ?? throw new InvalidDataException("Ресурс каталога не найден: catalog/power.json");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static PowerCatalog Parse(string json)
    {
        Document doc;
        try { doc = JsonSerializer.Deserialize<Document>(json, Options) ?? throw new InvalidDataException("power.json: пустой каталог"); }
        catch (JsonException e) { throw new InvalidDataException($"power.json: некорректный JSON: {e.Message}", e); }
        if (doc.Schemes is null || doc.Settings is null) throw Bad("power.json: нужны списки schemes и settings");
        Validate(doc.Schemes, doc.Settings);
        return new PowerCatalog(doc.Schemes, doc.Settings);
    }

    static void Validate(List<PowerSchemeEntry> schemes, List<PowerSettingEntry> settings)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var guids = new HashSet<Guid>();
        foreach (var s in schemes)
        {
            if (s is null) throw Bad("power.json: пустая схема");
            Text(s.Id, "id", "схема ?");
            var w = $"схема «{s.Id}»";
            if (!TweakCatalog.PowerPlans.Contains(s.Id)) throw Bad($"{w}: id вне saver|bal|high|ult");
            if (!ids.Add(s.Id)) throw Bad($"power.json: повторяющийся id схемы «{s.Id}»");
            Text(s.Title, "title", w); Text(s.Description, "description", w);
            if (s.Guid == Guid.Empty || !guids.Add(s.Guid)) throw Bad($"{w}: пустой или повторяющийся guid");
            if (s.BuiltIn && s.CreateFromTemplate is not null) throw Bad($"{w}: встроенная схема не создаётся из шаблона");
        }
        foreach (var p in TweakCatalog.PowerPlans)
            if (!ids.Contains(p)) throw Bad($"power.json: нет схемы «{p}»");

        ids.Clear();
        foreach (var t in settings)
        {
            if (t is null) throw Bad("power.json: пустой параметр");
            Text(t.Id, "id", "параметр ?");
            var w = $"параметр «{t.Id}»";
            if (!ids.Add(t.Id)) throw Bad($"power.json: повторяющийся id параметра «{t.Id}»");
            Text(t.Title, "title", w); Text(t.Description, "description", w);
            if (t.Subgroup == Guid.Empty || t.Setting == Guid.Empty) throw Bad($"{w}: пустой guid");
            if (!Units.Contains(t.Unit ?? "")) throw Bad($"{w}: недопустимая unit «{t.Unit}»");
            if (t.Min > t.Max) throw Bad($"{w}: min больше max");
            if (t.Unit == "percent" && t.Max > 100) throw Bad($"{w}: проценты не больше 100");
        }
    }

    static void Text(string? s, string field, string w)
    {
        if (string.IsNullOrWhiteSpace(s)) throw Bad($"{w}: пустое поле {field}");
    }

    static InvalidDataException Bad(string msg) => new(msg);
}
