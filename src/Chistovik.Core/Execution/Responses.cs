using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Ответ агента по записи журнала (и для первого выполнения, и для повтора запроса с тем же Id).</summary>
internal static class Responses
{
    static IReadOnlyList<StepResult> Steps(OperationView v)
        => v.Steps.Select(s => new StepResult(s.Id, s.Ord, s.Kind, s.State, s.Code)).ToList();

    static string FailureCode(string? stepCode) => stepCode switch
    {
        StepCodes.PolicyDenied => ResponseCodes.PolicyDenied,
        StepCodes.NotFound => ResponseCodes.NotFound,
        _ => ResponseCodes.Internal,
    };

    public static AgentResponse FromOperation(Guid requestId, OperationView v)
    {
        var op = v.Summary.Id;
        var steps = Steps(v);
        var code = v.Summary.Code;
        switch (v.Summary.State)
        {
            case OperationState.Done or OperationState.Undone or OperationState.PartiallyUndone:
                return code == StepCodes.NoChange
                    ? new AgentResponse(requestId, true, ResponseCodes.NoChange, "Изменений не потребовалось", op, steps)
                    : new AgentResponse(requestId, true, ResponseCodes.Ok, null, op, steps);
            case OperationState.Partial:
                return new AgentResponse(requestId, false, ResponseCodes.Partial, "Выполнено частично: " + code, op, steps);
            case OperationState.Denied:
                return new AgentResponse(requestId, false, code is ResponseCodes.NotFound or ResponseCodes.Internal or ResponseCodes.BadRequest ? code : ResponseCodes.PolicyDenied,
                    "Операция отклонена", op, steps);
            case OperationState.Failed:
                return new AgentResponse(requestId, false, FailureCode(code), "Операция не выполнена: " + code, op, steps);
            case OperationState.Interrupted:
                return new AgentResponse(requestId, false, ResponseCodes.Internal, "Операция была прервана", op, steps);
            default:
                return new AgentResponse(requestId, false, ResponseCodes.Busy, "Операция ещё выполняется", op, steps);
        }
    }
}

/// <summary>Короткое описание состояния цели для окна конфликта (по-русски, без служебных данных).</summary>
internal static class StateDisplay
{
    public static string Of(StepKind kind, string stateJson)
    {
        try
        {
            var s = kind switch
            {
                StepKind.RegistryValue => Reg(StateJson.ParseRegistry(stateJson)),
                StepKind.StartupApproved => StartupText(StateJson.ParseStartup(stateJson)),
                StepKind.ServiceStart => Svc(StateJson.ParseService(stateJson)),
                StepKind.TaskEnabled => StateJson.ParseTask(stateJson) is { Exists: true } t ? (t.Enabled ? "включена" : "выключена") : "задачи нет",
                StepKind.PowerActiveScheme => "активна схема " + StateJson.ParsePowerActive(stateJson).Scheme.ToString("D"),
                StepKind.PowerSetting => PowerText(StateJson.ParsePowerSetting(stateJson)),
                StepKind.PowerSchemeCreate => StateJson.ParsePowerScheme(stateJson).Exists ? "схема есть" : "схемы нет",
                StepKind.CleanupCategory or StepKind.RecycleBinEmpty => CleanupText(C2C3Json.Parse(stateJson, JournalJson.Default.CleanupState)),
                StepKind.RegistryKeyDelete => KeyText(RegTreeJson.ParseKeyState(stateJson)),
                StepKind.StartupRemove or StepKind.SharedDllRemove => Reg(StateJson.ParseRegistry(stateJson)),
                _ => "неизвестно",
            };
            return s.Length <= 200 ? s : s[..200];
        }
        catch (InvalidDataException)
        {
            return "неизвестно";
        }
    }

    static string Reg(RegValueState v)
    {
        if (!v.Exists) return "значения нет";
        return v.Type switch
        {
            RegType.DWord => "число " + v.Data,
            RegType.String or RegType.ExpandString => "строка «" + v.Data + "»",
            RegType.Binary => "двоичные данные",
            _ => v.Type.ToString(),
        };
    }

    static string StartupText((bool Exists, byte[]? Bytes) s)
        => StartupStateComparer.Class(s) switch
        {
            true => "включена",
            false => "выключена",
            _ => "неизвестное состояние",
        };

    static string CleanupText(CleanupState c)
        => c.Done ? "очищено" : c.Files is > 0 ? $"файлов: {c.Files}" : c.Bytes is > 0 ? "есть данные для очистки" : "очищать нечего";

    static string KeyText(RegKeyState k)
        => !k.Exists ? "ключа нет" : k.Subkeys is { } n ? $"ключ есть, подключей: {n}" : "ключ есть";

    static string PowerText(PowerSettingState p) => $"от сети {p.Ac}, от батареи {p.Dc}";

    static string Svc(ServiceState s) => s.Start switch
    {
        2 => s.Delayed ? "запуск: автоматически (отложенный)" : "запуск: автоматически",
        3 => "запуск: вручную",
        4 => "запуск: отключена",
        _ => "запуск: особый",
    };
}
