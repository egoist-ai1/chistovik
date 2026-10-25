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
        const int MaxNodes = 200;

        // 1. кандидаты: твики с отсутствующим ключом, затем самые маленькие существующие; ключи политик в конце, потому что в Sandbox
        // запись в HKU\...\Policies закрыта даже для администратора. Если запись в ключ запрещена, берём следующего кандидата.
        var users = TweakCatalog.Shared.Tweaks.Where(Tweaks.IsUser).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        bool IsPolicy(TweakEntry x) => x.Key.Contains("Policies", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<(TweakEntry Tweak, RegKeySnap? Snap)>();
        candidates.AddRange(users.Where(x => !Reg.KeyExists(true, c.Sid, x.Key)).OrderBy(IsPolicy).Select(x => (x, (RegKeySnap?)null)));
        candidates.AddRange(users
            .Where(x => Reg.KeyExists(true, c.Sid, x.Key))
            .Select(x => (Tweak: x, Snap: SafeCapture(c.Sid, x.Key, MaxNodes)))
            .Where(x => x.Snap is not null)
            .OrderBy(x => IsPolicy(x.Tweak)).ThenBy(x => x.Snap!.Nodes).ThenBy(x => x.Snap!.ValueCount)
            .Select(x => (x.Tweak, x.Snap)));
        if (candidates.Count == 0)
        {
            c.Report.Skip(Sc, Check, $"нет пользовательского твика с отсутствующим ключом или ключом не больше {MaxNodes} узлов; ссылку на его место не поставить");
            return;
        }

        string? lastDenied = null;
        foreach (var (t, snap) in candidates.Take(5))
        {
            lastDenied = await SymlinkTry(c, s, t, snap);
            if (lastDenied is null) return;
            lastDenied = t.Key + ": " + lastDenied;
        }
        c.Report.Skip(Sc, Check, "запись в ключи кандидатов запрещена, проверить нечем. Последний отказ: " + lastDenied);
    }

    /// <summary>Одна попытка проверки на ключе твика. Возвращает null, если результат (в том числе пропуск) записан, иначе текст отказа в доступе.</summary>
    static async Task<string?> SymlinkTry(LiveContext c, Session s, TweakEntry t, RegKeySnap? snap)
    {
        const string Check = "ссылка HKU->HKLM";
        const string Target = @"SOFTWARE\ChistovikTest_LinkTarget";

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
                    return null;
                }
                if (Reg.KeyExists(true, c.Sid, t.Key))
                {
                    c.Report.Skip(Sc, Check, $"ключ {t.Key} не удалился (открытые дескрипторы); ссылку на его место не поставить");
                    return null;
                }
            }

            Reg.SetRaw(false, c.Sid, Target, "Marker", 1, Microsoft.Win32.RegistryValueKind.DWord);
            var rc = RegLink.CreateInUsers(linkPath, @"\Registry\Machine\" + Target);
            if (rc != 0)
            {
                c.Report.Skip(Sc, Check, $"система не дала создать ссылку (Win32 {rc}; для REG_OPTION_CREATE_LINK нужны права и свободное место ключа); проверить нечем");
                return null;
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
                return null;
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
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return e.Message;
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

/// <summary>
/// Сценарий 1b. Твики каталога v2 (ContentDeliveryManager, Siuf, ConsentStore, TaskbarDa, Search, InputPersonalization;
/// HKLM-политики System/WindowsCopilot/WindowsAI/CloudContent/Windows Search, Remote Assistance) отдельно от общего круга S1:
/// применение из исходного состояния -> значение в реестре равно желаемому -> Undo -> значение равно исходному (или удалено,
/// если его не было). Затем то же в обратную сторону. Для твиков, привязанных к редакциям, проверяется модель каталога и
/// EditionID: UI Automation здесь не используется, пометку «не действует на этой редакции» стенд только предсказывает
/// по той же логике, что TweakRowViewModel.IsNotApplicable (проект App из стенда недоступен), и пишет это в лог как ИНФО.
/// </summary>
static class S1bTweaksV2
{
    const string Sc = "S1b твики v2";

    static readonly string[] KeyFragments =
    [
        "ContentDeliveryManager", @"\Siuf\", "ConsentStore", @"CurrentVersion\Search", "InputPersonalization",
        @"Policies\Microsoft\Windows\", "Remote Assistance",
    ];

    static bool IsV2(TweakEntry t)
        => t.Value.Equals("TaskbarDa", StringComparison.OrdinalIgnoreCase)
           || KeyFragments.Any(f => t.Key.Contains(f, StringComparison.OrdinalIgnoreCase));

    static string Label(bool enable) => enable ? "включить" : "выключить";

    public static async Task Run(LiveContext c)
    {
        var set = TweakCatalog.Shared.Tweaks.Where(IsV2).ToList();
        if (set.Count == 0) { c.Report.Skip(Sc, "твики v2", "в каталоге нет твиков с ключами v2: каталог устарел или фрагменты ключей изменились"); return; }
        c.Report.Info(Sc, "твики v2 в каталоге", $"{set.Count}: HKCU {set.Count(Tweaks.IsUser)}, HKLM {set.Count(t => !Tweaks.IsUser(t))}");

        using var s = Session.Open(c, "s1b");
        foreach (var t in set)
            foreach (var enable in new[] { true, false })
            {
                try { await Round(c, s, t, enable); }
                catch (Exception e) { c.Report.Fail(Sc, $"{t.Id}/{Label(enable)}", "исключение: " + e.GetType().Name + ": " + e.Message); }
            }

        Editions(c, set);
    }

    static async Task Round(LiveContext c, Session s, TweakEntry t, bool enable)
    {
        var name = $"{t.Id}/{Label(enable)} ({t.Hive}\\{t.Key}\\{t.Value})";
        var want = Tweaks.Expect(t, enable);
        var other = Tweaks.Expect(t, !enable);
        if (want.ToString() == other.ToString()) { c.Report.Skip(Sc, name, "оба состояния твика совпадают, проверять нечего"); return; }

        var user = Tweaks.IsUser(t);
        var orig = Tweaks.Read(c, t);
        var keyExisted = Reg.KeyExists(user, c.Sid, t.Key);
        try
        {
            // «включить» идёт из исходного состояния (если оно уже равно желаемому, ставим противоположное, чтобы изменение было настоящим);
            // «выключить» начинается из включённого состояния
            var needForce = enable ? want.Matches(orig) : !other.Matches(orig);
            if (needForce)
            {
                try { Tweaks.Force(c, t, other); }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    c.Report.Skip(Sc, name, "подготовка: запись в ключ закрыта даже для администратора (" + e.GetType().Name + ")");
                    return;
                }
            }
            var before = Tweaks.Read(c, t);

            var r = await s.Send(Tweaks.Request(t.Id, enable));
            var after = Tweaks.Read(c, t);
            if (!r.Ok) { c.Report.Fail(Sc, name + " применение", "агент отказал: " + s.Describe(r)); return; }
            if (!want.Matches(after)) { c.Report.Fail(Sc, name + " применение", $"агент ответил {r.Code}, но в реестре {after}, ожидалось {want}"); return; }
            c.Report.Pass(Sc, name + " применение", $"{before} -> {after}");

            var u = await s.Undo(r.OperationId!.Value);
            var back = Tweaks.Read(c, t);
            if (!u.Ok) c.Report.Fail(Sc, name + " откат", "агент отказал: " + s.Describe(u));
            else if (!back.SameAs(before)) c.Report.Fail(Sc, name + " откат", $"после отката {back}, было до применения {before}");
            else c.Report.Pass(Sc, name + " откат", before.Exists ? $"возврат к {back}" : "значения не было: после отката его снова нет");
        }
        finally
        {
            Reg.Restore(user, c.Sid, t.Key, t.Value, orig);
            if (!keyExisted) Reg.DeleteTree(user, c.Sid, t.Key);
        }
    }

    /// <summary>Сводит EditionID к Home | Pro | Enterprise | Education (как WindowsEdition.Normalize в App); неизвестное даёт null.</summary>
    static string? Normalize(string? editionId)
    {
        if (string.IsNullOrWhiteSpace(editionId)) return null;
        var id = editionId.Trim();
        bool Is(string prefix) => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (Is("ProfessionalEducation") || Is("Education")) return "Education";
        if (Is("Professional")) return "Pro";
        if (Is("Core")) return "Home";
        if (Is("Enterprise") || Is("IoT") || Is("Server")) return "Enterprise";
        if (Is("Cloud")) return "Pro";
        return null;
    }

    static void Editions(LiveContext c, List<TweakEntry> set)
    {
        var bound = set.Where(t => t.Editions is { Length: > 0 }).ToList();
        if (bound.Count == 0) { c.Report.Skip(Sc, "редакции", "среди твиков v2 нет привязанных к редакциям"); return; }

        string id;
        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            id = k?.GetValue("EditionID") as string ?? "";
        var edition = Normalize(id);
        c.Report.Info(Sc, "редакция образа", $"EditionID «{id}» -> {edition ?? "не определена (пометка «не действует» не показывается)"}; проверка по модели каталога, не через UI Automation");

        foreach (var t in bound)
        {
            var eds = t.Editions!;
            var valid = eds.All(e => TweakCatalog.Editions.Contains(e));
            var described = eds.All(e => t.Description.Contains(e, StringComparison.Ordinal));
            c.Report.Check(Sc, $"{t.Id}: редакции в каталоге", valid && described,
                $"{string.Join(", ", eds)}; описание называет все редакции",
                valid ? $"описание не называет все редакции {string.Join(", ", eds)}: «{t.Description}»" : $"неизвестная редакция в {string.Join(", ", eds)}");

            var notApplicable = edition is not null && !eds.Contains(edition, StringComparer.Ordinal);
            c.Report.Info(Sc, $"{t.Id}: на этой редакции", notApplicable ? "UI покажет «не действует на этой редакции Windows»" : "действует, пометки нет");
        }
    }
}
