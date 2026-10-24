namespace Chistovik.Core.Policy;

/// <summary>Итог проверки политики. Deny — действие запрещено всегда, ExpertOnly — только с явным подтверждением эксперта на каждый объект.</summary>
public enum Verdict
{
    Allow = 0,
    ExpertOnly = 1,
    Deny = 2,
}

/// <summary>Решение политики: вердикт, понятная причина для интерфейса и код правила для журнала и тестов.</summary>
public readonly record struct PolicyDecision(Verdict Verdict, string Reason, string Rule)
{
    public bool IsAllowed => Verdict == Verdict.Allow;
    public bool IsDenied => Verdict == Verdict.Deny;

    public static PolicyDecision Allow(string rule = "allow") => new(Verdict.Allow, "Не запрещено политикой", rule);
    public static PolicyDecision Expert(string reason, string rule) => new(Verdict.ExpertOnly, reason, rule);
    public static PolicyDecision Deny(string reason, string rule) => new(Verdict.Deny, reason, rule);
}
