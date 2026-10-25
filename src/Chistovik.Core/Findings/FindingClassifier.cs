using System.Text.RegularExpressions;
using Chistovik.Core.Policy;

namespace Chistovik.Core.Findings;

// Факты об объекте, собранные слоем App (только чтение). Классификатор ничего не читает сам и ничего не меняет.

/// <summary>
/// Запись автозагрузки. <paramref name="ExePath"/> это раскрытый путь файла; <paramref name="FileMissing"/> true только если
/// путь разобран и файла на диске нет, null если проверить не удалось. Подпись: null значит «не проверяли».
/// </summary>
public sealed record StartupSubject(
    string Id, string Name, string Command, string Origin, ChangeClass Class, string? ClassReason,
    string? ExePath = null, bool? FileMissing = null, bool? SignatureValid = null, bool? MicrosoftSigned = null);

public sealed record ServiceSubject(string Name, string DisplayName, string ImagePath, ChangeClass Class, string? ClassReason);

public sealed record TaskSubject(string Path, string Name, string Summary, ChangeClass Class, string? ClassReason);

public sealed record ProgramSubject(string Name, string Publisher, string Version, long SizeBytes, string Source);

/// <summary>
/// Чистая функция «факты → находка». Правила для автозагрузки (первое совпавшее): Forbidden → Protected; ReadOnly или
/// нераскрытая команда → Unknown; файла нет → Remove; роль защиты или помощника устройства → Keep; обновлялка с действительной
/// подписью → Remove; временная папка или неподписанный файл в AppData → Optional (риск средний); остальное → Optional.
/// Remove без подписи и без «файла нет» не выдаётся. Класс по политике вердиктом не меняется и не ослабляется:
/// объект с советом «убрать» и классом Forbidden остаётся только для просмотра.
/// </summary>
public sealed class FindingClassifier
{
    public static FindingClassifier Shared { get; } = new(AdviceCatalog.Shared);

    static readonly string[] RuntimeMarkers =
    [
        "Visual C++", "Redistributable", ".NET", " Runtime", "WebView2", "Java(TM)", "Java ", "DirectX", "OpenAL", "Windows SDK",
    ];

    const string StdUndoStartup = "Запись отключается и включается обратно; изменение пишется в журнал и откатывается кнопкой «Откатить».";
    const string StdUndoService = "Тип запуска меняется и возвращается; изменение пишется в журнал и откатывается кнопкой «Откатить».";
    const string StdUndoTask = "Задачу можно включить обратно; изменение пишется в журнал и откатывается кнопкой «Откатить».";
    const string NothingToUndo = "Менять нечего: объект защищён или только для просмотра.";

    readonly AdviceCatalog _catalog;

    public FindingClassifier(AdviceCatalog catalog) => _catalog = catalog;

    // ----- автозагрузка -----

    public Finding Startup(StartupSubject s)
    {
        var fileName = ExeName(s.ExePath ?? s.Command);
        var advice = _catalog.FindStartup(s.Name, fileName);
        var id = "startup:" + (s.Id.Length > 0 ? s.Id : $"ro:{s.Origin}:{s.Name}");
        var title = advice?.Title ?? s.Name;
        var target = string.IsNullOrEmpty(fileName) ? "программу" : fileName;
        var cls = s.Class;
        var pathCode = PathCode(s.ExePath ?? s.Command);
        var known = advice is null ? null : ReasonCodes.Make(ReasonCodes.PublisherKnown);
        var role = advice is null ? null : ReasonCodes.Make(advice.Role);

        // 1. Закрыто политикой.
        if (cls == ChangeClass.Forbidden)
            return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Protected,
                Reasons(ReasonCodes.Make(ReasonCodes.ClassForbidden, s.ClassReason), role, MsReason(s)),
                Risk.High, Reversibility.NotAvailable, Confidence.L0,
                advice is null
                    ? new Explanation(
                        $"Запись автозагрузки «{s.Name}» относится к системе или защите Windows.",
                        "Нужна для работы Windows или защиты компьютера.",
                        "Не меняется: программа не позволяет это отключить.", NothingToUndo)
                    : Advice(advice, protectedNote: true),
                cls, "Автозагрузка");

        // 2. Только чтение или команда не разобрана.
        var unresolved = s.ExePath is null && s.FileMissing is null;
        if (cls != ChangeClass.Modifiable || unresolved)
            return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Unknown,
                Reasons(cls != ChangeClass.Modifiable ? ReasonCodes.Make(ReasonCodes.ClassReadOnly, s.ClassReason) : null,
                        unresolved ? ReasonCodes.Make(ReasonCodes.CommandUnresolved) : null, role),
                Risk.Medium, Reversibility.NotAvailable, Confidence.L4,
                advice is null
                    ? new Explanation(
                        $"Запись автозагрузки «{s.Name}».",
                        "Программа не смогла надёжно определить, что именно запускается, поэтому совет не даётся.",
                        "Не меняется: запись только для просмотра.", NothingToUndo)
                    : Advice(advice, protectedNote: true),
                cls, "Автозагрузка");

        // 3. Файла нет: запись ничего не запускает.
        if (s.FileMissing == true)
            return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Remove,
                Reasons(ReasonCodes.Make(ReasonCodes.FileMissing, s.ExePath), role),
                Risk.Low, Reversibility.Undoable, Confidence.L2,
                new Explanation(
                    $"Запись автозагрузки «{s.Name}»: при входе в Windows пытается запустить {target}, но этого файла уже нет.",
                    "Файла нет, поэтому запись ничего не запускает. Скорее всего, программу уже удалили.",
                    "Ничего не изменится: запускать нечего.", StdUndoStartup),
                cls, "Автозагрузка");

        // 4. Защита и помощники устройств.
        if (advice is not null && advice.Role is ReasonCodes.RoleSecurity or ReasonCodes.RoleDeviceHelper)
            return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Keep,
                Reasons(role, known, MsReason(s)),
                advice.Role == ReasonCodes.RoleSecurity ? Risk.High : Risk.Medium, Reversibility.NotAvailable, Confidence.L1,
                Advice(advice, startupUndo: StdUndoStartup), cls, "Автозагрузка");

        // 5. Обновлялка с действительной подписью.
        if (advice is not null && advice.Role == ReasonCodes.RoleUpdater && s.SignatureValid == true)
            return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Remove,
                Reasons(role, known, MsReason(s), PathReason(pathCode)),
                Risk.Low, Reversibility.Undoable, Confidence.L1,
                Advice(advice, startupUndo: StdUndoStartup), cls, "Автозагрузка");

        // 6. Временная папка или неподписанный файл в AppData: осторожнее.
        var unsigned = s.SignatureValid == false;
        var risky = pathCode == ReasonCodes.PathTemp || (unsigned && pathCode == ReasonCodes.PathAppData);
        var rest = Reasons(role, known, unsigned ? ReasonCodes.Make(ReasonCodes.Unsigned) : null,
            MsReason(s), PathReason(pathCode), ReasonCodes.Make(ReasonCodes.ImpactUnknown));
        var explanation = advice is not null
            ? Advice(advice, startupUndo: StdUndoStartup)
            : new Explanation(
                $"Запись автозагрузки «{s.Name}»: при входе в Windows запускает {target}.",
                "Программа хочет стартовать вместе с Windows. Нужна ли она сразу после входа, решаете вы.",
                "Программа не запустится сама, но останется установленной: её можно открыть вручную.",
                StdUndoStartup);
        return Build(id, FindingArea.Startup, title, s.Command, VerdictKind.Optional, rest,
            risky ? Risk.Medium : Risk.Low, Reversibility.Undoable, advice is not null ? Confidence.L1 : Confidence.L3,
            explanation, cls, "Автозагрузка");
    }

    // ----- службы -----

    public Finding Service(ServiceSubject s)
    {
        var id = "service:" + s.Name;
        var title = string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName;
        var advice = _catalog.FindService(s.Name);
        var hard = ServicePolicy.IsHardListed(s.Name);

        if (hard || (advice is null && s.Class == ChangeClass.Forbidden))
            return Build(id, FindingArea.Services, title, s.ImagePath, VerdictKind.Protected,
                Reasons(ReasonCodes.Make(ReasonCodes.ClassForbidden, s.ClassReason)),
                Risk.High, Reversibility.NotAvailable, Confidence.L0,
                new Explanation(
                    $"Системная служба Windows «{title}».",
                    "Нужна для работы Windows или защиты компьютера.",
                    "Не меняется: служба защищена.", NothingToUndo),
                s.Class, "Службы");

        if (advice is not null)
            return FromAdvice(id, FindingArea.Services, title, s.ImagePath, advice, s.Class, s.ClassReason, "Службы", StdUndoService);

        if (s.Class != ChangeClass.Modifiable)
            return Build(id, FindingArea.Services, title, s.ImagePath, VerdictKind.Unknown,
                Reasons(ReasonCodes.Make(ReasonCodes.ClassReadOnly, s.ClassReason)),
                Risk.Medium, Reversibility.NotAvailable, Confidence.L4,
                new Explanation(
                    $"Служба «{title}».",
                    "Программа не смогла надёжно определить назначение службы, поэтому совет не даётся.",
                    "Не меняется: служба только для просмотра.", NothingToUndo),
                s.Class, "Службы");

        return Build(id, FindingArea.Services, title, s.ImagePath, VerdictKind.Optional,
            Reasons(PathReason(PathCode(s.ImagePath)), ReasonCodes.Make(ReasonCodes.ImpactUnknown)),
            Risk.Low, Reversibility.Undoable, Confidence.L3,
            new Explanation(
                $"Служба «{title}» установленной программы.",
                "Работает вместе с программой, которой принадлежит. Если программой вы не пользуетесь, службу можно перевести на ручной запуск.",
                "Программа может не получить фоновые функции (обновления, синхронизацию), пока вы не запустите службу.",
                StdUndoService),
            s.Class, "Службы");
    }

    // ----- задачи -----

    public Finding Task(TaskSubject s)
    {
        var id = "task:" + s.Path;
        var advice = _catalog.FindTask(s.Path);

        if (advice is not null)
            return FromAdvice(id, FindingArea.Tasks, s.Name, s.Path, advice, s.Class, s.ClassReason, "Задачи", StdUndoTask);

        if (s.Class == ChangeClass.Forbidden)
            return Build(id, FindingArea.Tasks, s.Name, s.Path, VerdictKind.Protected,
                Reasons(ReasonCodes.Make(ReasonCodes.ClassForbidden, s.ClassReason)),
                Risk.High, Reversibility.NotAvailable, Confidence.L0,
                new Explanation(
                    $"Системная задача Windows «{s.Name}».",
                    "Нужна для обслуживания Windows.",
                    "Не меняется: задача защищена.", NothingToUndo),
                s.Class, "Задачи");

        if (s.Class != ChangeClass.Modifiable)
            return Build(id, FindingArea.Tasks, s.Name, s.Path, VerdictKind.Unknown,
                Reasons(ReasonCodes.Make(ReasonCodes.ClassReadOnly, s.ClassReason)),
                Risk.Medium, Reversibility.NotAvailable, Confidence.L4,
                new Explanation(
                    $"Задача планировщика «{s.Name}».",
                    "Программа не смогла надёжно определить назначение задачи, поэтому совет не даётся.",
                    "Не меняется: задача только для просмотра.", NothingToUndo),
                s.Class, "Задачи");

        return Build(id, FindingArea.Tasks, s.Name, s.Path, VerdictKind.Optional,
            Reasons(PathReason(PathCode(s.Summary)), ReasonCodes.Make(ReasonCodes.ImpactUnknown)),
            Risk.Low, Reversibility.Undoable, Confidence.L3,
            new Explanation(
                $"Задача планировщика «{s.Name}»: запускает {(string.IsNullOrWhiteSpace(s.Summary) ? "программу" : s.Summary)}.",
                "Нужна программе, которой принадлежит, для фоновых дел: обновления, проверки.",
                "Фоновые дела программы выполняться не будут, пока вы не включите задачу или не запустите программу.",
                StdUndoTask),
            s.Class, "Задачи");
    }

    // ----- программы -----

    public Finding Program(ProgramSubject s)
    {
        var id = $"program:{s.Source}:{s.Name}";
        var detail = $"{s.Publisher} · {s.Version}".Trim(' ', '·');
        var runtime = RuntimeMarkers.Any(m => s.Name.Contains(m, StringComparison.OrdinalIgnoreCase));
        var ms = s.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        if (runtime)
            return Build(id, FindingArea.Programs, s.Name, detail, VerdictKind.Keep,
                Reasons(ReasonCodes.Make(ReasonCodes.RuntimeShared), ms ? ReasonCodes.Make(ReasonCodes.PublisherMicrosoft) : null),
                Risk.Medium, Reversibility.ReinstallOnly, Confidence.L3,
                new Explanation(
                    "Общая библиотека или среда выполнения, которую используют другие программы.",
                    "От неё могут зависеть программы, которые вы с ней не связываете.",
                    "Программы, которым она нужна, перестанут запускаться или будут просить её установить.",
                    "Только скачать и установить заново: удаление отменить нельзя."),
                ChangeClass.ReadOnly, "");

        return Build(id, FindingArea.Programs, s.Name, detail, VerdictKind.Optional,
            Reasons(ReasonCodes.Make(ReasonCodes.ProgramInstalled), ms ? ReasonCodes.Make(ReasonCodes.PublisherMicrosoft) : null),
            Risk.Low, Reversibility.ReinstallOnly, Confidence.L3,
            new Explanation(
                $"Программа «{s.Name}» {s.Version}, издатель: {(string.IsNullOrWhiteSpace(s.Publisher) ? "не указан" : s.Publisher)}.",
                "Нужна, пока вы ею пользуетесь. Решить это можете только вы.",
                "Программа перестанет открываться. Личные файлы в документах обычно остаются, настройки программы могут пропасть.",
                "Только установкой заново: удаление программы отменить нельзя."),
            ChangeClass.ReadOnly, "");
    }

    // ----- общее -----

    Finding FromAdvice(string id, FindingArea area, string title, string detail, ObjectAdvice a,
        ChangeClass cls, string? classReason, string openIn, string undo)
    {
        var kind = Enum.Parse<VerdictKind>(a.Verdict);
        var risk = Enum.Parse<Risk>(a.Risk);
        var blocked = cls != ChangeClass.Modifiable;
        var list = new List<Reason>();
        foreach (var code in a.Reasons) list.Add(ReasonCodes.Make(code));
        list.Add(ReasonCodes.Make(ReasonCodes.CatalogAdvice));
        if (a.WuMayRevert) list.Add(ReasonCodes.Make(ReasonCodes.WuMayRevert));
        if (blocked)
            list.Add(ReasonCodes.Make(cls == ChangeClass.Forbidden ? ReasonCodes.ClassForbidden : ReasonCodes.ClassReadOnly, classReason));

        var canUndo = kind is VerdictKind.Remove or VerdictKind.Optional && !blocked;
        return Build(id, area, title, detail, kind, list, risk,
            canUndo ? Reversibility.Undoable : Reversibility.NotAvailable,
            kind == VerdictKind.Protected ? Confidence.L0 : Confidence.L1,
            new Explanation(a.What, a.Why, a.IfRemoved, canUndo ? undo : NothingToUndo),
            cls, openIn);
    }

    static Explanation Advice(StartupAdvice a, bool protectedNote = false, string? startupUndo = null)
        => new(a.What, a.Why, a.IfRemoved, protectedNote ? NothingToUndo : startupUndo ?? NothingToUndo);

    static Finding Build(string id, FindingArea area, string title, string detail, VerdictKind kind,
        IReadOnlyList<Reason> reasons, Risk risk, Reversibility rev, Confidence conf, Explanation e, ChangeClass cls, string openIn)
    {
        if (reasons.Count == 0) throw new InvalidOperationException("Вердикт без причин: " + id);
        var blocked = cls != ChangeClass.Modifiable && kind is VerdictKind.Remove or VerdictKind.Optional && area != FindingArea.Programs;
        var headline = Headline(kind, area) + (blocked ? " · изменение пока недоступно" : "");
        return new Finding(id, area, title, detail, new Verdict(kind, headline, reasons, risk, rev, conf), e, cls, openIn);
    }

    public static string Headline(VerdictKind kind, FindingArea area) => kind switch
    {
        VerdictKind.Remove => "Можно убрать",
        VerdictKind.Optional => area == FindingArea.Programs ? "Можно удалить, если не пользуетесь" : "На ваше усмотрение",
        VerdictKind.Keep => "Лучше оставить",
        VerdictKind.Protected => "Защищено, менять нельзя",
        _ => "Мало данных, только просмотр",
    };

    static List<Reason> Reasons(params Reason?[] items) => items.Where(r => r is not null).Cast<Reason>().ToList();

    static Reason? PathReason(string? code) => code is null ? null : ReasonCodes.Make(code);

    static Reason? MsReason(StartupSubject s)
        => s.MicrosoftSigned == true ? ReasonCodes.Make(ReasonCodes.PublisherMicrosoft) : null;

    /// <summary>Код расположения файла по тексту пути или команды; null, если путь ничего не говорит.</summary>
    public static string? PathCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Replace('/', '\\').ToLowerInvariant();
        if (p.Contains(@"\temp\") || p.Contains(@"\tmp\") || p.StartsWith("%temp%") || p.StartsWith("%tmp%")) return ReasonCodes.PathTemp;
        if (p.Contains(@"\appdata\")) return ReasonCodes.PathAppData;
        if (p.Contains(@"\program files")) return ReasonCodes.PathProgramFiles;
        return null;
    }

    static readonly Regex ExeToken = new(@"^(.*?\.(exe|lnk|bat|cmd|com|scr|msc|cpl))(\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Имя исполняемого файла без пути из пути или командной строки; пусто, если выделить нельзя.</summary>
    public static string ExeName(string? commandOrPath)
    {
        if (string.IsNullOrWhiteSpace(commandOrPath)) return "";
        var s = commandOrPath.Trim();
        string path;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end < 2) return "";
            path = s[1..end];
        }
        else
        {
            var m = ExeToken.Match(s);
            path = m.Success ? m.Groups[1].Value : s.Split(' ')[0];
        }
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }
}
