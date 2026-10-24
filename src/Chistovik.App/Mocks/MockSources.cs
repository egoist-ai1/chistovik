using Chistovik.App.Models;
using Chistovik.App.Services;

namespace Chistovik.App.Mocks;

public sealed class MockProgramsSource : IProgramsSource
{
    private static readonly string[] Names =
    {
        "Notepad++", "7-Zip", "VLC media player", "Git", "Visual Studio Code", "Python", "Node.js", "Docker Desktop",
        "Steam", "Discord", "Telegram Desktop", "OBS Studio", "Blender", "GIMP", "Inkscape", "Audacity", "HandBrake",
        "qBittorrent", "WinRAR", "Foxit PDF Reader", "Mozilla Firefox", "Google Chrome", "Opera", "Zoom", "Slack",
        "Postman", "DBeaver", "PuTTY", "WinSCP", "FileZilla", "CPU-Z", "HWiNFO", "MSI Afterburner", "Everything",
        "Rufus", "Paint.NET", "IrfanView", "Krita", "Figma", "Unity Hub", "Epic Games Launcher", "GOG Galaxy"
    };

    private static readonly string[] Publishers =
    {
        "Open Source", "Microsoft", "Google", "JetBrains", "Valve", "Mozilla", "Adobe", "Canonical", "Oracle", "NVIDIA"
    };

    private static readonly string[] Sources = { "Классическая", "MSI", "Магазин", "Портативная" };

    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync()
    {
        var rnd = new Random(42);
        var list = new List<ProgramInfo>(500);
        for (var i = 0; i < 500; i++)
        {
            var baseName = Names[i % Names.Length];
            var name = i < Names.Length ? baseName : $"{baseName} {2 + i / Names.Length}";
            list.Add(new ProgramInfo(
                name,
                Publishers[rnd.Next(Publishers.Length)],
                $"{rnd.Next(1, 25)}.{rnd.Next(0, 20)}.{rnd.Next(0, 99)}",
                (long)rnd.Next(2, 4000) * 1024 * 1024 / rnd.Next(1, 8),
                new DateTime(2022, 1, 1).AddDays(rnd.Next(0, 1400)),
                Sources[rnd.Next(Sources.Length)]));
        }
        return Task.FromResult<IReadOnlyList<ProgramInfo>>(list);
    }
}

public sealed class MockStartupSource : IStartupSource
{
    public Task<IReadOnlyList<StartupInfo>> GetStartupAsync()
    {
        IReadOnlyList<StartupInfo> list = new List<StartupInfo>
        {
            new("Microsoft OneDrive", @"C:\Users\user\AppData\Local\Microsoft\OneDrive\OneDrive.exe /background", "Microsoft", true, RiskLevel.Known, 820),
            new("Discord", @"C:\Users\user\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Discord Inc.", true, RiskLevel.Known, 640),
            new("Steam Client Bootstrapper", @"C:\Program Files (x86)\Steam\steam.exe -silent", "Valve", true, RiskLevel.Known, 710),
            new("Telegram Desktop", @"C:\Users\user\AppData\Roaming\Telegram Desktop\Telegram.exe -autostart", "Telegram FZ-LLC", true, RiskLevel.Known, 380),
            new("NVIDIA Overlay", @"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", "NVIDIA", true, RiskLevel.Known, 450),
            new("UpdaterHelper", @"C:\Users\user\AppData\Local\Temp\upd\helper.exe /s", "Не указан", true, RiskLevel.Suspicious, 290),
            new("Adobe Updater", @"C:\Program Files\Common Files\Adobe\OOBE\PDApp\UWA\UpdaterStartupUtility.exe", "Adobe", true, RiskLevel.Review, 510),
            new("Spotify", @"C:\Users\user\AppData\Roaming\Spotify\Spotify.exe --minimized", "Spotify AB", false, RiskLevel.Known, 560),
            new("Zoom", @"C:\Users\user\AppData\Roaming\Zoom\bin\Zoom.exe --autostart", "Zoom", false, RiskLevel.Review, 340),
            new("Утилита без подписи", @"D:\Tools\mon\agent.exe", "Не указан", true, RiskLevel.Suspicious, 230),
            new("Everything", @"C:\Program Files\Everything\Everything.exe -startup", "voidtools", true, RiskLevel.Known, 120),
            new("Logitech Options", @"C:\Program Files\Logitech\LogiOptions\LogiOptions.exe /noui", "Logitech", true, RiskLevel.Review, 480),
        };
        return Task.FromResult(list);
    }
}

public sealed class MockJournalSource : IJournalSource
{
    private static IReadOnlyList<JournalStep> Steps(int done, string name) => new[]
    {
        new JournalStep("Точка восстановления", "Создана перед операцией", done > 0),
        new JournalStep("Штатный деинсталлятор", $"Запуск удаления «{name}»", done > 1),
        new JournalStep("Поиск остатков", "Файлы, ключи реестра, задачи планировщика", done > 2),
        new JournalStep("Перенос в карантин", "Остатки перенесены, не удалены", done > 3),
        new JournalStep("Проверка", "Система загружается, ярлыки и ассоциации целы", done > 4),
    };

    public Task<IReadOnlyList<JournalOperation>> GetOperationsAsync()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0);
        IReadOnlyList<JournalOperation> list = new List<JournalOperation>
        {
            new("op-0412", "Удаление: Adobe Reader", now.AddHours(-2), "Завершено", true, Steps(5, "Adobe Reader")),
            new("op-0411", "Удаление: Opera 112", now.AddDays(-1), "Завершено", true, Steps(5, "Opera 112")),
            new("op-0410", "Удаление: Zoom", now.AddDays(-2), "Частично", true, Steps(3, "Zoom")),
            new("op-0409", "Удаление: CPU-Z", now.AddDays(-4), "Откачено", false, Steps(5, "CPU-Z")),
            new("op-0408", "Удаление: Unity Hub", now.AddDays(-6), "Завершено", true, Steps(5, "Unity Hub")),
            new("op-0407", "Удаление: WinRAR", now.AddDays(-9), "Завершено", true, Steps(5, "WinRAR")),
            new("op-0406", "Удаление: Postman", now.AddDays(-12), "Прервано", false, Steps(2, "Postman")),
        };
        return Task.FromResult(list);
    }
}
