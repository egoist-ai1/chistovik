using System.Text.Json;
using Chistovik.Core.Policy;

namespace Chistovik.Core.Findings;

/// <summary>Известная программа в автозагрузке: роль и три фразы объяснения. Четвёртая («как вернуть») общая.</summary>
public sealed class StartupAdvice
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Имена записи; «*» только в конце означает «начинается с».</summary>
    public string[] Names { get; init; } = [];
    /// <summary>Имена исполняемых файлов (без пути).</summary>
    public string[] Files { get; init; } = [];
    public string Role { get; init; } = "";
    public string What { get; init; } = "";
    public string Why { get; init; } = "";
    public string IfRemoved { get; init; } = "";
}

/// <summary>Совет по службе или задаче. Это данные для объяснения: менять объект каталог не разрешает.</summary>
public sealed class ObjectAdvice
{
    /// <summary>Для службы это имя, для задачи путь; «*» только в конце означает «начинается с».</summary>
    public string Name { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Risk { get; init; } = "Low";
    public string What { get; init; } = "";
    public string Why { get; init; } = "";
    public string IfRemoved { get; init; } = "";
    public string[] Reasons { get; init; } = [];
    public bool WuMayRevert { get; init; }
    /// <summary>
    /// Объект входит в именованное исключение владельца (<see cref="ServicePolicy.CarveOut"/> / <see cref="TaskPolicy.CarveOutTasks"/>)
    /// и показывается как управляемый. Это пометка для интерфейса: права даёт только политика.
    /// </summary>
    public bool Controllable { get; init; }
}

sealed class ServicesTasksFile
{
    public List<ObjectAdvice> Services { get; init; } = [];
    public List<ObjectAdvice> Tasks { get; init; } = [];
}

/// <summary>
/// Встроенные каталоги советов (startup-known.json и services-tasks-catalog.json). Любое нарушение формата это
/// InvalidDataException. Каталог не расширяет права: службы из запретного списка и из перечня исключений в него не попадают.
/// </summary>
public sealed class AdviceCatalog
{
    static readonly Lazy<AdviceCatalog> SharedLazy = new(Load);
    public static AdviceCatalog Shared => SharedLazy.Value;

    /// <summary>Службы, которые спецификация (раздел 5) запрещает трогать совсем.</summary>
    public static readonly IReadOnlySet<string> NeverAdvise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "WinDefend", "wlidsvc", "LicenseManager", "ClipSVC",
        "StateRepository", "AppXSvc", "TokenBroker", "TextInputManagementService", "Dnscache",
    };

    static readonly HashSet<string> ServiceVerdicts = new(StringComparer.Ordinal) { "Remove", "Optional", "Keep" };
    static readonly HashSet<string> TaskVerdicts = new(StringComparer.Ordinal) { "Remove", "Optional", "Keep", "Protected" };
    static readonly HashSet<string> Risks = new(StringComparer.Ordinal) { "Low", "Medium", "High" };

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public IReadOnlyList<StartupAdvice> Startup { get; }
    public IReadOnlyList<ObjectAdvice> Services { get; }
    public IReadOnlyList<ObjectAdvice> Tasks { get; }

    AdviceCatalog(IReadOnlyList<StartupAdvice> startup, IReadOnlyList<ObjectAdvice> services, IReadOnlyList<ObjectAdvice> tasks)
    {
        Startup = startup;
        Services = services;
        Tasks = tasks;
    }

    public static AdviceCatalog Load()
        => Parse(ReadResource("catalog/startup-known.json"), ReadResource("catalog/services-tasks-catalog.json"));

    public static AdviceCatalog Parse(string startupJson, string servicesTasksJson)
    {
        var startup = Deserialize<List<StartupAdvice>>(startupJson, "startup-known.json");
        var st = Deserialize<ServicesTasksFile>(servicesTasksJson, "services-tasks-catalog.json");
        ValidateStartup(startup);
        ValidateObjects(st.Services, "службы", ServiceVerdicts, isTask: false);
        ValidateObjects(st.Tasks, "задачи", TaskVerdicts, isTask: true);
        ValidateCarveOutComplete(st.Services, "службы", ServicePolicy.CarveOut);
        ValidateCarveOutComplete(st.Tasks, "задачи", TaskPolicy.CarveOutTasks);
        return new AdviceCatalog(startup, st.Services, st.Tasks);
    }

    public StartupAdvice? FindStartup(string name, string? fileName)
    {
        foreach (var a in Startup)
        {
            if (a.Names.Any(n => Matches(n, name))) return a;
            if (!string.IsNullOrEmpty(fileName) && a.Files.Any(f => f.Equals(fileName, StringComparison.OrdinalIgnoreCase))) return a;
        }
        return null;
    }

    public ObjectAdvice? FindService(string name)
        => Services.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Совет по пути задачи: точное совпадение сильнее шаблона, из шаблонов побеждает самый длинный.</summary>
    public ObjectAdvice? FindTask(string path)
    {
        ObjectAdvice? best = null;
        var bestLen = -1;
        foreach (var a in Tasks)
        {
            if (!Matches(a.Name, path)) continue;
            var len = a.Name.EndsWith('*') ? a.Name.Length - 1 : int.MaxValue;
            if (len > bestLen) { best = a; bestLen = len; }
        }
        return best;
    }

    static bool Matches(string pattern, string value)
        => pattern.EndsWith('*')
            ? value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    static string ReadResource(string name)
    {
        var asm = typeof(AdviceCatalog).Assembly;
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

    static void ValidateStartup(List<StartupAdvice> list)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in list)
        {
            if (a is null) throw Bad("startup-known.json: пустая запись");
            Text(a.Id, "id", "?");
            var w = $"автозагрузка «{a.Id}»";
            if (!ids.Add(a.Id)) throw Bad($"startup-known.json: повторяющийся id «{a.Id}»");
            Text(a.Title, "title", w); Text(a.What, "what", w); Text(a.Why, "why", w); Text(a.IfRemoved, "ifRemoved", w);
            if (!ReasonCodes.Roles.Contains(a.Role))
                throw Bad($"{w}: недопустимая роль «{a.Role}» (допустимо: {string.Join(", ", ReasonCodes.Roles)})");
            if (a.Names is null || a.Files is null || a.Names.Length + a.Files.Length == 0)
                throw Bad($"{w}: нужно хотя бы одно имя или файл");
            foreach (var n in a.Names.Concat(a.Files))
            {
                Text(n, "имя", w);
                if (n.IndexOf('*') is var star && star >= 0 && star != n.Length - 1)
                    throw Bad($"{w}: «*» допустима только в конце («{n}»)");
            }
        }
    }

    static void ValidateObjects(List<ObjectAdvice> list, string kind, HashSet<string> verdicts, bool isTask)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list)
        {
            if (a is null) throw Bad($"{kind}: пустая запись");
            Text(a.Name, "name", kind);
            var w = $"{kind} «{a.Name}»";
            if (!names.Add(a.Name)) throw Bad($"{kind}: повторяющееся имя «{a.Name}»");
            Text(a.What, "what", w); Text(a.Why, "why", w); Text(a.IfRemoved, "ifRemoved", w);
            if (!verdicts.Contains(a.Verdict))
                throw Bad($"{w}: недопустимый вердикт «{a.Verdict}» (допустимо: {string.Join(", ", verdicts)})");
            if (!Risks.Contains(a.Risk)) throw Bad($"{w}: недопустимый риск «{a.Risk}»");
            if (a.Reasons is null) throw Bad($"{w}: нет списка reasons");
            foreach (var code in a.Reasons)
                if (!ReasonCodes.CatalogReasons.Contains(code))
                    throw Bad($"{w}: код причины «{code}» нельзя указывать в каталоге");
            if (a.Name.IndexOf('*') is var star && star >= 0 && star != a.Name.Length - 1)
                throw Bad($"{w}: «*» допустима только в конце");
            if (a.Controllable)
            {
                var carve = isTask ? TaskPolicy.CarveOutTasks : ServicePolicy.CarveOut;
                if (a.Name.Contains('*') || !carve.Contains(a.Name))
                    throw Bad($"{w}: controllable допустим только для точного имени из перечня исключений политики");
            }

            if (isTask)
            {
                if (!a.Name.StartsWith('\\')) throw Bad($"{w}: путь задачи должен начинаться с «\\»");
            }
            else
            {
                if (a.Name.Contains('*')) throw Bad($"{w}: для службы шаблоны не допускаются");
                if (ServicePolicy.IsHardListed(a.Name))
                    throw Bad($"{w}: служба из запретного списка политики, давать совет по ней нельзя");
                if (NeverAdvise.Contains(a.Name))
                    throw Bad($"{w}: служба из списка «никогда не трогать»");
            }
        }
    }

    /// <summary>Каждый элемент перечня исключений должен быть в каталоге точной записью с controllable: иначе он невидим в интерфейсе.</summary>
    static void ValidateCarveOutComplete(List<ObjectAdvice> list, string kind, IEnumerable<string> carveOut)
    {
        foreach (var name in carveOut)
            if (!list.Any(a => a.Controllable && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw Bad($"{kind}: «{name}» входит в перечень исключений политики, но в каталоге нет записи с controllable");
    }

    static void Text(string? s, string field, string w)
    {
        if (string.IsNullOrWhiteSpace(s)) throw Bad($"{w}: пустое поле {field}");
    }

    static InvalidDataException Bad(string msg) => new(msg);
}
