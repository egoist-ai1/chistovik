using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Chistovik.Core.Ipc;

namespace Sandbox.Live;

/// <summary>Разбор JSON-фактов ответа агента (общий для S11 и S12): имена полей без учёта регистра, пропавшее поле это ноль.</summary>
static class LiveJson
{
    public static JsonElement? Prop(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static long Num(JsonElement o, string name)
        => Prop(o, name) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var v) ? v : 0;

    public static string? Str(JsonElement o, string name)
        => Prop(o, name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
}

/// <summary>Факт очистки одной категории (cleanup:&lt;область&gt;:&lt;sid|-&gt;:&lt;id&gt;).</summary>
sealed record CleanFact(long Bytes, long Files, long Deleted, long Freed, long Failed, IReadOnlyDictionary<string, long> Skipped)
{
    public long Skip(string reason) => Skipped.GetValueOrDefault(reason);

    public override string ToString()
        => $"bytes {Bytes}, files {Files}, deleted {Deleted}, freed {Freed}, failed {Failed}, skipped {{{string.Join(",", Skipped.Select(x => x.Key + ":" + x.Value))}}}";

    public static CleanFact? Find(AgentResponse r, string categoryId)
    {
        var f = r.Facts?.FirstOrDefault(x => x.Key.StartsWith("cleanup:", StringComparison.OrdinalIgnoreCase)
                                             && x.Key.EndsWith(":" + categoryId, StringComparison.OrdinalIgnoreCase));
        if (f is null) return null;
        try
        {
            using var d = JsonDocument.Parse(f.Json);
            var root = d.RootElement;
            var skipped = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (LiveJson.Prop(root, "skipped") is { ValueKind: JsonValueKind.Object } s)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n)) skipped[p.Name] = n;
            return new CleanFact(LiveJson.Num(root, "bytes"), LiveJson.Num(root, "files"), LiveJson.Num(root, "deleted"),
                LiveJson.Num(root, "freedBytes"), LiveJson.Num(root, "failed"), skipped);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Сценарий 11. Очистка диска настоящим графом агента (AgentPorts.Windows, журнал SQLite) в Windows Sandbox. В C:\Windows\Temp,
/// %LOCALAPPDATA%\Temp и очереди WER лежат старые файлы (возраст 3 суток), свежие файлы, занятый файл, пустой старый каталог и три
/// ловушки: junction на System32, символическая ссылка на файл и на каталог. Проверяется: измерение не удаляет; очистка удаляет
/// только старое и не заходит в ссылки (число файлов в System32 и канарейка не меняются); журнал очистки; откат отвергается;
/// повтор ничего не делает; корень Temp, подменённый junction на System32, отвергается; запущенный Edge пропускает свою
/// категорию. Вне Sandbox сценарий печатает ПРОПУСК и ничего не трогает.
/// </summary>
static class S11Disk
{
    const string Sc = "S11 диск";
    static readonly TimeSpan OldAge = TimeSpan.FromDays(3);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct RbInfo { public uint CbSize; public long Size; public long NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBinW(string? root, ref RbInfo info);

    static long BinItems()
    {
        var i = new RbInfo { CbSize = (uint)Marshal.SizeOf<RbInfo>() };
        return SHQueryRecycleBinW(null, ref i) == 0 ? i.NumItems : -1;
    }

    static void Age(string path)
    {
        var t = DateTime.UtcNow - OldAge;
        File.SetCreationTimeUtc(path, t);
        File.SetLastWriteTimeUtc(path, t);
        File.SetLastAccessTimeUtc(path, t);
    }

    static void AgeDir(string path)
    {
        var t = DateTime.UtcNow - OldAge;
        Directory.SetCreationTimeUtc(path, t);
        Directory.SetLastWriteTimeUtc(path, t);
        Directory.SetLastAccessTimeUtc(path, t);
    }

    static void MakeFile(string path, int bytes, bool old)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (old) Age(path);
    }

    static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Сама ссылка или обычный файл/каталог (без захода в цель).</summary>
    static void RemoveLink(string path)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((attrs & FileAttributes.Directory) != 0) Directory.Delete(path, false);
        else File.Delete(path);
    }

    /// <summary>Независимый подсчёт: старые файлы и старые каталоги без захода в ссылки.</summary>
    static (long Files, long Bytes) Count(string root, DateTime limitUtc)
    {
        long files = 0, bytes = 0;
        void Walk(string dir)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var e in entries)
            {
                try
                {
                    var attrs = File.GetAttributes(e);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        Walk(e);
                        var di = new DirectoryInfo(e);
                        if (Math.Max(di.CreationTimeUtc.Ticks, di.LastWriteTimeUtc.Ticks) <= limitUtc.Ticks) files++;
                    }
                    else
                    {
                        var fi = new FileInfo(e);
                        if (Math.Max(fi.CreationTimeUtc.Ticks, fi.LastWriteTimeUtc.Ticks) <= limitUtc.Ticks) { files++; bytes += fi.Length; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        Walk(root);
        return (files, bytes);
    }

    static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    static CleanCategoriesRequest Req(bool measure, params string[] ids)
        => new() { Id = Guid.NewGuid(), Categories = ids, MeasureOnly = measure };

    public static async Task Run(LiveContext c)
    {
        var guard = SandboxGuard.Check(c.Token);
        if (!guard.Ok)
        {
            c.Report.Skip(Sc, "признак Sandbox", "не Windows Sandbox, сценарий ничего не трогает: " + guard.Reason);
            return;
        }

        var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var userTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        var sys32 = Environment.SystemDirectory;
        var werDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\WER\ReportQueue", "Report.test");
        var edgeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge");
        var edgeCache = Path.Combine(edgeRoot, @"User Data\Default\Cache");

        var oldTmp = Path.Combine(winTemp, "s11-old.tmp");
        var freshTmp = Path.Combine(winTemp, "s11-fresh.tmp");
        var locked = Path.Combine(winTemp, "s11-locked.tmp");
        var oldDir = Path.Combine(winTemp, "s11-olddir");
        var oldDirFile = Path.Combine(oldDir, "a.log");
        var emptyOld = Path.Combine(winTemp, "s11-emptyold");
        var junction = Path.Combine(winTemp, "s11-J-System32");
        var linkFile = Path.Combine(winTemp, "s11-L-notepad");
        var linkDir = Path.Combine(winTemp, "s11-LD-Sys");
        var canary = Path.Combine(sys32, "chistovik-canary.txt");
        var uOld = Path.Combine(userTemp, "s11-old.tmp");
        var uFresh = Path.Combine(userTemp, "s11-fresh.tmp");
        var werFile = Path.Combine(werDir, "Report.wer");
        var edgeFile = Path.Combine(edgeCache, "s11-old.bin");
        var recycleSrc = Path.Combine(c.WorkDir, "s11-to-bin.txt");
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

        var undo = new List<Action>();
        FileStream? hold = null;
        var edgeExisted = Directory.Exists(edgeRoot);
        try
        {
            using var s = Session.Open(c, "s11");

            // ---------- подготовка ----------
            Directory.CreateDirectory(winTemp);
            Directory.CreateDirectory(userTemp);
            MakeFile(oldTmp, 1024 * 1024, true);
            MakeFile(freshTmp, 8 * 1024 * 1024, false);
            MakeFile(locked, 4096, true);
            MakeFile(oldDirFile, 8192, true);
            AgeDir(oldDir);
            Directory.CreateDirectory(emptyOld);
            AgeDir(emptyOld);
            MakeFile(uOld, 512 * 1024, true);
            MakeFile(uFresh, 4096, false);
            MakeFile(werFile, 4096, true);
            AgeDir(werDir);
            MakeFile(edgeFile, 4096, true);
            undo.Add(() => { if (File.Exists(edgeFile)) File.Delete(edgeFile); if (!edgeExisted && Directory.Exists(edgeRoot)) Directory.Delete(edgeRoot, true); });
            undo.Add(() => { if (Directory.Exists(werDir)) Directory.Delete(werDir, true); });
            undo.Add(() => { if (File.Exists(uOld)) File.Delete(uOld); if (File.Exists(uFresh)) File.Delete(uFresh); });
            undo.Add(() => { if (Directory.Exists(emptyOld)) Directory.Delete(emptyOld, true); if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true); });
            undo.Add(() => { foreach (var f in new[] { oldTmp, freshTmp, locked }) if (File.Exists(f)) File.Delete(f); });

            var canaryOk = false;
            try { MakeFile(canary, 2048, true); canaryOk = true; undo.Add(() => { if (File.Exists(canary)) File.Delete(canary); }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { c.Report.Info(Sc, "канарейка", "в System32 файл не создать: " + e.Message); }

            // Ссылки создаются последними, поэтому и убираются первыми.
            var j = Proc.Run("cmd.exe", ["/c", "mklink", "/J", junction, sys32]);
            var linksOk = j.Exit == 0 && Directory.Exists(junction);
            undo.Add(() => RemoveLink(junction));
            try
            {
                File.CreateSymbolicLink(linkFile, notepad);
                Directory.CreateSymbolicLink(linkDir, sys32);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                linksOk = false;
                c.Report.Info(Sc, "символические ссылки", "не созданы: " + e.Message);
            }
            undo.Add(() => RemoveLink(linkFile));
            undo.Add(() => RemoveLink(linkDir));
            c.Report.Check(Sc, "ловушки созданы (junction и две символические ссылки)", linksOk && IsReparse(junction) && IsReparse(linkFile) && IsReparse(linkDir),
                "junction на System32, ссылки на notepad.exe и System32", $"mklink: {j.Exit} {j.Output.Trim()}");

            hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

            var sysFilesBefore = Directory.GetFiles(sys32).Length;

            // Файл в Корзине (если Корзина работает).
            var binBefore = BinItems();
            var binUsed = false;
            try
            {
                File.WriteAllText(recycleSrc, "корзина");
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(recycleSrc, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                binUsed = BinItems() > Math.Max(0, binBefore);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                c.Report.Info(Sc, "Корзина", "файл в Корзину не отправлен: " + e.Message);
            }
            if (!binUsed) c.Report.Skip(Sc, "Корзина", "в Корзину ничего не попало (Корзина отключена или недоступна)");

            // ---------- 1. измерение ----------
            var m = await s.Send(Req(true, "temp-system"));
            var mf = CleanFact.Find(m, "temp-system");
            c.Report.Check(Sc, "измерение: ответ ok и факт есть", m.Ok && mf is not null, mf?.ToString() ?? "", s.Describe(m) + (mf is null ? "; факта temp-system нет" : ""));
            c.Report.Check(Sc, "измерение: ничего не удалено", File.Exists(oldTmp) && File.Exists(oldDirFile) && Directory.Exists(emptyOld), "все объекты на месте", "измерение удалило файлы");
            if (mf is not null)
            {
                c.Report.Check(Sc, "измерение: байт не меньше 1 МиБ", mf.Bytes >= 1024 * 1024, $"{mf.Bytes}", $"{mf.Bytes} меньше 1 МиБ");
                var mine = Count(winTemp, DateTime.UtcNow - TimeSpan.FromHours(24));
                var fileDiff = Math.Abs(mf.Files - mine.Files);
                var byteDiff = Math.Abs(mf.Bytes - mine.Bytes);
                c.Report.Check(Sc, "измерение: сходится с независимым подсчётом", fileDiff <= 3 && byteDiff <= 256 * 1024,
                    $"агент {mf.Files} файлов/{mf.Bytes} байт, стенд {mine.Files}/{mine.Bytes}", $"агент {mf.Files}/{mf.Bytes}, стенд {mine.Files}/{mine.Bytes}");
            }

            // ---------- 2. очистка ----------
            var r = await s.Send(Req(false, "temp-system", "temp-user", "wer-system", "recycle-bin"));
            var rf = CleanFact.Find(r, "temp-system");
            c.Report.Check(Sc, "очистка: ответ ok, no-change или partial", r.Code is ResponseCodes.Ok or ResponseCodes.NoChange or ResponseCodes.Partial,
                s.Describe(r), s.Describe(r));
            c.Report.Check(Sc, "очистка: старое удалено (Windows\\Temp, Temp профиля, WER)",
                !Exists(oldTmp) && !Exists(oldDir) && !Exists(emptyOld) && !Exists(uOld) && !Exists(werFile),
                "старые файлы и каталоги исчезли",
                $"остались: {string.Join(", ", new[] { oldTmp, oldDir, emptyOld, uOld, werFile }.Where(Exists))}");
            c.Report.Check(Sc, "очистка: свежее и занятое на месте", File.Exists(freshTmp) && File.Exists(uFresh) && File.Exists(locked),
                "fresh и locked целы", "свежий или занятый файл исчез");
            c.Report.Check(Sc, "очистка: ссылки не тронуты и не раскрыты",
                Exists(junction) && Exists(linkFile) && Exists(linkDir) && IsReparse(junction) && IsReparse(linkFile) && IsReparse(linkDir),
                "junction и символические ссылки на месте", "ссылка удалена или заменена");
            c.Report.Check(Sc, "очистка: System32 цела", Directory.GetFiles(sys32).Length == sysFilesBefore && File.Exists(notepad) && (!canaryOk || File.Exists(canary)),
                $"файлов в System32 {sysFilesBefore}, канарейка и notepad.exe целы",
                $"было {sysFilesBefore}, стало {Directory.GetFiles(sys32).Length}; канарейка {(File.Exists(canary) ? "цела" : "УДАЛЕНА")}");
            c.Report.Check(Sc, "очистка: корни Temp на месте", Directory.Exists(winTemp) && Directory.Exists(userTemp), "корни целы", "корень удалён");
            if (rf is null) c.Report.Fail(Sc, "очистка: факт temp-system", "факта нет: " + s.Describe(r));
            else
            {
                c.Report.Check(Sc, "очистка: пропуски reparse не меньше 3 и in-use не меньше 1", rf.Skip("reparse") >= 3 && rf.Skip("in-use") >= 1,
                    rf.ToString(), rf.ToString());
                c.Report.Check(Sc, "очистка: удалено не меньше четырёх объектов", rf.Deleted >= 4, rf.ToString(), rf.ToString());
            }
            if (binUsed) c.Report.Check(Sc, "очистка: Корзина пуста", BinItems() == 0, "элементов 0", $"в Корзине {BinItems()}");

            if (r.OperationId is { } op)
            {
                var logPath = Path.Combine(s.Dir, "cleanup", op.ToString("D") + ".log");
                string[] log = [];
                try { if (File.Exists(logPath)) log = File.ReadAllLines(logPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var dLines = log.Where(l => l.StartsWith("D ", StringComparison.Ordinal)).ToArray();
                c.Report.Check(Sc, "журнал очистки: строка об удалении s11-old.tmp", dLines.Any(l => l.EndsWith("s11-old.tmp", StringComparison.OrdinalIgnoreCase)),
                    $"строк «D»: {dLines.Length}", log.Length == 0 ? $"лога нет: {logPath}" : $"строк {log.Length}, «D» про s11-old.tmp нет");
                c.Report.Check(Sc, "журнал очистки: в удалённых нет System32", !dLines.Any(l => l.Contains("System32", StringComparison.OrdinalIgnoreCase)),
                    "строки «D» без System32", string.Join("; ", dLines.Where(l => l.Contains("System32", StringComparison.OrdinalIgnoreCase)).Take(3)));

                // ---------- 3. откат очистки ----------
                var u = await s.Undo(op);
                c.Report.Check(Sc, "откат очистки отвергается", !u.Ok && u.Code == ResponseCodes.NotRevertible, s.Describe(u), s.Describe(u));
            }
            else c.Report.Fail(Sc, "очистка: идентификатор операции", "в ответе нет OperationId: " + s.Describe(r));

            // ---------- 4. повтор ----------
            var again = await s.Send(Req(false, "temp-system", "temp-user"));
            var af = CleanFact.Find(again, "temp-system");
            c.Report.Check(Sc, "повтор: ничего нового не удалено", again.Code is ResponseCodes.Ok or ResponseCodes.NoChange or ResponseCodes.Partial && af is { Deleted: 0 },
                af?.ToString() ?? "", s.Describe(again) + (af is null ? "; факта нет" : "; " + af));

            hold.Dispose();
            hold = null;

            // ---------- 5. ловушка: корень Temp профиля подменён junction на System32 ----------
            await Trap(c, s, userTemp, sys32, canary, canaryOk);

            // ---------- 6. запущенный Edge ----------
            await EdgeCase(c, s, edgeFile);

            // ---------- 7. справка по Defender ----------
            var d = Proc.PowerShell("Get-MpThreatDetection | Select-Object -First 5 | Format-List ThreatID,ProcessName,Resources | Out-String");
            var text = d.Output.Trim();
            c.Report.Info(Sc, "Defender: обнаружения", text.Length == 0 ? $"нет записей (код {d.Exit})" : (text.Length > 400 ? text[..400] : text));
        }
        finally
        {
            Try(() => hold?.Dispose());
            for (var i = undo.Count - 1; i >= 0; i--) Try(undo[i]);
            Try(() => { if (File.Exists(recycleSrc)) File.Delete(recycleSrc); });
        }
    }

    static async Task Trap(LiveContext c, Session s, string userTemp, string sys32, string canary, bool canaryOk)
    {
        var bak = userTemp + ".bak";
        var moved = false;
        try
        {
            if (Directory.Exists(bak)) { c.Report.Skip(Sc, "ловушка корня Temp", $"{bak} уже есть"); return; }
            try { Directory.Move(userTemp, bak); moved = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                c.Report.Skip(Sc, "ловушка корня Temp", "Temp профиля не переименовать (в нём открытые файлы): " + e.Message);
                return;
            }
            var j = Proc.Run("cmd.exe", ["/c", "mklink", "/J", userTemp, sys32]);
            if (j.Exit != 0 || !IsReparse(userTemp))
            {
                c.Report.Skip(Sc, "ловушка корня Temp", "junction не создан: " + j.Output.Trim());
                return;
            }
            var before = Directory.GetFiles(sys32).Length;
            var r = await s.Send(Req(false, "temp-user"));
            var after = Directory.GetFiles(sys32).Length;
            c.Report.Check(Sc, "ловушка: корень Temp, подменённый junction, отвергнут", !r.Ok && s.Describe(r).Contains("cleanup-root-reparse", StringComparison.Ordinal),
                s.Describe(r), s.Describe(r));
            c.Report.Check(Sc, "ловушка: System32 цела", after == before && (!canaryOk || File.Exists(canary)), $"файлов {after}, канарейка цела", $"было {before}, стало {after}");
        }
        finally
        {
            if (moved)
            {
                Try(() => { if (Directory.Exists(userTemp) && IsReparse(userTemp)) Directory.Delete(userTemp, false); });
                Try(() => { if (!Directory.Exists(userTemp)) Directory.Move(bak, userTemp); });
            }
        }
    }

    static async Task EdgeCase(LiveContext c, Session s, string edgeFile)
    {
        var edge = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        }.FirstOrDefault(File.Exists);
        if (edge is null) { c.Report.Skip(Sc, "запущенный Edge", "msedge.exe в образе нет"); return; }
        if (!File.Exists(edgeFile)) File.WriteAllBytes(edgeFile, new byte[4096]);
        Age(edgeFile);

        try
        {
            using var p = Process.Start(new ProcessStartInfo(edge, "--no-first-run --no-default-browser-check about:blank") { UseShellExecute = false });
            var started = false;
            for (var i = 0; i < 40 && !started; i++)
            {
                await Task.Delay(500);
                started = Process.GetProcessesByName("msedge").Length > 0;
            }
            if (!started) { c.Report.Skip(Sc, "запущенный Edge", "msedge не стартовал"); return; }
            await Task.Delay(3000);

            var r = await s.Send(Req(false, "browser-edge"));
            c.Report.Check(Sc, "Edge запущен: категория пропущена с правилом cleanup-browser-running",
                r.Ok && File.Exists(edgeFile) && s.Describe(r).Contains("cleanup-browser-running", StringComparison.Ordinal),
                s.Describe(r), s.Describe(r) + (File.Exists(edgeFile) ? "" : "; файл кэша удалён"));
        }
        finally
        {
            foreach (var q in Process.GetProcessesByName("msedge"))
            {
                Try(() => q.Kill(true));
                q.Dispose();
            }
        }
        for (var i = 0; i < 40 && Process.GetProcessesByName("msedge").Length > 0; i++) await Task.Delay(500);

        var r2 = await s.Send(Req(false, "browser-edge"));
        c.Report.Check(Sc, "Edge закрыт: кэш очищается", r2.Code is ResponseCodes.Ok or ResponseCodes.NoChange or ResponseCodes.Partial && !File.Exists(edgeFile),
            s.Describe(r2), s.Describe(r2) + (File.Exists(edgeFile) ? "; файл кэша остался" : ""));
    }
}
