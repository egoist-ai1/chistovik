using Chistovik.App.Services.Changes;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.App.ViewModels;

/// <summary>
/// Сводка результата операции для экрана: сколько шагов применено, пропущено и не получилось, и по каждому
/// проблемному шагу причина простыми словами с подсказкой, что делать.
/// </summary>
internal static class ResultSummary
{
    public static (string Counts, IReadOnlyList<string> Problems) Build(IReadOnlyList<StepResult> steps, bool applied)
    {
        int ok = 0, skipped = 0, failed = 0;
        var problems = new List<string>();
        foreach (var s in steps)
        {
            switch (Classify(s))
            {
                case Bucket.Ok: ok++; continue;
                case Bucket.Skipped: skipped++; break;
                default: failed++; break;
            }
            var line = ResponseText.StepKindName(s.Kind) + ": " + ResponseText.StepStateName(s.State).ToLowerInvariant();
            var why = Why(s.Code);
            problems.Add(why.Length > 0 ? line + ". " + why : line);
        }

        var parts = new List<string> { (applied ? "Применено: " : "Возвращено: ") + ok };
        if (skipped > 0) parts.Add("Пропущено: " + skipped);
        if (failed > 0) parts.Add("Не получилось: " + failed);
        return (string.Join(" · ", parts), problems);
    }

    private enum Bucket { Ok, Skipped, Failed }

    private static Bucket Classify(StepResult s) => s.State switch
    {
        StepState.Done or StepState.Undone => Bucket.Ok,
        StepState.Skipped when s.Code is "no-change" or "already-reverted" => Bucket.Ok,
        StepState.Skipped or StepState.Pending => Bucket.Skipped,
        _ => Bucket.Failed,
    };

    /// <summary>Причина и что делать; пусто, если кода нет.</summary>
    public static string Why(string? code) => code switch
    {
        null or "" => "",
        "external-change" => "Значение изменили не через Чистовик, поэтому мы его не трогаем. Если нужно, верните его вручную.",
        "policy-denied" => "Защита не разрешила это изменение: так мы оберегаем систему. Ничего не изменено.",
        "not-found" => "Объекта уже нет в системе: возможно, программу удалили. Обновите список.",
        "access-denied" => "Windows не дала доступ. Повторите действие и подтвердите запрос на права администратора.",
        "verify-mismatch" => "После записи значение оказалось другим. Проверьте журнал и повторите.",
        "recovered-not-applied" => "Работа прервалась до этого шага, он не выполнялся. Повторите действие.",
        "recovered-done" => "Работа прервалась, но этот шаг успел выполниться.",
        _ when code.StartsWith("win32:", StringComparison.Ordinal) => "Windows вернула ошибку " + code[6..] + ". Повторите действие; если не помогло, откатите операцию.",
        _ when Models.ReasonTexts.IsKnown(code) => Models.ReasonTexts.Explain(code),
        _ => ResponseText.StepCodeName(code),
    };
}
