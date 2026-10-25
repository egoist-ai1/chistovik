using Chistovik.Agent;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Native.Ipc;

namespace Sandbox.Live;

/// <summary>
/// Живой стенд Чистовика. ТОЛЬКО для Windows Sandbox: первая же проверка (SandboxGuard) на любой другой системе
/// завершает программу с кодом 3, ничего не трогая. Запускается скриптом tools/sandbox/guest-run.ps1.
/// Коды выхода: 0 всё PASS (или ПРОПУСК), 1 есть FAIL, 2 неверные аргументы, 3 не Sandbox.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); Console.Error.Flush(); } catch (IOException) { }
        var o = Options.Parse(args);

        var guard = SandboxGuard.Check(o.Get("--token"));
        if (!guard.Ok)
        {
            Console.Error.WriteLine("Только в Windows Sandbox. Стенд ничего не сделал: " + guard.Reason + ".");
            return SandboxGuard.ExitNotSandbox;
        }

        // Режимы дочерних процессов (их запускает сам стенд из сценариев; защита выше действует и на них).
        if (o.Has("--reader")) return ChildModes.Reader(o);
        if (o.Has("--child-hang")) return ChildModes.Hang(o);
        if (o.Has("--child-client")) return await ChildModes.ForeignClient(o);

        var results = o.Get("--results");
        if (string.IsNullOrWhiteSpace(results))
        {
            Console.Error.WriteLine("Нужен --results <папка>.");
            return 2;
        }

        var report = new Report(results);
        var ctx = new LiveContext
        {
            Report = report,
            Sid = Sids.Current(),
            WorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ChistovikTest", "run-" + DateTime.Now.ToString("HHmmss")),
            Token = o.Get("--token")!,
            SelfExe = Environment.ProcessPath ?? throw new InvalidOperationException("Нет пути к exe"),
            AgentPath = o.Get("--agent"),
            SvcExe = o.Get("--svc-exe"),
            SvcSkipReason = o.Get("--svc-skip-reason"),
            SkipIdle = o.Has("--skip-idle"),
            InstallersDir = o.Get("--installers"),
            ResultsDir = results,
        };
        Directory.CreateDirectory(ctx.WorkDir);
        report.Pass("S0", "признак Sandbox", $"пользователь {Environment.UserName}, администратор: {Sids.IsAdmin()}");
        if (!Sids.IsAdmin()) report.Fail("S0", "права администратора", "стенд запущен без прав администратора: большинство сценариев невозможно");

        var all = new (string Name, Func<LiveContext, Task> Run)[]
        {
            ("S1", S1Tweaks.Run),
            ("S2", S2Startup.Run),
            ("S3", S3Power.Run),
            ("S4", S4Services.Run),
            ("S5", S5Tasks.Run),
            ("S6", S6Journal.Run),
            ("S7", S7Agent.Run),
            ("S9", S9Uninstall.Run),
            ("S10", S10Ui.Run),
        };
        var only = o.Get("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (name, run) in all)
        {
            if (only is not null && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"=== {name} ===");
            try { await run(ctx); }
            catch (Exception e)
            {
                report.Fail(name, "необработанное исключение", e.GetType().Name + ": " + e.Message);
            }
            SaveSafe(report, results);
        }

        SaveSafe(report, results);
        Console.WriteLine($"Готово. FAIL: {report.FailCount}. Отчёт: {results}");
        return report.FailCount == 0 ? 0 : 1;
    }

    /// <summary>Отчёт не должен теряться: при ошибке записи в папку результатов пишем в запасную папку и говорим об этом в консоль.</summary>
    static void SaveSafe(Report report, string dir)
    {
        try { report.Save(); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Не удалось записать отчёт в {dir}: {e.Message}. Пишу в {FallbackDir}.");
        }
        try { new Report(FallbackDir).CopyFrom(report); Console.Error.WriteLine("Отчёт записан в " + FallbackDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Запасная запись тоже не удалась: " + e.Message);
        }
    }

    const string FallbackDir = @"C:\Users\Public\ChistovikResults";
}
