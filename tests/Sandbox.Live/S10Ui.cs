using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 10. Настоящее приложение (Chistovik.App.exe из релиза, агент рядом) управляется изнутри Sandbox через
/// UI Automation: ни мыши, ни клавиатуры хоста. Ставятся реальные программы (MSI, NSIS, Inno), затем окно обходит все
/// разделы релиза, в «Чистке» находит программу, жмёт «Удалить программу», подтверждает диалог, и проверяется, что запись
/// ARP и файлы исчезли, а в «Журнале и откате» появилась операция. Для защищённых объектов (Edge, рантаймы) кнопки
/// удаления быть не должно. Выписки с экранов лежат в папке результатов (s10-*.txt).
/// </summary>
static class S10Ui
{
    const string Sc = "S10 интерфейс";
    static readonly string[] Sections = ["Обзор", "Чистка", "Программы", "Режимы", "Питание", "Автозагрузка", "Службы", "Задачи", "Твики", "Журнал и откат"];

    sealed record Target(string Kind, string Pattern, string File, string Args, string NamePrefix, string Probe);

    public static async Task Run(LiveContext c)
    {
        if (c.AgentPath is null || c.InstallersDir is null || !Directory.Exists(c.InstallersDir))
        {
            c.Report.Skip(Sc, "условия", "нужны --agent (релиз с приложением) и --installers");
            return;
        }
        var dir = Path.GetDirectoryName(c.AgentPath)!;
        var appExe = Path.Combine(dir, "Chistovik.App.exe");
        if (!File.Exists(appExe))
        {
            c.Report.Skip(Sc, "приложение", $"нет {appExe}");
            return;
        }

        // 1. Ставим программы тремя разными установщиками.
        var targets = new List<Target>
        {
            new("MSI", "*.msi", "msiexec.exe", "/i \"{0}\" /qn /norestart", "7-Zip", @"7-Zip\7z.exe"),
            new("NSIS", "npp*.exe", "{0}", "/S", "Notepad++", @"Notepad++\notepad++.exe"),
            new("Inno", "Git-*.exe", "{0}", "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-", "Git", @"Git\bin\git.exe"),
        };
        var installed = InstallAll(c, targets, "установка");

        // 2. Запуск приложения.
        var psi = new ProcessStartInfo(appExe) { UseShellExecute = false, WorkingDirectory = dir };
        using var app = Process.Start(psi) ?? throw new InvalidOperationException("приложение не запущено");
        try
        {
            var main = WaitFor(() =>
            {
                app.Refresh();
                return app.MainWindowHandle != IntPtr.Zero ? AutomationElement.FromHandle(app.MainWindowHandle) : null;
            }, TimeSpan.FromSeconds(90));
            c.Report.Check(Sc, "окно приложения появилось", main is not null, "окно найдено", "окно не появилось за 90 с");
            if (main is null) return;

            c.Report.Info(Sc, "окно", $"имя «{main.Current.Name}», процесс отвечает: {app.Responding}");
            c.Report.Check(Sc, "данные настоящие, не демонстрационные", FindByName(main, "Демонстрационные данные") is null, "плашки демонстрации нет", "на экране плашка демонстрации");

            await Walk(c, app, main);
            await TweakThroughUi(c, app, main);
            await UninstallThroughUi(c, app, main, installed);
            await ProtectedHasNoButton(c, app, main);
            await BatchThroughUi(c, app, main, targets);
            await CleanupExtension(c, app, main);
        }
        finally
        {
            try { if (!app.HasExited) { app.Kill(true); app.WaitForExit(5000); } } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    static List<Target> InstallAll(LiveContext c, List<Target> targets, string what)
    {
        var installed = new List<Target>();
        foreach (var t in targets)
        {
            var file = Directory.EnumerateFiles(c.InstallersDir!, t.Pattern).FirstOrDefault();
            if (file is null) { c.Report.Skip(Sc, $"{what} {t.Kind}", $"нет {t.Pattern}"); continue; }
            if (S9Uninstall.FindArp(t.NamePrefix) is not null) { c.Report.Skip(Sc, $"{what} {t.Kind}", $"«{t.NamePrefix}» уже есть"); continue; }
            var exe = t.File.Contains("{0}") ? string.Format(t.File, file) : t.File;
            var code = S9Uninstall.RunProcess(exe, string.Format(t.Args, file), TimeSpan.FromMinutes(6));
            var ok = code is 0 or 3010 && S9Uninstall.FindArp(t.NamePrefix) is not null;
            c.Report.Check(Sc, $"{what} {t.Kind} ({t.NamePrefix})", ok, $"код {code}", $"код {code}, записи ARP нет");
            if (ok) installed.Add(t);
        }
        return installed;
    }

    const string BrokenName = "Aaa Chistovik Broken Test";
    const string BrokenKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ChistovikTest_Broken";

    /// <summary>
    /// Пакетное удаление через «Инвентарь»: ставим три программы заново плюс «сломанную» запись (деинсталлятора нет на диске,
    /// по алфавиту она первая). Отмечаем все четыре, жмём одну кнопку: настоящие программы должны удалиться, сломанная
    /// не должна остановить пакет и остаться в реестре, а итог должен назвать, что с ней.
    /// </summary>
    static async Task BatchThroughUi(LiveContext c, Process app, AutomationElement main, List<Target> targets)
    {
        const string tag = "пакет";
        var again = InstallAll(c, targets, "пакет: повторная установка");
        if (again.Count < 2) { c.Report.Skip(Sc, tag, "для пакета нужно минимум две установленные программы"); return; }
        using (var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).CreateSubKey(BrokenKey))
        {
            k.SetValue("DisplayName", BrokenName);
            k.SetValue("Publisher", "Chistovik Test");
            k.SetValue("DisplayVersion", "1.0");
            k.SetValue("UninstallString", "\"C:\\ChistovikNoSuch\\unins000.exe\" /S");
        }

        var names = new List<string> { BrokenName };
        names.AddRange(again.Select(t => t.NamePrefix));
        Select(FindListItem(main, "Чистка")!);   // уход с экрана и возврат: «Инвентарь» перечитывает реестр при показе
        await Task.Delay(1500);
        Select(FindListItem(main, "Программы")!);
        await Task.Delay(1500);
        WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));
        var search = FindByName(main, "Поиск программ");
        if (search is null) { c.Report.Fail(Sc, $"{tag}: поиск", "поля поиска нет"); return; }

        var checkedCount = 0;
        foreach (var n in names)
        {
            SetText(search, n);
            await Task.Delay(1200);
            var row = WaitFor(() => ListItems(main, "Список установленных программ").FirstOrDefault(i => i.Current.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
            var box = row?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            if (box is null) { c.Report.Fail(Sc, $"{tag}: отметка «{n}»", row is null ? "строки нет" : "у строки нет флажка"); continue; }
            Invoke(box);
            checkedCount++;
            await Task.Delay(500);
        }
        SetText(search, "");
        await Task.Delay(1200);
        c.Report.Check(Sc, $"{tag}: отмечено программ", checkedCount == names.Count, $"{checkedCount}", $"отмечено {checkedCount} из {names.Count}");
        if (checkedCount == 0) return;

        var button = FindByName(main, "Удалить выбранные программы");
        if (button is not { Current.IsEnabled: true }) { c.Report.Fail(Sc, $"{tag}: кнопка удаления", "нет или выключена"); return; }
        Invoke(button);
        var confirm = WaitFor(() => FindDialogOk(app, "Удалить: "), TimeSpan.FromSeconds(20));
        c.Report.Check(Sc, $"{tag}: диалог подтверждения", confirm is not null, "диалог показан", "диалога нет");
        if (confirm is null) { DumpWhy(c, app, main, "s10-why-batch-dialog.txt"); return; }
        DumpDialogs(c, app, "s10-dialog-batch.txt");
        Invoke(confirm);

        foreach (var t in again)
        {
            var gone = WaitFor(() => S9Uninstall.FindArp(t.NamePrefix) is null ? "gone" : null, TimeSpan.FromMinutes(8)) is not null;
            c.Report.Check(Sc, $"{tag}: {t.Kind} ({t.NamePrefix}) удалена", gone, "запись ARP исчезла", "запись ARP осталась через 8 минут");
        }
        // Итог появляется, когда агент вернул ответ по последней программе и список перечитан: ждём, а не спим фиксированно.
        var summary = WaitFor(() => Texts(main).FirstOrDefault(x => x.StartsWith("Удалено", StringComparison.Ordinal)), TimeSpan.FromSeconds(90));
        var texts = Texts(main);
        Save(c, "s10-after-batch.txt", texts);
        c.Report.Check(Sc, $"{tag}: итог на экране", summary is not null, summary ?? "", "строки «Удалено …» нет");
        var stillThere = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(BrokenKey) is not null;
        c.Report.Check(Sc, $"{tag}: «сломанная» запись не тронута", stillThere, "ключ на месте", "ключ исчез");
        c.Report.Check(Sc, $"{tag}: приложение живо", !app.HasExited && app.Responding, "отвечает", "не отвечает или закрылось");
        try { Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).DeleteSubKeyTree(BrokenKey, false); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>Обход разделов релиза: пункт меню выбирается, экран должен прогрузиться, выписка пишется в файл.</summary>
    static async Task Walk(LiveContext c, Process app, AutomationElement main)
    {
        foreach (var s in Sections)
        {
            var item = FindListItem(main, s);
            if (item is null) { c.Report.Fail(Sc, $"раздел «{s}»", "пункта нет в меню"); continue; }
            Select(item);
            await Task.Delay(1500);
            // Экраны читают систему в фоне: ждём, пока исчезнет «Загружаем…» (и для «Чистки» появится «Показано …»).
            var started = DateTime.UtcNow;
            WaitFor(() =>
            {
                var now = Texts(main);
                var loading = now.Any(t => t.StartsWith("Загружаем", StringComparison.Ordinal));
                var scanned = s != "Чистка" || now.Any(t => t.StartsWith("Показано ", StringComparison.Ordinal));
                return !loading && scanned ? main : null;
            }, TimeSpan.FromSeconds(180));
            c.Report.Info(Sc, $"раздел «{s}»: загрузка", $"{(DateTime.UtcNow - started).TotalSeconds:0.0} с");
            var texts = Texts(main);
            Save(c, $"s10-{Array.IndexOf(Sections, s) + 1:00}-{s.Replace(' ', '_')}.txt", texts);
            var alive = !app.HasExited && app.Responding;
            var errors = texts.Where(t => t.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase) || t.Contains("ошибка", StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
            c.Report.Check(Sc, $"раздел «{s}»: открылся, приложение отвечает", alive && texts.Count > 5,
                $"{texts.Count} надписей", $"{texts.Count} надписей, отвечает: {alive}");
            if (errors.Length > 0) c.Report.Info(Sc, $"раздел «{s}»: подозрительные надписи", string.Join(" | ", errors));
        }
        var agent = Texts(main).FirstOrDefault(t => t.StartsWith("Агент ", StringComparison.Ordinal));
        c.Report.Info(Sc, "строка состояния агента до изменений", agent ?? "<нет>");
    }

    /// <summary>
    /// Твики → включить пользовательский твик → «Применить» → подтвердить → значение в реестре изменилось (независимое чтение) →
    /// «Журнал и откат» → «Откатить» → значение вернулось. Это же первый запуск агента из приложения.
    /// </summary>
    static async Task TweakThroughUi(LiveContext c, Process app, AutomationElement main)
    {
        var t = Chistovik.Core.Catalog.TweakCatalog.Shared.Tweaks.FirstOrDefault(x => x.Title == "Показывать расширения файлов" && Tweaks.IsUser(x));
        if (t is null) { c.Report.Skip(Sc, "твик через интерфейс", "в каталоге нет «Показывать расширения файлов»"); return; }
        var on = Tweaks.Expect(t, true);
        var off = Tweaks.Expect(t, false);
        var before = Tweaks.Read(c, t);
        var wasOn = on.Matches(before);
        var target = wasOn ? off : on;
        c.Report.Info(Sc, "твик: исходное состояние", $"{t.Title}: {(wasOn ? "включён" : "выключен")} ({before})");

        Select(FindListItem(main, "Твики")!);
        await Task.Delay(1500);
        WaitFor(() => Texts(main).Any(x => x == t.Title) ? main : null, TimeSpan.FromSeconds(60));
        var box = FindByName(main, "Включить: " + t.Title);
        if (box is null) { c.Report.Fail(Sc, "твик: переключатель", "переключателя нет на экране"); return; }
        Invoke(box);
        await Task.Delay(700);
        var apply = WaitFor(() => FindByName(main, "Применить выбранные изменения") is { } a && a.Current.IsEnabled ? a : null, TimeSpan.FromSeconds(10));
        c.Report.Check(Sc, "твик: кнопка «Применить» стала доступна", apply is not null, "доступна", "осталась выключенной");
        if (apply is null) return;
        Invoke(apply);

        // Обычный пользовательский твик подтверждения не требует (только «осторожные» и общие для машины), диалог необязателен.
        var ok = WaitFor(() => FindDialogOk(app, "Применить: "), TimeSpan.FromSeconds(6));
        c.Report.Info(Sc, "твик: диалог подтверждения", ok is null ? "не показан (для этого твика не нужен)" : "показан");
        if (ok is not null) { DumpDialogs(c, app, "s10-dialog-tweak.txt"); Invoke(ok); }

        var applied = WaitFor(() => target.Matches(Tweaks.Read(c, t)) ? "ok" : null, TimeSpan.FromSeconds(120)) is not null;
        c.Report.Check(Sc, "твик: значение в реестре изменилось через агента", applied, $"теперь {Tweaks.Read(c, t)}", $"осталось {Tweaks.Read(c, t)}, ждали {target}");
        if (!applied) DumpWhy(c, app, main, "s10-why-tweak-apply.txt");
        await Task.Delay(2000);
        Save(c, "s10-after-tweak.txt", Texts(main));
        c.Report.Info(Sc, "твик: агент после изменения", Texts(main).FirstOrDefault(x => x.StartsWith("Агент", StringComparison.Ordinal)) ?? "<нет>");
        if (!applied) return;

        Select(FindListItem(main, "Журнал и откат")!);
        await Task.Delay(2000);
        var op = WaitFor(() => FindByName(main, "Список операций")?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)), TimeSpan.FromSeconds(30));
        c.Report.Check(Sc, "твик: операция появилась в журнале", op is not null, op?.Current.Name ?? "", "список операций пуст");
        if (op is null) return;
        Select(op);
        await Task.Delay(800);
        var undo = FindByName(main, "Откатить выбранную операцию");
        c.Report.Check(Sc, "твик: кнопка «Откатить» доступна", undo is { Current.IsEnabled: true }, "доступна", "нет или выключена");
        if (undo is not { Current.IsEnabled: true }) return;
        Invoke(undo);
        var undoOk = WaitFor(() => FindDialogOk(app, "Откатить: "), TimeSpan.FromSeconds(20));
        c.Report.Check(Sc, "твик: диалог отката", undoOk is not null, "диалог показан", "диалога нет");
        if (undoOk is null) return;
        Invoke(undoOk);
        var restored = WaitFor(() => (wasOn ? on : off).Matches(Tweaks.Read(c, t)) ? "ok" : null, TimeSpan.FromSeconds(60)) is not null;
        c.Report.Check(Sc, "твик: откат вернул прежнее значение", restored, $"снова {Tweaks.Read(c, t)}", $"осталось {Tweaks.Read(c, t)}");
        await Task.Delay(1500);
        Save(c, "s10-after-undo.txt", Texts(main));
    }

    /// <summary>Чистка → найти программу → «Удалить программу» → подтвердить → ARP исчез → операция в журнале.</summary>
    static async Task UninstallThroughUi(LiveContext c, Process app, AutomationElement main, List<Target> installed)
    {
        if (installed.Count == 0) { c.Report.Skip(Sc, "удаление через интерфейс", "ни одна программа не установилась"); return; }
        foreach (var t in installed)
        {
            var tag = $"{t.Kind} ({t.NamePrefix})";
            // Inno (Git) удаляется с экрана «Инвентарь» (отметка строки и кнопка «Удалить»), остальные из «Чистки».
            var viaInventory = t.Kind == "Inno";
            var screen = viaInventory ? "Программы" : "Чистка";
            var nav = FindListItem(main, screen);
            if (nav is null) { c.Report.Fail(Sc, $"{tag}: меню", $"пункта «{screen}» не видно, окно недоступно"); DumpWhy(c, app, main, $"s10-why-{t.Kind}-nav.txt"); continue; }
            Select(nav);
            await Task.Delay(1500);
            AutomationElement? search;
            AutomationElement? row;
            if (viaInventory)
            {
                // Возврат на экран сам тихо перечитывает реестр (OnShown), отдельной кнопки обновления нет.
                WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));
                search = FindByName(main, "Поиск программ");
                if (search is null) { c.Report.Fail(Sc, $"{tag}: поиск", "поля поиска нет"); continue; }
                SetText(search, t.NamePrefix);
                await Task.Delay(1200);
                row = WaitFor(() => ListItems(main, "Список установленных программ").FirstOrDefault(i => i.Current.Name.StartsWith(t.NamePrefix, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
                c.Report.Check(Sc, $"{tag}: программа видна в «Инвентаре»", row is not null, row?.Current.Name ?? "", "строки нет в списке");
                if (row is null) { Save(c, $"s10-miss-{t.Kind}.txt", Texts(main)); continue; }
                var check = row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                if (check is null) { c.Report.Fail(Sc, $"{tag}: отметка", "у строки нет флажка"); continue; }
                Invoke(check);
                await Task.Delay(800);
                Save(c, $"s10-detail-{t.Kind}.txt", Texts(main));
            }
            else
            {
                WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));

                // Свежий скан: программа поставлена после первого просмотра.
                var rescan = FindByName(main, "Сканировать систему заново");
                if (rescan is not null) { Invoke(rescan); await Task.Delay(1500); }
                WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(180));

                search = FindByName(main, "Поиск в списке чистки");
                if (search is null) { c.Report.Fail(Sc, $"{tag}: поиск", "поля поиска нет"); continue; }
                SetText(search, t.NamePrefix);
                await Task.Delay(1200);

                row = WaitFor(() => ListItems(main).FirstOrDefault(i => i.Current.Name.StartsWith(t.NamePrefix, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
                c.Report.Check(Sc, $"{tag}: программа видна в «Чистке»", row is not null, row?.Current.Name ?? "", "строки нет в списке");
                if (row is null) { Save(c, $"s10-miss-{t.Kind}.txt", Texts(main)); continue; }
                Select(row);
                await Task.Delay(800);
                Save(c, $"s10-detail-{t.Kind}.txt", Texts(main));
            }

            var button = FindByName(main, viaInventory ? "Удалить выбранные программы" : "Удалить выбранную программу через агента");
            var canClick = button is not null && button.Current.IsEnabled;
            c.Report.Check(Sc, $"{tag}: кнопка «Удалить программу» доступна", canClick, "кнопка есть", button is null ? "кнопки нет" : "кнопка выключена");
            if (!canClick) continue;
            Invoke(button!);

            var confirm = WaitFor(() => FindDialogOk(app, "Удалить: "), TimeSpan.FromSeconds(20));
            c.Report.Check(Sc, $"{tag}: диалог подтверждения", confirm is not null, "диалог показан", "диалога нет");
            if (confirm is null) { DumpWhy(c, app, main, $"s10-why-{t.Kind}-dialog.txt"); continue; }
            DumpDialogs(c, app, $"s10-dialog-{t.Kind}.txt");
            Invoke(confirm);

            var gone = WaitFor(() => S9Uninstall.FindArp(t.NamePrefix) is null ? "gone" : null, TimeSpan.FromMinutes(6)) is not null;
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), t.Probe);
            c.Report.Check(Sc, $"{tag}: запись ARP исчезла после кнопки", gone, "агент удалил программу", "запись ARP осталась через 6 минут");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fileGone = WaitFor(() => File.Exists(file) ? null : "gone", TimeSpan.FromSeconds(90)) is not null;
            c.Report.Check(Sc, $"{tag}: файлы программы исчезли", fileGone, $"файла нет (ждали {sw.Elapsed.TotalSeconds:F1} с после ARP)", $"остался {file} через 90 с после ARP");
            await Task.Delay(2500);
            Save(c, $"s10-after-{t.Kind}.txt", Texts(main));
            var agent = Texts(main).FirstOrDefault(x => x.StartsWith("Агент ", StringComparison.Ordinal));
            c.Report.Info(Sc, $"{tag}: агент после удаления", agent ?? "<нет>");
            c.Report.Check(Sc, $"{tag}: приложение живо", !app.HasExited && app.Responding, "отвечает", "не отвечает или закрылось");
        }

        Select(FindListItem(main, "Журнал и откат")!);
        await Task.Delay(2500);
        var journal = Texts(main);
        Save(c, "s10-journal-after.txt", journal);
        c.Report.Check(Sc, "журнал показывает удаления программ", journal.Any(x => x.Contains("Удаление программы", StringComparison.OrdinalIgnoreCase)),
            "операция «Удаление программы» есть", "в журнале нет записи об удалении");
        // Заголовок несёт имя программы, а не сырой ключ реестра («git_is1», «{GUID}»).
        foreach (var t in installed)
        {
            var title = journal.FirstOrDefault(x => x.StartsWith("Удаление программы: " + t.NamePrefix, StringComparison.OrdinalIgnoreCase));
            c.Report.Check(Sc, $"журнал: заголовок удаления {t.Kind} ({t.NamePrefix}) с именем программы", title is not null,
                title ?? "", "в журнале нет заголовка с именем программы");
        }
    }

    /// <summary>Edge и рантаймы: в подробностях не должно быть рабочей кнопки удаления.</summary>
    static async Task ProtectedHasNoButton(LiveContext c, Process app, AutomationElement main)
    {
        Select(FindListItem(main, "Чистка")!);
        await Task.Delay(1500);
        var search = FindByName(main, "Поиск в списке чистки");
        if (search is null) return;
        foreach (var prefix in new[] { "Microsoft Edge", "Microsoft Visual C++" })
        {
            SetText(search, prefix);
            await Task.Delay(1200);
            var row = ListItems(main).FirstOrDefault(i => i.Current.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (row is null) { c.Report.Skip(Sc, $"защита «{prefix}»", "в списке нет такой строки"); continue; }
            Select(row);
            await Task.Delay(800);
            var button = FindByName(main, "Удалить выбранную программу через агента");
            var blocked = button is null || !button.Current.IsEnabled;
            c.Report.Check(Sc, $"защита «{prefix}»: удалить из интерфейса нельзя", blocked, button is null ? "кнопки нет" : "кнопка выключена", "кнопка удаления доступна");
            Save(c, $"s10-protected-{prefix.Replace(' ', '_').Replace("+", "p")}.txt", Texts(main));
        }
        SetText(search, "");
    }

    /// <summary>
    /// Расширение для «Чистки» (C2/C3): чипы «Диск» и «Реестр»; категория «Временные файлы пользователя» очищается кнопкой и
    /// подтверждением; запись реестра (сирота) удаляется с подтверждением про копию, затем откатывается из «Журнала и отката»
    /// и снова видна после пересканирования; защищённые строки (системный компонент, живая программа) без флажка.
    /// </summary>
    static async Task CleanupExtension(LiveContext c, Process app, AutomationElement main)
    {
        const string tag = "чистка диска и реестра";
        _ctx = c;
        const string diskTitle = "Временные файлы пользователя";
        var userTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        var oldFile = Path.Combine(userTemp, "s10-old.tmp");
        var orphanKey = S12Registry.UninstallKey + "\\" + S12Registry.Orphan;
        try
        {
            Directory.CreateDirectory(userTemp);
            File.WriteAllBytes(oldFile, new byte[256 * 1024]);
            var old = DateTime.UtcNow - TimeSpan.FromDays(3);
            File.SetCreationTimeUtc(oldFile, old);
            File.SetLastWriteTimeUtc(oldFile, old);
            S12Registry.MakeOrphan(S12Registry.Orphan);
            S12Registry.MakeLive(S12Registry.Live);
            S12Registry.MakeUninstall(S12Registry.Sys, @"C:\Program Files\" + S12Registry.Sys, "\"C:\\Program Files\\" + S12Registry.Sys + "\\unins.exe\"", systemComponent: true);

            // Уход с экрана и возврат, затем свежий скан: фикстуры созданы уже после первого просмотра.
            Select(FindListItem(main, "Журнал и откат")!);
            await Task.Delay(1000);
            Select(FindListItem(main, "Чистка")!);
            await Task.Delay(1500);
            WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));
            await Rescan(main);
            var search = FindByName(main, "Поиск в списке чистки");
            if (search is null) { c.Report.Fail(Sc, $"{tag}: поиск", "поля поиска нет"); return; }

            var diskChip = WaitFor(() => FindChip(main, "Диск"), TimeSpan.FromSeconds(8));
            var regChip = FindChip(main, "Реестр");
            // Деревья UI Automation у ItemsControl бывают без кнопок внутри элементов (кнопки есть на экране, но не видны клиенту):
            // наличие чипов тогда подтверждаем числом элементов данных (7 областей и 6 советов).
            // Фильтры области и совета теперь выпадающие списки (так они помещаются на малых экранах): проверяем, что оба есть
            // и что в каждом достаточно пунктов (7 областей и 6 советов, включая «Все»).
            var areaCount = ComboItemCount(main, "Фильтр по области");
            var verdictCount = ComboItemCount(main, "Фильтр по совету");
            var chipsOk = areaCount >= 7 && verdictCount >= 6;
            c.Report.Check(Sc, $"{tag}: фильтры областей и советов есть", chipsOk,
                $"областей {areaCount}, советов {verdictCount}",
                $"областей {areaCount}, советов {verdictCount}");
            if (diskChip is null || regChip is null) DumpWhy(c, app, main, "s10-why-chips.txt");   // остальное идёт через поиск по названию

            // ----- диск -----
            if (diskChip is not null) Invoke(diskChip);
            await Task.Delay(800);
            var row = await RowFor(main, search, diskTitle);
            c.Report.Check(Sc, $"{tag}: категория «{diskTitle}» видна в области «Диск»", row is not null, row?.Current.Name ?? "", "строки нет в списке");
            if (row is not null)
            {
                var box = row.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                if (box is null) c.Report.Fail(Sc, $"{tag}: флажок категории", "у строки нет флажка");
                else
                {
                    EnsureChecked(box);
                    await Task.Delay(500);
                    var clean = FindByName(main, "Очистить выбранные категории");
                    var canClean = clean is { Current.IsEnabled: true };
                    c.Report.Check(Sc, $"{tag}: кнопка «Очистить выбранные категории» доступна", canClean, "доступна", clean is null ? "кнопки нет" : "выключена");
                    if (canClean)
                    {
                        Invoke(clean!);
                        var ok = WaitFor(() => FindDialogOk(app, "Очистить: "), TimeSpan.FromSeconds(20));
                        c.Report.Check(Sc, $"{tag}: диалог подтверждения очистки", ok is not null, "диалог показан", "диалога нет");
                        if (ok is not null)
                        {
                            DumpDialogs(c, app, "s10-dialog-clean.txt");
                            Invoke(ok);
                            var gone = WaitFor(() => File.Exists(oldFile) ? null : "gone", TimeSpan.FromSeconds(120)) is not null;
                            c.Report.Check(Sc, $"{tag}: старый файл из Temp профиля удалён агентом", gone, "файла нет", "файл остался через 120 с");
                            var summary = WaitFor(() => Texts(main).FirstOrDefault(x => x.Contains("Освобождено", StringComparison.Ordinal)), TimeSpan.FromSeconds(60));
                            c.Report.Check(Sc, $"{tag}: итог очистки показан", summary is not null, summary ?? "", "строки «Освобождено…» нет");
                            Save(c, "s10-after-clean.txt", Texts(main));
                        }
                    }
                }
            }

            // ----- реестр -----
            if (regChip is not null) Invoke(regChip);
            await Task.Delay(800);
            await Rescan(main);
            var orphanRow = await RowFor(main, search, S12Registry.Orphan);
            c.Report.Check(Sc, $"{tag}: сирота реестра видна в области «Реестр»", orphanRow is not null, orphanRow?.Current.Name ?? "", "строки нет в списке");
            if (orphanRow is not null)
            {
                var box = orphanRow.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                if (box is null) c.Report.Fail(Sc, $"{tag}: флажок записи реестра", "у строки нет флажка");
                else
                {
                    EnsureChecked(box);
                    await Task.Delay(500);
                    var del = FindByName(main, "Удалить выбранные записи реестра");
                    var canDel = del is { Current.IsEnabled: true };
                    c.Report.Check(Sc, $"{tag}: кнопка «Удалить выбранные записи реестра» доступна", canDel, "доступна", del is null ? "кнопки нет" : "выключена");
                    if (canDel)
                    {
                        Invoke(del!);
                        var ok = WaitFor(() => FindDialogOk(app, "Удалить: Удалить из реестра"), TimeSpan.FromSeconds(60));
                        c.Report.Check(Sc, $"{tag}: диалог удаления из реестра", ok is not null, "диалог показан", "диалога нет");
                        if (ok is null) DumpWhy(c, app, main, "s10-why-regdialog.txt");
                        if (ok is not null)
                        {
                            DumpDialogs(c, app, "s10-dialog-registry.txt");
                            var dialogText = string.Join(" ", DialogTexts(app));
                            c.Report.Check(Sc, $"{tag}: диалог говорит про копию", dialogText.Contains("копия", StringComparison.OrdinalIgnoreCase), "«копия» названа", "в тексте диалога нет слова «копия»");
                            Invoke(ok);
                            var gone = WaitFor(() => S12Registry.Snap(false, "", orphanKey) is null ? "gone" : null, TimeSpan.FromSeconds(120)) is not null;
                            c.Report.Check(Sc, $"{tag}: ключ сироты удалён агентом", gone, "ключа нет", "ключ остался через 120 с");
                            var summary = WaitFor(() => Texts(main).FirstOrDefault(x => x.Contains("Убрано записей реестра", StringComparison.Ordinal)), TimeSpan.FromSeconds(60));
                            c.Report.Check(Sc, $"{tag}: итог удаления показан", summary is not null, summary ?? "", "строки «Убрано записей реестра…» нет");
                            Save(c, "s10-after-registry.txt", Texts(main));
                        }
                    }
                }
            }

            // ----- защищённые строки -----
            foreach (var name in new[] { S12Registry.Sys, S12Registry.Live })
            {
                var prow = await RowFor(main, search, name);
                if (prow is null) { c.Report.Info(Sc, $"{tag}: защищённая «{name}»", "в списке реестра строки нет (скрыта)"); continue; }
                var pbox = prow.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
                var blocked = pbox is null || pbox.Current.IsOffscreen || !pbox.Current.IsEnabled;
                c.Report.Check(Sc, $"{tag}: у защищённой «{name}» нет рабочего флажка", blocked, pbox is null ? "флажка нет" : "флажок скрыт или выключен", "флажок доступен");
            }

            // ----- откат из журнала -----
            Select(FindListItem(main, "Журнал и откат")!);
            await Task.Delay(2000);
            var op = WaitFor(() => ListItems(main, "Список операций").FirstOrDefault(i => i.Current.Name.Contains("Реестр", StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
            c.Report.Check(Sc, $"{tag}: операция «Реестр» есть в журнале", op is not null, op?.Current.Name ?? "", "в списке операций нет «Реестр»");
            if (op is null) { Save(c, "s10-journal-registry.txt", Texts(main)); return; }
            Select(op);
            await Task.Delay(800);
            var undo = FindByName(main, "Откатить выбранную операцию");
            var canUndo = undo is { Current.IsEnabled: true };
            c.Report.Check(Sc, $"{tag}: кнопка «Откатить» для операции реестра доступна", canUndo, "доступна", undo is null ? "кнопки нет" : "выключена");
            if (!canUndo) return;
            Invoke(undo!);
            var undoOk = WaitFor(() => FindDialogOk(app, "Откатить: "), TimeSpan.FromSeconds(20));
            c.Report.Check(Sc, $"{tag}: диалог отката", undoOk is not null, "диалог показан", "диалога нет");
            if (undoOk is null) return;
            Invoke(undoOk);
            var back = WaitFor(() => S12Registry.Snap(false, "", orphanKey) is not null ? "back" : null, TimeSpan.FromSeconds(120)) is not null;
            c.Report.Check(Sc, $"{tag}: откат вернул ключ сироты", back, "ключ на месте", "ключа нет через 120 с");

            Select(FindListItem(main, "Чистка")!);
            await Task.Delay(1500);
            WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));
            await Rescan(main);
            search = FindByName(main, "Поиск в списке чистки");
            if (search is not null)
            {
                var chipAgain = FindChip(main, "Реестр") ?? regChip;
                if (chipAgain is not null) Invoke(chipAgain);
                await Task.Delay(800);
                var again = await RowFor(main, search, S12Registry.Orphan);
                c.Report.Check(Sc, $"{tag}: после отката и скана сирота снова в списке", again is not null, again?.Current.Name ?? "", "строки нет");
            }
        }
        finally
        {
            try { if (File.Exists(oldFile)) File.Delete(oldFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            S12Registry.RemoveUninstall(S12Registry.Orphan, S12Registry.Live, S12Registry.Sys);
            c.Report.Check(Sc, $"{tag}: приложение живо", !app.HasExited && app.Responding, "отвечает", "не отвечает или закрылось");
        }
    }

    static async Task Rescan(AutomationElement main)
    {
        var rescan = FindByName(main, "Сканировать систему заново");
        if (rescan is not null) { Invoke(rescan); await Task.Delay(1500); }
        WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(180));
    }

    static LiveContext _ctx = null!;

    static async Task<AutomationElement?> RowFor(AutomationElement main, AutomationElement search, string prefix)
    {
        SetText(search, prefix);
        await Task.Delay(1200);
        var row = WaitFor(() => ListItems(main).FirstOrDefault(i => i.Current.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
        if (row is not null) return row;

        // Диагностика: что записано в поле поиска и что показывает список. Затем одна повторная попытка с очисткой поля.
        var diag = new List<string>();
        try
        {
            diag.Add("искали: " + prefix);
            diag.Add("значение поля поиска: " + (search.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value : "(нет ValuePattern)"));
            diag.AddRange(Texts(main).Where(t => t.StartsWith("Показано ", StringComparison.Ordinal)));
            diag.AddRange(ListItems(main).Take(15).Select(i => "[строка] " + i.Current.Name));
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { diag.Add("не прочитать: " + e.Message); }
        diag.Add("== повтор ==");
        SetText(search, "");
        await Task.Delay(1500);
        SetText(search, prefix);
        await Task.Delay(2500);
        row = WaitFor(() => ListItems(main).FirstOrDefault(i => i.Current.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
        diag.Add(row is null ? "повтор: строки нет" : "повтор: строка найдена");
        try { diag.AddRange(Texts(main).Where(t => t.StartsWith("Показано ", StringComparison.Ordinal))); } catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { }
        Save(_ctx, $"s10-why-row-{prefix.Replace(' ', '_')}.txt", diag);
        return row;
    }

    /// <summary>Чип области («Диск», «Реестр»): кнопка в «Фильтр по области» (или любая кнопка окна с таким именем).</summary>
    static AutomationElement? FindChip(AutomationElement main, string text)
    {
        var scope = FindByName(main, "Фильтр по области") ?? main;
        foreach (AutomationElement b in scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            if (b.Current.Name.StartsWith(text, StringComparison.Ordinal)) return b;
        return null;
    }

    /// <summary>Число пунктов выпадающего списка: раскрывает его, считает ListItem и сворачивает. Нет списка: 0.</summary>
    static int ComboItemCount(AutomationElement main, string name)
    {
        var combo = FindByName(main, name);
        if (combo is null) return 0;
        try
        {
            if (!combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var p)) return 0;
            var ec = (ExpandCollapsePattern)p;
            ec.Expand();
            Thread.Sleep(400);
            var n = combo.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
            if (n == 0)
            {
                // у WPF список раскрытого ComboBox иногда лежит в отдельном окне-попапе верхнего уровня
                n = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ProcessIdProperty, combo.Current.ProcessId))).Count;
            }
            ec.Collapse();
            return n;
        }
        catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException)
        {
            return 0;
        }
    }

    static int ChipCount(AutomationElement main, string groupName)
    {
        var group = FindByName(main, groupName);
        if (group is null) return 0;
        return group.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Count;
    }

    static void EnsureChecked(AutomationElement box)
    {
        if (box.TryGetCurrentPattern(TogglePattern.Pattern, out var p) && ((TogglePattern)p).Current.ToggleState != ToggleState.On) ((TogglePattern)p).Toggle();
    }

    static List<string> DialogTexts(Process app)
    {
        var all = new List<string>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id)))
            all.AddRange(Texts(w));
        return all;
    }

    // ---------- UI Automation ----------

    static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            try { if (probe() is { } v) return v; }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            if (DateTime.UtcNow >= end) return null;
            Thread.Sleep(400);
        }
    }

    static AutomationElement? FindByName(AutomationElement scope, string name)
        => scope.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    static AutomationElement? FindListItem(AutomationElement scope, string name)
        => scope.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));

    static IEnumerable<AutomationElement> ListItems(AutomationElement main, string listName = "Список объектов")
    {
        var list = FindByName(main, listName);
        if (list is null) return [];
        return list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>();
    }

    /// <summary>
    /// Кнопка подтверждения в диалоге: окно отдельное, а имя кнопки «Удалить: Удалить «…»?» (текст кнопки и заголовок).
    /// </summary>
    static AutomationElement? FindDialogOk(Process app, string namePrefix)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        // WPF показывает окна-диалоги в дереве UIA внутри окна владельца, поэтому ищем и в главном окне.
        foreach (AutomationElement w in windows)
        {
            var buttons = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
                if (b.Current.Name.StartsWith(namePrefix, StringComparison.Ordinal)) return b;
        }
        return null;
    }

    static void DumpTree(AutomationElement e, int depth, int maxDepth, List<string> lines)
    {
        var r = e.Current.BoundingRectangle;
        lines.Add($"{new string(' ', depth * 2)}{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}; класс {e.Current.ClassName}; имя «{e.Current.Name}»; вне экрана {e.Current.IsOffscreen}; {r}");
        if (depth >= maxDepth) return;
        for (var ch = TreeWalker.RawViewWalker.GetFirstChild(e); ch is not null; ch = TreeWalker.RawViewWalker.GetNextSibling(ch))
            DumpTree(ch, depth + 1, maxDepth, lines);
    }

    /// <summary>Диагностика, когда ожидаемое окно не появилось: все окна рабочего стола (кроме совсем пустых) и надписи главного окна.</summary>
    static void DumpWhy(LiveContext c, Process app, AutomationElement main, string file)
    {
        var lines = new List<string> { $"процесс завершён: {app.HasExited}; отвечает: {(!app.HasExited && app.Responding)}", "== окна рабочего стола ==" };
        try
        {
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                lines.Add($"pid {w.Current.ProcessId}; класс {w.Current.ClassName}; имя «{w.Current.Name}»; включено {w.Current.IsEnabled}");
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { lines.Add("не прочитать: " + e.Message); }
        lines.Add("== надписи главного окна ==");
        try { lines.AddRange(Texts(main)); } catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { lines.Add("не прочитать: " + e.Message); }
        lines.Add("== дерево вокруг кнопки «Сканировать систему заново» ==");
        try
        {
            var rescan = FindByName(main, "Сканировать систему заново");
            var root = rescan;
            for (var i = 0; i < 3 && root is not null; i++) root = TreeWalker.RawViewWalker.GetParent(root);
            if (root is not null) DumpTree(root, 0, 6, lines);
            lines.Add("«Фильтр по области» найден: " + (FindByName(main, "Фильтр по области") is not null));
            lines.Add("«Фильтр по совету» найден: " + (FindByName(main, "Фильтр по совету") is not null));
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { lines.Add("не прочитать: " + e.Message); }
        lines.Add("== кнопки главного окна ==");
        try
        {
            foreach (AutomationElement b in main.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                lines.Add($"[кнопка] «{b.Current.Name}»; включена {b.Current.IsEnabled}; вне экрана {b.Current.IsOffscreen}");
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException) { lines.Add("не прочитать: " + e.Message); }
        Save(c, file, lines);
    }

    /// <summary>Скриншотов нет: пока на экране окно подтверждения, выписываем его надписи, чтобы видеть, что именно спросили.</summary>
    static void DumpDialogs(LiveContext c, Process app, string file)
    {
        var lines = new List<string>();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id));
        foreach (AutomationElement w in windows)
        {
            lines.Add("== окно: " + w.Current.Name);
            foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                if (b.Current.Name.Contains(": ", StringComparison.Ordinal)) lines.Add("[кнопка] " + b.Current.Name);
            lines.AddRange(Texts(w).Take(30));
        }
        Save(c, file, lines);
    }

    static List<string> Texts(AutomationElement scope)
        => scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

    static void Select(AutomationElement item)
    {
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) ((SelectionItemPattern)p).Select();
        else Invoke(item);
    }

    static void Invoke(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) ((InvokePattern)p).Invoke();
        else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var t)) ((TogglePattern)t).Toggle();
        else throw new InvalidOperationException($"у «{e.Current.Name}» нет Invoke");
    }

    static void SetText(AutomationElement box, string text)
    {
        if (box.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) ((ValuePattern)p).SetValue(text);
        else throw new InvalidOperationException("у поля нет ValuePattern");
    }

    static void Save(LiveContext c, string file, IEnumerable<string> lines)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var l in lines) sb.AppendLine(l);
            File.WriteAllText(Path.Combine(c.ResultsDir, file), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
