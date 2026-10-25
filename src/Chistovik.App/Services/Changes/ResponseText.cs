using Chistovik.App.Services.Agent;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Changes;

/// <summary>Перевод кодов агента и клиента в русский текст. Подробности исключений сюда не попадают.</summary>
public static class ResponseText
{
    public static ChangeOutcome ViewOnly()
        => new(OutcomeKind.ViewOnly, "Права администратора не выданы: доступен только просмотр. Нажмите «Запросить права», чтобы менять настройки.");

    public static ChangeOutcome ForApply(AgentResponse r) => Map(r, undo: false);

    public static ChangeOutcome ForUndo(AgentResponse r) => Map(r, undo: true);

    /// <summary>
    /// Удаление программы: один шаг. Успех это «удалена» (код win32:3010 добавляет просьбу о перезагрузке); неудача шага
    /// объясняется его кодом (занят установщик, срок вышел, программа осталась). Откат удаления не предлагается.
    /// </summary>
    public static ChangeOutcome ForUninstall(AgentResponse r)
    {
        var o = Map(r, undo: false);
        var step = r.Steps?.FirstOrDefault();
        var why = step?.Code is { Length: > 0 } c ? StepCodeName(c) : "";
        switch (r.Code)
        {
            case ResponseCodes.Ok:
                return o with
                {
                    Message = "Программа удалена. Вернуть её можно только установкой заново.",
                    RestartHint = step?.Code == "win32:3010" ? "Чтобы удаление завершилось, перезагрузите компьютер." : null,
                };
            case ResponseCodes.NoChange:
                return o with { Message = "Программа уже удалена: в списке она осталась по ошибке, обновите его." };
            case ResponseCodes.Partial:
                return o with
                {
                    Kind = OutcomeKind.Unavailable,
                    Message = "Программа не удалена" + (why.Length > 0 ? ": " + why.TrimEnd('.') + "." : ". Подробности в журнале."),
                };
            default:
                return o;
        }
    }

    static ChangeOutcome Map(AgentResponse r, bool undo)
    {
        var (done, total) = Count(r.Steps);
        ChangeOutcome Make(OutcomeKind k, string msg) => new(k, msg, r.OperationId, done, total, r.Conflicts, null, r);

        switch (r.Code)
        {
            case ResponseCodes.Ok:
                return Make(OutcomeKind.Success, undo
                    ? (total > 0 ? $"Вернули {done} из {total}." : "Операция откачена.")
                    : (total > 0 ? $"Применено шагов: {done} из {total}." : "Готово."));
            case ResponseCodes.NoChange:
                return Make(OutcomeKind.NoChange, undo
                    ? "Откатывать нечего: значения уже такие, как до операции."
                    : "Менять нечего: значение уже такое.");
            case ResponseCodes.Partial:
                return Make(OutcomeKind.Partial, undo
                    ? $"Вернули {done} из {total}. Остальное не откатилось, подробности в журнале."
                    : $"Выполнено {done} из {total}. Остальное не применилось, подробности в журнале. Выполненное можно откатить.");
            case ResponseCodes.Conflict:
                return Make(OutcomeKind.Conflict, "После операции эти значения изменились. Выберите, что с ними делать.");
            case ResponseCodes.PolicyDenied:
                return Make(OutcomeKind.Denied, "Защита не разрешила это изменение. Ничего не изменено." + Tail(r.Message));
            case ResponseCodes.NotFound:
                return Make(OutcomeKind.Denied, "Объект не найден в системе. Ничего не изменено." + Tail(r.Message));
            case ResponseCodes.UnknownOperation:
                return Make(OutcomeKind.Denied, "Такой операции нет в журнале.");
            case ResponseCodes.NotRevertible:
                return Make(OutcomeKind.Denied, "Эту операцию откатить нельзя: удалённую программу можно вернуть только установкой заново.");
            case ResponseCodes.BadRequest:
                return Make(OutcomeKind.Denied, "Запрос отклонён проверкой." + Tail(r.Message));
            case ResponseCodes.Busy:
                return Make(OutcomeKind.Unavailable, "Агент занят другой операцией. Повторите через несколько секунд.");
            case ResponseCodes.Internal:
                return Make(OutcomeKind.Unavailable, "Агент столкнулся с внутренней ошибкой. Проверьте журнал, прежде чем повторять." + Tail(r.Message));
            case ResponseCodes.JournalUnavailable:
                return Make(OutcomeKind.Unavailable, "Журнал недоступен, поэтому изменения не выполнены.");
            case ResponseCodes.JournalUntrusted:
                return Make(OutcomeKind.Unavailable, "Папка журнала небезопасна (права доступа), изменения не выполнены.");
            case ResponseCodes.JournalNewerSchema:
                return Make(OutcomeKind.Unavailable, "Журнал создан более новой версией программы. Обновите Чистовик.");
            case ClientCodes.Declined:
                return ViewOnly() with { Response = r };
            case ClientCodes.NotRunning:
            case ClientCodes.LaunchFailed:
                return Make(OutcomeKind.Unavailable, "Не удалось запустить агент с правами." + Tail(r.Message));
            case ClientCodes.Timeout:
                return Make(OutcomeKind.Unavailable, "Агент не ответил вовремя. Результат неизвестен: проверьте журнал.");
            case ClientCodes.Disconnected:
                return Make(OutcomeKind.Unavailable, "Связь с агентом оборвалась. Проверьте журнал, прежде чем повторять.");
            default:
                return Make(OutcomeKind.Unavailable, "Агент вернул ошибку (" + r.Code + ").");
        }
    }

    /// <summary>Хвост сообщения агента; если сообщение — известный код причины, он переводится на русский.</summary>
    static string Tail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var m = message.Trim();
        return " " + (Models.ReasonTexts.IsKnown(m) ? Models.ReasonTexts.Explain(m) : m);
    }

    /// <summary>Done и пропущенные «уже так» считаются выполненными; Failed/Denied/Pending/Undone нет.</summary>
    static (int Done, int Total) Count(IReadOnlyList<StepResult>? steps)
    {
        if (steps is null) return (0, 0);
        var done = steps.Count(s => s.State == StepState.Done
            || s.State == StepState.Undone
            || (s.State == StepState.Skipped && s.Code is "no-change" or "already-reverted"));
        return (done, steps.Count);
    }

    public static string RestartHint(IReadOnlySet<string> restarts)
    {
        if (restarts.Contains("reboot")) return "Чтобы изменения заработали, перезагрузите компьютер.";
        if (restarts.Contains("logoff")) return "Чтобы изменения заработали, выйдите из системы и войдите снова.";
        if (restarts.Contains("explorer")) return "Чтобы изменения стали видны, перезапустите Проводник.";
        return "";
    }

    public static string KindName(OperationKind k) => k switch
    {
        OperationKind.Tweaks => "Твики",
        OperationKind.Mode => "Режим",
        OperationKind.Startup => "Автозагрузка",
        OperationKind.Service => "Служба",
        OperationKind.Task => "Задача",
        OperationKind.PowerScheme => "Схема питания",
        OperationKind.PowerSetting => "Параметр питания",
        OperationKind.Undo => "Откат",
        OperationKind.Uninstall => "Удаление программы",
        _ => k.ToString(),
    };

    public static string StateName(OperationState s) => s switch
    {
        OperationState.Running => "Выполняется",
        OperationState.Done => "Завершено",
        OperationState.Partial => "Частично",
        OperationState.Failed => "Не выполнено",
        OperationState.Denied => "Отказано",
        OperationState.Interrupted => "Прервано",
        OperationState.Undone => "Откачено",
        OperationState.PartiallyUndone => "Откачено частично",
        _ => s.ToString(),
    };

    public static string StepStateName(StepState s) => s switch
    {
        StepState.Pending => "Не завершён",
        StepState.Done => "Выполнено",
        StepState.Failed => "Ошибка",
        StepState.Skipped => "Пропущено",
        StepState.Denied => "Отказано",
        StepState.Undone => "Возвращено",
        _ => s.ToString(),
    };

    public static string StepCodeName(string? code) => code switch
    {
        null or "" => "",
        "no-change" => "значение уже было таким",
        "external-change" => "значение изменили снаружи",
        "policy-denied" => "защита не разрешила",
        "not-found" => "объект не найден",
        "access-denied" => "нет доступа",
        "verify-mismatch" => "после записи значение не совпало",
        "recovered-done" => "восстановлено после сбоя: выполнено",
        "recovered-not-applied" => "восстановлено после сбоя: не применялось",
        "already-reverted" => "уже возвращено",
        "win32:3010" => "программа удалена, нужна перезагрузка компьютера",
        "win32:1603" => "установщик сообщил о неустранимой ошибке (1603)",
        _ when code.StartsWith("win32:", StringComparison.Ordinal) => "ошибка Windows " + code[6..],
        _ when Models.ReasonTexts.IsKnown(code) => Models.ReasonTexts.Explain(code),
        _ => code,
    };

    public static string StepKindName(StepKind k) => k switch
    {
        StepKind.RegistryValue => "Значение реестра",
        StepKind.StartupApproved => "Автозагрузка",
        StepKind.ServiceStart => "Запуск службы",
        StepKind.TaskEnabled => "Задача планировщика",
        StepKind.PowerActiveScheme => "Активная схема питания",
        StepKind.PowerSetting => "Параметр питания",
        StepKind.PowerSchemeCreate => "Создание схемы питания",
        StepKind.UninstallRun => "Удаление программы",
        _ => k.ToString(),
    };
}
