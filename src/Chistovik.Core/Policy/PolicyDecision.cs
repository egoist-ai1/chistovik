namespace Chistovik.Core.Policy;

/// <summary>
/// Итог проверки политики. Deny — действие запрещено всегда, ExpertOnly — только с явным подтверждением эксперта на каждый объект.
/// Порядок значений важен: нулевое значение (default) это Deny, чтобы неинициализированное решение никогда не разрешало действие.
/// </summary>
public enum Verdict
{
    Deny = 0,
    ExpertOnly = 1,
    Allow = 2,
}

/// <summary>
/// Решение политики: вердикт, понятная причина для интерфейса, код правила для журнала и тестов
/// и нормализованный объект проверки (к нему привязывается подтверждение эксперта).
/// </summary>
public readonly record struct PolicyDecision(Verdict Verdict, string Reason, string Rule, string Target = "")
{
    public bool IsAllowed => Verdict == Verdict.Allow;
    public bool IsDenied => Verdict == Verdict.Deny;

    public static PolicyDecision Allow(string rule = "allow") => new(Verdict.Allow, "Не запрещено политикой", rule);
    public static PolicyDecision Expert(string reason, string rule) => new(Verdict.ExpertOnly, reason, rule);
    public static PolicyDecision Deny(string reason, string rule) => new(Verdict.Deny, reason, rule);

    /// <summary>Копия решения с нормализованным объектом проверки.</summary>
    public PolicyDecision WithTarget(string target) => this with { Target = target };
}

/// <summary>Подтверждение эксперта для одного конкретного объекта (нормализованный путь, ключ реестра и т. п.).</summary>
public readonly record struct ExpertConfirmation(string NormalizedTarget);
