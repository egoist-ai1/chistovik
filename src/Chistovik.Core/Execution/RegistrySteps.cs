using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Сессия записи значения реестра через один хэндл. handle = null: ключа нет (значение отсутствует).</summary>
internal sealed class RegistrySession(IRegKeyHandle? handle, ApprovedRegistryWrite? approved, string valueName,
    Func<RegValueState, string> toJson) : IStepSession
{
    public string ReadStateJson() => toJson(handle is null ? new RegValueState(false, RegType.None, null) : handle.Read(valueName));

    public void Write()
    {
        if (handle is null || approved is null) throw new InvalidOperationException("Сессия открыта без права записи");
        handle.Write(approved);
    }

    public void Dispose() => handle?.Dispose();
}

internal static class RegistryOpen
{
    /// <summary>
    /// Общая логика открытия значения реестра: хэндл, повторная проверка шлюзом по ResolvedPath и IsSymbolicLink этого же хэндла.
    /// Ключа нет: при записи значения он создаётся; при удалении значения отсутствие ключа это «значения нет».
    /// </summary>
    public static OpenOutcome Open(ExecEnv env, RegLocation loc, RegValueState desired, string clientSid, bool forWrite,
        Func<RegValueState, string> toJson)
    {
        var handle = forWrite ? env.Registry.Open(loc, true, desired.Exists) : env.Registry.Open(loc, false, false);
        if (handle is null)
        {
            if (forWrite)
            {
                if (desired.Exists) return OpenOutcome.Refuse(StepCodes.AccessDenied, "reg-open-failed");
                // Удаление значения: ключа нет или нет доступа. Различаем повторным открытием на чтение.
                using var probe = env.Registry.Open(loc, false, false);
                if (probe is not null) return OpenOutcome.Refuse(StepCodes.AccessDenied, "reg-open-failed");
            }
            var t = env.Gate.CheckRegistryTarget(loc, desired, clientSid);
            if (!t.IsAllowed) return OpenOutcome.Refuse(t);
            return OpenOutcome.Ok(new RegistrySession(null, null, loc.ValueName, toJson));
        }

        var g = env.Gate.CheckRegistryWrite(loc, desired, handle, clientSid);
        if (!g.IsAllowed)
        {
            handle.Dispose();
            return OpenOutcome.Refuse(g);
        }
        return OpenOutcome.Ok(new RegistrySession(handle, g.Approved, loc.ValueName, toJson));
    }
}

/// <summary>Значение реестра из каталога твиков (HKLM или HKU\SID клиента).</summary>
internal sealed class RegistryValueStep : PlannedStep
{
    readonly RegLocation _loc;
    readonly RegValueState _desired;
    readonly string _sid;

    public RegistryValueStep(RegLocation loc, RegValueState desired, string clientSid, string restart = "none")
        : base(StepKind.RegistryValue, StateJson.RegistryKey(loc), StateJson.RegistryTargetJson(loc), StateJson.RegistryJson(desired), restart)
    {
        _loc = loc;
        _desired = desired;
        _sid = clientSid;
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
        => RegistryOpen.Open(env, _loc, _desired, _sid, forWrite, StateJson.RegistryJson);
}

/// <summary>Флаг включения записи автозагрузки в StartupApproved. Запись должна быть в собственном перечислении агента, команда не изменилась.</summary>
internal sealed class StartupStep : PlannedStep
{
    readonly StartupEntryId _id;
    readonly string _commandSha;
    readonly RegLocation _loc;
    readonly RegValueState _desired;
    readonly string _sid;

    public StartupStep(StartupEntryId id, StartupTarget target, string commandSha256, RegLocation approvedLocation, RegValueState desired, string clientSid)
        : base(StepKind.StartupApproved, StateJson.StartupKey(target.Root, target.Sid, target.Approved, target.Name),
            StateJson.StartupTargetJson(target), StateJson.StartupJson(desired))
    {
        _id = id;
        _commandSha = commandSha256;
        _loc = approvedLocation;
        _desired = desired;
        _sid = clientSid;
    }

    /// <summary>Команда записи изменилась после операции, которую откатываем (шаг построен по текущей команде). UndoService трактует это как конфликт.</summary>
    public bool CommandChanged { get; init; }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        // 1) цель выводится из перечисления агента (свежего); команда не должна измениться с момента планирования
        var entry = StartupEnumerator.Find(env, _sid, _id);
        if (entry is null) return OpenOutcome.Refuse(StepCodes.NotFound, "startup-entry-missing");
        if (!string.Equals(entry.CommandSha256, _commandSha, StringComparison.OrdinalIgnoreCase))
            return OpenOutcome.Refuse(StepCodes.PolicyDenied, "startup-command-changed");
        // 2) StartupPolicy по свежей команде и подписи
        var gate = env.Gate.CheckStartupEntry(_id, entry.Command);
        if (!gate.IsAllowed) return OpenOutcome.Refuse(gate);
        // 3) запись флага: тот же путь, что у любого значения реестра
        return RegistryOpen.Open(env, _loc, _desired, _sid, forWrite, StateJson.StartupJson);
    }
}
