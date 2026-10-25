using System.Diagnostics;
using Chistovik.Core.Ipc;
using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>
/// Сценарий 9. Удаление программ настоящим графом агента (AgentPorts.Windows, журнал SQLite) на настоящих установщиках,
/// которые хост кладёт в C:\Installers (--installers): *.msi ставится msiexec /qn, *.exe (Notepad++ NSIS) ключом /S.
/// Проверяется: после установки запись ARP есть; UninstallRequest по идентификатору arp:machine:...; ответ ok, шаг Done,
/// запись ARP и файлы программы исчезли; Undo отвечает not-revertible; повторное удаление и чужие идентификаторы отвергаются
/// и ничего не меняют. Установщики запускает сам стенд, не агент: агент только удаляет.
/// </summary>
static class S9Uninstall
{
    const string Sc = "S9 удаление программ";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static async Task Run(LiveContext c)
    {
        if (c.InstallersDir is null || !Directory.Exists(c.InstallersDir))
        {
            c.Report.Skip(Sc, "установщики", "папка с установщиками не передана (параметр --installers)");
            return;
        }

        using var session = Session.Open(c, "s9");

        await Negative(c, session);

        var msi = Directory.EnumerateFiles(c.InstallersDir, "*.msi").FirstOrDefault();
        if (msi is null) c.Report.Skip(Sc, "MSI", "в папке установщиков нет *.msi");
        else await Case(c, session, "MSI", msi, "msiexec.exe", $"/i \"{msi}\" /qn /norestart", "7-Zip", @"7-Zip\7z.exe");

        var exe = Directory.EnumerateFiles(c.InstallersDir, "*.exe").FirstOrDefault();
        if (exe is null) c.Report.Skip(Sc, "NSIS", "в папке установщиков нет *.exe");
        else await Case(c, session, "NSIS", exe, exe, "/S", "Notepad++", @"Notepad++\notepad++.exe");
    }

    static async Task Case(LiveContext c, Session s, string kind, string installer, string file, string args, string namePrefix, string probeFile)
    {
        var tag = $"{kind} ({Path.GetFileName(installer)})";
        var before = FindArp(namePrefix);
        if (before is not null)
        {
            c.Report.Skip(Sc, tag, $"«{before.Value.Name}» уже установлена в Sandbox, сценарий не трогает чужое");
            return;
        }

        var code = RunProcess(file, args, TimeSpan.FromMinutes(5));
        var arp = FindArp(namePrefix);
        var installed = code is 0 or 3010 && arp is not null;
        c.Report.Check(Sc, $"{tag}: тихая установка", installed, $"код {code}, запись {arp?.Key}", $"код {code}, запись ARP: {(arp is null ? "<нет>" : arp.Value.Key)}");
        if (!installed) return;

        var (key, view, name) = arp!.Value;
        var id = $"arp:machine:{(view == RegistryView.Registry64 ? "x64" : "x86")}:{key}";
        var probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), probeFile);
        var probe86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), probeFile);
        c.Report.Info(Sc, $"{tag}: что установлено", $"{name}; id {id}; файл {(File.Exists(probe) ? probe : File.Exists(probe86) ? probe86 : "<не найден>")}");

        var resp = await s.Send(new UninstallRequest { Id = Guid.NewGuid(), FindingId = id });
        var step = resp.Steps?.FirstOrDefault();
        c.Report.Check(Sc, $"{tag}: агент удалил программу", resp.Ok && resp.Code == ResponseCodes.Ok && step is { State: Chistovik.Core.Ports.StepState.Done },
            s.Describe(resp), s.Describe(resp));

        var gone = FindArp(namePrefix) is null;
        var filesGone = !File.Exists(probe) && !File.Exists(probe86);
        c.Report.Check(Sc, $"{tag}: запись ARP и файлы исчезли", gone && filesGone, "reg query пуст, файла нет",
            $"запись ARP: {(gone ? "нет" : "осталась")}; файл: {(filesGone ? "нет" : "остался")}");

        if (resp.OperationId is { } op)
        {
            var undo = await s.Undo(op);
            c.Report.Check(Sc, $"{tag}: откат отвергается", !undo.Ok && undo.Code == ResponseCodes.NotRevertible, s.Describe(undo), s.Describe(undo));
        }

        var again = await s.Send(new UninstallRequest { Id = Guid.NewGuid(), FindingId = id });
        c.Report.Check(Sc, $"{tag}: повторное удаление ничего не делает", !again.Ok || again.Code == ResponseCodes.NoChange, s.Describe(again), s.Describe(again));
    }

    /// <summary>Идентификаторы вне белого списка не доходят до запуска ничего.</summary>
    static async Task Negative(LiveContext c, Session s)
    {
        foreach (var bad in new[]
                 {
                     "arp:user:user:Atlas", "arp:machine:x64:..\\..\\Windows", "arp:machine:x64:", "program:Классическая:Atlas",
                     "arp:machine:x64:{00000000-0000-0000-0000-000000000000}",
                 })
        {
            AgentResponse r;
            try { r = await s.Send(new UninstallRequest { Id = Guid.NewGuid(), FindingId = bad }); }
            catch (ArgumentException e) { c.Report.Pass(Sc, $"отказ для «{bad}»", "запрос отвергнут при проверке: " + e.Message); continue; }
            c.Report.Check(Sc, $"отказ для «{bad}»", !r.Ok, s.Describe(r), s.Describe(r));
        }
    }

    static (string Key, RegistryView View, string Name)? FindArp(string namePrefix)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hive.OpenSubKey(UninstallKey);
            if (root is null) continue;
            foreach (var k in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(k);
                if (sub?.GetValue("DisplayName") is string n && n.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                    return (k, view, n);
            }
        }
        return null;
    }

    static int RunProcess(string file, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("установщик не запущен");
        if (p.WaitForExit(timeout)) return p.ExitCode;
        try { p.Kill(true); } catch (InvalidOperationException) { }
        return -1;
    }
}
