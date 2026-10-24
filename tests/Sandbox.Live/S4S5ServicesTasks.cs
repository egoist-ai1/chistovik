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
        var (_, o) = Proc.Run("sc.exe", ["qc", service]);
        var m = Regex.Match(o, @"START_TYPE\s*:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static async Task Run(LiveContext c)
    {
        using var s = Session.Open(c, "s4");

        // запретный список: отказ, состояние не меняется
        foreach (var hard in new[] { "Dnscache", "WinDefend" })
        {
            var was = StartType(hard);
            if (was is null) { c.Report.Skip(Sc, $"запретная служба {hard}", "службы нет в этом образе"); continue; }
            var r = await s.Send(new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = hard, Start = StartMode.Disabled });
            var now = StartType(hard);
            c.Report.Check(Sc, $"запретная служба {hard}: отказ", !r.Ok && now == was,
                $"ответ {s.Describe(r)}; START_TYPE остался {now}", $"ответ: {s.Describe(r)}; START_TYPE было {was}, стало {now}");
        }

        if (c.SvcExe is null || !File.Exists(c.SvcExe))
        {
            c.Report.Skip(Sc, "тестовая служба", "нет подписанного тестового файла (--svc-exe): его готовит guest-run.ps1");
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
