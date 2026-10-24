using Chistovik.Core.Ipc;
using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 2. Байты StartupApproved для записей Run (HKU, HKLM 64 и 32) и папок «Автозагрузка» (пользователя и общей).
/// Записи создаёт сам стенд с префиксом ChistovikTest_. Проверяется 12-байтное значение: первый байт чётный = включено (02),
/// нечётный = выключено (03). Это та самая модель, по которой Проводник читает флаг. Реакцию Проводника и «Диспетчера
/// задач» на флаг живой стенд не проверяет (см. README, ручной режим).
/// </summary>
static class S2Startup
{
    const string Sc = "S2 автозагрузка";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    sealed record Entry(string Label, string EntryId, bool ApprovedInUser, string ApprovedKey, string ApprovedName, Action Setup, Action Cleanup);

    public static async Task Run(LiveContext c)
    {
        var command = @"""C:\Program Files\ChistovikTest\svc.exe"" /noop";
        var startupUser = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var startupCommon = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var fileUser = Path.Combine(startupUser, "ChistovikTest_Startup.cmd");
        var fileCommon = Path.Combine(startupCommon, "ChistovikTest_Common.cmd");

        var entries = new List<Entry>
        {
            new("Run HKU", "run:hku:ChistovikTest_RunHku", true, Approved + "Run", "ChistovikTest_RunHku",
                () => Reg.SetRaw(true, c.Sid, RunKey, "ChistovikTest_RunHku", command, RegistryValueKind.String),
                () => Reg.Delete(true, c.Sid, RunKey, "ChistovikTest_RunHku")),
            new("Run HKLM 64", "run:hklm64:ChistovikTest_RunHklm64", false, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "ChistovikTest_RunHklm64",
                () => Reg.SetRaw(false, c.Sid, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "ChistovikTest_RunHklm64", command, RegistryValueKind.String),
                () => Reg.Delete(false, c.Sid, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "ChistovikTest_RunHklm64")),
            new("Run HKLM 32", "run:hklm32:ChistovikTest_RunHklm32", false, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "ChistovikTest_RunHklm32",
                () => Reg.SetRaw(false, c.Sid, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "ChistovikTest_RunHklm32", command, RegistryValueKind.String),
                () => Reg.Delete(false, c.Sid, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "ChistovikTest_RunHklm32")),
            new("Папка пользователя", "folder:user:ChistovikTest_Startup.cmd", true, Approved + "StartupFolder", "ChistovikTest_Startup.cmd",
                () => { Directory.CreateDirectory(startupUser); File.WriteAllText(fileUser, "@echo off\r\n"); },
                () => File.Delete(fileUser)),
            new("Общая папка", "folder:common:ChistovikTest_Common.cmd", false, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "ChistovikTest_Common.cmd",
                () => { Directory.CreateDirectory(startupCommon); File.WriteAllText(fileCommon, "@echo off\r\n"); },
                () => File.Delete(fileCommon)),
        };

        using var s = Session.Open(c, "s2");
        foreach (var e in entries)
        {
            try { await One(c, s, e); }
            catch (Exception ex) { c.Report.Fail(Sc, e.Label, "исключение: " + ex.GetType().Name + ": " + ex.Message); }
        }

        c.Report.Skip(Sc, "RunOnce", "Чистовик 0.1 не перечисляет RunOnce (StartupEnumerator: только Run и папки), такой записи агент не выключит");
        c.Report.Skip(Sc, "реакция Проводника и «Диспетчера задач» на флаг", "автоматически не проверяется: нужен ручной режим (README, агент запущен вручную человеком)");
    }

    static string Bytes(RegSnap v) => v.Exists ? v.Text : "<нет значения>";

    static bool IsFlag(RegSnap v, bool enabled)
        => v is { Exists: true, Kind: RegistryValueKind.Binary, Raw: byte[] { Length: 12 } b } && (enabled ? b[0] % 2 == 0 : b[0] % 2 == 1);

    static async Task One(LiveContext c, Session s, Entry e)
    {
        RegSnap Read() => Reg.Read(e.ApprovedInUser, c.Sid, e.ApprovedKey, e.ApprovedName);
        var keyExisted = Reg.KeyExists(e.ApprovedInUser, c.Sid, e.ApprovedKey);
        var orig = Read();
        e.Setup();
        try
        {
            AgentResponse Set(bool enabled) => s.Send(new SetStartupEntryRequest { Id = Guid.NewGuid(), EntryId = e.EntryId, Enabled = enabled }).GetAwaiter().GetResult();

            // 1. выключить: значение 12 байт, первый байт нечётный
            var start = Read();
            var off = Set(false);
            var vOff = Read();
            c.Report.Check(Sc, $"{e.Label}: выключение", off.Ok && IsFlag(vOff, false),
                $"флаг {Bytes(vOff)}", $"ответ агента: {s.Describe(off)}; флаг {Bytes(vOff)} (ждали 12 байт с нечётным первым)");

            // 2. откат выключения: флаг как до операции
            if (off.OperationId is { } offOp)
            {
                var u = await s.Undo(offOp);
                var back = Read();
                c.Report.Check(Sc, $"{e.Label}: откат выключения", u.Ok && back.SameAs(start),
                    $"флаг {Bytes(back)}", $"ответ: {s.Describe(u)}; флаг {Bytes(back)}, до операции было {Bytes(start)}");
            }

            // 3. включить: 12 байт, первый чётный
            var on = Set(true);
            var vOn = Read();
            c.Report.Check(Sc, $"{e.Label}: включение", on.Ok && IsFlag(vOn, true),
                $"флаг {Bytes(vOn)}", $"ответ агента: {s.Describe(on)}; флаг {Bytes(vOn)} (ждали 12 байт с чётным первым)");

            // 4. выключить после включения и откатить: возврат к 02-значению
            var mid = Read();
            var off2 = Set(false);
            if (off2.OperationId is { } op2)
            {
                var u2 = await s.Undo(op2);
                var back2 = Read();
                c.Report.Check(Sc, $"{e.Label}: откат после включения", off2.Ok && u2.Ok && back2.SameAs(mid),
                    $"флаг {Bytes(back2)}", $"выкл: {s.Describe(off2)}; откат: {s.Describe(u2)}; флаг {Bytes(back2)}, ждали {Bytes(mid)}");
            }
            else c.Report.Fail(Sc, $"{e.Label}: откат после включения", "повторное выключение не дало операции: " + s.Describe(off2));
        }
        finally
        {
            e.Cleanup();
            Reg.Restore(e.ApprovedInUser, c.Sid, e.ApprovedKey, e.ApprovedName, orig);
            if (!keyExisted) Reg.DeleteTree(e.ApprovedInUser, c.Sid, e.ApprovedKey);
        }
    }
}
