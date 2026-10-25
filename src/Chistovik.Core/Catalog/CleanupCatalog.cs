using System.Text.Json;
using Chistovik.Core.Policy;

namespace Chistovik.Core.Catalog;

/// <summary>Корень очистки: база (windir | programdata | systemdrive | userlocal | userroaming) и подпуть. «*» допустим одним средним компонентом.</summary>
public sealed record CleanupRoot(string Base, string Sub);

/// <summary>
/// Категория очистки диска. Scope: machine | user. Mechanism: files | recyclebin | handler (handler в C2 только карточка).
/// Restart: none | explorer. SkipIfRunning: имя образа процесса, при котором категория пропускается.
/// </summary>
public sealed record CleanupCategory(
    string Id,
    string Title,
    string Description,
    string Scope,
    IReadOnlyList<CleanupRoot> Roots,
    int MinAgeHours,
    IReadOnlyList<string>? KeepNames,
    IReadOnlyList<string>? Patterns,
    string Mechanism,
    string Risk,
    bool DefaultOn,
    string? SkipIfRunning,
    string Restart);

/// <summary>Каталог очистки, зашитый в сборку (cleanup.json). Любое нарушение формата или политики — InvalidDataException.</summary>
public sealed class CleanupCatalog
{
    public static readonly IReadOnlySet<string> Risks = new HashSet<string> { "low", "medium", "high" };

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    static readonly Lazy<CleanupCatalog> SharedLazy = new(Load);

    /// <summary>Общий экземпляр встроенного каталога.</summary>
    public static CleanupCatalog Shared => SharedLazy.Value;

    sealed record Document(List<CleanupCategory> Categories);

    public IReadOnlyList<CleanupCategory> Categories { get; }

    CleanupCatalog(IReadOnlyList<CleanupCategory> categories) => Categories = categories;

    public CleanupCategory? Find(string id) => Categories.FirstOrDefault(c => c.Id == id);

    public static CleanupCatalog Load()
    {
        var asm = typeof(CleanupCatalog).Assembly;
        using var s = asm.GetManifestResourceStream("catalog/cleanup.json")
            ?? throw new InvalidDataException("Ресурс каталога не найден: catalog/cleanup.json");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    public static CleanupCatalog Parse(string json)
    {
        Document doc;
        try { doc = JsonSerializer.Deserialize<Document>(json, Options) ?? throw new InvalidDataException("cleanup.json: пустой каталог"); }
        catch (JsonException e) { throw new InvalidDataException($"cleanup.json: некорректный JSON: {e.Message}", e); }
        if (doc.Categories is null || doc.Categories.Count == 0) throw Bad("cleanup.json: нужен непустой список categories");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in doc.Categories)
        {
            var d = CleanupPolicy.ValidateCategory(c);
            if (!d.IsAllowed) throw Bad($"cleanup.json: {d.Reason} [{d.Rule}]");
            if (!ids.Add(c.Id)) throw Bad($"cleanup.json: повторяющийся id «{c.Id}»");
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Description))
                throw Bad($"категория «{c.Id}»: пустой title или description");
            if (!Risks.Contains(c.Risk ?? "")) throw Bad($"категория «{c.Id}»: недопустимый risk «{c.Risk}»");
        }
        return new CleanupCatalog(doc.Categories);
    }

    static InvalidDataException Bad(string msg) => new(msg);
}
