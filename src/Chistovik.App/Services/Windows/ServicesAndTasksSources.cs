using Chistovik.App.Models;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using PolicyStartMode = Chistovik.Core.Policy.StartMode;
using PortStartMode = Chistovik.Core.Ports.StartMode;

namespace Chistovik.App.Services.Windows;

/// <summary>
/// Службы через диспетчер служб (<see cref="IServiceControl"/>, в работе WindowsServiceControl; только чтение, без прав).
/// Класс «можно менять» это подсказка по <see cref="ServicePolicy"/> (тот же классификатор, что у агента): агент всё равно
/// перепроверяет каждый запрос. Служба, настройки которой без прав прочитать нельзя, показывается как «нет доступа».
/// </summary>
public sealed class ScmServiceSource : IServiceSource
{
    const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    readonly IServiceControl _scm;
    readonly IRegistryReader _reg;
    readonly Func<IReadOnlyList<InstalledProgram>> _programs;
    readonly IFileTrust _trust;
    readonly ServicePolicy _policy;

    public ScmServiceSource(IServiceControl scm, IRegistryReader reg, Func<IReadOnlyList<InstalledProgram>> programs, IFileTrust trust, ServicePolicy policy)
    {
        _scm = scm;
        _reg = reg;
        _programs = programs;
        _trust = trust;
        _policy = policy;
    }

    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default) => Task.Run(() => Read(ct), ct);

    public IReadOnlyList<ServiceInfo> Read(CancellationToken ct = default)
    {
        var names = _scm.ListNames();
        var programs = _programs();
        var list = new List<ServiceInfo>(names.Count);
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(ReadOne(name, programs));
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    ServiceInfo ReadOne(string name, IReadOnlyList<InstalledProgram> programs)
    {
        var display = DisplayNameOf(name);
        ServiceRaw? raw;
        try
        {
            using var handle = _scm.Open(name, write: false);
            raw = handle?.Query();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            raw = null; // отказ в доступе, служба исчезла или система не ответила: показываем как недоступную
        }
        if (raw is null)
            return new ServiceInfo(name, display, null, Changeability.ReadOnly, ReasonTexts.Explain(ReasonTexts.ServiceNoAccess), "",
                ReasonTexts.ServiceNoAccess, NoAccess: true);

        var image = raw.ImagePath ?? "";
        var imageExpanded = image.Length == 0 ? image : Environment.ExpandEnvironmentVariables(image);
        var exe = _policy.ResolveImagePath(imageExpanded);
        var owner = exe is null ? null : programs.FirstOrDefault(p => p.InstallLocation.Length > 0 && IsUnder(exe, p.InstallLocation));
        var sig = exe is null ? new SignatureFacts(false, false) : _trust.GetSignature(exe);

        var facts = new ServiceFacts(name, KindOf(raw.TypeFlags), imageExpanded, sig.Valid, sig.MicrosoftSigned, raw.LaunchProtected,
            raw.DependentCount, owner is not null, ToPolicyStart(raw.StartType, raw.Delayed), false, owner?.InstallLocation, exe);
        var (cls, policyReason, rule) = _policy.Classify(facts);
        var mapped = Map(cls);
        var reason = ReasonTexts.ExplainOrKeep(mapped, rule, policyReason);
        if (mapped == Changeability.Modifiable && raw.TriggerCount > 0) reason += " (запускается по событию)";
        return new ServiceInfo(name, display, ToPortStart(raw.StartType, raw.Delayed), mapped, reason, imageExpanded, rule, TriggerCount: raw.TriggerCount);
    }

    string DisplayNameOf(string name)
    {
        var v = _reg.ReadValues(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, ServicesPath + "\\" + name);
        if (v is null || !v.TryGetValue("DisplayName", out var o) || o is not string { Length: > 0 } s) return name;
        if (!s.StartsWith('@')) return s;
        // Ссылка на строковый ресурс («@%SystemRoot%\system32\x.dll,-100»): берём локализованное имя у системы, иначе короткое имя.
        return ResolveIndirect(s) ?? name;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, uint outputLength, IntPtr reserved);

    static string? ResolveIndirect(string reference)
    {
        try
        {
            var sb = new System.Text.StringBuilder(260);
            return SHLoadIndirectString(reference, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    static bool IsUnder(string exe, string dir)
        => PathNormalizer.TryNormalize(dir, out var d, out _) && PathNormalizer.IsEqualOrUnder(exe, d);

    static ServiceType KindOf(uint type)
    {
        if ((type & 0x1) != 0) return ServiceType.KernelDriver;
        if ((type & 0x2) != 0) return ServiceType.FileSystemDriver;
        if ((type & 0x10) != 0) return ServiceType.OwnProcess;
        if ((type & 0x20) != 0) return ServiceType.ShareProcess;
        return ServiceType.Other;
    }

    static PolicyStartMode ToPolicyStart(uint start, bool delayed) => start switch
    {
        0 => PolicyStartMode.Boot,
        1 => PolicyStartMode.System,
        2 => delayed ? PolicyStartMode.AutomaticDelayed : PolicyStartMode.Automatic,
        3 => PolicyStartMode.Manual,
        _ => PolicyStartMode.Disabled,
    };

    static PortStartMode? ToPortStart(uint start, bool delayed) => start switch
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
}

/// <summary>
/// Задачи планировщика через COM (<see cref="ITaskScheduler"/>, в работе WindowsTaskScheduler; только чтение).
/// Класс по <see cref="TaskPolicy"/>: «откат только собственного шага» для интерфейса это «только чтение»,
/// кроме задач каталога-исключения (<c>task-catalog-carveout</c>): их можно выключить.
/// Задача, которую без прав прочитать нельзя, показывается как «нет доступа».
/// </summary>
public sealed class SchedulerTaskSource(ITaskScheduler scheduler, TaskPolicy policy) : ITaskSource
{
    const int MaxTasks = 3000;

    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default) => Task.Run(() => Read(ct), ct);

    public IReadOnlyList<TaskInfo> Read(CancellationToken ct = default)
    {
        var list = new List<TaskInfo>();
        foreach (var path in scheduler.ListPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (list.Count >= MaxTasks) break;
            list.Add(ReadOne(path));
        }
        return list.OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    TaskInfo ReadOne(string path)
    {
        var name = path[(path.LastIndexOf('\\') + 1)..];
        TaskRaw? raw;
        try
        {
            using var handle = scheduler.Open(path);
            raw = handle?.Query();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            raw = null;
        }
        if (raw is null)
            return new TaskInfo(path, name, true, Changeability.ReadOnly, ReasonTexts.Explain(ReasonTexts.TaskNoAccess), "",
                ReasonTexts.TaskNoAccess, NoAccess: true);

        var (cls, policyReason, rule) = policy.Classify(raw);
        var mapped = cls switch
        {
            ChangeClass.Modifiable => Changeability.Modifiable,
            ChangeClass.Forbidden => Changeability.Forbidden,
            // Именованное исключение владельца: выключать можно (TaskInfo.DisableOnly запрещает включение напрямую).
            ChangeClass.RevertOnly when rule == "task-catalog-carveout" => Changeability.Modifiable,
            _ => Changeability.ReadOnly, // ReadOnly и остальной RevertOnly: напрямую задачу не меняем
        };
        return new TaskInfo(path, name, raw.Enabled, mapped, ReasonTexts.ExplainOrKeep(mapped, rule, policyReason), Summary(raw), rule);
    }

    static string Summary(TaskRaw raw)
    {
        var exec = raw.Actions.FirstOrDefault(a => string.Equals(a.Kind, "Exec", StringComparison.OrdinalIgnoreCase));
        if (exec is null || string.IsNullOrWhiteSpace(exec.Path)) return "Действие не из программы (COM-обработчик или другое)";
        return string.IsNullOrWhiteSpace(exec.Args) ? exec.Path : exec.Path + " " + exec.Args;
    }
}
