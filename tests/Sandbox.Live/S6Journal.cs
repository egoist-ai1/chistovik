using System.Diagnostics;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 6. Журнал. (а) WAL: пока писатель (агент) держит базу открытой, читатель без прав администратора
/// (runas /trustlevel:0x20000) читает журнал. (б) Восстановление: дочерний процесс применяет твик и замирает до (или после)
/// записи реестра, родитель его убивает, новый граф агента на том же каталоге обязан закрыть pending-шаг и согласовать
/// состояние с реестром.
/// </summary>
static class S6Journal
{
    const string Sc = "S6 журнал";

    public static async Task Run(LiveContext c)
    {
        var t = Tweaks.SimpleUserDword();
        if (t is null) { c.Report.Skip(Sc, "журнал", "в каталоге нет простого пользовательского DWord-твика"); return; }

        await Wal(c, t);
        await Recovery(c, t, "after-write");
        await Recovery(c, t, "before-write");
    }

    static async Task Wal(LiveContext c, Chistovik.Core.Catalog.TweakEntry t)
    {
        var orig = Tweaks.Read(c, t);
        var keyExisted = Reg.KeyExists(true, c.Sid, t.Key);
        try
        {
            using var s = Session.Open(c, "s6wal");
            Tweaks.Force(c, t, Tweaks.Expect(t, false));
            var r = await s.Send(Tweaks.Request(t.Id, true));
            if (!r.Ok) { c.Report.Fail(Sc, "WAL: подготовка", "применение твика не прошло: " + s.Describe(r)); return; }

            var outDir = @"C:\Users\Public\ChistovikTestOut";
            Directory.CreateDirectory(outDir);
            var outFile = Path.Combine(outDir, "reader-" + Guid.NewGuid().ToString("N") + ".txt");
            // писатель (s) остаётся открытым: так читатель видит базу в режиме WAL с живым -wal/-shm
            var inner = $"\"{c.SelfExe}\" --reader \"{s.DbPath}\" --out \"{outFile}\" --token {c.Token}";
            var (rc, o) = Proc.RunRaw("runas.exe", "/trustlevel:0x20000 \"" + inner.Replace("\"", "\\\"") + "\"", 30000);
            var text = await WaitFile(outFile, 40);
            if (text is null)
            {
                c.Report.Skip(Sc, "WAL: чтение без прав записи", $"читатель не отчитался (runas код {rc}: {o.Trim()}); проверить нечем");
                return;
            }
            if (!text.Contains("admin=False", StringComparison.Ordinal))
            {
                c.Report.Skip(Sc, "WAL: чтение без прав записи", "runas не снял права администратора, проверка была бы ненастоящей: " + text);
                return;
            }
            c.Report.Check(Sc, "WAL: чтение без прав записи при открытом писателе", text.StartsWith("ok;", StringComparison.Ordinal) && !text.Contains("count=0", StringComparison.Ordinal),
                text, "читатель без прав не смог прочитать журнал: " + text);
        }
        finally
        {
            Reg.Restore(true, c.Sid, t.Key, t.Value, orig);
            if (!keyExisted) Reg.DeleteTree(true, c.Sid, t.Key);
        }
    }

    static async Task<string?> WaitFile(string path, int seconds)
    {
        for (var i = 0; i < seconds * 4; i++)
        {
            if (File.Exists(path))
            {
                try { return File.ReadAllText(path); }
                catch (IOException) { }
            }
            await Task.Delay(250);
        }
        return null;
    }

    static async Task Recovery(LiveContext c, Chistovik.Core.Catalog.TweakEntry t, string mode)
    {
        var name = $"восстановление ({mode})";
        var orig = Tweaks.Read(c, t);
        var keyExisted = Reg.KeyExists(true, c.Sid, t.Key);
        var dir = Path.Combine(c.WorkDir, "s6-" + mode, "journal");
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var signal = Path.Combine(c.WorkDir, "s6-" + mode + ".signal");
        Process? child = null;
        try
        {
            var start = Tweaks.Expect(t, false);
            var want = Tweaks.Expect(t, true);
            Tweaks.Force(c, t, start);

            var psi = new ProcessStartInfo(c.SelfExe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--child-hang", dir, "--tweak", t.Id, "--enable", "1", "--signal", signal, "--token", c.Token }) psi.ArgumentList.Add(a);
            psi.Environment["CHISTOVIK_LIVE_HANG"] = mode;
            child = Process.Start(psi) ?? throw new InvalidOperationException("дочерний процесс не запустился");

            if (await WaitFile(signal, 60) is null)
            {
                c.Report.Fail(Sc, name, $"дочерний процесс не дошёл до записи реестра (код выхода {(child.HasExited ? child.ExitCode.ToString() : "ещё работает")})");
                return;
            }
            child.Kill(true); // «гибель процесса» посреди шага
            child.WaitForExit(10000);
            var atKill = Tweaks.Read(c, t);

            using var s = Session.Open(c, "s6-" + mode + "-r", existingDir: dir); // Build запускает RecoveryService
            var rec = s.Comp.Recovery;
            using var reader = new SqliteJournalReader(s.DbPath);
            var ops = reader.List(0, 5);
            var view = ops.Count > 0 ? reader.Get(ops[0].Id) : null;
            var step = view?.Steps.FirstOrDefault();
            var now = Tweaks.Read(c, t);

            var info = $"отчёт восстановления: {rec}; операция {view?.Summary.State}; шаг {step?.State}/{step?.Code}; реестр после убийства {atKill}, сейчас {now}";
            if (mode == "after-write")
                c.Report.Check(Sc, name, rec is { RecoveredDone: 1, Failed: 0 } && step?.State == StepState.Done && want.Matches(now) && view?.Summary.State == OperationState.Interrupted,
                    info, "ждали: запись успела, шаг закрыт как done (recovered-done), операция interrupted. " + info);
            else
                c.Report.Check(Sc, name, rec is { NotApplied: 1, Failed: 0 } && step?.State == StepState.Skipped && start.Matches(now) && view?.Summary.State == OperationState.Interrupted,
                    info, "ждали: записи не было, шаг закрыт как skipped (recovered-not-applied), реестр прежний, операция interrupted. " + info);
        }
        finally
        {
            try { if (child is { HasExited: false }) child.Kill(true); } catch (InvalidOperationException) { }
            child?.Dispose();
            Reg.Restore(true, c.Sid, t.Key, t.Value, orig);
            if (!keyExisted) Reg.DeleteTree(true, c.Sid, t.Key);
        }
    }
}
