using System.Reflection;
using System.Text.Json;

namespace Chistovik.Core.Catalog;

/// <summary>Каталог твиков и режимов, зашитый в сборку. Любое нарушение формата — InvalidDataException.</summary>
public sealed class TweakCatalog
{
    public static readonly IReadOnlySet<string> Areas = new HashSet<string> { "tw", "fx" };
    public static readonly IReadOnlySet<string> Risks = new HashSet<string> { "safe", "careful" };
    public static readonly IReadOnlySet<string> Hives = new HashSet<string> { "HKCU", "HKLM" };
    public static readonly IReadOnlySet<string> ValueTypes = new HashSet<string> { "DWord", "String" };
    public static readonly IReadOnlySet<string> Restarts = new HashSet<string> { "none", "explorer", "logoff", "reboot" };
    public static readonly IReadOnlySet<string> PowerPlans = new HashSet<string> { "saver", "bal", "high", "ult" };
    /// <summary>Что приложение делает в сеансе после применения: none, SystemParametersInfo (spi-*) или WM_SETTINGCHANGE (broadcast).</summary>
    public static readonly IReadOnlySet<string> Effects = new HashSet<string> { "none", "spi-animation", "spi-dragfull", "spi-mouse", "broadcast" };

    /// <summary>Редакции Windows, к которым привязан твик (EditionID сведён к четырём).</summary>
    public static readonly IReadOnlySet<string> Editions = new HashSet<string> { "Home", "Pro", "Enterprise", "Education" };
    /// <summary>Что возвращает значение после установки обновления Windows.</summary>
    public static readonly IReadOnlySet<string> RevertedByValues = new HashSet<string> { "feature-update", "cumulative-update", "none" };
    public static readonly IReadOnlySet<string> Scopes = new HashSet<string> { "user", "machine" };

    static readonly Lazy<TweakCatalog> SharedLazy = new(Load);

    /// <summary>Общий экземпляр встроенного каталога (для проверки запросов).</summary>
    public static TweakCatalog Shared => SharedLazy.Value;
    public ModeEntry? FindMode(string id) => Modes.FirstOrDefault(m => m.Id == id);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public IReadOnlyList<TweakEntry> Tweaks { get; }
    public IReadOnlyList<ModeEntry> Modes { get; }

    TweakCatalog(IReadOnlyList<TweakEntry> tweaks, IReadOnlyList<ModeEntry> modes)
    {
        Tweaks = tweaks;
        Modes = modes;
    }

    public TweakEntry? FindTweak(string id) => Tweaks.FirstOrDefault(t => t.Id == id);

    /// <summary>Загружает каталог из ресурсов сборки и проверяет его.</summary>
    public static TweakCatalog Load()
        => Parse(ReadResource("catalog/tweaks.json"), ReadResource("catalog/modes.json"));

    /// <summary>Разбирает и проверяет каталог из JSON-строк (для тестов).</summary>
    public static TweakCatalog Parse(string tweaksJson, string modesJson)
    {
        var tweaks = Deserialize<List<TweakEntry>>(tweaksJson, "tweaks.json");
        var modes = Deserialize<List<ModeEntry>>(modesJson, "modes.json");
        Validate(tweaks, modes);
        return new TweakCatalog(tweaks, modes);
    }

    static string ReadResource(string name)
    {
        var asm = typeof(TweakCatalog).Assembly;
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Ресурс каталога не найден: {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static T Deserialize<T>(string json, string file) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidDataException($"{file}: пустой каталог");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{file}: некорректный JSON: {e.Message}", e);
        }
    }

    static void Validate(IReadOnlyList<TweakEntry> tweaks, IReadOnlyList<ModeEntry> modes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tweaks)
        {
            if (t is null) throw Bad("tweaks.json: пустая запись");
            Text(t.Id, "id", "?");
            var w = $"твик «{t.Id}»";
            if (!ids.Add(t.Id)) throw Bad($"tweaks.json: повторяющийся id «{t.Id}»");
            Text(t.Title, "title", w); Text(t.Description, "description", w);
            Text(t.Key, "key", w); Text(t.Value, "value", w); Text(t.Source, "source", w);
            OneOf(t.Area, Areas, "area", w); OneOf(t.Risk, Risks, "risk", w);
            OneOf(t.Hive, Hives, "hive", w); OneOf(t.ValueType, ValueTypes, "valueType", w);
            OneOf(t.Restart, Restarts, "restart", w);
            OneOf(t.Effect, Effects, "effect", w);
            if (t.Key.StartsWith('\\') || t.Key.EndsWith('\\') || t.Key.Contains(':'))
                throw Bad($"{w}: key должен быть путём без куста и без «:» («{t.Key}»)");
            if (t.Editions is { } eds)
            {
                if (eds.Length == 0) throw Bad($"{w}: editions не может быть пустым");
                foreach (var ed in eds) OneOf(ed, Editions, "editions", w);
            }
            if (t.RevertedBy is not null) OneOf(t.RevertedBy, RevertedByValues, "revertedBy", w);
            if (t.Scope is not null)
            {
                OneOf(t.Scope, Scopes, "scope", w);
                if (t.Scope != (t.Hive == "HKCU" ? "user" : "machine"))
                    throw Bad($"{w}: scope «{t.Scope}» не совпадает с hive «{t.Hive}»");
            }
            if (t.MinBuild is <= 0) throw Bad($"{w}: minBuild должен быть положительным");
            if (t.MaxBuild is <= 0) throw Bad($"{w}: maxBuild должен быть положительным");
            if (t.MinBuild is { } lo && t.MaxBuild is { } hi && lo > hi)
                throw Bad($"{w}: minBuild больше maxBuild");
            CheckValue(t.OnValue, t.ValueType, "onValue", w);
            if (t.OffValue is { } off && off.ValueKind != JsonValueKind.Null) CheckValue(off, t.ValueType, "offValue", w);
        }

        var modeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in modes)
        {
            if (m is null) throw Bad("modes.json: пустая запись");
            Text(m.Id, "id", "режим ?");
            var w = $"режим «{m.Id}»";
            if (!modeIds.Add(m.Id)) throw Bad($"modes.json: повторяющийся id «{m.Id}»");
            Text(m.Name, "name", w); Text(m.Tag, "tag", w); Text(m.Description, "description", w);
            OneOf(m.PowerPlan, PowerPlans, "powerPlan", w);
            if (m.Tweaks is null) throw Bad($"{w}: нет словаря tweaks");
            foreach (var id in m.Tweaks.Keys)
            {
                var t = tweaks.FirstOrDefault(x => x.Id == id)
                    ?? throw Bad($"{w}: ссылка на несуществующий твик «{id}»");
                if (t.RequiresAdmin && !m.RequiresAdmin)
                    throw Bad($"{w}: твик «{id}» требует прав администратора, а режим не помечен requiresAdmin");
            }
        }
    }

    static void CheckValue(JsonElement v, string type, string field, string w)
    {
        var ok = type == "DWord"
            ? v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out _)
            : v.ValueKind == JsonValueKind.String;
        if (!ok) throw Bad($"{w}: {field} не соответствует типу {type}");
    }

    static void Text(string? s, string field, string w)
    {
        if (string.IsNullOrWhiteSpace(s)) throw Bad($"{w}: пустое поле {field}");
    }

    static void OneOf(string? s, IReadOnlySet<string> allowed, string field, string w)
    {
        if (s is null || !allowed.Contains(s))
            throw Bad($"{w}: недопустимое значение {field} «{s}» (допустимо: {string.Join(", ", allowed)})");
    }

    static InvalidDataException Bad(string msg) => new(msg);
}
