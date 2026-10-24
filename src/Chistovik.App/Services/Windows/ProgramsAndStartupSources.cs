using System.IO;
using Chistovik.App.Models;
using Chistovik.Core.Catalog;
using Chistovik.Core.Inventory;
using Chistovik.Core.Ports;
using System.Text.Json;

namespace Chistovik.App.Services.Windows;

public sealed class InventoryProgramsSource(IRegistryReader reader) : IProgramsSource
{
    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync() => Task.Run(() =>
    {
        IReadOnlyList<ProgramInfo> list = Read(reader).Select(p => new ProgramInfo(
            p.DisplayName,
            string.IsNullOrWhiteSpace(p.Publisher) ? "Не указан" : p.Publisher,
            p.DisplayVersion,
            (p.EstimatedSizeKb ?? 0) * 1024,
            p.InstallDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : null,
            SourceName(p.Source))).ToList();
        return list;
    });

    public static IReadOnlyList<InstalledProgram> Read(IRegistryReader reader)
        => new UninstallInventory(reader).Read().Where(p => !p.IsUpdate && !p.IsSystemComponent).ToList();

    static string SourceName(ProgramSource s) => s switch
    {
        ProgramSource.Uninstall64 => "Классическая",
        ProgramSource.Uninstall32 => "32-разрядная",
        ProgramSource.UninstallHkcu => "Пользовательская",
        ProgramSource.Msix => "Магазин",
        _ => "",
    };
}

/// <summary>
/// Автозагрузка: Run и RunOnce (HKLM 64/32, HKCU), папки Startup, состояние из StartupApproved. Только чтение.
/// EntryId в формате протокола (DESIGN-0.1 §3); агент ищет запись в своём перечислении и сам решает, можно ли менять.
/// </summary>
public sealed class RegistryStartupSource(IRegistryReader reg, IFileSystem fs, string? userStartupDir, string? commonStartupDir)
    : IStartupSource
{
    const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public Task<IReadOnlyList<StartupInfo>> GetStartupAsync() => Task.Run(Read);

    public IReadOnlyList<StartupInfo> Read()
    {
        var all = new List<StartupInfo>();
        AddRun(all, RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "hklm64", "Реестр, все пользователи", "Run");
        AddRun(all, RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "hklm32", "Реестр, все пользователи, 32-разрядные", "Run32");
        AddRun(all, RegistryHiveKind.CurrentUser, RegistryViewKind.Registry64, "hku", "Реестр, текущий пользователь", "Run");
        AddOnce(all, RegistryHiveKind.LocalMachine, "Однократный запуск, все пользователи");
        AddOnce(all, RegistryHiveKind.CurrentUser, "Однократный запуск, текущий пользователь");
        AddFolder(all, userStartupDir, "user", RegistryHiveKind.CurrentUser, "Папка автозагрузки, текущий пользователь");
        AddFolder(all, commonStartupDir, "common", RegistryHiveKind.LocalMachine, "Папка автозагрузки, все пользователи");
        return all.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    void AddRun(List<StartupInfo> into, RegistryHiveKind hive, RegistryViewKind view, string idPart, string origin, string approvedKey)
    {
        var values = reg.ReadValues(hive, view, Run);
        if (values is null) return;
        var approved = reg.ReadValues(hive, RegistryViewKind.Registry64, Approved + approvedKey);
        foreach (var (name, raw) in values)
        {
            var id = $"run:{idPart}:{name}";
            var ok = IsValidId(id);
            into.Add(new StartupInfo(ok ? id : "", name, raw?.ToString() ?? "", origin, IsEnabled(approved, name), ok,
                ok ? null : "Имя записи нельзя передать агенту"));
        }
    }

    void AddOnce(List<StartupInfo> into, RegistryHiveKind hive, string origin)
    {
        var values = reg.ReadValues(hive, RegistryViewKind.Registry64, RunOnce);
        if (values is null) return;
        foreach (var (name, raw) in values)
            into.Add(new StartupInfo("", name, raw?.ToString() ?? "", origin, true, false,
                "Запускается один раз и сама пропадает из списка"));
    }

    void AddFolder(List<StartupInfo> into, string? dir, string idPart, RegistryHiveKind hive, string origin)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var approved = reg.ReadValues(hive, RegistryViewKind.Registry64, Approved + "StartupFolder");
        foreach (var path in fs.ListFiles(dir, "*"))
        {
            var file = Path.GetFileName(path);
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var id = $"folder:{idPart}:{file}";
            var ok = IsValidId(id);
            into.Add(new StartupInfo(ok ? id : "", file, path, origin, IsEnabled(approved, file), ok,
                ok ? null : "Имя файла нельзя передать агенту"));
        }
    }

    static bool IsValidId(string id) => id.Length <= 300 && !id.Any(c => c < ' ' || c == '\u007f');

    /// <summary>Нет записи в StartupApproved — включено. Нечётный первый байт — выключено (DESIGN-0.1 §9, сверить в Sandbox).</summary>
    public static bool IsEnabled(IReadOnlyDictionary<string, object?>? approved, string name)
    {
        if (approved is null || !approved.TryGetValue(name, out var v) || v is not byte[] { Length: > 0 } b) return true;
        return (b[0] & 1) == 0;
    }
}

/// <summary>Текущее значение твиков: сравнение с onValue/offValue каталога.</summary>
public sealed class RegistryTweakStateSource(IRegistryReader reg) : ITweakStateSource
{
    public Task<IReadOnlyDictionary<string, TweakState>> ReadAsync(IReadOnlyList<TweakEntry> tweaks) => Task.Run(() =>
    {
        var cache = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, TweakState>(StringComparer.Ordinal);
        foreach (var t in tweaks)
        {
            var cacheKey = t.Hive + "\\" + t.Key;
            if (!cache.TryGetValue(cacheKey, out var values))
            {
                var hive = t.Hive == "HKLM" ? RegistryHiveKind.LocalMachine : RegistryHiveKind.CurrentUser;
                values = reg.ReadValues(hive, RegistryViewKind.Registry64, t.Key);
                cache[cacheKey] = values;
            }
            result[t.Id] = Evaluate(t, values);
        }
        return (IReadOnlyDictionary<string, TweakState>)result;
    });

    public static TweakState Evaluate(TweakEntry t, IReadOnlyDictionary<string, object?>? values)
    {
        object? actual = null;
        var exists = values is not null && values.TryGetValue(t.Value, out actual);
        if (!exists)
            return t.OffValue is null || t.OffValue.Value.ValueKind == JsonValueKind.Null ? TweakState.Off : TweakState.Unknown;
        if (Matches(actual, t.OnValue, t.ValueType)) return TweakState.On;
        if (t.OffValue is { } off && off.ValueKind != JsonValueKind.Null && Matches(actual, off, t.ValueType)) return TweakState.Off;
        return TweakState.Unknown;
    }

    static bool Matches(object? actual, JsonElement expected, string type)
    {
        if (type == "DWord")
        {
            if (!expected.TryGetUInt32(out var want)) return false;
            return actual switch
            {
                int i => unchecked((uint)i) == want,
                uint u => u == want,
                long l => l == want,
                _ => false,
            };
        }
        return actual is string s && string.Equals(s, expected.GetString(), StringComparison.Ordinal);
    }
}
