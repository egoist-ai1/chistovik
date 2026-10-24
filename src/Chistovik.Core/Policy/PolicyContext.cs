namespace Chistovik.Core.Policy;

/// <summary>
/// Расположение системных каталогов конкретной машины. В тестах подставляются фиксированные значения,
/// в приложении берутся из окружения.
/// </summary>
public sealed record PolicyContext(string WindowsDir, string ProgramFiles, string ProgramFilesX86, string ProgramData, string UsersDir)
{
    public static PolicyContext Fixed { get; } =
        new(@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users");

    public static PolicyContext FromEnvironment()
    {
        static string Pick(Environment.SpecialFolder f, string fallback)
        {
            var v = Environment.GetFolderPath(f);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        var profile = Pick(Environment.SpecialFolder.UserProfile, @"C:\Users\user");
        var users = Path.GetDirectoryName(profile.TrimEnd('\\')) ?? @"C:\Users";
        return new PolicyContext(
            Pick(Environment.SpecialFolder.Windows, @"C:\Windows"),
            Pick(Environment.SpecialFolder.ProgramFiles, @"C:\Program Files"),
            Pick(Environment.SpecialFolder.ProgramFilesX86, @"C:\Program Files (x86)"),
            Pick(Environment.SpecialFolder.CommonApplicationData, @"C:\ProgramData"),
            users);
    }

    /// <summary>Нормализованный путь из контекста; ошибка в контексте это ошибка программы, а не входных данных.</summary>
    internal string Norm(string raw)
    {
        if (!PathNormalizer.TryNormalize(raw, out var n, out var err))
            throw new InvalidOperationException($"Неверный каталог в PolicyContext '{raw}': {err}");
        return n;
    }
}

/// <summary>Нарушение политики: действие запрещено или требует подтверждения эксперта, а его нет.</summary>
public sealed class PolicyViolationException(PolicyDecision decision)
    : Exception($"{decision.Rule}: {decision.Reason}")
{
    public PolicyDecision Decision { get; } = decision;

    /// <summary>Единая точка применения решения: Deny всегда бросает, ExpertOnly бросает без подтверждения.</summary>
    public static void Enforce(PolicyDecision decision, bool expertConfirmed = false)
    {
        if (decision.Verdict == Verdict.Allow) return;
        if (decision.Verdict == Verdict.ExpertOnly && expertConfirmed) return;
        throw new PolicyViolationException(decision);
    }
}
