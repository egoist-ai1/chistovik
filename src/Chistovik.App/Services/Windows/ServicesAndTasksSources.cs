using System.IO;
using System.Xml.Linq;
using Chistovik.App.Models;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using PolicyStartMode = Chistovik.Core.Policy.StartMode;
using PortStartMode = Chistovik.Core.Ports.StartMode;

namespace Chistovik.App.Services.Windows;

/// <summary>
/// Службы из HKLM\SYSTEM\CurrentControlSet\Services (только чтение). Класс «можно менять» это подсказка по ServicePolicy:
/// подпись и продукты безопасности в App не проверяются, поэтому подсказка строже, чем решение агента.
/// </summary>
public sealed class RegistryServiceSource : IServiceSource
{
    const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    readonly IRegistryReader _reg;
    readonly Func<IReadOnlyList<InstalledProgram>> _programs;
    readonly IFileTrust _trust;
    readonly ServicePolicy _policy;

    public RegistryServiceSource(IRegistryReader reg, Func<IReadOnlyList<InstalledProgram>> programs, IFileTrust trust, ServicePolicy policy)
    {
        _reg = reg;
        _programs = programs;
        _trust = trust;
        _policy = policy;
    }

    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync() => Task.Run(Read);

    public IReadOnlyList<ServiceInfo> Read()
    {
        var raw = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        var triggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _reg.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath))
        {
            var v = _reg.ReadValues(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath + "\\" + name);
            if (v is null) continue;
            raw[name] = v;
            if (_reg.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath + "\\" + name)
                .Contains("TriggerInfo", StringComparer.OrdinalIgnoreCase))
                triggers.Add(name);
        }

        var dependents = CountDependents(raw);
        var programs = _programs();
        var list = new List<ServiceInfo>();
        foreach (var (name, v) in raw)
        {
            var type = Dword(v, "Type");
            var kind = KindOf(type);
            if (kind is ServiceType.KernelDriver or ServiceType.FileSystemDriver || type == 0) continue; // драйверы не показываем

            var startRaw = Dword(v, "Start");
            var delayed = Dword(v, "DelayedAutostart") == 1;
            var start = ToPolicyStart(startRaw, delayed);
            var image = Str(v, "ImagePath");
            var imageExpanded = string.IsNullOrEmpty(image) ? image : Environment.ExpandEnvironmentVariables(image);
            var exe = _policy.ResolveImagePath(imageExpanded);

            var owner = exe is null ? null : programs.FirstOrDefault(p => p.InstallLocation.Length > 0 && IsUnder(exe, p.InstallLocation));
            var sig = exe is null ? new SignatureFacts(false, false) : _trust.GetSignature(exe);

            var facts = new ServiceFacts(name, kind, imageExpanded, sig.Valid, sig.MicrosoftSigned, Dword(v, "LaunchProtected") != 0,
                dependents.GetValueOrDefault(name), owner is not null, start, false, owner?.InstallLocation, exe);
            var (cls, reason, _) = _policy.Classify(facts);
            if (triggers.Contains(name) && cls == ServiceClass.Modifiable)
            {
                // Запуск по событию агент всё равно проверит; в списке помечаем как справку.
                reason += " (запускается по событию)";
            }

            list.Add(new ServiceInfo(name, Str(v, "DisplayName") is { Length: > 0 } dn && !dn.StartsWith('@') ? dn : name,
                ToPortStart(startRaw, delayed), Map(cls), reason, imageExpanded ?? ""));
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static Dictionary<string, int> CountDependents(Dictionary<string, IReadOnlyDictionary<string, object?>> raw)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in raw.Values)
        {
            if (!v.TryGetValue("DependOnService", out var d) || d is not string[] deps) continue;
            foreach (var dep in deps.Where(x => x.Length > 0))
                map[dep] = map.GetValueOrDefault(dep) + 1;
        }
        return map;
    }

    static bool IsUnder(string exe, string dir)
        => PathNormalizer.TryNormalize(dir, out var d, out _) && PathNormalizer.IsEqualOrUnder(exe, d);

    static ServiceType KindOf(int type)
    {
        if ((type & 0x1) != 0) return ServiceType.KernelDriver;
        if ((type & 0x2) != 0) return ServiceType.FileSystemDriver;
        if ((type & 0x10) != 0) return ServiceType.OwnProcess;
        if ((type & 0x20) != 0) return ServiceType.ShareProcess;
        return ServiceType.Other;
    }

    static PolicyStartMode ToPolicyStart(int start, bool delayed) => start switch
    {
        0 => PolicyStartMode.Boot,
        1 => PolicyStartMode.System,
        2 => delayed ? PolicyStartMode.AutomaticDelayed : PolicyStartMode.Automatic,
        3 => PolicyStartMode.Manual,
        _ => PolicyStartMode.Disabled,
    };

    static PortStartMode? ToPortStart(int start, bool delayed) => start switch
    {
        2 => delayed ? PortStartMode.AutomaticDelayed : PortStartMode.Automatic,
        3 => PortStartMode.Manual,
        4 => PortStartMode.Disabled,
        _ => null,
    };

    static Changeability Map(ServiceClass c) => c switch
    {
        ServiceClass.Modifiable => Changeability.Modifiable,
        ServiceClass.ReadOnly => Changeability.ReadOnly,
        _ => Changeability.Forbidden,
    };

    static int Dword(IReadOnlyDictionary<string, object?> v, string name)
        => v.TryGetValue(name, out var o) && o is int i ? i : 0;

    static string Str(IReadOnlyDictionary<string, object?> v, string name)
        => v.TryGetValue(name, out var o) && o is string s ? s : "";
}

/// <summary>Классификация задач. Реальная TaskPolicy пишется отдельным пакетом; до неё всё, что не запрещено, только для чтения.</summary>
public interface ITaskClassifier
{
    (Changeability Class, string Reason) Classify(string taskPath, bool enabled, string? runLevel, string? userId, IReadOnlyList<string> commands);
}

public sealed class ConservativeTaskClassifier : ITaskClassifier
{
    public (Changeability Class, string Reason) Classify(string taskPath, bool enabled, string? runLevel, string? userId, IReadOnlyList<string> commands)
    {
        if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
            return (Changeability.Forbidden, "Задача Windows: служебные задачи не меняем");
        if (taskPath.StartsWith(@"\Chistovik\", StringComparison.OrdinalIgnoreCase))
            return (Changeability.Forbidden, "Задача самого Чистовика");
        return (Changeability.ReadOnly, "Только чтение: правила для задач ещё не подключены");
    }
}

/// <summary>Задачи планировщика из %WINDIR%\System32\Tasks (XML-файлы, только чтение; недоступное пропускается).</summary>
public sealed class FolderTaskSource(string tasksRoot, ITaskClassifier classifier) : ITaskSource
{
    const int MaxTasks = 3000;

    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync() => Task.Run<IReadOnlyList<TaskInfo>>(() =>
    {
        var list = new List<TaskInfo>();
        if (!Directory.Exists(tasksRoot)) return list;
        foreach (var file in Enumerate(tasksRoot))
        {
            if (list.Count >= MaxTasks) break;
            try
            {
                var rel = "\\" + Path.GetRelativePath(tasksRoot, file);
                var info = Parse(rel, File.ReadAllText(file), classifier);
                if (info is not null) list.Add(info);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list.OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    static IEnumerable<string> Enumerate(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return Directory.EnumerateFiles(root, "*", opts);
    }

    public static TaskInfo? Parse(string path, string xml, ITaskClassifier classifier)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        var root = doc.Root;
        if (root is null) return null;
        var ns = root.Name.Namespace;
        var enabledText = root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
        var enabled = !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase);
        var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
        var runLevel = principal?.Element(ns + "RunLevel")?.Value;
        var userId = principal?.Element(ns + "UserId")?.Value ?? principal?.Element(ns + "GroupId")?.Value;
        var commands = root.Element(ns + "Actions")?.Elements(ns + "Exec")
            .Select(e => ((string?)e.Element(ns + "Command") ?? "") + " " + ((string?)e.Element(ns + "Arguments") ?? ""))
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList() ?? new List<string>();

        var (cls, reason) = classifier.Classify(path, enabled, runLevel, userId, commands);
        var name = path[(path.LastIndexOf('\\') + 1)..];
        var summary = commands.Count > 0 ? commands[0] : "Действие не из программы (COM-обработчик или другое)";
        return new TaskInfo(path, name, enabled, cls, reason, summary);
    }
}
