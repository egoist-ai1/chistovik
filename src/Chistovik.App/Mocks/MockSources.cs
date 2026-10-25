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
    private static readonly Guid A = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = new("22222222-2222-2222-2222-222222222222");

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
        => Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.Ok, "Демонстрация: ничего не изменено", Guid.NewGuid()));
}
