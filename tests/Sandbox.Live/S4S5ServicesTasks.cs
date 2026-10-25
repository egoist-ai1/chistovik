using System.Text.RegularExpressions;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 4. Службы. Тестовая служба ChistovikTest_Svc создаётся через sc.exe с подписанным файлом (подпись готовит
/// guest-run.ps1: самоподписанный сертификат в доверенных хранилищах Sandbox), затем SetServiceStart с откатом и независимой
/// проверкой через sc qc. Служба из запретного списка (Dnscache) обязана получить отказ без изменения.
/// </summary>
static class S4Services
{
    const string Sc = "S4 службы";
    const string Name = "ChistovikTest_Svc";

    static int? StartType(string service)
    {
        // Независимо от агента и от языка системы: sc qc печатает подписи по-русски («ТИП_ЗАПУСКА»), реестр от языка не зависит.
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
        return k?.GetValue("Start") is int v ? v : null;
    }

    public static async Task Run(LiveContext c)
    {
        using var s = Session.Open(c, "s4");

        // запретный список: отказ, состояние не меняется
        // Dnscache и WinDefend в образе Sandbox отсутствуют, поэтому берём весь список служб из жёсткого списка политики,
        // которые есть в любой Windows, и проверяем те, что реально установлены. Критерий: отказ с правилом service-hardlist
        // и неизменный START_TYPE (читается независимо через sc qc).
        string[] candidates =
        [
            "EventLog", "Schedule", "RpcSs", "Winmgmt", "BITS", "CryptSvc", "gpsvc", "ProfSvc", "SamSs", "PlugPlay", "Power",
            "BFE", "LanmanWorkstation", "DcomLaunch", "Dhcp", "Dnscache", "WinDefend", "mpssvc", "wuauserv", "TrustedInstaller",
        ];
        var tested = 0;
        var missing = new List<string>();
        foreach (var hard in candidates)
        {
            if (!Chistovik.Core.Policy.ServicePolicy.IsHardListed(hard)) { c.Report.Fail(Sc, $"запретная служба {hard}", "имени нет в жёстком списке политики: стенд устарел относительно кода"); continue; }
            var was = StartType(hard);
            if (was is null) { missing.Add(hard); continue; }
            var r = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = hard, Start = StartMode.Disabled });
            var now = StartType(hard);
            var text = s.Describe(r);
            tested++;
            // отказ по другой причине (не hardlist) тоже защитил бы, но проверка тогда не доказывает то, что должна
            var viaHardlist = text.Contains("service-hardlist", StringComparison.Ordinal);
            c.Report.Check(Sc, $"запретная служба {hard}: отказ", !r.Ok && now == was && viaHardlist,
                $"ответ {text}; START_TYPE остался {now}", $"ответ: {text}; START_TYPE было {was}, стало {now}; ждали отказ с правилом service-hardlist");
        }
        if (tested == 0) c.Report.Skip(Sc, "запретные службы", "ни одной службы из проверочного списка нет в образе");
        else if (missing.Count > 0) c.Report.Info(Sc, "запретные службы: проверено " + tested, "в образе нет: " + string.Join(", ", missing));

        if (c.SvcExe is null || !File.Exists(c.SvcExe))
        {
            c.Report.Skip(Sc, "тестовая служба", "нет подписанного тестового файла (--svc-exe)"
                + (string.IsNullOrEmpty(c.SvcSkipReason) ? ": его готовит guest-run.ps1" : ": " + c.SvcSkipReason));
            return;
        }

        Cleanup();
        try
        {
            var (rc, o) = Proc.Run("sc.exe", ["create", Name, "binPath=", "\"" + c.SvcExe + "\"", "start=", "auto", "type=", "own"]);
            if (rc != 0) { c.Report.Fail(Sc, "создание тестовой службы", "sc create: " + o.Trim()); return; }
            c.Report.Check(Sc, "создание тестовой службы", StartType(Name) == 2, "START_TYPE 2 (auto)", $"START_TYPE {StartType(Name)}");

            foreach (var (mode, expected, label) in new[] { (StartMode.Disabled, 4, "отключена"), (StartMode.Manual, 3, "вручную") })
            {
                var r = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = Name, Start = mode });
                var now = StartType(Name);
                c.Report.Check(Sc, $"{Name}: {label}", r.Ok && now == expected, $"START_TYPE {now}", $"ответ: {s.Describe(r)}; START_TYPE {now}, ждали {expected}");
                if (r.OperationId is { } op)
                {
                    var u = await s.Undo(op);
                    var back = StartType(Name);
                    c.Report.Check(Sc, $"{Name}: откат ({label})", u.Ok && back == 2, $"START_TYPE снова {back}", $"ответ: {s.Describe(u)}; START_TYPE {back}, ждали 2");
                }
            }
        }
        finally { Cleanup(); }
    }

    static void Cleanup()
    {
        Proc.Run("sc.exe", ["stop", Name]);
        Proc.Run("sc.exe", ["delete", Name]);
    }
}

/// <summary>
/// Сценарий 5. Задачи планировщика. Тестовая задача \ChistovikTest_Task от SYSTEM с повышенными правами: выключение разрешено,
/// включение напрямую запрещено, возможно только откатом собственного шага. Состояние проверяется через Get-ScheduledTask.
/// </summary>
static class S5Tasks
{
    const string Sc = "S5 задачи";
    const string TaskName = "ChistovikTest_Task";
    const string TaskPath = @"\ChistovikTest_Task";

    static string State()
    {
        var (_, o) = Proc.PowerShell($"(Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue).State");
        return o.Trim();
    }

    public static async Task Run(LiveContext c)
    {
        var exe = c.SvcExe is not null && File.Exists(c.SvcExe) ? c.SvcExe : @"C:\Program Files\ChistovikTest\svc.exe";
        Proc.Run("schtasks.exe", ["/delete", "/tn", TaskName, "/f"]);
        try
        {
            var (rc, o) = Proc.Run("schtasks.exe", ["/create", "/tn", TaskName, "/tr", "\"" + exe + "\"", "/sc", "onlogon", "/ru", "SYSTEM", "/rl", "HIGHEST", "/f"]);
            if (rc != 0) { c.Report.Fail(Sc, "создание тестовой задачи", "schtasks: " + o.Trim()); return; }
            c.Report.Check(Sc, "создание тестовой задачи", State() == "Ready", "состояние Ready", $"состояние «{State()}»");

            using var s = Session.Open(c, "s5");

            // 1. выключение разрешено
            var off = await s.Send(new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = TaskPath, Enabled = false });
            c.Report.Check(Sc, "SYSTEM-задача: выключение разрешено", off.Ok && State() == "Disabled", $"состояние {State()}", $"ответ: {s.Describe(off)}; состояние «{State()}»");

            // 2. включение напрямую запрещено
            var on = await s.Send(new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = TaskPath, Enabled = true });
            c.Report.Check(Sc, "SYSTEM-задача: прямое включение запрещено", !on.Ok && State() == "Disabled",
                $"ответ {s.Describe(on)}; задача осталась выключенной", $"ответ: {s.Describe(on)}; состояние «{State()}» (ждали отказ и Disabled)");

            // 3. включение откатом собственного шага разрешено
            if (off.OperationId is { } op)
            {
                var u = await s.Undo(op);
                c.Report.Check(Sc, "SYSTEM-задача: включение откатом", u.Ok && State() == "Ready", $"состояние {State()}", $"ответ: {s.Describe(u)}; состояние «{State()}»");
            }
        }
        finally { Proc.Run("schtasks.exe", ["/delete", "/tn", TaskName, "/f"]); }
    }
}

/// <summary>
/// Сценарий 4b. Службы каталога-исключения (ServicePolicy.CarveOut): запуск можно понизить до Disabled/Manual, повысить
/// напрямую нельзя (service-carveout-target, service-elevate-outside-program-files), вернуть можно откатом собственного шага.
/// Службы вне каталога: общий svchost (service-shared) и жёсткий список (service-hardlist) остаются закрытыми.
/// Допущение об образе: службы из списка есть в образе Sandbox не все; берётся первая существующая, которую политика
/// признала Modifiable. Исходный запуск службы восстанавливается через sc.exe в любом случае.
/// </summary>
static class S4bCarveOutServices
{
    const string Sc = "S4b службы (карв-аут)";

    static readonly string[] Candidates =
        ["DiagTrack", "dmwappushservice", "RetailDemo", "WMPNetworkSvc", "RemoteRegistry", "Fax", "WerSvc", "MapsBroker", "lfsvc", "wisvc", "SCardSvr"];

    static int? StartType(string service)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
        return k?.GetValue("Start") is int v ? v : null;
    }

    static bool Delayed(string service)
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
        return k?.GetValue("DelayedAutostart") is int v && v != 0;
    }

    static string? ModeName(int start, bool delayed) => start switch
    {
        2 => delayed ? "delayed-auto" : "auto",
        3 => "demand",
        4 => "disabled",
        _ => null,
    };

    static void RestoreStart(string service, int? start, bool delayed)
    {
        if (start is not { } st || ModeName(st, delayed) is not { } mode) return;
        Proc.Run("sc.exe", ["config", service, "start=", mode]);
    }

    static bool Denied(string text, string rule)
        => text.Contains("policy-denied", StringComparison.Ordinal) && text.Contains(rule, StringComparison.Ordinal);

    public static async Task Run(LiveContext c)
    {
        using var s = Session.Open(c, "s4b");
        await CarveOut(c, s);
        await Closed(c, s, "Themes", "service-shared");
        await Closed(c, s, "BITS", "service-hardlist");
    }

    static async Task CarveOut(LiveContext c, Session s)
    {
        var missing = new List<string>();
        var notUsable = new List<string>();
        foreach (var name in Candidates)
        {
            if (!Chistovik.Core.Policy.ServicePolicy.CarveOut.Contains(name))
            {
                c.Report.Fail(Sc, $"{name}: в каталоге-исключении", "имени нет в ServicePolicy.CarveOut: стенд устарел относительно кода");
                continue;
            }
            var was = StartType(name);
            if (was is null) { missing.Add(name); continue; }
            var wasDelayed = Delayed(name);
            if (was.Value is not (2 or 3 or 4)) { notUsable.Add($"{name}: START_TYPE {was}"); continue; }

            try
            {
                // подготовка: исходное состояние сценария Automatic (напрямую через sc.exe, не через агента)
                Proc.Run("sc.exe", ["config", name, "start=", "auto"]);
                if (StartType(name) != 2) { notUsable.Add($"{name}: не удалось выставить Automatic"); continue; }

                var off = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = name, Start = StartMode.Disabled });
                var offText = s.Describe(off);
                // политика не признала службу Modifiable в этом образе (другой файл, зависимые службы, подпись): пробуем следующую
                if (!off.Ok && offText.Contains("service-carveout-", StringComparison.Ordinal)) { notUsable.Add($"{name}: {offText}"); continue; }

                var nowOff = StartType(name);
                c.Report.Check(Sc, $"{name}: Automatic -> Disabled", off.Ok && nowOff == 4, $"START_TYPE {nowOff}", $"ответ: {offText}; START_TYPE {nowOff}, ждали 4");
                if (!off.Ok || off.OperationId is not { } op) return;

                var auto = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = name, Start = StartMode.Automatic });
                var autoText = s.Describe(auto);
                c.Report.Check(Sc, $"{name}: Disabled -> Automatic напрямую запрещено",
                    !auto.Ok && Denied(autoText, "service-carveout-target") && StartType(name) == 4,
                    $"ответ {autoText}; запуск остался Disabled", $"ответ: {autoText}; START_TYPE {StartType(name)} (ждали отказ policy-denied/service-carveout-target и 4)");

                var manual = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = name, Start = StartMode.Manual });
                var manualText = s.Describe(manual);
                c.Report.Check(Sc, $"{name}: Disabled -> Manual напрямую запрещено",
                    !manual.Ok && Denied(manualText, "service-elevate-outside-program-files") && StartType(name) == 4,
                    $"ответ {manualText}; запуск остался Disabled", $"ответ: {manualText}; START_TYPE {StartType(name)} (ждали отказ policy-denied/service-elevate-outside-program-files и 4)");

                var u = await s.Undo(op);
                var back = StartType(name);
                c.Report.Check(Sc, $"{name}: откат восстанавливает Automatic", u.Ok && back == 2, $"START_TYPE снова {back}", $"ответ: {s.Describe(u)}; START_TYPE {back}, ждали 2");
                return;
            }
            finally { RestoreStart(name, was, wasDelayed); }
        }

        var why = (missing.Count > 0 ? "нет в образе: " + string.Join(", ", missing) : "")
                  + (notUsable.Count > 0 ? (missing.Count > 0 ? "; " : "") + "не подошли: " + string.Join(" | ", notUsable) : "");
        c.Report.Skip(Sc, "службы карв-аута", "ни одну службу из списка нельзя проверить. " + why);
    }

    /// <summary>Служба вне каталога-исключения: отказ с нужным правилом и без изменения запуска.</summary>
    static async Task Closed(LiveContext c, Session s, string name, string rule)
    {
        var was = StartType(name);
        if (was is null) { c.Report.Skip(Sc, $"{name}: отказ {rule}", "службы нет в образе Sandbox"); return; }
        var wasDelayed = Delayed(name);
        try
        {
            var r = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = name, Start = StartMode.Disabled });
            var text = s.Describe(r);
            var now = StartType(name);
            c.Report.Check(Sc, $"{name}: отказ {rule}", !r.Ok && Denied(text, rule) && now == was,
                $"ответ {text}; START_TYPE остался {now}", $"ответ: {text}; START_TYPE было {was}, стало {now}; ждали отказ policy-denied/{rule}");
        }
        finally
        {
            if (StartType(name) != was) RestoreStart(name, was, wasDelayed);
        }
    }
}

/// <summary>
/// Сценарий 5b. Задачи каталога-исключения (TaskPolicy.CarveOutTasks): выключить можно, включить только откатом;
/// остальное поддерево \Microsoft\* закрыто (task-protected-tree). Состояние читается через Get-ScheduledTask (язык системы
/// не влияет, в отличие от текста schtasks /query). Исходное состояние задач восстанавливается через schtasks /change.
/// </summary>
static class S5bCarveOutTasks
{
    const string Sc = "S5b задачи (карв-аут)";
    const string Appraiser = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser";
    const string Protected = @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan";

    /// <summary>Состояние задачи (Ready, Disabled, Running...) или пустая строка, если задачи нет.</summary>
    static string State(string path)
    {
        var i = path.LastIndexOf('\\');
        var (_, o) = Proc.PowerShell($"(Get-ScheduledTask -TaskPath '{path[..(i + 1)]}' -TaskName '{path[(i + 1)..]}' -ErrorAction SilentlyContinue).State");
        return o.Trim();
    }

    static bool Denied(string text, string rule)
        => text.Contains("policy-denied", StringComparison.Ordinal) && text.Contains(rule, StringComparison.Ordinal);

    static void Force(string path, bool enable) => Proc.Run("schtasks.exe", ["/change", "/tn", path, enable ? "/enable" : "/disable"]);

    public static async Task Run(LiveContext c)
    {
        using var s = Session.Open(c, "s5b");
        await Catalog(c, s);
        await ProtectedTree(c, s);
    }

    static async Task Catalog(LiveContext c, Session s)
    {
        if (!Chistovik.Core.Policy.TaskPolicy.CarveOutTasks.Contains(Appraiser))
        {
            c.Report.Fail(Sc, "Appraiser в каталоге-исключении", "пути нет в TaskPolicy.CarveOutTasks: стенд устарел относительно кода");
            return;
        }
        var was = State(Appraiser);
        if (was.Length == 0) { c.Report.Skip(Sc, "Appraiser", "задачи нет в образе Sandbox"); return; }
        var wasDisabled = was == "Disabled";
        try
        {
            if (wasDisabled) Force(Appraiser, true);
            var before = State(Appraiser);
            if (before == "Disabled") { c.Report.Skip(Sc, "Appraiser", "не удалось включить задачу для подготовки сценария"); return; }

            var off = await s.Send(new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = Appraiser, Enabled = false });
            var st = State(Appraiser);
            c.Report.Check(Sc, "Appraiser: выключение разрешено", off.Ok && st == "Disabled", $"состояние {st}", $"ответ: {s.Describe(off)}; состояние «{st}»");

            var on = await s.Send(new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = Appraiser, Enabled = true });
            var onText = s.Describe(on);
            st = State(Appraiser);
            c.Report.Check(Sc, "Appraiser: прямое включение запрещено", !on.Ok && Denied(onText, "task-catalog-carveout") && st == "Disabled",
                $"ответ {onText}; задача осталась выключенной", $"ответ: {onText}; состояние «{st}» (ждали отказ policy-denied/task-catalog-carveout и Disabled)");

            if (off.OperationId is { } op)
            {
                var u = await s.Undo(op);
                st = State(Appraiser);
                c.Report.Check(Sc, "Appraiser: включение откатом", u.Ok && st.Length > 0 && st != "Disabled", $"состояние {st}", $"ответ: {s.Describe(u)}; состояние «{st}»");
            }
            else c.Report.Fail(Sc, "Appraiser: включение откатом", "у выключения нет OperationId, откатывать нечего");
        }
        finally
        {
            var now = State(Appraiser);
            if (now.Length > 0 && (now == "Disabled") != wasDisabled) Force(Appraiser, !wasDisabled);
        }
    }

    static async Task ProtectedTree(LiveContext c, Session s)
    {
        var was = State(Protected);
        if (was.Length == 0) { c.Report.Skip(Sc, "Schedule Scan: защищённое поддерево", "задачи нет в образе Sandbox"); return; }
        try
        {
            var r = await s.Send(new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = Protected, Enabled = false });
            var text = s.Describe(r);
            var now = State(Protected);
            c.Report.Check(Sc, "Schedule Scan: выключение запрещено", !r.Ok && Denied(text, "task-protected-tree") && now == was,
                $"ответ {text}; состояние осталось {now}", $"ответ: {text}; состояние было «{was}», стало «{now}»; ждали отказ policy-denied/task-protected-tree");
        }
        finally
        {
            if (State(Protected) == "Disabled" && was != "Disabled") Force(Protected, true);
        }
    }
}
