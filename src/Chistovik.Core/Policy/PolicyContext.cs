namespace Chistovik.Core.Policy;

/// <summary>Неверное расположение системных каталогов: политику на таком контексте строить нельзя.</summary>
public sealed class PolicyContextException(string message) : InvalidOperationException(message);

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

        var windows = Pick(Environment.SpecialFolder.Windows, @"C:\Windows");

        // 64-разрядный Program Files: в 32-разрядном процессе SpecialFolder.ProgramFiles вернул бы x86-каталог.
        // В 64-разрядном процессе (агент) переменной окружения не верим: её может переопределить HKCU\Environment.
        var pfEnv = Environment.Is64BitProcess ? null : Environment.GetEnvironmentVariable("ProgramW6432");
        var pf = string.IsNullOrWhiteSpace(pfEnv) ? Pick(Environment.SpecialFolder.ProgramFiles, @"C:\Program Files") : pfEnv;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var fallbackUsers = (string.IsNullOrWhiteSpace(systemDrive) ? "C:" : systemDrive.TrimEnd('\\')) + @"\Users";
        // Win32 GetProfilesDirectory здесь не вызываем (Core без P/Invoke): каталог профилей это родитель профиля,
        // а если он не подходит (учётная запись службы, профиль внутри Windows), берём запасной вариант.
        var users = string.IsNullOrWhiteSpace(profile) ? null : Path.GetDirectoryName(profile.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(users) || IsUnderRaw(users, windows)) users = fallbackUsers;

        return CreateValidated(
            windows,
            pf,
            Pick(Environment.SpecialFolder.ProgramFilesX86, @"C:\Program Files (x86)"),
            Pick(Environment.SpecialFolder.CommonApplicationData, @"C:\ProgramData"),
            users);
    }

    /// <summary>Создаёт контекст и проверяет его согласованность; при ошибке бросает PolicyContextException.</summary>
    public static PolicyContext CreateValidated(string windowsDir, string programFiles, string programFilesX86, string programData, string usersDir)
    {
        var ctx = new PolicyContext(windowsDir, programFiles, programFilesX86, programData, usersDir);
        ctx.Validate();
        return ctx;
    }

    /// <summary>Проверка согласованности каталогов (ошибка в контексте это ошибка программы, а не входных данных).</summary>
    public void Validate()
    {
        var win = Norm(WindowsDir);
        var pf = Norm(ProgramFiles);
        var pf86 = Norm(ProgramFilesX86);
        var pd = Norm(ProgramData);
        var users = Norm(UsersDir);

        if (PathNormalizer.IsEqualOrUnder(users, win))
            throw new PolicyContextException("Каталог профилей находится внутри каталога Windows: " + users);
        if (pf.Equals(pf86, StringComparison.OrdinalIgnoreCase))
            throw new PolicyContextException("Program Files и Program Files (x86) совпадают: " + pf);
        foreach (var (name, dir) in new[] { ("Program Files", pf), ("Program Files (x86)", pf86), ("ProgramData", pd) })
            if (PathNormalizer.IsEqualOrUnder(dir, win))
                throw new PolicyContextException($"{name} находится внутри каталога Windows: {dir}");
    }

    static bool IsUnderRaw(string path, string root)
        => PathNormalizer.TryNormalize(path, out var p, out _) && PathNormalizer.TryNormalize(root, out var r, out _)
           && PathNormalizer.IsEqualOrUnder(p, r);

    /// <summary>Нормализованный путь из контекста; ошибка в контексте это ошибка программы, а не входных данных.</summary>
    internal string Norm(string raw)
    {
        if (!PathNormalizer.TryNormalize(raw, out var n, out var err))
            throw new PolicyContextException($"Неверный каталог в PolicyContext '{raw}': {err}");
        return n;
    }
}

/// <summary>Нарушение политики: действие запрещено или требует подтверждения эксперта, а его нет.</summary>
public sealed class PolicyViolationException(PolicyDecision decision)
    : Exception($"{decision.Rule}: {decision.Reason}")
{
    public PolicyDecision Decision { get; } = decision;

    /// <summary>
    /// Единая точка применения решения. Allow проходит; ExpertOnly проходит только с подтверждением, данным
    /// для того же объекта (нормализованная цель решения); всё остальное, включая default, бросает.
    /// </summary>
    public static void Enforce(PolicyDecision decision, ExpertConfirmation? confirmation = null)
    {
        if (decision.Verdict == Verdict.Allow) return;
        if (decision.Verdict == Verdict.ExpertOnly
            && confirmation is { } c
            && decision.Target is { Length: > 0 }
            && string.Equals(c.NormalizedTarget, decision.Target, StringComparison.OrdinalIgnoreCase))
            return;
        throw new PolicyViolationException(decision);
    }
}
