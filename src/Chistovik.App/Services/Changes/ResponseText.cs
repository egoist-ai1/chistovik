using Chistovik.App.Services.Agent;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Changes;

/// <summary>Перевод кодов агента и клиента в русский текст. Подробности исключений сюда не попадают.</summary>
public static class ResponseText
{
    public static ChangeOutcome ViewOnly()
        => new(OutcomeKind.ViewOnly, "Права администратора не выданы: доступен только просмотр. Нажмите «Запросить права», чтобы менять настройки.");

    public static ChangeOutcome ViewOnly(AgentViewOnlyReason reason)
        => reason == AgentViewOnlyReason.NoAdminAccount
            ? new(OutcomeKind.ViewOnly, LaunchMessages.NoAdminAccount + ".")
            : ViewOnly();

    public static ChangeOutcome ForApply(AgentResponse r) => Map(r, undo: false);

    /// <summary>Режим: как обычное применение, плюс примечание, если агент пропустил недоступную схему питания.</summary>
    public static ChangeOutcome ForMode(AgentResponse r)
    {
        var o = Map(r, undo: false);
        if (r.Facts?.Any(f => f.Key == "mode:power-scheme-skipped") != true) return o;
        const string note = "Режим включён, кроме схемы питания: схемы нет на этом устройстве.";
        return o.Kind is OutcomeKind.Success or OutcomeKind.NoChange or OutcomeKind.Partial
            ? o with { Message = (o.Kind == OutcomeKind.Success ? "" : o.Message + " ") + note }
            : o;
    }

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

    /// <summary>Измерение очистки диска (<c>MeasureOnly</c>): ничего не удаляется и не журналируется, размеры лежат в <c>Facts</c>.</summary>
    public static ChangeOutcome ForMeasure(AgentResponse r)
    {
        var o = Map(r, undo: false);
        return r.Code == ResponseCodes.Ok
            ? o with { Message = "Размер посчитан. Ничего не удалено." }
            : o;
    }

    /// <summary>
    /// Очистка диска: итог по категориям. Освобождённое место и число файлов берутся из фактов ответа; категории, которые
    /// пришлось пропустить (браузер открыт, нет папки), считаются отдельно. Удаление необратимо, поэтому об откате не говорим.
    /// </summary>
    public static ChangeOutcome ForCleanup(AgentResponse r)
    {
        var o = Map(r, undo: false);
        var facts = ParseCleanupFacts(r.Facts);
        var freed = facts.Sum(f => f.FreedBytes);
        var deleted = facts.Sum(f => f.Deleted);
        var skipped = r.Steps?.Count(s => s.State is StepState.Skipped or StepState.Denied) ?? 0;
        var failed = r.Steps?.Count(s => s.State == StepState.Failed) ?? 0;
        var tail = (skipped > 0 ? $" Пропущено категорий: {skipped}." : "") + (failed > 0 ? $" Не удалось очистить: {failed}." : "");
        var what = freed > 0 || deleted > 0
            ? $"Освобождено {ViewModels.Fmt.Size(freed)}, удалено файлов: {ViewModels.Fmt.Number((int)Math.Min(deleted, int.MaxValue))}."
            : "Подходящих файлов не нашлось.";
        switch (r.Code)
        {
            case ResponseCodes.Ok:
                return o with { Message = "Очистка завершена. " + what + tail + " Вернуть удалённое нельзя." };
            case ResponseCodes.NoChange:
                return o with { Message = "Чистить было нечего." + tail };
            case ResponseCodes.Partial:
                return o with { Message = "Очистка выполнена частично. " + what + tail + " Подробности в журнале." };
            default:
                return o;
        }
    }

    /// <summary>
    /// Чистка реестра: пробная проверка (<c>dry-run</c>) только сообщает, что агент подтвердил; боевой запуск говорит,
    /// сколько записей убрано и что копии лежат в журнале. Запись, чьё доказательство не подтвердилось, остаётся нетронутой.
    /// </summary>
    public static ChangeOutcome ForFindings(AgentResponse r)
    {
        var o = Map(r, undo: false);
        var (done, total) = Count(r.Steps);
        var notConfirmed = r.Steps?.Count(s => s.Code == StepCodes.EvidenceFailed) ?? 0;
        var tail = notConfirmed > 0 ? $" Не подтвердилось ещё раз: {notConfirmed}, их не тронули." : "";
        switch (r.Code)
        {
            case ResponseCodes.DryRun:
                return o with
                {
                    Kind = OutcomeKind.NoChange,
                    Message = $"Пробная проверка: агент подтвердил записей: {done} из {total}. Ничего не изменено." + tail,
                };
            case ResponseCodes.Ok:
                return o with { Message = $"Убрано записей реестра: {done} из {total}. Копии сохранены в журнале, любую запись можно вернуть." };
            case ResponseCodes.NoChange:
                return o with { Message = "Убирать нечего: этих записей в реестре уже нет." };
            case ResponseCodes.Partial:
                return o with { Message = $"Убрано записей реестра: {done} из {total}." + tail + " Подробности в журнале; убранное можно вернуть." };
            default:
                return o;
        }
    }

    /// <summary>Итог очистки одной категории из фактов ответа.</summary>
    public sealed record CleanupFact(string Category, long Bytes, long Files, long Deleted, long FreedBytes,
        IReadOnlyDictionary<string, int> Skipped, int Failed);

    /// <summary>
    /// Разбирает факты очистки: ключ «cleanup:{scope}:{sid|-}:{категория}» либо «recyclebin:{sid}», значение это состояние
    /// категории (bytes, files, deleted, freedBytes, skipped, failed). Всё непонятное пропускается: факты только для показа.
    /// </summary>
    public static IReadOnlyList<CleanupFact> ParseCleanupFacts(IReadOnlyList<FactEntry>? facts)
    {
        var list = new List<CleanupFact>();
        if (facts is null) return list;
        foreach (var f in facts)
        {
            string? category = null;
            if (f.Key.StartsWith("cleanup:", StringComparison.OrdinalIgnoreCase) && f.Key.Split(':', 4) is { Length: 4 } p) category = p[3];
            else if (f.Key.StartsWith("recyclebin:", StringComparison.OrdinalIgnoreCase))
                category = Chistovik.Core.Catalog.CleanupCatalog.Shared.Categories.FirstOrDefault(c => c.Mechanism == "recyclebin")?.Id;
            if (category is null) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(f.Json);
                var e = doc.RootElement;
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
                if (e.TryGetProperty("skipped", out var sk) && sk.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var kv in sk.EnumerateObject())
                        if (kv.Value.TryGetInt32(out var n)) skipped[kv.Name] = n;
                list.Add(new CleanupFact(category, Num(e, "bytes"), Num(e, "files"), Num(e, "deleted"), Num(e, "freedBytes"),
                    skipped, (int)Num(e, "failed")));
            }
            catch (System.Text.Json.JsonException) { }
        }
        return list;

        static long Num(System.Text.Json.JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
    }

    static ChangeOutcome Map(AgentResponse r, bool undo)
    {
        var (done, total) = Count(r.Steps);
        ChangeOutcome Make(OutcomeKind k, string msg) => new(k, msg, r.OperationId, done, total, r.Conflicts, null, r, r.Facts);

        switch (r.Code)
        {
            case ResponseCodes.DryRun:
                return Make(OutcomeKind.NoChange, "Пробная проверка прошла. Ничего не изменено.");
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
            case ClientCodes.NoAdminAccount:
                return ViewOnly(AgentViewOnlyReason.NoAdminAccount) with { Response = r };
            case ClientCodes.AlreadyRunning:
                return Make(OutcomeKind.Unavailable, LaunchMessages.AlreadyRunning);
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
        OperationKind.Cleanup => "Очистка диска",
        OperationKind.Registry => "Реестр",
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
        "skipped-by-rule" => "пропущено по правилу очистки",
        "evidence-failed" => "при повторной проверке запись оказалась не лишней, её не тронули",
        "reg-tree-too-large" => "в записи слишком много вложенных ключей, автоматически её не убираем",
        "reg-tree-has-link" => "внутри записи есть ссылка на другое место реестра, её не убираем",
        "reg-tree-bad-name" => "имя ключа или значения содержит недопустимый символ, запись не тронута",
        "reg-tree-bad-type" => "внутри ключа есть значение особого типа, которое нельзя вернуть при откате, запись не тронута",
        "partial-delete" => "удаление остановилось на середине, ключ остался неполным; копия сохранена, её можно вернуть из журнала",
        "backup-mismatch" => "копия записи не совпала с оригиналом, запись не тронули",
        "backup-missing" => "копии записи нет, вернуть её нельзя",
        "interrupted" => "шаг прервала перезагрузка агента, проверьте результат",
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
        StepKind.CleanupCategory => "Очистка категории",
        StepKind.RecycleBinEmpty => "Очистка Корзины",
        StepKind.RegistryKeyDelete => "Удаление записи реестра",
        StepKind.StartupRemove => "Удаление записи автозапуска",
        StepKind.SharedDllRemove => "Удаление записи общего файла",
        _ => k.ToString(),
    };
}
