using Chistovik.Core.Ports;

namespace Chistovik.Core.Policy;

/// <summary>
/// Политика задач планировщика (DESIGN-0.1 §7). Единственное разрешённое действие над задачей это включить или выключить;
/// решение принимается по свежему <see cref="TaskRaw"/> того же хэндла. Всё неопознанное это ReadOnly или Forbidden.
/// </summary>
public sealed class TaskPolicy
{
    static readonly string[] ForbiddenRoots = ["Microsoft", "Chistovik"];
    static readonly char[] BadPathChars = ['/', ':', '*', '?', '"', '<', '>', '|'];

    // Учётные записи, под которыми задача получает больше прав обычного пользователя.
    static readonly HashSet<string> PrivilegedAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM", "LOCALSYSTEM", "LOCAL SYSTEM", "LOCALSERVICE", "LOCAL SERVICE", "NETWORKSERVICE", "NETWORK SERVICE",
        "ADMINISTRATORS", "ADMINISTRATOR", "S-1-5-18", "S-1-5-19", "S-1-5-20", "S-1-5-32-544",
    };

    readonly string _windowsDir;
    readonly string[] _allowedRoots;
    readonly string[] _systemOwnedRoots;
    readonly CommandResolver _resolver;

    public TaskPolicy(PolicyContext ctx)
    {
        _windowsDir = ctx.Norm(ctx.WindowsDir);
        var pf = ctx.Norm(ctx.ProgramFiles);
        var pf86 = ctx.Norm(ctx.ProgramFilesX86);
        var pd = ctx.Norm(ctx.ProgramData);
        _allowedRoots = [pf, pf86, pd, ctx.Norm(ctx.UsersDir)];
        _systemOwnedRoots = SystemRoots.Build(pf, pf86, pd);
        _resolver = new CommandResolver(ctx);
    }

    /// <summary>
    /// Проверяет путь задачи: начинается с «\», сегменты 1–255 символов без «/ : * ? " &lt; &gt; |», управляющих символов,
    /// «.» и «..», без хвостовых точек и пробелов, без пустых сегментов (в том числе хвостового «\»).
    /// <paramref name="segments"/> заполняется только при true.
    /// </summary>
    public static bool TryParsePath(string? path, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(path) || path.Length > 32767 || path[0] != '\\') return false;
        var parts = path[1..].Split('\\');
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 255) return false;
            if (p is "." or "..") return false;
            if (p[^1] is '.' or ' ') return false;
            foreach (var ch in p)
                if (ch < 0x20 || Array.IndexOf(BadPathChars, ch) >= 0) return false;
        }
        segments = parts;
        return true;
    }

    /// <summary>
    /// Классификация задачи по фактам: Forbidden (поддерево \Microsoft\* и \Chistovik\*, неверный путь, Exec в каталоге Windows
    /// или в системной зоне), ReadOnly (ComHandler/Other, неразобранный или внешний путь действия, нет действий, неизвестная
    /// учётная запись или RunLevel), RevertOnly (SYSTEM, LocalService, NetworkService, группа администраторов, служебные
    /// учётные записи, RunLevel=Highest/HighestAvailable, LogonType=ServiceAccount), иначе Modifiable.
    /// Берётся худший из классов по всем признакам.
    /// </summary>
    public (ChangeClass Class, string Reason, string Rule) Classify(TaskRaw t)
    {
        if (!TryParsePath(t.Path, out var seg))
            return (ChangeClass.Forbidden, "Недопустимый путь задачи", "task-bad-path");
        foreach (var root in ForbiddenRoots)
            if (seg[0].Equals(root, StringComparison.OrdinalIgnoreCase))
                return (ChangeClass.Forbidden, root == "Chistovik" ? "Задачи Чистовика не изменяются" : "Задачи Microsoft не изменяются", "task-protected-tree");

        if (t.Actions is null || t.Actions.Count == 0)
            return (ChangeClass.ReadOnly, "У задачи нет распознанных действий", "task-no-actions");
        if (t.Xml is not null && t.Xml.Contains("<ComHandler", StringComparison.OrdinalIgnoreCase))
            return (ChangeClass.ReadOnly, "Задача запускает COM-обработчик", "task-action-not-exec");

        var worst = (Class: ChangeClass.Modifiable, Reason: "Задача установленной программы", Rule: "task-modifiable");
        foreach (var a in t.Actions)
        {
            var r = ClassifyAction(a);
            if (r.Class < worst.Class) worst = r;       // Forbidden < ReadOnly < RevertOnly < Modifiable
            if (worst.Class == ChangeClass.Forbidden) return worst;
        }

        var principal = ClassifyPrincipal(t);
        if (principal.Class < worst.Class) worst = principal;
        return worst;
    }

    (ChangeClass Class, string Reason, string Rule) ClassifyAction(TaskAction a)
    {
        if (a is null || !string.Equals(a.Kind, "Exec", StringComparison.OrdinalIgnoreCase))
            return (ChangeClass.ReadOnly, "Действие задачи не запуск программы (COM-обработчик или иное)", "task-action-not-exec");
        if (!_resolver.TryResolve(a.Path, out var exe, out _))
            return (ChangeClass.ReadOnly, "Не удалось определить программу действия", "task-action-unresolved");
        if (PathNormalizer.IsEqualOrUnder(exe, _windowsDir))
            return (ChangeClass.Forbidden, "Действие задачи запускает файл из каталога Windows", "task-exec-in-windows");
        foreach (var root in _systemOwnedRoots)
            if (PathNormalizer.IsEqualOrUnder(exe, root))
                return (ChangeClass.Forbidden, "Действие задачи запускает файл из системной зоны", "task-exec-system-zone");
        foreach (var root in _allowedRoots)
            if (PathNormalizer.IsEqualOrUnder(exe, root))
                return (ChangeClass.Modifiable, "", "");
        return (ChangeClass.ReadOnly, "Программа действия вне Program Files, ProgramData и профилей", "task-exec-outside-roots");
    }

    static (ChangeClass Class, string Reason, string Rule) ClassifyPrincipal(TaskRaw t)
    {
        var level = (t.RunLevel ?? string.Empty).Trim();
        if (level.Equals("Highest", StringComparison.OrdinalIgnoreCase) || level.Equals("HighestAvailable", StringComparison.OrdinalIgnoreCase))
            return (ChangeClass.RevertOnly, "Задача с повышенными правами: только откат собственного шага", "task-elevated");
        if (!level.Equals("Limited", StringComparison.OrdinalIgnoreCase) && !level.Equals("LeastPrivilege", StringComparison.OrdinalIgnoreCase))
            return (ChangeClass.ReadOnly, "Неизвестный уровень прав задачи", "task-unknown-runlevel");

        if ((t.LogonType ?? string.Empty).Trim().Equals("ServiceAccount", StringComparison.OrdinalIgnoreCase))
            return (ChangeClass.RevertOnly, "Задача от служебной учётной записи: только откат собственного шага", "task-privileged-principal");

        // SID надёжнее имени (имя зависит от языка и домена): служебные S-1-5-18/19/20 и подобные определяются в первую очередь.
        var sid = (t.UserSid ?? string.Empty).Trim();
        if (sid.Length > 0 && IsPrivilegedAccount(sid))
            return (ChangeClass.RevertOnly, "Задача от SYSTEM или служебной учётной записи (по SID): только откат собственного шага", "task-privileged-principal");

        var user = (t.UserId ?? string.Empty).Trim();
        if (user.Length == 0)
            return (ChangeClass.ReadOnly, "Не указана учётная запись задачи", "task-unknown-principal");
        if (IsPrivilegedAccount(user))
            return (ChangeClass.RevertOnly, "Задача от SYSTEM или служебной учётной записи: только откат собственного шага", "task-privileged-principal");

        return (ChangeClass.Modifiable, "", "");
    }

    static bool IsPrivilegedAccount(string user)
    {
        var name = user;
        var slash = user.LastIndexOf('\\');
        var domain = slash >= 0 ? user[..slash] : string.Empty;
        if (slash >= 0) name = user[(slash + 1)..];
        if (domain.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) || domain.Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase)
            || domain.Equals("BUILTIN", StringComparison.OrdinalIgnoreCase))
            return true;
        if (PrivilegedAccounts.Contains(name) || PrivilegedAccounts.Contains(user)) return true;
        // Служебные SID: S-1-5-18/19/20, S-1-5-32-544, S-1-5-80-* (SID служб), S-1-5-90-* (Window Manager), S-1-5-96-*.
        return name.StartsWith("S-1-5-80-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("S-1-5-90-", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("S-1-5-96-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Разрешает включение (<paramref name="enable"/>=true) или выключение задачи по свежему <paramref name="t"/>.
    /// Сигнатура для StepGate: <c>CheckSetEnabled(TaskRaw t, bool enable, bool isRevertOfOwnStep = false)</c>.
    /// Allow: класс Modifiable, либо RevertOnly при <paramref name="isRevertOfOwnStep"/>=true (откат собственного шага журнала;
    /// флаг выставляет только StepGate). Forbidden и ReadOnly это Deny всегда, откатом тоже. Target = «task:&lt;путь&gt;».
    /// </summary>
    public PolicyDecision CheckSetEnabled(TaskRaw t, bool enable, bool isRevertOfOwnStep = false)
    {
        var (cls, reason, rule) = Classify(t);
        var target = "task:" + t.Path;
        return cls switch
        {
            ChangeClass.Modifiable => PolicyDecision.Allow("task-modifiable").WithTarget(target),
            ChangeClass.RevertOnly when isRevertOfOwnStep => PolicyDecision.Allow("task-revert-own-step").WithTarget(target),
            ChangeClass.RevertOnly => PolicyDecision.Deny(reason, rule).WithTarget(target),
            ChangeClass.ReadOnly => PolicyDecision.Deny("Только просмотр: " + reason, rule).WithTarget(target),
            _ => PolicyDecision.Deny(reason, rule).WithTarget(target),
        };
    }
}
