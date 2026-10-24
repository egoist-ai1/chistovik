using Chistovik.Core.Inventory;
using Xunit;

namespace Chistovik.Core.Tests.Inventory;

public sealed class FakeRegistryReader : IRegistryReader
{
    private const string U = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string U32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly Dictionary<string, Dictionary<string, object?>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryReader Add(RegistryHiveKind hive, bool x86, string key, Dictionary<string, object?> values)
    {
        _keys[$"{hive}|{(x86 ? U32 : U)}\\{key}"] = values;
        return this;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHiveKind hive, RegistryViewKind view, string path)
    {
        var prefix = $"{hive}|{path}\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..]).ToList();
    }

    public IReadOnlyDictionary<string, object?>? ReadValues(RegistryHiveKind hive, RegistryViewKind view, string path) =>
        _keys.TryGetValue($"{hive}|{path}", out var v) ? v : null;
}

public class UninstallInventoryTests
{
    private static Dictionary<string, object?> App(string name, string ver = "1.0", string pub = "Acme") =>
        new() { ["DisplayName"] = name, ["DisplayVersion"] = ver, ["Publisher"] = pub };

    private static IReadOnlyList<InstalledProgram> Run(FakeRegistryReader r) => new UninstallInventory(r).Read();

    [Fact]
    public void Skips_entries_without_DisplayName()
    {
        var r = new FakeRegistryReader()
            .Add(RegistryHiveKind.LocalMachine, false, "a", new() { ["Publisher"] = "X" })
            .Add(RegistryHiveKind.LocalMachine, false, "b", new() { ["DisplayName"] = "   " })
            .Add(RegistryHiveKind.LocalMachine, false, "c", App("Real"));
        var list = Run(r);
        Assert.Single(list);
        Assert.Equal("Real", list[0].DisplayName);
        Assert.Equal("arp:machine:x64:c", list[0].Id);
        Assert.Equal(ProgramSource.Uninstall64, list[0].Source);
    }

    [Fact]
    public void Marks_system_component_without_dropping()
    {
        var v = App("Sys"); v["SystemComponent"] = 1;
        var list = Run(new FakeRegistryReader().Add(RegistryHiveKind.LocalMachine, false, "s", v));
        Assert.True(Assert.Single(list).IsSystemComponent);
    }

    [Theory]
    [InlineData("Update for Windows (KB5000001)")]
    [InlineData("Security Update for Microsoft Office (KB5002222)")]
    [InlineData("KB1234567")]
    public void Marks_updates_by_name(string name)
    {
        var list = Run(new FakeRegistryReader().Add(RegistryHiveKind.LocalMachine, false, "u", App(name)));
        Assert.True(Assert.Single(list).IsUpdate);
    }

    [Fact]
    public void Marks_update_by_ParentKeyName_and_not_plain_apps()
    {
        var v = App("Patch"); v["ParentKeyName"] = "OperatingSystem";
        var r = new FakeRegistryReader()
            .Add(RegistryHiveKind.LocalMachine, false, "p", v)
            .Add(RegistryHiveKind.LocalMachine, false, "n", App("Notepad Plus"));
        var list = Run(r);
        Assert.True(list.Single(x => x.DisplayName == "Patch").IsUpdate);
        Assert.False(list.Single(x => x.DisplayName == "Notepad Plus").IsUpdate);
    }

    [Fact]
    public void Deduplicates_x64_and_x86_keeping_both_paths()
    {
        var r = new FakeRegistryReader()
            .Add(RegistryHiveKind.LocalMachine, false, "k64", App("Tool", "2.0", "Acme"))
            .Add(RegistryHiveKind.LocalMachine, true, "k32", App("tool", "2.0", "ACME"));
        var p = Assert.Single(Run(r));
        Assert.Equal(ProgramSource.Uninstall64, p.Source);
        Assert.Equal(2, p.RegistryKeyPaths.Count);
        Assert.EndsWith(@"WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\k32", p.RegistryKeyPaths[1]);
    }

    [Fact]
    public void Different_version_is_not_duplicate_and_hkcu_is_separate()
    {
        var r = new FakeRegistryReader()
            .Add(RegistryHiveKind.LocalMachine, false, "a", App("Tool", "1.0"))
            .Add(RegistryHiveKind.LocalMachine, true, "b", App("Tool", "2.0"))
            .Add(RegistryHiveKind.CurrentUser, false, "c", App("Tool", "1.0"));
        var list = Run(r);
        Assert.Equal(3, list.Count);
        Assert.Contains(list, x => x.Source == ProgramSource.UninstallHkcu && x.Id == "arp:user:user:c");
    }

    [Fact]
    public void Handles_garbage_and_dword_instead_of_string()
    {
        var v = new Dictionary<string, object?>
        {
            ["DisplayName"] = "Odd",
            ["DisplayVersion"] = 5,
            ["Publisher"] = null,
            ["InstallLocation"] = new byte[] { 1, 2 },
            ["WindowsInstaller"] = "1",
            ["EstimatedSize"] = "not a number",
            ["InstallDate"] = "garbage",
        };
        var p = Assert.Single(Run(new FakeRegistryReader().Add(RegistryHiveKind.LocalMachine, false, "{12345678-1234-1234-1234-123456789ABC}", v)));
        Assert.Equal("5", p.DisplayVersion);
        Assert.Equal("", p.Publisher);
        Assert.Equal("", p.InstallLocation);
        Assert.True(p.IsMsi);
        Assert.Equal("{12345678-1234-1234-1234-123456789ABC}", p.ProductCode);
        Assert.Null(p.EstimatedSizeKb);
        Assert.Null(p.InstallDate);
    }

    [Fact]
    public void Parses_size_date_and_dword_values()
    {
        var v = App("Sized"); v["EstimatedSize"] = 2048; v["InstallDate"] = "20260131"; v["WindowsInstaller"] = 1;
        var p = Assert.Single(Run(new FakeRegistryReader().Add(RegistryHiveKind.LocalMachine, false, "plain", v)));
        Assert.Equal(2048, p.EstimatedSizeKb);
        Assert.Equal(new DateOnly(2026, 1, 31), p.InstallDate);
        Assert.Null(p.ProductCode);
    }
}
