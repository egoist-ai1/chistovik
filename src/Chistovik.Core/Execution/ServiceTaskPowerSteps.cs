using Chistovik.Core.Catalog;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

// ---------- службы ----------

internal sealed class ServiceSession(IServiceHandle handle, ApprovedServiceChange? approved) : IStepSession
{
    public string ReadStateJson() => StateJson.ServiceJson(handle.Query());

    public void Write()
    {
        if (approved is null) throw new InvalidOperationException("Сессия открыта без права записи");
        handle.SetStart(approved);
    }

    public void Dispose() => handle.Dispose();
}

/// <summary>Тип запуска службы. Желаемое состояние это (StartType, Delayed); Boot и System построить нельзя.</summary>
internal sealed class ServiceStep : PlannedStep
{
    readonly string _name;
    readonly Ports.StartMode _desired;

    public ServiceStep(string name, Ports.StartMode desired)
        : base(StepKind.ServiceStart, StateJson.ServiceKey(name), StateJson.ServiceTargetJson(name), ServiceDesiredJson(desired))
    {
        _name = name;
        _desired = desired;
    }

    public static string ServiceDesiredJson(Ports.StartMode m) => m switch
    {
        Ports.StartMode.Automatic => StateJson.ServiceJson(2, false, false, false),
        Ports.StartMode.AutomaticDelayed => StateJson.ServiceJson(2, true, false, false),
        Ports.StartMode.Manual => StateJson.ServiceJson(3, false, false, false),
        _ => StateJson.ServiceJson(4, false, false, false),
    };

    /// <summary>Режим по сырому (Start, Delayed) из журнала; false для Boot, System и неизвестных значений.</summary>
    public static bool TryFromState(ServiceState s, out Ports.StartMode mode)
    {
        mode = Ports.StartMode.Manual;
        switch (s.Start)
        {
            case 2: mode = s.Delayed ? Ports.StartMode.AutomaticDelayed : Ports.StartMode.Automatic; return true;
            case 3: mode = Ports.StartMode.Manual; return true;
            case 4: mode = Ports.StartMode.Disabled; return true;
            default: return false;
        }
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var handle = env.Services.Open(_name, forWrite);
        if (handle is null) return OpenOutcome.Refuse(StepCodes.NotFound, "service-not-found");
        ServiceRaw raw;
        try { raw = handle.Query(); }
        catch
        {
            handle.Dispose();
            throw;
        }
        var g = env.Gate.CheckServiceChange(raw, _desired, IsRevert, _name);
        if (!g.IsAllowed)
        {
            handle.Dispose();
            return OpenOutcome.Refuse(g);
        }
        return OpenOutcome.Ok(new ServiceSession(handle, g.Approved));
    }
}

// ---------- задачи ----------

internal sealed class TaskSession(ITaskHandle handle, ApprovedTaskChange? approved) : IStepSession
{
    public string ReadStateJson() => StateJson.TaskJson(handle.Query());

    public void Write()
    {
        if (approved is null) throw new InvalidOperationException("Сессия открыта без права записи");
        handle.SetEnabled(approved);
    }

    public void Dispose() => handle.Dispose();
}

internal sealed class TaskStep : PlannedStep
{
    readonly string _path;
    readonly bool _enabled;

    public TaskStep(string path, bool enabled)
        : base(StepKind.TaskEnabled, StateJson.TaskKey(path), StateJson.TaskTargetJson(path), StateJson.TaskJson(true, enabled))
    {
        _path = path;
        _enabled = enabled;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var handle = env.Tasks.Open(_path);
        if (handle is null) return OpenOutcome.Refuse(StepCodes.NotFound, "task-not-found");
        TaskRaw raw;
        try { raw = handle.Query(); }
        catch
        {
            handle.Dispose();
            throw;
        }
        var g = env.Gate.CheckTaskChange(raw, _enabled, IsRevert, _path);
        if (!g.IsAllowed)
        {
            handle.Dispose();
            return OpenOutcome.Refuse(g);
        }
        return OpenOutcome.Ok(new TaskSession(handle, g.Approved));
    }
}

// ---------- питание ----------

internal sealed class DelegateSession(Func<string> read, Action? write) : IStepSession
{
    public string ReadStateJson() => read();

    public void Write()
    {
        if (write is null) throw new InvalidOperationException("Сессия открыта без права записи");
        write();
    }

    public void Dispose()
    {
    }
}

/// <summary>Создание (desiredExists=true) или удаление (false, только откат) нашей схемы питания под заранее выбранным GUID.</summary>
internal sealed class PowerCreateStep : PlannedStep
{
    readonly PowerSchemeEntry _entry;
    readonly Guid _scheme;
    readonly bool _desiredExists;

    public PowerCreateStep(PowerSchemeEntry entry, Guid scheme, bool desiredExists)
        : base(StepKind.PowerSchemeCreate, StateJson.PowerCreateKey(entry.CreateFromTemplate ?? Guid.Empty),
            StateJson.PowerSchemeTargetJson(entry.CreateFromTemplate ?? Guid.Empty, scheme), StateJson.PowerSchemeJson(desiredExists))
    {
        _entry = entry;
        _scheme = scheme;
        _desiredExists = desiredExists;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var installed = sim?.Installed ?? [.. env.Power.ListSchemes()];
        var present = installed.Contains(_scheme);
        string Read() => StateJson.PowerSchemeJson(env.Power.ListSchemes().Contains(_scheme));

        if (_desiredExists)
        {
            // Схема с этим GUID уже есть (повтор после обрыва): шлюз видит список без неё, шаг дальше станет «ничего менять не нужно».
            IReadOnlyCollection<Guid> others = present ? installed.Where(x => x != _scheme).ToArray() : installed;
            var g = env.Gate.CheckSchemeCreate(_entry, _scheme, others);
            if (!g.IsAllowed) return OpenOutcome.Refuse(g);
            return OpenOutcome.Ok(new DelegateSession(Read, forWrite ? () => env.Power.Duplicate(g.Approved!) : null));
        }

        if (!present) return OpenOutcome.Ok(new DelegateSession(Read, null));
        var template = _entry.CreateFromTemplate ?? Guid.Empty;
        var ours = OwnSchemes.JournalSaysOurs(env, template, _scheme);
        // Журнал не единственное доказательство: имя схемы в системе должно быть нашим (если порт его отдаёт).
        var name = env.Power.GetName(_scheme);
        if (name is not null && !string.Equals(name, StepGate.OwnSchemeName(_entry), StringComparison.Ordinal))
            return OpenOutcome.Refuse(new Refusal(StepCodes.PolicyDenied, "power-scheme-name-mismatch"));
        var active = sim?.Active ?? env.Power.GetActive();
        var d = env.Gate.CheckSchemeDelete(_scheme, active, ours);
        if (!d.IsAllowed) return OpenOutcome.Refuse(d);
        return OpenOutcome.Ok(new DelegateSession(Read, forWrite ? () => env.Power.Delete(d.Approved!) : null));
    }

    public override void Simulate(SimState sim)
    {
        if (_desiredExists)
        {
            sim.Installed.Add(_scheme);
            sim.PlannedOwn.Add(_scheme);
        }
        else sim.Installed.Remove(_scheme);
    }
}

/// <summary>Активная схема питания. entry = null только для отката на ранее активную схему.</summary>
internal sealed class PowerActiveStep : PlannedStep
{
    readonly PowerSchemeEntry? _entry;
    readonly Guid _scheme;

    public PowerActiveStep(PowerSchemeEntry? entry, Guid scheme)
        : base(StepKind.PowerActiveScheme, StateJson.PowerActiveKey, StateJson.PowerActiveTargetJson(), StateJson.PowerActiveJson(scheme))
    {
        _entry = entry;
        _scheme = scheme;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var installed = sim?.Installed ?? [.. env.Power.ListSchemes()];
        var ours = OwnSchemes.IsOurs(env, _scheme, installed) || sim?.PlannedOwn.Contains(_scheme) == true;
        var g = env.Gate.CheckPowerScheme(_entry, _scheme, installed, ours, IsRevert);
        if (!g.IsAllowed)
        {
            // Встроенная схема, которой нет на устройстве (Modern Standby): шаг пропускается, это не ошибка (DESIGN-0.1 §9).
            if (g.Code == StepCodes.NotFound && !IsRevert && _entry is not null && _scheme == _entry.Guid)
                return OpenOutcome.Refuse(new Refusal(StepCodes.NotFound, g.Rule, SkipNotFound: true));
            return OpenOutcome.Refuse(g);
        }
        return OpenOutcome.Ok(new DelegateSession(() => StateJson.PowerActiveJson(env.Power.GetActive()),
            forWrite ? () => env.Power.SetActive(g.Approved!) : null));
    }

    public override void Simulate(SimState sim) => sim.Active = _scheme;
}

/// <summary>Параметр питания (AC и DC) в схеме: при применении это активная схема на момент выполнения.</summary>
internal sealed class PowerSettingStep : PlannedStep
{
    readonly PowerSettingEntry _entry;
    readonly Guid _scheme;
    readonly uint _ac;
    readonly uint _dc;

    public PowerSettingStep(PowerSettingEntry entry, Guid scheme, uint ac, uint dc)
        : base(StepKind.PowerSetting, StateJson.PowerSettingKey(scheme, entry.Subgroup, entry.Setting),
            StateJson.PowerSettingTargetJson(scheme, entry.Subgroup, entry.Setting), StateJson.PowerSettingJson(ac, dc))
    {
        _entry = entry;
        _scheme = scheme;
        _ac = ac;
        _dc = dc;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var installed = sim?.Installed ?? [.. env.Power.ListSchemes()];
        var active = sim?.Active ?? env.Power.GetActive();
        var ours = OwnSchemes.IsOurs(env, _scheme, installed) || sim?.PlannedOwn.Contains(_scheme) == true;
        var g = env.Gate.CheckPowerSetting(_entry, _scheme, active, !IsRevert, ours, installed, _ac, _dc);
        if (!g.IsAllowed) return OpenOutcome.Refuse(g);
        return OpenOutcome.Ok(new DelegateSession(
            () =>
            {
                var (ac, dc) = env.Power.Read(_scheme, _entry.Subgroup, _entry.Setting);
                return StateJson.PowerSettingJson(ac, dc);
            },
            forWrite ? () => env.Power.Write(g.Approved!) : null));
    }
}
