using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;
using Chistovik.Core.Inventory;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

/// <summary>Настоящие значки в списках: разбор источника, кэш и лимиты службы, подстановка в строки, один живой вызов Win32.</summary>
public class IconTests
{
    static readonly string Sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

    static ImageSource Pixel()
    {
        var b = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        b.Freeze();
        return b;
    }

    // ----- разбор источника -----

    [Fact]
    public void Parse_quoted_path_with_comma_index()
    {
        var r = IconSources.Parse("\"C:\\Program Files\\App\\app.exe\",2");
        Assert.NotNull(r);
        Assert.Equal(@"C:\Program Files\App\app.exe", r!.Path);
        Assert.Equal(2, r.Index);
    }

    [Fact]
    public void Parse_negative_index_without_quotes()
    {
        var r = IconSources.Parse(@"C:\Windows\System32\imageres.dll,-109");
        Assert.Equal(@"C:\Windows\System32\imageres.dll", r!.Path, ignoreCase: true);
        Assert.Equal(-109, r.Index);
    }

    [Fact]
    public void Parse_command_line_cuts_arguments()
    {
        Assert.Equal(@"C:\Program Files\A B\x.exe", IconSources.Parse("\"C:\\Program Files\\A B\\x.exe\" --silent /s")!.Path);
        var plain = IconSources.Parse(@"C:\Program Files\A B\x.exe /minimized");
        Assert.Equal(@"C:\Program Files\A B\x.exe", plain!.Path);
        Assert.Null(plain.Index);
    }

    [Fact]
    public void Parse_expands_environment_variables_and_systemroot_prefix()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot")!;
        Assert.Equal(Path.Combine(root, "system32", "shell32.dll"), IconSources.Parse(@"%SystemRoot%\system32\shell32.dll,3")!.Path, ignoreCase: true);
        Assert.Equal(Path.Combine(root, "System32", "svchost.exe"), IconSources.Parse(@"\SystemRoot\System32\svchost.exe -k x")!.Path, ignoreCase: true);
    }

    [Fact]
    public void Parse_rundll32_takes_the_dll_not_the_host()
    {
        var r = IconSources.Parse("rundll32.exe shell32.dll,Control_RunDLL x.cpl");
        Assert.Equal(Path.Combine(Sys, "shell32.dll"), r!.Path, ignoreCase: true);
        Assert.Null(r.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    [InlineData("%CHISTOVIK_NO_SUCH_VAR%\\x.exe")]
    [InlineData(@"C:\a\readme.txt")]
    [InlineData(@"C:\Windows\System32\drivers\x.sys")]
    [InlineData(@"relative\x.exe")]
    public void Parse_garbage_gives_nothing(string? raw) => Assert.Null(IconSources.Parse(raw));

    [Theory]
    [InlineData(@"\\server\share\a.exe")]
    [InlineData(@"C:\a\..\b\x.exe")]
    [InlineData(@"..\x.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x.exe")]
    [InlineData(@"\\.\pipe\x.exe")]
    [InlineData(@"C:\a\x.exe:stream")]
    [InlineData(@"C:\CON\x.exe")]
    public void Parse_rejects_network_device_and_traversal_paths(string raw) => Assert.Null(IconSources.Parse(raw));

    [Fact]
    public void Parse_rejects_overlong_path()
    {
        var path = @"C:\" + new string('a', 1100) + @"\x.exe";
        Assert.Null(IconSources.Parse(path));
        Assert.Null(IconSources.Parse(new string('x', IconSources.MaxSourceLength + 1)));
    }

    [Fact]
    public void Format_round_trips()
    {
        var r = new IconRequest(@"C:\Program Files\App\app.exe", 5);
        Assert.Equal(r, IconSources.Parse(r.Format()));
        var n = new IconRequest(@"C:\Program Files\App\app.exe", null);
        Assert.Equal(n, IconSources.Parse(n.Format()));
    }

    // ----- источник значка программы -----

    sealed class FakeProbe(params string[] files) : IconSources.IProbe
    {
        readonly HashSet<string> _files = new(files, StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => _files.Contains(path);
        public IEnumerable<string> ListExecutables(string directory)
            => _files.Where(f => f.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    static InstalledProgram Prog(string name, string icon = "", string location = "", string uninstall = "") => new()
    {
        Id = "arp:machine:x64:" + name, DisplayName = name, DisplayIcon = icon, InstallLocation = location, UninstallString = uninstall,
        Source = ProgramSource.Uninstall64, RegistryKeyPath = @"HKLM\x",
    };

    [Fact]
    public void Program_icon_prefers_display_icon_then_install_folder_then_uninstaller()
    {
        var probe = new FakeProbe(@"C:\P\Foo\foo.exe", @"C:\P\Foo\unins000.exe", @"C:\P\Foo\Foo.ico");
        Assert.Equal("\"C:\\P\\Foo\\Foo.ico\",0", IconSources.ForProgram(Prog("Foo", @"C:\P\Foo\Foo.ico,0", @"C:\P\Foo"), probe));
        // DisplayIcon указывает на файл, которого нет: берём главный exe каталога (служебный unins* пропускается).
        Assert.Equal("\"C:\\P\\Foo\\foo.exe\"", IconSources.ForProgram(Prog("Foo", @"C:\gone\x.ico", @"C:\P\Foo"), probe));
        Assert.Equal("\"C:\\P\\Foo\\unins000.exe\"", IconSources.ForProgram(Prog("Bar", "", "", "\"C:\\P\\Foo\\unins000.exe\" /S"), probe));
        Assert.Null(IconSources.ForProgram(Prog("Baz", "", "", "MsiExec.exe /X{GUID}"), probe));
        Assert.Null(IconSources.ForProgram(Prog("Qux"), probe));
    }

    [Fact]
    public void Pick_executable_matches_name_and_skips_service_files()
    {
        var pick = IconSources.PickExecutable([@"C:\P\setup.exe", @"C:\P\crashpad.exe", @"C:\P\Telegram.exe", @"C:\P\other.exe"], "Telegram Desktop");
        Assert.Equal(@"C:\P\Telegram.exe", pick);
        Assert.Null(IconSources.PickExecutable([@"C:\P\setup.exe", @"C:\P\unins000.exe"], "Any"));
    }

    // ----- кэш -----

    [Fact]
    public void Lru_cache_evicts_the_least_recently_used()
    {
        var c = new LruCache<int>(3);
        c.Set("a", 1); c.Set("b", 2); c.Set("c", 3);
        Assert.True(c.TryGet("a", out _)); // «a» свежая, старейшая теперь «b»
        c.Set("d", 4);
        Assert.Equal(3, c.Count);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out _));
        Assert.True(c.TryGet("d", out _));
        c.Set("a", 9);
        Assert.Equal(3, c.Count);
        Assert.True(c.TryGet("a", out var v));
        Assert.Equal(9, v);
    }

    // ----- служба значков -----

    static string Src(string name) => $"\"C:\\Icons\\{name}.exe\"";

    static IconService Service(Func<IconRequest, int, ImageSource?> extract, Func<string, bool>? local = null,
        int cache = 512, int parallel = 3, TimeSpan? timeout = null)
        => new(new IconServiceOptions
        {
            Extract = extract, IsLocalFile = local ?? (_ => true), CacheSize = cache, MaxParallel = parallel,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        });

    [Fact]
    public async Task Service_loads_once_and_serves_repeats_from_cache()
    {
        var calls = 0;
        using var svc = Service((_, _) => { Interlocked.Increment(ref calls); return Pixel(); });

        Assert.False(svc.TryGetCached(Src("a"), IconSize.Row, out _));
        var first = await svc.LoadAsync(Src("a"), IconSize.Row);
        var second = await svc.LoadAsync(Src("a"), IconSize.Row);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.True(svc.TryGetCached(Src("a"), IconSize.Row, out var cached));
        Assert.Same(first, cached);
        // Другой размер это другой ключ.
        await svc.LoadAsync(Src("a"), IconSize.Detail);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Service_merges_parallel_requests_for_the_same_icon()
    {
        var calls = 0;
        using var svc = Service((_, _) => { Interlocked.Increment(ref calls); Thread.Sleep(50); return Pixel(); });

        var all = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => svc.LoadAsync(Src("same"), IconSize.Row)));

        Assert.Equal(1, calls);
        Assert.All(all, x => Assert.NotNull(x));
    }

    [Fact]
    public async Task Service_cache_is_bounded()
    {
        using var svc = Service((_, _) => Pixel(), cache: 4);
        for (var i = 0; i < 12; i++) await svc.LoadAsync(Src("i" + i), IconSize.Row);
        Assert.True(svc.CachedCount <= 4, "в кэше " + svc.CachedCount);
    }

    [Fact]
    public async Task Service_limits_parallel_extractions()
    {
        int now = 0, peak = 0;
        using var svc = Service((_, _) =>
        {
            var n = Interlocked.Increment(ref now);
            int p;
            while (n > (p = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, n, p);
            Thread.Sleep(40);
            Interlocked.Decrement(ref now);
            return Pixel();
        }, parallel: 2);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => svc.LoadAsync(Src("p" + i), IconSize.Row)));

        Assert.True(peak is >= 1 and <= 2, "одновременно " + peak);
    }

    [Fact]
    public async Task Service_does_not_touch_files_it_must_not()
    {
        var calls = 0;
        using var svc = Service((_, _) => { calls++; return Pixel(); }, local: _ => false);

        Assert.Null(await svc.LoadAsync(Src("net"), IconSize.Row));
        Assert.Null(await svc.LoadAsync(@"\\server\share\a.exe", IconSize.Row));
        Assert.Null(await svc.LoadAsync(@"C:\a\..\b\x.exe", IconSize.Row));
        Assert.Null(await svc.LoadAsync(null, IconSize.Row));
        Assert.Equal(0, calls);
        // «Значка нет» запоминается: повторно диск не трогаем.
        Assert.True(svc.TryGetCached(Src("net"), IconSize.Row, out var icon));
        Assert.Null(icon);
    }

    [Fact]
    public async Task Service_turns_extractor_failure_into_no_icon()
    {
        using var svc = Service((_, _) => throw new InvalidOperationException("boom"));
        Assert.Null(await svc.LoadAsync(Src("bad"), IconSize.Row));
    }

    [Fact]
    public async Task Service_gives_up_on_a_hung_extraction()
    {
        using var release = new ManualResetEventSlim();
        using var svc = Service((_, _) => { release.Wait(TimeSpan.FromSeconds(10)); return Pixel(); }, timeout: TimeSpan.FromMilliseconds(150));
        try
        {
            Assert.Null(await svc.LoadAsync(Src("hang"), IconSize.Row));
        }
        finally { release.Set(); }
    }

    // ----- строки и модели представления -----

    sealed class FakeIcons(Func<string?, ImageSource?> answer) : IIconService
    {
        public List<string?> Requested { get; } = new();
        public Dictionary<string, ImageSource?> Cached { get; } = new();

        public Task<ImageSource?> LoadAsync(string? source, IconSize size, CancellationToken ct = default)
        {
            lock (Requested) Requested.Add(source);
            return Task.FromResult(answer(source));
        }

        public bool TryGetCached(string? source, IconSize size, out ImageSource? icon)
        {
            icon = null;
            return source is not null && Cached.TryGetValue(source, out icon);
        }
    }

    [Fact]
    public async Task Slot_swaps_in_the_real_icon_after_load()
    {
        var icon = Pixel();
        var slot = new IconSlot(new FakeIcons(_ => icon), Src("a"));
        var changed = new List<string?>();
        slot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _ = slot.Image;               // чтение запускает загрузку; фейк отвечает сразу, поэтому «до» не проверяем
        await slot.Loading;

        Assert.Same(icon, slot.Image);
        Assert.True(slot.HasImage);
        Assert.Contains("Image", changed);
        Assert.Contains("HasImage", changed);
    }

    [Fact]
    public async Task Slot_keeps_the_generic_icon_when_load_fails_or_returns_nothing()
    {
        var empty = new IconSlot(new FakeIcons(_ => null), Src("a"));
        _ = empty.Image;
        await empty.Loading;
        Assert.Null(empty.Image);
        Assert.False(empty.HasImage);

        var failing = new IconSlot(new ThrowingIcons(), Src("a"));
        _ = failing.Image;
        await failing.Loading;
        Assert.False(failing.HasImage);

        Assert.False(new IconSlot(null, Src("a")).HasImage);
        Assert.False(new IconSlot(new FakeIcons(_ => Pixel()), null).HasImage);
        Assert.False(new IconSlot(new FakeIcons(_ => Pixel()), "  ").HasImage);
    }

    sealed class ThrowingIcons : IIconService
    {
        public Task<ImageSource?> LoadAsync(string? source, IconSize size, CancellationToken ct = default) => throw new IOException("x");
    }

    [Fact]
    public void Slot_takes_a_cached_icon_at_once_without_loading()
    {
        var icons = new FakeIcons(_ => null);
        var icon = Pixel();
        icons.Cached[Src("a")] = icon;
        var slot = new IconSlot(icons, Src("a"));

        Assert.Same(icon, slot.Image);
        Assert.Empty(icons.Requested);
        Assert.True(slot.Loading.IsCompleted);
    }

    sealed class ProgramsSrc(params ProgramInfo[] items) : IProgramsSource
    {
        public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync() => Task.FromResult<IReadOnlyList<ProgramInfo>>(items);
    }

    [Fact]
    public async Task Program_rows_show_the_real_icon_and_fall_back_to_the_generic_one()
    {
        var icon = Pixel();
        var icons = new FakeIcons(s => s == Src("with") ? icon : null);
        var vm = new ProgramsViewModel(
            new ProgramsSrc(
                new ProgramInfo("With", "P", "1", 0, null, "Классическая", IconSource: Src("with")),
                new ProgramInfo("Broken", "P", "1", 0, null, "Классическая", IconSource: Src("broken")),
                new ProgramInfo("None", "P", "1", 0, null, "Классическая")),
            new FakeDialogs(), icons: icons);
        await vm.Loaded;

        var rows = vm.Items.ToDictionary(x => x.Name);
        foreach (var r in rows.Values) { _ = r.AppIcon.Image; await r.AppIcon.Loading; }

        Assert.Same(icon, rows["With"].AppIcon.Image);
        Assert.False(rows["Broken"].AppIcon.HasImage);
        Assert.False(rows["None"].AppIcon.HasImage);
        Assert.Equal(2, icons.Requested.Count); // у строки без источника загрузка не запускалась
    }

    [Fact]
    public async Task Startup_and_service_rows_derive_the_source_from_their_own_data()
    {
        var icons = new FakeIcons(_ => null);
        var startup = new StartupViewModel(new StartupSrc(
            new StartupInfo("run:hku:A", "A", @"C:\x\a.exe -s", "Реестр", true, true, null, ExePath: @"C:\x\a.exe"),
            new StartupInfo("run:hku:B", "B", @"C:\x\b.exe -s", "Реестр", true, true, null)), icons: icons);
        await startup.Loaded;
        foreach (var r in startup.Items) { _ = r.AppIcon.Image; await r.AppIcon.Loading; }
        Assert.Contains(@"C:\x\a.exe", icons.Requested);
        Assert.Contains(@"C:\x\b.exe -s", icons.Requested);

        var icons2 = new FakeIcons(_ => null);
        var row = new ServiceRowViewModel(new ServiceInfo("S", "S", StartMode.Manual, Changeability.Modifiable, "r", @"C:\s\svc.exe -k x"), icons: icons2);
        _ = row.AppIcon.Image;
        await row.AppIcon.Loading;
        Assert.Equal(@"C:\s\svc.exe -k x", Assert.Single(icons2.Requested));
    }

    sealed class StartupSrc(params StartupInfo[] items) : IStartupSource
    {
        public Task<IReadOnlyList<StartupInfo>> GetStartupAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StartupInfo>>(items);
    }

    // ----- живой вызов Win32 -----

    /// <summary>
    /// Единственный тест, который трогает систему: значок из shell32.dll (есть в любой Windows). Может оказаться хрупким
    /// на сервере без оболочки или в службе без рабочего стола: тогда результат null, и тест это покажет.
    /// </summary>
    [Fact]
    public async Task Real_extraction_from_shell32_returns_a_non_empty_frozen_image()
    {
        var shell32 = Path.Combine(Sys, "shell32.dll");
        Assert.True(File.Exists(shell32));

        var direct = NativeIcons.Extract(new IconRequest(shell32, 0), 48);
        Assert.NotNull(direct);
        Assert.True(direct!.IsFrozen);
        Assert.True(direct.Width > 0 && direct.Height > 0);

        using var svc = new IconService();
        var viaService = await svc.LoadAsync(new IconRequest(shell32, 3).Format(), IconSize.Row);
        Assert.NotNull(viaService);
        Assert.True(((BitmapSource)viaService!).PixelWidth > 0);
    }
}
