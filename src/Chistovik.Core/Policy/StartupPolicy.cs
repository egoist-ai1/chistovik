using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>
/// Политика записей автозагрузки (DESIGN-0.1 §7). Запись приходит из собственного перечисления агента:
/// идентификатор, команда (для записи папки это цель ярлыка, найденная слоем Native) и факты подписи файла.
/// Закрыты Windows, Defender и Security Health; команда, которую нельзя разобрать однозначно, это ReadOnly.
/// </summary>
public sealed class StartupPolicy
{
    // Имена файлов, которые не меняем независимо от каталога (в том числе при относительной команде).
    static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealthSystray.exe", "SecurityHealthService.exe", "SecurityHealthHost.exe", "MSASCuiL.exe", "MpCmdRun.exe",
        "MsMpEng.exe", "NisSrv.exe", "SecHealthUI.exe", "smartscreen.exe",
    };

    // Имена значений Run, которыми Windows регистрирует собственные компоненты безопасности.
    static readonly HashSet<string> ProtectedRunNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealth", "WindowsDefender", "Windows Defender",
    };

    readonly string _windowsDir;
    readonly string[] _systemOwnedRoots;
    readonly CommandResolver _resolver;

    public StartupPolicy(PolicyContext ctx)
    {
        _windowsDir = ctx.Norm(ctx.WindowsDir);
        _systemOwnedRoots = SystemRoots.Build(ctx.Norm(ctx.ProgramFiles), ctx.Norm(ctx.ProgramFilesX86), ctx.Norm(ctx.ProgramData));
        _resolver = new CommandResolver(ctx);
    }

    /// <summary>
    /// Разбирает команду автозагрузки до пути исполняемого файла (кавычки, аргументы после файла, %SystemRoot%, %windir%,
    /// %ProgramFiles%, %ProgramFiles(x86)%, %ProgramW6432%, %ProgramData%). Любая иная переменная окружения, относительный
    /// или сетевой путь, незакрытая кавычка и управляющие символы дают false. Путь в <paramref name="exe"/> нормализован.
    /// </summary>
    public bool TryResolveCommand(string? command, out string exe, out string error)
        => _resolver.TryResolve(command, out exe, out error);

    /// <summary>
    /// Классифицирует запись. Forbidden: файл в каталоге Windows (в том числе System32 и SecurityHealthSystray), в Windows
    /// Defender, Windows Security, Security Health, WindowsApps, Common Files\Microsoft Shared, известное защищённое имя файла
    /// или значения Run (SecurityHealth); подпись Microsoft из каталога Windows. ReadOnly: команда не разобрана, или факты
    /// подписи противоречивы (MicrosoftSigned при Valid=false). Иначе Modifiable. Подпись для остального не используется:
    /// сторонняя и неподписанная программа переключается так же.
    /// </summary>
    public (ChangeClass Class, string Reason, string Rule) Classify(StartupEntryId id, string? command, SignatureFacts signature)
    {
        if (id.IsRun && ProtectedRunNames.Contains(id.Name))
            return (ChangeClass.Forbidden, "Запись компонентов безопасности Windows", "startup-security-component");

        // Имя файла проверяем даже тогда, когда путь не разобрался (относительная команда «SecurityHealthSystray.exe»).
        var looseName = LooseFileName(command);
        if (looseName is not null && ProtectedFileNames.Contains(looseName))
            return (ChangeClass.Forbidden, "Компонент безопасности Windows", "startup-security-component");

        if (!_resolver.TryResolve(command, out var exe, out _))
            return (ChangeClass.ReadOnly, "Не удалось однозначно определить программу автозагрузки", "startup-unparsed");

        if (PathNormalizer.IsEqualOrUnder(exe, _windowsDir))
            return signature.MicrosoftSigned && signature.Valid
                ? (ChangeClass.Forbidden, "Файл Microsoft из каталога Windows", "startup-microsoft-in-windows")
                : (ChangeClass.Forbidden, "Файл лежит в каталоге Windows", "startup-in-windows");
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(exe, root))
                return (ChangeClass.Forbidden, "Файл в системной зоне (Defender, Security, Store)", "startup-system-zone");
        var fileName = exe[(exe.LastIndexOf('\\') + 1)..];
        if (ProtectedFileNames.Contains(fileName))
            return (ChangeClass.Forbidden, "Компонент безопасности Windows", "startup-security-component");

        if (signature.MicrosoftSigned && !signature.Valid)
            return (ChangeClass.ReadOnly, "Противоречивые факты подписи файла", "startup-bad-signature-facts");

        return (ChangeClass.Modifiable, "Запись автозагрузки программы", "startup-modifiable");
    }

    /// <summary>
    /// Разрешает включение или выключение записи. Сигнатура для StepGate:
    /// <c>CheckToggle(StartupEntryId id, string? command, SignatureFacts signature)</c>; <c>id</c> получен из
    /// <see cref="StartupEntryId.TryParse"/>, команда и подпись взяты из собственного перечисления агента (не из запроса).
    /// Allow только для класса Modifiable; Forbidden и ReadOnly это Deny. Target = нормализованный идентификатор.
    /// </summary>
    public PolicyDecision CheckToggle(StartupEntryId id, string? command, SignatureFacts signature)
    {
        var (cls, reason, rule) = Classify(id, command, signature);
        var target = "startup:" + id;
        return cls switch
        {
            ChangeClass.Modifiable => PolicyDecision.Allow(rule).WithTarget(target),
            ChangeClass.ReadOnly => PolicyDecision.Deny("Только просмотр: " + reason, rule).WithTarget(target),
            _ => PolicyDecision.Deny(reason, rule).WithTarget(target),
        };
    }

    // Имя файла из первого слова или кавычек без раскрытия путей: подстраховка для относительных и нераскрытых команд.
    static string? LooseFileName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.Trim();
        string head;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            head = end > 1 ? s[1..end] : s[1..];
        }
        else
        {
            var sp = s.IndexOf(' ');
            head = sp > 0 ? s[..sp] : s;
        }
        var cut = Math.Max(head.LastIndexOf('\\'), head.LastIndexOf('/'));
        var name = head[(cut + 1)..].Trim();
        return name.Length == 0 ? null : name;
    }
}
