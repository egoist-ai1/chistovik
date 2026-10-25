using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 12. Чистка реестра (находки C3) настоящим графом агента в Windows Sandbox. Под HKLM\...\Uninstall создаются сироты
/// и ловушки (символическая ссылка, дерево больше 50 подключей, ссылка внутри дерева, системный компонент, живая программа),
/// плюс «битые» App Paths, Run (HKLM и HKU), RunOnce и SharedDLLs. Проверяется: пробный прогон ничего не пишет и называет причины;
/// боевое применение удаляет только сирот и сохраняет копию в карантин (ключ и .reg, который принимает reg.exe import);
/// откат возвращает всё, повторный откат ничего не делает; изменённый вручную ключ даёт конфликт, который снимается пропуском;
/// посторонние записи (живая программа, SecurityHealth, StartupApproved) не меняются. Вне Sandbox: ПРОПУСК, ничего не трогает.
/// </summary>
static class S12Registry
{
    const string Sc = "S12 реестр";
    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    const string SharedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
    const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string QuarantineKey = @"SOFTWARE\Chistovik\Quarantine";

    internal const string Orphan = "ChistovikOrphan";
    internal const string Live = "ChistovikLive";
    internal const string Sys = "ChistovikSys";
    const string LinkName = "ChistovikLink";
    const string Big = "ChistovikBig";
    const string Orphan2 = "ChistovikOrphan2";
    const string BadName = "ChistovikBadName";
    const string AppExe = "chistoviktest.exe";
    const string RunName = "ChistovikRun";
    const string OnceName = "ChistovikOnce";
    const string SharedMissing = @"C:\nope\shared.dll";
    const string SharedNoDrive = @"Z:\x.dll";

    // ---------- общее для S10 и S12 ----------

    internal static string ArpId(string name) => "arp:machine:x64:" + name;

    internal static void Put(bool user, string sid, string key, string name, object value, RegistryValueKind kind) => Reg.SetRaw(user, sid, key, name, value, kind);

    /// <summary>Запись Uninstall: сирота (InstallLocation и деинсталлятора на диске нет) или с заданными путями.</summary>
    internal static void MakeUninstall(string name, string? installLocation, string? uninstallString, bool systemComponent = false)
    {
        var key = UninstallKey + "\\" + name;
        Put(false, "", key, "DisplayName", name + " Test", RegistryValueKind.String);
        Put(false, "", key, "Publisher", "Chistovik Test", RegistryValueKind.String);
        if (installLocation is not null) Put(false, "", key, "InstallLocation", installLocation, RegistryValueKind.String);
        if (uninstallString is not null) Put(false, "", key, "UninstallString", uninstallString, RegistryValueKind.String);
        if (systemComponent) Put(false, "", key, "SystemComponent", 1, RegistryValueKind.DWord);
    }

    /// <summary>Сирота с тремя подключами и значениями пяти типов.</summary>
    internal static void MakeOrphan(string name)
    {
        MakeUninstall(name, @"C:\Program Files\" + name, "\"C:\\Program Files\\" + name + "\\unins.exe\" /S");
        var key = UninstallKey + "\\" + name;
        Put(false, "", key, "DwordValue", 7, RegistryValueKind.DWord);
        Put(false, "", key, "BinValue", new byte[] { 1, 2, 3, 250 }, RegistryValueKind.Binary);
        Put(false, "", key, "MultiValue", new[] { "один", "два" }, RegistryValueKind.MultiString);
        Put(false, "", key, "ExpandValue", @"%SystemRoot%\nope", RegistryValueKind.ExpandString);
        for (var i = 1; i <= 3; i++) Put(false, "", key + "\\Sub" + i, "v" + i, "значение " + i, RegistryValueKind.String);
    }

    internal static void MakeLive(string name)
    {
        MakeUninstall(name, @"C:\Users\Public", "\"C:\\Windows\\System32\\cmd.exe\" /c exit");
        var key = UninstallKey + "\\" + name;
        Put(false, "", key + "\\SubA", "v", "a", RegistryValueKind.String);
        Put(false, "", key + "\\SubB", "v", "b", RegistryValueKind.String);
    }

    internal static void RemoveUninstall(params string[] names)
    {
        foreach (var n in names) Try(() => Reg.DeleteTree(false, "", UninstallKey + "\\" + n));
    }

    static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException) { }
    }

    /// <summary>Структурный снимок дерева: значения (имя, тип, данные) и подключи рекурсивно, имена в нижнем регистре. null: ключа нет.</summary>
    internal static string? Snap(bool user, string sid, string key)
    {
        using var b = RegistryKey.OpenBaseKey(user ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = b.OpenSubKey(Reg.Full(user, sid, key));
        if (k is null) return null;
        var sb = new StringBuilder();
        Dump(k, "", sb, 0);
        return sb.ToString();
    }

    static void Dump(RegistryKey k, string path, StringBuilder sb, int depth)
    {
        if (depth > 8) return;
        foreach (var n in k.GetValueNames().OrderBy(x => x.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var kind = k.GetValueKind(n);
            var text = new RegSnap(true, kind, k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)).Text;
            sb.Append(path).Append('|').Append(n.ToLowerInvariant()).Append('|').Append(kind).Append('|').Append(text).Append('\n');
        }
        foreach (var sub in k.GetSubKeyNames().OrderBy(x => x.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var p = path + "/" + sub.ToLowerInvariant();
            sb.Append(p).Append('\n');
            using var child = k.OpenSubKey(sub);
            if (child is not null) Dump(child, p, sb, depth + 1);
        }
    }

    static int SubkeyCount(string key)
    {
        using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = b.OpenSubKey(key);
        return k?.SubKeyCount ?? -1;
    }

    static string SharedId(string valueName)
        => "shareddll:x64:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(valueName.ToLowerInvariant()))).ToLowerInvariant();

    static string Hex(Guid g) => g.ToString("N");

    // ---------- сценарий ----------

    public static async Task Run(LiveContext c)
    {
        var guard = SandboxGuard.Check(c.Token);
        if (!guard.Ok)
        {
            c.Report.Skip(Sc, "признак Sandbox", "не Windows Sandbox, сценарий ничего не трогает: " + guard.Reason);
            return;
        }

        var sid = c.Sid;
        var ops = new List<Guid>();
        var linkKey = UninstallKey + "\\" + LinkName;
        var innerLinkKey = UninstallKey + "\\" + Orphan2 + "\\Link";
        var liveNt = @"\Registry\Machine\" + UninstallKey + "\\" + Live;
        try
        {
            using var s = Session.Open(c, "s12");

            // ---------- подготовка ----------
            MakeOrphan(Orphan);
            MakeLive(Live);
            MakeUninstall(Sys, @"C:\Program Files\" + Sys, "\"C:\\Program Files\\" + Sys + "\\unins.exe\"", systemComponent: true);
            MakeUninstall(Big, @"C:\Program Files\" + Big, "\"C:\\Program Files\\" + Big + "\\unins.exe\"");
            for (var i = 0; i < 60; i++) Put(false, "", UninstallKey + "\\" + Big + "\\K" + i.ToString("D2"), "v", "x", RegistryValueKind.String);
            MakeUninstall(Orphan2, @"C:\Program Files\" + Orphan2, "\"C:\\Program Files\\" + Orphan2 + "\\unins.exe\"");
            var rcLink = RegLink.CreateInMachine(linkKey, liveNt);
            var rcInner = RegLink.CreateInMachine(innerLinkKey, liveNt);
            var linksOk = rcLink == 0 && rcInner == 0;
            c.Report.Check(Sc, "ловушки реестра созданы (две ссылки REG_LINK)", linksOk, "ChistovikLink и Orphan2\\Link указывают на ChistovikLive", $"коды Win32: {rcLink}, {rcInner}");

            var badOk = true;
            try { Put(false, "", UninstallKey + "\\" + BadName + "\\bad]sub", "v", "x", RegistryValueKind.String); MakeUninstall(BadName, @"C:\Program Files\" + BadName, "\"C:\\Program Files\\" + BadName + "\\unins.exe\""); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { badOk = false; }

            Put(false, "", AppPathsKey + "\\" + AppExe, "", @"C:\nope\t.exe", RegistryValueKind.String);
            Put(false, "", RunKey, RunName, "\"C:\\nope\\run.exe\" /x", RegistryValueKind.String);
            Put(true, sid, RunKey, RunName, "\"C:\\nope\\run.exe\" /x", RegistryValueKind.String);
            Put(false, "", RunOnceKey, OnceName, "\"C:\\nope\\once.exe\"", RegistryValueKind.String);
            Put(false, "", SharedKey, SharedMissing, 1, RegistryValueKind.DWord);
            var noDrive = !Directory.Exists(@"Z:\");
            if (noDrive) Put(false, "", SharedKey, SharedNoDrive, 1, RegistryValueKind.DWord);

            // Эталоны и канарейки.
            var origOrphan = Snap(false, "", UninstallKey + "\\" + Orphan);
            var origApp = Snap(false, "", AppPathsKey + "\\" + AppExe);
            var runHklm = Reg.Read(false, "", RunKey, RunName);
            var runHku = Reg.Read(true, sid, RunKey, RunName);
            var once = Reg.Read(false, "", RunOnceKey, OnceName);
            var shared = Reg.Read(false, "", SharedKey, SharedMissing);
            var liveSnap = Snap(false, "", UninstallKey + "\\" + Live);
            var liveSubs = SubkeyCount(UninstallKey + "\\" + Live);
            var secHealth = Reg.Read(false, "", RunKey, "SecurityHealth");
            var approvedHklm = Snap(false, "", ApprovedKey);
            var approvedHku = Snap(true, sid, ApprovedKey);
            bool Untouched() => Snap(false, "", UninstallKey + "\\" + Live) == liveSnap && SubkeyCount(UninstallKey + "\\" + Live) == liveSubs
                                && Reg.Read(false, "", RunKey, "SecurityHealth").SameAs(secHealth)
                                && Snap(false, "", ApprovedKey) == approvedHklm && Snap(true, sid, ApprovedKey) == approvedHku;
            string UntouchedWhy() => $"Live подключей {SubkeyCount(UninstallKey + "\\" + Live)} (было {liveSubs}); SecurityHealth {Reg.Read(false, "", RunKey, "SecurityHealth")} (было {secHealth})";

            // ---------- 1. пробный прогон ----------
            var items = new List<(string Id, string? Rule, bool RuleIsPrefix, string What)>
            {
                (ArpId(Orphan), null, false, "сирота"),
                (ArpId(LinkName), "reg-symlink", false, "ссылка вместо ключа"),
                (ArpId(Big), "reg-tree-too-large", false, "больше 50 подключей"),
                (ArpId(Orphan2), "reg-tree-has-link", false, "ссылка внутри дерева"),
                (ArpId(Live), "evidence-", true, "живая программа"),
                (ArpId(Sys), "arp-system-component", false, "системный компонент"),
            };
            if (badOk) items.Add((ArpId(BadName), "reg-tree-bad-name", false, "подключ с «]»"));
            items.Add(("apppath:machine:x64:" + AppExe, null, false, "App Paths"));
            items.Add(("run:hklm64:" + RunName, null, false, "Run HKLM"));
            items.Add(("run:hku:" + RunName, null, false, "Run HKU"));
            items.Add(("runonce:hklm64:" + OnceName, null, false, "RunOnce"));
            items.Add((SharedId(SharedMissing), null, false, "SharedDLLs, файла нет"));
            if (noDrive) items.Add((SharedId(SharedNoDrive), "drive-missing", false, "SharedDLLs, диска нет"));
            if (!linksOk) items.RemoveAll(x => x.Id == ArpId(LinkName) || x.Id == ArpId(Orphan2));

            var dry = await s.Send(new ApplyFindingsRequest
            {
                Id = Guid.NewGuid(),
                Items = [.. items.Select(x => new FindingAction(x.Id, FindingActionKind.Remove))],
                DryRun = true,
            });
            c.Report.Check(Sc, "пробный прогон: код dry-run", dry.Ok && dry.Code == ResponseCodes.DryRun, s.Describe(dry), s.Describe(dry));
            var facts = dry.Facts ?? [];
            if (facts.Count != items.Count) c.Report.Fail(Sc, "пробный прогон: фактов столько же, сколько находок", $"фактов {facts.Count}, находок {items.Count}; " + s.Describe(dry));
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var (id, rule, prefix, what) = items[i];
                    using var doc = JsonDocument.Parse(facts[i].Json);
                    var got = LiveJson.Str(doc.RootElement, "rule");
                    var ok = rule is null ? got is null : got is not null && (prefix ? got.StartsWith(rule, StringComparison.Ordinal) : got == rule);
                    c.Report.Check(Sc, $"пробный прогон: {what}", ok, got ?? "правил отказа нет", $"правило «{got ?? "<нет>"}», ждали «{rule ?? "<нет>"}»; факт {facts[i].Json}");
                    if (i == 0)
                    {
                        var subs = LiveJson.Num(doc.RootElement, "subkeys");
                        var rev = LiveJson.Prop(doc.RootElement, "revertible") is { ValueKind: JsonValueKind.True };
                        c.Report.Check(Sc, "пробный прогон: у сироты три подключа и откат возможен", subs == 3 && rev, $"подключей {subs}, revertible", $"подключей {subs}, revertible {rev}");
                    }
                }
            }
            int opCount;
            using (var rd = new SqliteJournalReader(s.DbPath)) opCount = rd.List(0, 100).Count;
            c.Report.Check(Sc, "пробный прогон: ничего не записано", opCount == 0 && Snap(false, "", UninstallKey + "\\" + Orphan) == origOrphan && Untouched(),
                "операций в журнале 0, реестр без изменений", $"операций {opCount}; {UntouchedWhy()}");

            // ---------- 2. боевое применение ----------
            var applyItems = new[]
            {
                ArpId(Orphan), "apppath:machine:x64:" + AppExe, "run:hklm64:" + RunName, "run:hku:" + RunName, "runonce:hklm64:" + OnceName, SharedId(SharedMissing),
            };
            var ap = await s.Send(new ApplyFindingsRequest
            {
                Id = Guid.NewGuid(),
                Items = [.. applyItems.Select(x => new FindingAction(x, FindingActionKind.Remove))],
                DryRun = false,
            });
            c.Report.Check(Sc, "применение: ответ ok", ap.Ok && ap.Code == ResponseCodes.Ok, s.Describe(ap), s.Describe(ap));
            if (ap.OperationId is not { } op)
            {
                c.Report.Fail(Sc, "применение: идентификатор операции", "нет OperationId: " + s.Describe(ap));
                return;
            }
            ops.Add(op);

            c.Report.Check(Sc, "применение: сирота, App Paths, Run, RunOnce и SharedDLLs удалены",
                !Reg.KeyExists(false, "", UninstallKey + "\\" + Orphan) && !Reg.KeyExists(false, "", AppPathsKey + "\\" + AppExe)
                && !Reg.Read(false, "", RunKey, RunName).Exists && !Reg.Read(true, sid, RunKey, RunName).Exists
                && !Reg.Read(false, "", RunOnceKey, OnceName).Exists && !Reg.Read(false, "", SharedKey, SharedMissing).Exists,
                "всё исчезло", "что-то осталось");
            c.Report.Check(Sc, "применение: SharedDLLs с отсутствующим диском и посторонние записи не тронуты",
                (!noDrive || Reg.Read(false, "", SharedKey, SharedNoDrive).Exists) && Untouched(), "Z:\\x.dll, Live, SecurityHealth, StartupApproved на месте", UntouchedWhy());

            var copyKey = QuarantineKey + "\\" + Hex(op) + "\\1";
            var copy = Snap(false, "", copyKey);
            c.Report.Check(Sc, "применение: копия дерева в карантине совпадает с оригиналом", copy is not null && copy == origOrphan,
                "значения и 3 подключа сохранены", copy is null ? $"ключа HKLM\\{copyKey} нет" : "структура копии отличается от оригинала");
            c.Report.Info(Sc, "применение: копия App Paths", Snap(false, "", QuarantineKey + "\\" + Hex(op) + "\\2") is null ? "ключа копии \\2 нет" : "ключ копии \\2 есть");

            var regFile = Path.Combine(s.Dir, "quarantine", Hex(op), "1.reg");
            string regText = "";
            try { if (File.Exists(regFile)) regText = File.ReadAllText(regFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var header = @"[HKEY_LOCAL_MACHINE\" + UninstallKey + "\\" + Orphan + "]";
            c.Report.Check(Sc, "применение: .reg-копия есть и верного вида",
                regText.StartsWith("Windows Registry Editor Version 5.00", StringComparison.Ordinal) && regText.Contains(header, StringComparison.OrdinalIgnoreCase),
                $"{regFile}, {regText.Length} знаков", File.Exists(regFile) ? "файл есть, заголовок или раздел не найдены" : $"файла нет: {regFile}");
            if (regText.Length > 0)
            {
                var imp = Proc.Run("reg.exe", ["import", regFile]);
                var restored = Snap(false, "", UninstallKey + "\\" + Orphan);
                c.Report.Check(Sc, "применение: reg import из .reg-копии восстанавливает ключ", imp.Exit == 0 && restored == origOrphan,
                    "ключ вернулся и совпадает", $"reg import: код {imp.Exit} {imp.Output.Trim()}; совпадение: {restored == origOrphan}");
                Try(() => Reg.DeleteTree(false, "", UninstallKey + "\\" + Orphan));   // чтобы откат агента шёл от исходного состояния
            }

            // ---------- 3. откат ----------
            var u = await s.Undo(op);
            c.Report.Check(Sc, "откат: ответ ok", u.Ok, s.Describe(u), s.Describe(u));
            c.Report.Check(Sc, "откат: сирота возвращена целиком", Snap(false, "", UninstallKey + "\\" + Orphan) == origOrphan, "структура совпала с исходной", "структура отличается или ключа нет");
            c.Report.Check(Sc, "откат: App Paths возвращены", Snap(false, "", AppPathsKey + "\\" + AppExe) == origApp, "совпало", "не совпало");
            c.Report.Check(Sc, "откат: Run, RunOnce, SharedDLLs возвращены с типом и данными",
                Reg.Read(false, "", RunKey, RunName).SameAs(runHklm) && Reg.Read(true, sid, RunKey, RunName).SameAs(runHku)
                && Reg.Read(false, "", RunOnceKey, OnceName).SameAs(once) && Reg.Read(false, "", SharedKey, SharedMissing).SameAs(shared),
                "значения как были", $"HKLM {Reg.Read(false, "", RunKey, RunName)}; HKU {Reg.Read(true, sid, RunKey, RunName)}; RunOnce {Reg.Read(false, "", RunOnceKey, OnceName)}; Shared {Reg.Read(false, "", SharedKey, SharedMissing)}");
            c.Report.Check(Sc, "откат: посторонние записи не тронуты", Untouched(), "без изменений", UntouchedWhy());
            var u2 = await s.Undo(op);
            c.Report.Check(Sc, "повторный откат ничего не делает",
                u2.Code == ResponseCodes.NoChange || u2.Steps?.Any(x => x.Code == "already-reverted") == true || (!u2.Ok && u2.Code != ResponseCodes.Internal),
                s.Describe(u2), s.Describe(u2));

            // ---------- 4. конфликт при откате ----------
            var ap2 = await s.Send(new ApplyFindingsRequest
            {
                Id = Guid.NewGuid(),
                Items = [.. applyItems.Select(x => new FindingAction(x, FindingActionKind.Remove))],
                DryRun = false,
            });
            if (!ap2.Ok || ap2.OperationId is not { } op2) c.Report.Fail(Sc, "конфликт: повторное применение", s.Describe(ap2));
            else
            {
                ops.Add(op2);
                var manualKey = UninstallKey + "\\" + Orphan;
                Put(false, "", manualKey, "DisplayName", "Создано вручную", RegistryValueKind.String);
                Put(false, "", manualKey, "Marker", 42, RegistryValueKind.DWord);
                var manualSnap = Snap(false, "", manualKey);
                var cu = await s.Undo(op2);
                c.Report.Check(Sc, "конфликт: откат при ручном ключе даёт conflict", !cu.Ok && cu.Code == ResponseCodes.Conflict && cu.Conflicts is { Count: > 0 },
                    s.Describe(cu), s.Describe(cu));
                c.Report.Check(Sc, "конфликт: ручной ключ не тронут", Snap(false, "", manualKey) == manualSnap, "без изменений", "ручной ключ изменён");
                if (cu.Conflicts is { Count: > 0 } cf)
                {
                    var sk = await s.Send(new UndoRequest
                    {
                        Id = Guid.NewGuid(),
                        OperationId = op2,
                        Resolutions = [.. cf.Select(x => new UndoResolution { StepId = x.StepId, Action = ConflictAction.Skip })],
                    });
                    c.Report.Check(Sc, "конфликт: откат с пропуском конфликтного шага", sk.Ok || sk.Code == ResponseCodes.Partial, s.Describe(sk), s.Describe(sk));
                    c.Report.Check(Sc, "конфликт: ручной ключ цел, остальное возвращено", Snap(false, "", manualKey) == manualSnap && Reg.Read(false, "", RunKey, RunName).SameAs(runHklm),
                        "ручной ключ цел, Run вернулся", $"ручной ключ: {(Snap(false, "", manualKey) == manualSnap ? "цел" : "ИЗМЕНЁН")}; Run {Reg.Read(false, "", RunKey, RunName)}");
                }

                // ---------- 5. удаление программы по находке реестра ----------
                var un = await s.Send(new UninstallRequest { Id = Guid.NewGuid(), FindingId = ArpId(Orphan) });
                c.Report.Check(Sc, "удаление программы: у ключа без рабочего деинсталлятора запрос отвергнут, ключ цел", !un.Ok && Snap(false, "", manualKey) == manualSnap,
                    s.Describe(un), s.Describe(un) + (Snap(false, "", manualKey) == manualSnap ? "" : "; ключ изменён"));
            }
        }
        finally
        {
            // Ссылки раньше деревьев: иначе удаление дерева пошло бы по ссылке в ChistovikLive.
            Try(() => RegLink.DeleteInMachine(innerLinkKey));
            Try(() => RegLink.DeleteInMachine(linkKey));
            RemoveUninstall(Orphan, Orphan2, Big, Sys, BadName, Live);
            Try(() => Reg.DeleteTree(false, "", AppPathsKey + "\\" + AppExe));
            Try(() => Reg.Delete(false, "", RunKey, RunName));
            Try(() => Reg.Delete(true, c.Sid, RunKey, RunName));
            Try(() => Reg.Delete(false, "", RunOnceKey, OnceName));
            Try(() => Reg.Delete(false, "", SharedKey, SharedMissing));
            Try(() => Reg.Delete(false, "", SharedKey, SharedNoDrive));
            foreach (var o in ops) Try(() => Reg.DeleteTree(false, "", QuarantineKey + "\\" + Hex(o)));
        }
    }
}
