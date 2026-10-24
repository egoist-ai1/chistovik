using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 1. Каждый твик каталога отдельной записью, в обе стороны (включить, выключить): подготовка (значение ставится
/// в противоположное состояние напрямую) -> применение через настоящий OperationHandler -> независимая проверка реестра ->
/// откат (Undo) -> проверка возврата. Исходное значение восстанавливается в любом случае. Отдельно: ссылка HKU -> HKLM.
/// </summary>
static class S1Tweaks
{
    const string Sc = "S1 твики";

    public static async Task Run(LiveContext c)
    {
        using var s = Session.Open(c, "s1");
        foreach (var t in TweakCatalog.Shared.Tweaks)
        {
            foreach (var enable in new[] { true, false })
            {
                try { await One(c, s, t, enable); }
                catch (Exception e) { c.Report.Fail(Sc, $"{t.Id}/{Label(enable)}", "исключение: " + e.GetType().Name + ": " + e.Message); }
            }
        }
        await Symlink(c, s);
    }

    static string Label(bool enable) => enable ? "включить" : "выключить";

    static async Task One(LiveContext c, Session s, TweakEntry t, bool enable)
    {
        var name = $"{t.Id}/{Label(enable)} ({t.Hive}\\{t.Key}\\{t.Value})";
        var want = Tweaks.Expect(t, enable);
        var other = Tweaks.Expect(t, !enable);
        if (want.ToString() == other.ToString())
        {
            c.Report.Skip(Sc, name, "оба состояния твика совпадают, проверять нечего");
            return;
        }

        var user = Tweaks.IsUser(t);
        var orig = Tweaks.Read(c, t);
        var keyExisted = Reg.KeyExists(user, c.Sid, t.Key);
        try
        {
            Tweaks.Force(c, t, other); // подготовка: противоположное состояние, чтобы изменение было настоящим
            var before = Tweaks.Read(c, t);

            var r = await s.Send(Tweaks.Request(t.Id, enable));
            var after = Tweaks.Read(c, t);
            if (!r.Ok)
            {
                c.Report.Fail(Sc, name + " применение", "агент отказал: " + s.Describe(r));
                return;
            }
            if (!want.Matches(after))
            {
                c.Report.Fail(Sc, name + " применение", $"агент ответил {r.Code}, но в реестре {after}, ожидалось {want}");
                return;
            }
            c.Report.Pass(Sc, name + " применение", $"{before} -> {after}");

            var u = await s.Undo(r.OperationId!.Value);
            var back = Tweaks.Read(c, t);
            if (!u.Ok) c.Report.Fail(Sc, name + " откат", "агент отказал: " + s.Describe(u));
            else if (!back.SameAs(before)) c.Report.Fail(Sc, name + " откат", $"после отката {back}, было до применения {before}");
            else c.Report.Pass(Sc, name + " откат", $"возврат к {back}");
        }
        finally
        {
            Reg.Restore(user, c.Sid, t.Key, t.Value, orig);
            if (!keyExisted) Reg.DeleteTree(user, c.Sid, t.Key); // ключа не было: убираем и его
        }
    }

    /// <summary>
    /// На месте ключа пользовательского твика создаётся ссылка HKU\SID\... -> HKLM\SOFTWARE\ChistovikTest_LinkTarget.
    /// Запись через ссылку не должна пройти: в цели в HKLM значения не появится. Нужен твик, чьего ключа в HKU ещё нет.
    /// </summary>
    static async Task Symlink(LiveContext c, Session s)
    {
        const string Target = @"SOFTWARE\ChistovikTest_LinkTarget";
        var t = TweakCatalog.Shared.Tweaks.FirstOrDefault(x => Tweaks.IsUser(x) && !Reg.KeyExists(true, c.Sid, x.Key));
        if (t is null)
        {
            c.Report.Skip(Sc, "ссылка HKU->HKLM", "у всех пользовательских твиков ключ в HKU уже существует; ссылку на его место не поставить");
            return;
        }

        var linkPath = c.Sid + "\\" + t.Key;
        var segments = t.Key.Split('\\');
        var top = segments[0]; // самый верхний ключ цепочки, которого ещё нет: его потом удалим целиком
        for (var i = 1; i <= segments.Length; i++)
        {
            var prefix = string.Join('\\', segments.Take(i));
            if (!Reg.KeyExists(true, c.Sid, prefix)) { top = prefix; break; }
        }

        Reg.SetRaw(false, c.Sid, Target, "Marker", 1, Microsoft.Win32.RegistryValueKind.DWord);
        var rc = RegLink.CreateInUsers(linkPath, @"\Registry\Machine\" + Target);
        if (rc != 0)
        {
            c.Report.Skip(Sc, "ссылка HKU->HKLM", $"система не дала создать ссылку (Win32 {rc}); проверить нечем");
            Reg.DeleteTree(false, c.Sid, Target);
            if (!Reg.KeyExists(true, c.Sid, t.Key)) Reg.DeleteTree(true, c.Sid, top);
            return;
        }

        try
        {
            var want = Tweaks.Expect(t, true);
            var r = await s.Send(Tweaks.Request(t.Id, true));
            var leaked = Reg.Read(false, c.Sid, Target, t.Value);
            c.Report.Check(Sc, $"ссылка HKU->HKLM: запись {t.Id} не ушла в HKLM", !leaked.Exists && !r.Ok,
                $"агент: {s.Describe(r)}; в цели HKLM значения нет",
                leaked.Exists ? $"ЗНАЧЕНИЕ ПОПАЛО В HKLM ({leaked}, ожидалось бы {want})" : $"запись не попала в HKLM, но агент ответил {r.Code} (ожидался отказ)");
        }
        finally
        {
            var d = RegLink.DeleteInUsers(linkPath);
            if (d != 0) c.Report.Fail(Sc, "ссылка HKU->HKLM: уборка", $"ссылка не удалена (код {d})");
            Reg.DeleteTree(false, c.Sid, Target);
            if (d == 0) Reg.DeleteTree(true, c.Sid, top);
        }
    }
}
