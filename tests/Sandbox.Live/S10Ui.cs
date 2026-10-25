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
    static readonly string[] Sections = ["Обзор", "Чистка", "Режимы", "Питание", "Автозагрузка", "Службы", "Задачи", "Твики", "Журнал и откат"];

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
        var installed = new List<Target>();
        foreach (var t in targets)
        {
            var file = Directory.EnumerateFiles(c.InstallersDir, t.Pattern).FirstOrDefault();
            if (file is null) { c.Report.Skip(Sc, $"установка {t.Kind}", $"нет {t.Pattern}"); continue; }
            if (S9Uninstall.FindArp(t.NamePrefix) is not null) { c.Report.Skip(Sc, $"установка {t.Kind}", $"«{t.NamePrefix}» уже есть"); continue; }
            var exe = t.File.Contains("{0}") ? string.Format(t.File, file) : t.File;
            var code = S9Uninstall.RunProcess(exe, string.Format(t.Args, file), TimeSpan.FromMinutes(6));
            var ok = code is 0 or 3010 && S9Uninstall.FindArp(t.NamePrefix) is not null;
            c.Report.Check(Sc, $"установка {t.Kind} ({t.NamePrefix})", ok, $"код {code}", $"код {code}, записи ARP нет");
            if (ok) installed.Add(t);
        }

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
        }
        finally
        {
            try { if (!app.HasExited) { app.Kill(true); app.WaitForExit(5000); } } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
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
            var nav = FindListItem(main, "Чистка");
            if (nav is null) { c.Report.Fail(Sc, $"{tag}: меню", "пункта «Чистка» не видно, окно недоступно"); DumpWhy(c, app, main, $"s10-why-{t.Kind}-nav.txt"); continue; }
            Select(nav);
            await Task.Delay(1500);
            WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(120));

            // Свежий скан: программа поставлена после первого просмотра.
            var rescan = FindByName(main, "Сканировать систему заново");
            if (rescan is not null) { Invoke(rescan); await Task.Delay(1500); }
            WaitFor(() => Texts(main).Any(x => x.StartsWith("Показано ", StringComparison.Ordinal)) ? main : null, TimeSpan.FromSeconds(180));

            var search = FindByName(main, "Поиск в списке чистки");
            if (search is null) { c.Report.Fail(Sc, $"{tag}: поиск", "поля поиска нет"); continue; }
            SetText(search, t.NamePrefix);
            await Task.Delay(1200);

            var row = WaitFor(() => ListItems(main).FirstOrDefault(i => i.Current.Name.StartsWith(t.NamePrefix, StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(30));
            c.Report.Check(Sc, $"{tag}: программа видна в «Чистке»", row is not null, row?.Current.Name ?? "", "строки нет в списке");
            if (row is null) { Save(c, $"s10-miss-{t.Kind}.txt", Texts(main)); continue; }
            Select(row);
            await Task.Delay(800);
            Save(c, $"s10-detail-{t.Kind}.txt", Texts(main));

            var button = FindByName(main, "Удалить выбранную программу через агента");
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

    static IEnumerable<AutomationElement> ListItems(AutomationElement main)
    {
        var list = FindByName(main, "Список объектов");
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
