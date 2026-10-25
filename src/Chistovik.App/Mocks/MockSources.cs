using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Agent;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.App.Mocks;

// ДЕМОНСТРАЦИОННЫЕ данные: используются только в --selftest и --demo (предпросмотр дизайна).
// В обычном запуске их нет, на экране в демо-режиме есть пометка. Данные не отражают этот компьютер.

public sealed class MockProgramsSource : IProgramsSource
{
    private static readonly string[] Names =
    {
        "Notepad++", "7-Zip", "VLC media player", "Git", "Visual Studio Code", "Python", "Node.js", "Docker Desktop",
        "Steam", "Discord", "Telegram Desktop", "OBS Studio", "Blender", "GIMP", "Inkscape", "Audacity", "HandBrake",
        "qBittorrent", "WinRAR", "Foxit PDF Reader", "Mozilla Firefox", "Google Chrome", "Opera", "Zoom", "Slack",
    };

    private static readonly string[] Publishers = { "Open Source", "Microsoft", "Google", "JetBrains", "Valve", "Mozilla" };
    private static readonly string[] Sources = { "Классическая", "32-разрядная", "Пользовательская", "Магазин" };

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync()
    {
        var rnd = new Random(42);
        var list = new List<ProgramInfo>(120);
        for (var i = 0; i < 120; i++)
        {
            var baseName = Names[i % Names.Length];
            var name = i < Names.Length ? baseName : $"{baseName} {2 + i / Names.Length}";
            list.Add(new ProgramInfo(name, Publishers[rnd.Next(Publishers.Length)], $"{rnd.Next(1, 25)}.{rnd.Next(0, 20)}",
                (long)rnd.Next(2, 4000) * 1024 * 1024 / rnd.Next(1, 8), new DateTime(2022, 1, 1).AddDays(rnd.Next(0, 1400)),
                Sources[rnd.Next(Sources.Length)]));
        }
        return Task.FromResult<IReadOnlyList<ProgramInfo>>(list);
    }
}

public sealed class MockStartupSource : IStartupSource
{
    public Task<IReadOnlyList<StartupInfo>> GetStartupAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StartupInfo> list = new List<StartupInfo>
        {
            new("run:hku:Discord", "Discord", @"C:\Users\demo\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Реестр, текущий пользователь", true, true, null),
            new("run:hklm64:Steam", "Steam", @"C:\Program Files (x86)\Steam\steam.exe -silent", "Реестр, все пользователи", true, true, null),
            new("folder:user:Telegram.lnk", "Telegram.lnk", @"C:\Users\demo\...\Startup\Telegram.lnk", "Папка автозагрузки, текущий пользователь", false, true, null),
            new("run:hku:Skype", "Skype", @"C:\Program Files\Skype\Skype.exe /minimized", "Реестр, текущий пользователь", true, true, null,
                Changeability.Modifiable, "", @"C:\Program Files\Skype\Skype.exe", true, null, null),
            new("run:hklm64:SecurityHealth", "SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe", "Реестр, все пользователи", true, true, null,
                Changeability.Modifiable, "", @"C:\Windows\System32\SecurityHealthSystray.exe", false, true, true),
            new("", "SetupHelper", @"C:\Setup\helper.exe", "Однократный запуск, все пользователи", true, false,
                ReasonTexts.Explain(ReasonTexts.StartupRunOnce), Changeability.ReadOnly, ReasonTexts.StartupRunOnce),
        };
        return Task.FromResult(list);
    }
}

public sealed class MockJournalSource : IJournalSource
{
    private static readonly Guid A = new("7f3c9a2e-5b14-4d68-9e07-a1c2d3e4f5b6");
    private static readonly Guid B = new("c84e1d06-92a7-4b3f-8d55-0e6f7a8b9c10");

    public Task<IReadOnlyList<JournalOperation>> GetOperationsAsync()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0);
        IReadOnlyList<JournalOperation> list = new List<JournalOperation>
        {
            new(A, OperationKind.Mode, "Режим: Работа (демо)", now.AddHours(-2), OperationState.Done, "Завершено", true, null),
            new(B, OperationKind.Tweaks, "Твики: ext, hidden (демо)", now.AddDays(-1), OperationState.Partial, "Частично", true, null),
        };
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<JournalStep>> GetStepsAsync(Guid operationId)
    {
        IReadOnlyList<JournalStep> list = new List<JournalStep>
        {
            new(1, "Значение реестра", "HKU\\...\\Advanced\\HideFileExt", StepState.Done, "Выполнено"),
            new(2, "Значение реестра", "HKU\\...\\Advanced\\Hidden", StepState.Skipped, "Пропущено"),
        };
        return Task.FromResult(list);
    }
}

public sealed class MockServiceSource : IServiceSource
{
    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ServiceInfo> list = new List<ServiceInfo>
        {
            new("DemoUpdater", "Демо-служба обновления программы", StartMode.Automatic, Changeability.Modifiable, "Служба установленной программы", @"C:\Program Files\Demo\updater.exe"),
            new("DiagTrack", "Функциональность для подключённых пользователей и телеметрия (демо)", StartMode.Automatic, Changeability.Modifiable, "", @"C:\Windows\System32\svchost.exe -k utcsvc -p"),
            new("WSearch", "Windows Search (демо)", StartMode.Automatic, Changeability.Modifiable, "", @"C:\Windows\System32\SearchIndexer.exe /Embedding"),
            new("WinDefend", "Защитник Windows (демо)", StartMode.Automatic, Changeability.Forbidden, ReasonTexts.Explain("service-hardlist"), @"C:\Program Files\Windows Defender\MsMpEng.exe", "service-hardlist"),
        };
        return Task.FromResult(list);
    }
}

public sealed class MockTaskSource : ITaskSource
{
    public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TaskInfo> list = new List<TaskInfo>
        {
            new(@"\DemoUpdateTask", "DemoUpdateTask", true, Changeability.ReadOnly, ReasonTexts.Explain("task-exec-outside-roots"), @"C:\Program Files\Demo\update.exe /check", "task-exec-outside-roots"),
            new(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Microsoft Compatibility Appraiser", true, Changeability.Modifiable, "", "Демо"),
            new(@"\Microsoft\Windows\Demo\Task", "Task", true, Changeability.Forbidden, ReasonTexts.Explain("task-protected-tree"), "Демо", "task-protected-tree"),
        };
        return Task.FromResult(list);
    }
}

public sealed class MockTweakStateSource : ITweakStateSource
{
    public Task<IReadOnlyDictionary<string, TweakState>> ReadAsync(IReadOnlyList<TweakEntry> tweaks)
    {
        var d = tweaks.Select((t, i) => (t.Id, State: i % 3 == 0 ? TweakState.On : i % 3 == 1 ? TweakState.Off : TweakState.Unknown))
            .ToDictionary(x => x.Id, x => x.State);
        return Task.FromResult<IReadOnlyDictionary<string, TweakState>>(d);
    }
}

public sealed class MockPowerSource : IPowerSource
{
    public Task<PowerSnapshot> ReadAsync()
    {
        var cat = PowerCatalog.Shared;
        var values = cat.Settings.ToDictionary(s => s.Id, s => (s.Min, s.Max));
        return Task.FromResult(new PowerSnapshot(cat.FindScheme("bal")?.Guid, values, null));
    }
}

/// <summary>Демо-находки очистки диска: несколько категорий с размерами, одна без размера, одна «браузер запущен».</summary>
public sealed class MockDiskScanner : Services.Scanners.IFindingScanner
{
    public Core.Findings.FindingArea Area => Core.Findings.FindingArea.Disk;

    public Task<IReadOnlyList<Core.Findings.Finding>> ScanAsync(CancellationToken ct = default)
    {
        static Core.Findings.Finding F(string id, string title, string detail, Core.Findings.VerdictKind kind, string code, string? evidence,
            Core.Findings.Confidence conf, string what)
            => Services.Scanners.FindingFactory.Make("cleanup:" + id, Core.Findings.FindingArea.Disk, title, detail, kind,
                [Core.Findings.ReasonCodes.Make(code, evidence)], Core.Findings.Risk.Low, Core.Findings.Reversibility.Irreversible, conf,
                new Core.Findings.Explanation(what, "Нужны только на время работы: программы создают их заново.",
                    "Файлы пропадут, программы пересоздадут кэш при необходимости.",
                    "Вернуть нельзя: файлы удаляются без копии."),
                Core.Policy.ChangeClass.Modifiable);

        IReadOnlyList<Core.Findings.Finding> list =
        [
            F("temp-user", "Временные файлы пользователя", "Около 1,2 ГБ, файлов: 4 310", Core.Findings.VerdictKind.Remove,
                Core.Findings.ReasonCodes.CacheRegenerable, null, Core.Findings.Confidence.L2, "Временные файлы программ в вашей папке Temp."),
            F("thumbcache", "Кэш эскизов", "Около 38 МБ, файлов: 12", Core.Findings.VerdictKind.Remove,
                Core.Findings.ReasonCodes.CacheRegenerable, null, Core.Findings.Confidence.L2, "Кэш миниатюр картинок в Проводнике."),
            F("temp-system", "Временные файлы Windows", "Размер уточнит агент", Core.Findings.VerdictKind.Remove,
                Core.Findings.ReasonCodes.SizeNeedsAgent, null, Core.Findings.Confidence.L1, "Временные файлы системы."),
            F("browser-chrome", "Кэш Google Chrome", "Около 640 МБ. Браузер запущен, категория будет пропущена", Core.Findings.VerdictKind.Optional,
                Core.Findings.ReasonCodes.BrowserRunning, "chrome.exe", Core.Findings.Confidence.L2, "Сохранённые страницы и картинки браузера."),
            F("recycle-bin", "Корзина", "Размер уточнит агент", Core.Findings.VerdictKind.Optional,
                Core.Findings.ReasonCodes.SizeNeedsAgent, null, Core.Findings.Confidence.L1, "Удалённые файлы, которые ещё можно вернуть."),
        ];
        return Task.FromResult(list);
    }
}

/// <summary>Демо-находки реестра: сирота, сомнительная запись и защищённая.</summary>
public sealed class MockRegistryScanner : Services.Scanners.IFindingScanner
{
    public Core.Findings.FindingArea Area => Core.Findings.FindingArea.Registry;

    public Task<IReadOnlyList<Core.Findings.Finding>> ScanAsync(CancellationToken ct = default)
    {
        static Core.Findings.Finding F(string id, string title, string detail, Core.Findings.VerdictKind kind, string code, string evidence,
            Core.Policy.ChangeClass cls)
            => Services.Scanners.FindingFactory.Make(id, Core.Findings.FindingArea.Registry, title, detail, kind,
                [Core.Findings.ReasonCodes.Make(code, evidence)], Core.Findings.Risk.Low,
                kind == Core.Findings.VerdictKind.Remove ? Core.Findings.Reversibility.Undoable : Core.Findings.Reversibility.NotAvailable,
                Core.Findings.Confidence.L2,
                new Core.Findings.Explanation("Запись реестра без файлов.", "Нужна, только пока файл на месте.",
                    "Запись пропадёт, на работу Windows это не влияет.", "Копия сохраняется в журнале: «Отменить»."), cls);

        IReadOnlyList<Core.Findings.Finding> list =
        [
            F("arp:machine:x64:OldTool_is1", "Old Tool 2.1", "Запись в списке программ: самой программы на диске нет",
                Core.Findings.VerdictKind.Remove, Core.Findings.ReasonCodes.EvidenceUninstallerMissing, @"D:\Old Tool\unins000.exe", Core.Policy.ChangeClass.Modifiable),
            F("run:hku:UpdaterX", "UpdaterX", @"Запускает C:\Users\demo\AppData\Local\UpdaterX\upd.exe, а файла там нет",
                Core.Findings.VerdictKind.Remove, Core.Findings.ReasonCodes.EvidenceFileMissing, @"C:\Users\demo\AppData\Local\UpdaterX\upd.exe", Core.Policy.ChangeClass.Modifiable),
            F("apppath:machine:x64:portable.exe", "portable.exe", @"Диск с программой сейчас недоступен",
                Core.Findings.VerdictKind.Unknown, Core.Findings.ReasonCodes.DriveMissing, @"Z:\Portable\portable.exe", Core.Policy.ChangeClass.ReadOnly),
        ];
        return Task.FromResult(list);
    }
}

/// <summary>Демо-эффекты: ничего не делают.</summary>
public sealed class DemoShellEffects : IShellEffects
{
    public void Apply(string effect) { }
    public void RestartExplorer() { }
}

/// <summary>Демо-агент: отвечает «готово» без запуска процесса и без изменений в системе.</summary>
public sealed class DemoAgentClient : IAgentClient
{
    public AgentAccess Access => AgentAccess.Connected;
    public bool IsElevated => true;
    public event EventHandler? AccessChanged { add { } remove { } }
    public void AllowElevationRetry() { }

    public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default)
    {
        // Измерение очистки и пробная проверка реестра отвечают так же, как настоящий агент, чтобы диалоги подтверждения были видны.
        if (request is CleanCategoriesRequest { MeasureOnly: true } measure)
            return Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.Ok, null, null, null, null,
                measure.Categories.Select(c => new FactEntry($"cleanup:user:-:{c}", "{\"v\":1,\"bytes\":52428800,\"files\":120}")).ToList()));
        if (request is ApplyFindingsRequest { DryRun: true } dry)
            return Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.DryRun, null, null, null, null,
                dry.Items.Select((i, n) => new FactEntry($"regkey:hklm:-:x64:demo{n}",
                    "{\"v\":1,\"display\":\"" + i.FindingId.Replace("\\", "").Replace("\"", "") + "\",\"subkeys\":3,\"revertible\":true,\"rule\":null}")).ToList()));
        return Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.Ok, "Демонстрация: ничего не изменено", Guid.NewGuid()));
    }
}
