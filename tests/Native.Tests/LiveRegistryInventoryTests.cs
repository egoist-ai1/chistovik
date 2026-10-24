using System.Diagnostics;
using System.Text.Json;
using Chistovik.Core.Inventory;
using Chistovik.Native.Inventory;
using Xunit;
using Xunit.Abstractions;

namespace Chistovik.Native.Tests;

public class LiveRegistryInventoryTests(ITestOutputHelper output)
{
    private static IReadOnlyList<InstalledProgram> ReadLive() => new UninstallInventory(new WindowsRegistryReader()).Read();

    [Fact]
    public void Live_registry_returns_programs_with_names()
    {
        var list = ReadLive();
        output.WriteLine($"programs: {list.Count}");
        Assert.NotEmpty(list);
        Assert.All(list, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.DisplayName));
            Assert.False(string.IsNullOrEmpty(p.RegistryKeyPath));
        });
    }

    [Fact]
    public void Reading_does_not_change_registry()
    {
        string Snapshot()
        {
            var r = new WindowsRegistryReader();
            const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var lines = new List<string>();
            foreach (var hive in new[] { RegistryHiveKind.LocalMachine, RegistryHiveKind.CurrentUser })
                foreach (var k in r.GetSubKeyNames(hive, RegistryViewKind.Registry64, path).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var v = r.ReadValues(hive, RegistryViewKind.Registry64, path + "\\" + k);
                    lines.Add($"{hive}|{k}|{string.Join(",", (v?.Keys ?? []).OrderBy(x => x, StringComparer.Ordinal))}");
                }
            return string.Join("\n", lines);
        }

        var before = Snapshot();
        _ = ReadLive();
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Count_is_close_to_inventory_ps1()
    {
        var script = FindScript();
        if (script is null) { output.WriteLine("inventory.ps1 not found, skipped"); return; }

        var tmp = Path.Combine(Path.GetTempPath(), $"chistovik-inv-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Out", tmp }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(120_000), "powershell timeout");

            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            var psArp = doc.RootElement.GetProperty("items").EnumerateArray()
                .Count(i => i.GetProperty("source").GetString() != "msix");

            var ours = ReadLive();
            var oursNonUpdate = ours.Where(p => !p.IsUpdate).Sum(p => p.RegistryKeyPaths.Count); // до дедупликации
            output.WriteLine($"ps1 ARP: {psArp}; C# total: {ours.Count}; C# non-update before dedup: {oursNonUpdate}; updates: {ours.Count(p => p.IsUpdate)}");

            // Допуск: у скрипта и порта немного разные фильтры обновлений; тест не подгоняется под число.
            Assert.InRange(Math.Abs(psArp - oursNonUpdate), 0, Math.Max(10, psArp / 10));
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    private static string? FindScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "inventory.ps1");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
