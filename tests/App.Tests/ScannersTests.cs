using System.IO;
using Xunit;
using Chistovik.App.Services;
using Chistovik.App.Services.Scanners;
using Chistovik.App.Services.Windows;
using Chistovik.Core.Catalog;
using Chistovik.Core.Findings;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace App.Tests;

public class RegistryOrphanScannerTests
{
    sealed class FakeRegistry : IRegistryReader, IRegistryDefaultValue
    {
        public readonly Dictionary<string, List<string>> Subs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Dictionary<string, object?>> Values = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase);

        static string K(RegistryHiveKind h, RegistryViewKind v, string p) => $"{h}|{v}|{p}";

        public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path)
            => Subs.TryGetValue(K(hive, view, path), out var l) ? l : [];

        public IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path)
            => Values.TryGetValue(K(hive, view, path), out var d) ? d : null;

        public string? ReadDefault(RegistryHiveKind hive, RegistryViewKind view, string path)
            => Defaults.TryGetValue(K(hive, view, path), out var s) ? s : null;

        const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";

        public void Arp(string key, Dictionary<string, object?> values)
        {
            var parent = K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + "Uninstall");
            if (!Subs.TryGetValue(parent, out var l)) Subs[parent] = l = [];
            l.Add(key);
            Values[K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + "Uninstall\\" + key)] = values;
        }

        public void AppPath(string exe, string? def)
        {
            var parent = K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + "App Paths");
            if (!Subs.TryGetValue(parent, out var l)) Subs[parent] = l = [];
            l.Add(exe);
            if (def is not null) Defaults[K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + "App Paths\\" + exe)] = def;
        }

        public void Run(string leaf, string name, object? value)
        {
            var p = K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + leaf);
            if (!Values.TryGetValue(p, out var d)) Values[p] = d = new Dictionary<string, object?>();
            d[name] = value;
        }

        public void Shared(string file)
        {
            var p = K(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, Cv + "SharedDLLs");
            if (!Values.TryGetValue(p, out var d)) Values[p] = d = new Dictionary<string, object?>();
            d[file] = 1;
        }
    }

    sealed class FakeFiles : IFileExists
    {
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<char> Drives = ['C'];

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Dirs.Contains(path);
        public bool DriveExists(string path) => path.Length >= 2 && path[1] == ':' && Drives.Contains(char.ToUpperInvariant(path[0]));
    }

    sealed class FakeMsi(int state) : IMsiState
    {
        public int QueryProductState(string productCode) => state;
    }

    static async Task<IReadOnlyList<Finding>> Scan(FakeRegistry r, FakeFiles f, int msi = -1)
        => await new RegistryOrphanScanner(r, f, new FakeMsi(msi), new CommandResolver(PolicyContext.Fixed)).ScanAsync();

    static Finding One(IReadOnlyList<Finding> list, string id) => Assert.Single(list, x => x.Id == id);

    // ---------- R1 ----------

    [Fact]
    public async Task R1_missing_dir_and_uninstaller_is_orphan()
    {
        var r = new FakeRegistry();
        r.Arp("OldTool_is1", new()
        {
            ["DisplayName"] = "Old Tool",
            ["InstallLocation"] = @"C:\Program Files\OldTool\",
            ["UninstallString"] = "\"C:\\Program Files\\OldTool\\unins000.exe\" /SILENT",
        });

        var f = One(await Scan(r, new FakeFiles()), "arp:machine:x64:OldTool_is1");

        Assert.Equal(FindingArea.Registry, f.Area);
        Assert.Equal(VerdictKind.Remove, f.Verdict.Kind);
        Assert.Equal(Reversibility.Undoable, f.Verdict.Reversibility);
        Assert.Contains(f.Verdict.Reasons, x => x.Code == ReasonCodes.EvidenceUninstallerMissing);
    }

    [Fact]
    public async Task R1_uninstaller_present_is_not_reported()
    {
        var r = new FakeRegistry();
        r.Arp("Live_is1", new()
        {
            ["DisplayName"] = "Live",
            ["InstallLocation"] = @"C:\Program Files\Live\",
            ["UninstallString"] = "\"C:\\Program Files\\Live\\unins000.exe\"",
        });
        var files = new FakeFiles();
        files.Files.Add(@"C:\Program Files\Live\unins000.exe");

        Assert.Empty(await Scan(r, files));
    }

    [Fact]
    public async Task R1_install_dir_present_is_not_reported()
    {
        var r = new FakeRegistry();
        r.Arp("Live_is1", new()
        {
            ["DisplayName"] = "Live",
            ["InstallLocation"] = @"C:\Program Files\Live\",
            ["UninstallString"] = "\"C:\\Program Files\\Live\\unins000.exe\"",
        });
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Program Files\Live\");

        Assert.Empty(await Scan(r, files));
    }

    [Fact]
    public async Task R1_missing_drive_gives_unknown_not_remove()
    {
        var r = new FakeRegistry();
        r.Arp("Game_is1", new()
        {
            ["DisplayName"] = "Game on D",
            ["UninstallString"] = "\"D:\\Games\\Game\\unins000.exe\"",
        });

        var f = One(await Scan(r, new FakeFiles()), "arp:machine:x64:Game_is1");

        Assert.Equal(VerdictKind.Unknown, f.Verdict.Kind);
        Assert.Contains(f.Verdict.Reasons, x => x.Code == ReasonCodes.DriveMissing);
    }

    [Fact]
    public async Task R1_system_component_is_protected()
    {
        var r = new FakeRegistry();
        r.Arp("Sys_is1", new()
        {
            ["DisplayName"] = "Sys",
            ["InstallLocation"] = @"C:\Program Files\Sys\",
            ["UninstallString"] = "\"C:\\Program Files\\Sys\\unins000.exe\"",
            ["SystemComponent"] = 1,
        });

        Assert.Equal(VerdictKind.Protected, One(await Scan(r, new FakeFiles()), "arp:machine:x64:Sys_is1").Verdict.Kind);
    }

    [Fact]
    public async Task R1_msi_product_state_decides()
    {
        const string guid = "{12345678-1234-1234-1234-123456789ABC}";
        RegistryOrphanScannerTests.FakeRegistry Reg()
        {
            var r = new FakeRegistry();
            r.Arp(guid, new()
            {
                ["DisplayName"] = "Msi App",
                ["InstallLocation"] = @"C:\Program Files\MsiApp\",
                ["WindowsInstaller"] = 1,
                ["UninstallString"] = "MsiExec.exe /X" + guid,
            });
            return r;
        }

        // Windows Installer не знает продукт: сирота.
        var gone = One(await Scan(Reg(), new FakeFiles(), msi: -1), "arp:machine:x64:" + guid);
        Assert.Equal(VerdictKind.Remove, gone.Verdict.Kind);
        Assert.Contains(gone.Verdict.Reasons, x => x.Code == ReasonCodes.EvidenceMsiUnknown);

        // Установлен (5): не трогаем.
        Assert.Empty(await Scan(Reg(), new FakeFiles(), msi: 5));
    }

    // ---------- R2 ----------

    [Fact]
    public async Task R2_app_path_to_missing_file_is_orphan_existing_is_not()
    {
        var r = new FakeRegistry();
        r.AppPath("gone.exe", @"C:\Tools\gone.exe");
        r.AppPath("here.exe", @"C:\Tools\here.exe");
        var files = new FakeFiles();
        files.Files.Add(@"C:\Tools\here.exe");

        var list = await Scan(r, files);

        Assert.Equal(VerdictKind.Remove, One(list, "apppath:machine:x64:gone.exe").Verdict.Kind);
        Assert.DoesNotContain(list, x => x.Id == "apppath:machine:x64:here.exe");
    }

    [Fact]
    public async Task R2_missing_drive_is_unknown()
    {
        var r = new FakeRegistry();
        r.AppPath("far.exe", @"E:\Tools\far.exe");

        Assert.Equal(VerdictKind.Unknown, One(await Scan(r, new FakeFiles()), "apppath:machine:x64:far.exe").Verdict.Kind);
    }

    // ---------- R3 ----------

    [Fact]
    public async Task R3_run_entry_with_missing_file_is_orphan_and_id_matches_startup_format()
    {
        var r = new FakeRegistry();
        r.Run("Run", "OldUpdater", "\"C:\\Apps\\Old\\updater.exe\" --silent");
        r.Run("Run", "Alive", "C:\\Apps\\Alive\\a.exe");
        var files = new FakeFiles();
        files.Files.Add(@"C:\Apps\Alive\a.exe");

        var list = await Scan(r, files);

        var f = One(list, "run:hklm64:OldUpdater");
        Assert.Equal(VerdictKind.Remove, f.Verdict.Kind);
        Assert.True(StartupEntryId.TryParse(f.Id, out _, out _));
        Assert.DoesNotContain(list, x => x.Id == "run:hklm64:Alive");
    }

    [Fact]
    public async Task R3_runonce_entry_with_missing_file_is_orphan()
    {
        var r = new FakeRegistry();
        r.Run("RunOnce", "Setup", "\"C:\\Temp\\setup.exe\"");

        Assert.Equal(VerdictKind.Remove, One(await Scan(r, new FakeFiles()), "runonce:hklm64:Setup").Verdict.Kind);
    }

    [Fact]
    public async Task R3_missing_drive_is_unknown()
    {
        var r = new FakeRegistry();
        r.Run("Run", "Usb", "\"F:\\portable\\p.exe\"");

        Assert.Equal(VerdictKind.Unknown, One(await Scan(r, new FakeFiles()), "run:hklm64:Usb").Verdict.Kind);
    }

    // ---------- R4 ----------

    [Fact]
    public async Task R4_shared_dll_missing_file_is_orphan_present_or_network_is_not_removed()
    {
        var r = new FakeRegistry();
        r.Shared(@"C:\Program Files\Common\gone.dll");
        r.Shared(@"C:\Program Files\Common\here.dll");
        r.Shared(@"\\server\share\net.dll");
        r.Shared(@"D:\Off\off.dll");
        var files = new FakeFiles();
        files.Files.Add(@"C:\Program Files\Common\here.dll");

        var list = await Scan(r, files);

        Assert.Equal(3, list.Count);
        var gone = Assert.Single(list, x => x.Verdict.Kind == VerdictKind.Remove);
        Assert.StartsWith("shareddll:x64:", gone.Id);
        Assert.Equal("gone.dll", gone.Title);
        Assert.Equal(2, list.Count(x => x.Verdict.Kind == VerdictKind.Unknown));
    }

    [Fact]
    public async Task Every_finding_has_registry_area_and_never_remove_without_evidence()
    {
        var r = new FakeRegistry();
        r.AppPath("a.exe", @"C:\x\a.exe");
        r.Run("Run", "n", "C:\\x\\b.exe");
        r.Shared(@"C:\x\c.dll");

        var list = await Scan(r, new FakeFiles());

        Assert.All(list, f => Assert.Equal(FindingArea.Registry, f.Area));
        Assert.All(list.Where(f => f.Verdict.Kind == VerdictKind.Remove),
            f => Assert.Contains(f.Verdict.Reasons, x => x.Code == ReasonCodes.EvidenceFileMissing));
    }
}

public class DiskCleanupScannerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "chistovik-scan-" + Guid.NewGuid().ToString("N"));

    public DiskCleanupScannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    sealed class FakeProcesses(bool running) : IProcessList
    {
        public bool IsRunning(string imageName, string userSid) => running;
    }

    const string Json = """
        { "categories": [
          { "id":"cache-a","title":"Кэш A","description":"d","scope":"user","roots":[{"base":"userlocal","sub":"CacheA"}],
            "minAgeHours":0,"mechanism":"files","risk":"low","defaultOn":true,"restart":"none" },
          { "id":"cache-b","title":"Кэш B","description":"d","scope":"user","roots":[{"base":"userlocal","sub":"CacheB"}],
            "minAgeHours":0,"mechanism":"files","risk":"low","defaultOn":true,"skipIfRunning":"browser","restart":"none" },
          { "id":"sys-x","title":"Система X","description":"d","scope":"machine","roots":[{"base":"windir","sub":"Temp"}],
            "minAgeHours":24,"mechanism":"files","risk":"low","defaultOn":true,"restart":"none" },
          { "id":"bin","title":"Корзина","description":"d","scope":"user","roots":[],
            "minAgeHours":0,"mechanism":"recyclebin","risk":"medium","defaultOn":false,"restart":"none" },
          { "id":"hand","title":"Обработчик","description":"d","scope":"machine","roots":[],
            "minAgeHours":0,"mechanism":"handler","risk":"medium","defaultOn":false,"restart":"none" }
        ] }
        """;

    IReadOnlyList<Finding> Scan(bool browserRunning = false)
    {
        var scanner = new DiskCleanupScanner(CleanupCatalog.Parse(Json), new FileSystemDiskMeasure(_dir, _dir), new FakeProcesses(browserRunning), "S-1-5-21-1");
        return scanner.ScanAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Measures_user_category_in_temp_directory()
    {
        var a = Path.Combine(_dir, "CacheA");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        File.WriteAllBytes(Path.Combine(a, "one.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(a, "sub", "two.bin"), new byte[500]);

        var f = Assert.Single(Scan(), x => x.Id == "cleanup:cache-a");

        var m = new FileSystemDiskMeasure(_dir, _dir).Measure(CleanupCatalog.Parse(Json).Find("cache-a")!)!;
        Assert.Equal(1500, m.Bytes);
        Assert.Equal(2, m.Files);
        Assert.True(m.Exists);
        Assert.Equal(FindingArea.Disk, f.Area);
        Assert.Equal(VerdictKind.Remove, f.Verdict.Kind);
        Assert.Equal(Reversibility.Irreversible, f.Verdict.Reversibility);
        Assert.Equal(Confidence.L2, f.Verdict.Confidence);
        Assert.Contains("файлов: 2", f.Detail);
    }

    [Fact]
    public void Missing_folder_means_nothing_to_clean()
    {
        var f = Assert.Single(Scan(), x => x.Id == "cleanup:cache-a");

        Assert.Contains("очищать нечего", f.Detail);
    }

    [Fact]
    public void Machine_recyclebin_and_handler_categories_wait_for_agent()
    {
        var list = Scan();

        Assert.Contains("уточнит агент", Assert.Single(list, x => x.Id == "cleanup:sys-x").Detail);
        Assert.Contains("уточнит агент", Assert.Single(list, x => x.Id == "cleanup:bin").Detail);
        var hand = Assert.Single(list, x => x.Id == "cleanup:hand");
        Assert.Contains("Размер неизвестен", hand.Detail);
        Assert.Contains(hand.Verdict.Reasons, r => r.Code == ReasonCodes.SizeNeedsAgent);
    }

    [Fact]
    public void Recycle_bin_is_optional_and_everything_is_irreversible()
    {
        var list = Scan();

        Assert.Equal(VerdictKind.Optional, Assert.Single(list, x => x.Id == "cleanup:bin").Verdict.Kind);
        Assert.Equal(VerdictKind.Optional, Assert.Single(list, x => x.Id == "cleanup:hand").Verdict.Kind);
        Assert.Equal(5, list.Count);
        Assert.All(list, f => Assert.Equal(Reversibility.Irreversible, f.Verdict.Reversibility));
    }

    [Fact]
    public void Running_browser_adds_reason()
    {
        Assert.DoesNotContain(Scan(false).Single(x => x.Id == "cleanup:cache-b").Verdict.Reasons, r => r.Code == ReasonCodes.BrowserRunning);

        var f = Scan(true).Single(x => x.Id == "cleanup:cache-b");

        Assert.Contains(f.Verdict.Reasons, r => r.Code == ReasonCodes.BrowserRunning);
        Assert.Contains("Браузер запущен", f.Detail);
    }

    [Fact]
    public void Measure_skips_machine_handler_and_bin()
    {
        var cat = CleanupCatalog.Parse(Json);
        var measure = new FileSystemDiskMeasure(_dir, _dir);

        Assert.Null(measure.Measure(cat.Find("sys-x")!));
        Assert.Null(measure.Measure(cat.Find("bin")!));
        Assert.Null(measure.Measure(cat.Find("hand")!));
    }
}

public class CleanupScannerAggregationTests
{
    sealed class Stub(FindingArea area, params Finding[] findings) : IFindingScanner
    {
        public FindingArea Area => area;
        public Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    static Finding F(string id, FindingArea area) => FindingFactory.Make(id, area, id, "d", VerdictKind.Optional,
        [ReasonCodes.Make(ReasonCodes.SizeNeedsAgent)], Risk.Low, Reversibility.Irreversible, Confidence.L1,
        new Explanation("a", "b", "c", "d"), ChangeClass.Modifiable);

    [Fact]
    public async Task Aggregates_findings_of_all_scanners()
    {
        var scanner = new CleanupScanner([new Stub(FindingArea.Disk, F("cleanup:x", FindingArea.Disk)),
            new Stub(FindingArea.Registry, F("run:hklm64:a", FindingArea.Registry))]);

        var list = await scanner.ScanAsync();

        Assert.Contains(list, f => f.Id == "cleanup:x");
        Assert.Contains(list, f => f.Id == "run:hklm64:a");
    }

    [Fact]
    public void Area_descriptions_exist_for_every_area()
    {
        // Описание есть у новых областей; у прежних его нет намеренно.
        Assert.False(string.IsNullOrWhiteSpace(Chistovik.App.Models.ReasonTexts.AreaDescription(FindingArea.Disk)));
        Assert.False(string.IsNullOrWhiteSpace(Chistovik.App.Models.ReasonTexts.AreaDescription(FindingArea.Registry)));
        Assert.Equal("", Chistovik.App.Models.ReasonTexts.AreaDescription(FindingArea.Programs));
        Assert.Contains("2563254", Chistovik.App.Models.ReasonTexts.RegistryAreaDescription);
    }

    [Fact]
    public void Every_reason_code_with_finding_has_text()
    {
        foreach (var code in new[]
                 {
                     ReasonCodes.EvidenceUninstallerMissing, ReasonCodes.EvidenceFileMissing, ReasonCodes.EvidenceMsiUnknown,
                     ReasonCodes.CommandUnresolved, ReasonCodes.DriveMissing, ReasonCodes.SizeNeedsAgent, ReasonCodes.BrowserRunning,
                     ReasonCodes.CacheRegenerable,
                 })
            Assert.False(string.IsNullOrWhiteSpace(ReasonCodes.Make(code, "x").Text), code);
    }
}
