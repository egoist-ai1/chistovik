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
    /// Запись через ссылку не должна пройти: в цели в HKLM значения не появится. Ключа твика в HKU обычно уже нет только у
    /// редких твиков, поэтому существующий небольшой ключ на время сценария снимается в память, удаляется и после сценария
    /// возвращается как был. Контроль: обычная запись через ту же ссылку в цель доходит (иначе проверка ничего не доказывает).
    /// </summary>
    static async Task Symlink(LiveContext c, Session s)
    {
        const string Check = "ссылка HKU->HKLM";
        const string Target = @"SOFTWARE\ChistovikTest_LinkTarget";
        const int MaxNodes = 200;

        // 1. выбор твика: сначала с отсутствующим ключом, затем с самым маленьким существующим (политики предпочтительнее)
        TweakEntry? t = null;
        RegKeySnap? snap = null;
        var users = TweakCatalog.Shared.Tweaks.Where(Tweaks.IsUser).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        t = users.FirstOrDefault(x => !Reg.KeyExists(true, c.Sid, x.Key));
        if (t is null)
        {
            var best = users
                .Select(x => (Tweak: x, Snap: SafeCapture(c.Sid, x.Key, MaxNodes)))
                .Where(x => x.Snap is not null)
                .OrderBy(x => x.Tweak.Key.Contains("Policies", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(x => x.Snap!.Nodes).ThenBy(x => x.Snap!.ValueCount)
                .FirstOrDefault();
            if (best.Tweak is null)
            {
                c.Report.Skip(Sc, Check, $"нет пользовательского твика с отсутствующим ключом или ключом не больше {MaxNodes} узлов; ссылку на его место не поставить");
                return;
            }
            t = best.Tweak;
            snap = best.Snap;
        }

        var linkPath = c.Sid + "\\" + t.Key;
        var segments = t.Key.Split('\\');
        var top = t.Key; // самый верхний ключ цепочки, которого ещё нет: при отсутствующем ключе твика его потом удалим целиком
        if (snap is null)
            for (var i = 1; i <= segments.Length; i++)
            {
                var prefix = string.Join('\\', segments.Take(i));
                if (!Reg.KeyExists(true, c.Sid, prefix)) { top = prefix; break; }
            }

        var removed = false;
        var linkCreated = false;
        Reg.DeleteTree(false, c.Sid, Target);
        try
        {
            // 2. освобождаем место под ссылку, если ключ был
            if (snap is not null)
            {
                try { Reg.DeleteTree(true, c.Sid, t.Key); removed = true; }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
                {
                    c.Report.Skip(Sc, Check, $"ключ {t.Key} нельзя убрать на время сценария ({e.GetType().Name}: {e.Message})");
                    return;
                }
                if (Reg.KeyExists(true, c.Sid, t.Key))
                {
                    c.Report.Skip(Sc, Check, $"ключ {t.Key} не удалился (открытые дескрипторы); ссылку на его место не поставить");
                    return;
                }
            }

            Reg.SetRaw(false, c.Sid, Target, "Marker", 1, Microsoft.Win32.RegistryValueKind.DWord);
            var rc = RegLink.CreateInUsers(linkPath, @"\Registry\Machine\" + Target);
            if (rc != 0)
            {
                c.Report.Skip(Sc, Check, $"система не дала создать ссылку (Win32 {rc}; для REG_OPTION_CREATE_LINK нужны права и свободное место ключа); проверить нечем");
                return;
            }
            linkCreated = true;

            // 3. контроль: обычная запись через ссылку доходит до цели
            const string ControlName = "ChistovikTest_Control";
            Reg.SetRaw(true, c.Sid, t.Key, ControlName, 1, Microsoft.Win32.RegistryValueKind.DWord);
            var control = Reg.Read(false, c.Sid, Target, ControlName);
            Reg.Delete(false, c.Sid, Target, ControlName);
            if (!control.Exists)
            {
                c.Report.Skip(Sc, Check, "ссылка создана, но обычная запись через неё в HKLM не доходит: проверка отказа была бы пустой");
                return;
            }

            // 4. сам сценарий
            var want = Tweaks.Expect(t, true);
            var r = await s.Send(Tweaks.Request(t.Id, true));
            var leaked = Reg.Read(false, c.Sid, Target, t.Value);
            var others = TargetValueNames(Target).Where(n => !n.Equals("Marker", StringComparison.OrdinalIgnoreCase)).ToArray();
            c.Report.Check(Sc, $"{Check}: запись {t.Id} не ушла в HKLM", !leaked.Exists && others.Length == 0 && !r.Ok,
                $"агент: {s.Describe(r)}; в цели HKLM ничего не появилось (контроль записи через ссылку работал)",
                leaked.Exists ? $"ЗНАЧЕНИЕ ПОПАЛО В HKLM ({leaked}, ожидалось бы {want})"
                : others.Length > 0 ? $"В ЦЕЛИ HKLM появились значения: {string.Join(",", others)}"
                : $"запись не попала в HKLM, но агент ответил {r.Code} (ожидался отказ)");
        }
        finally
        {
            if (linkCreated)
            {
                var d = RegLink.DeleteInUsers(linkPath);
                if (d != 0) c.Report.Fail(Sc, $"{Check}: уборка", $"ссылка не удалена (код {d})");
                else linkCreated = false;
            }
            Reg.DeleteTree(false, c.Sid, Target);
            if (!linkCreated)
            {
                try
                {
                    if (snap is not null && removed) snap.Restore(c.Sid, t.Key);
                    else if (snap is null) Reg.DeleteTree(true, c.Sid, top);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    c.Report.Fail(Sc, $"{Check}: возврат ключа", $"ключ {t.Key} не восстановлен: {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }

    static RegKeySnap? SafeCapture(string sid, string key, int max)
    {
        try { return RegKeySnap.Capture(sid, key, max); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    static string[] TargetValueNames(string target)
    {
        using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var k = b.OpenSubKey(target);
        return k?.GetValueNames() ?? [];
    }
}
