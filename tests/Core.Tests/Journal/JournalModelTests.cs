using System.Text.Json;
using Chistovik.Core.Execution;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.CoreTests.Journal;

public class JournalModelTests
{
    static readonly Guid G1 = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    static readonly Guid G2 = Guid.Parse("54533251-82be-4824-96c1-47b60b740d00");
    static readonly Guid G3 = Guid.Parse("893dee8e-2bef-41e0-89c6-b55d0929964c");

    static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static void RoundTrip<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.Serialize(value, info);
        var back = JsonSerializer.Deserialize(json, info);
        Assert.Equal(json, JsonSerializer.Serialize(back!, info));
        Assert.NotNull(back);
    }

    [Fact]
    public void Registry_payloads_roundtrip()
    {
        RoundTrip(new RegistryTarget("HKU", "S-1-5-21-1-2-3-1001", "64", @"Software\X", "Val"), JournalJson.Default.RegistryTarget);
        RoundTrip(new RegistryState(1, true, "DWord", El("1")), JournalJson.Default.RegistryState);
        RoundTrip(new RegistryState(1, true, "QWord", El("\"18446744073709551615\"")), JournalJson.Default.RegistryState);
        RoundTrip(new RegistryState(1, true, "Binary", El("\"AQID\"")), JournalJson.Default.RegistryState);
        RoundTrip(new RegistryState(1, true, "MultiString", El("[\"a\",\"b\"]")), JournalJson.Default.RegistryState);
        RoundTrip(new RegistryState(1, false, null, null), JournalJson.Default.RegistryState);
    }

    [Fact]
    public void Other_payloads_roundtrip()
    {
        RoundTrip(new StartupTarget("HKLM", null, "Run32", "OneDrive", new string('a', 64)), JournalJson.Default.StartupTarget);
        RoundTrip(new StartupState(1, true, Convert.ToBase64String(new byte[12])), JournalJson.Default.StartupState);
        RoundTrip(new ServiceTarget("Spooler"), JournalJson.Default.ServiceTarget);
        RoundTrip(new ServiceState(1, 2, true, false, true), JournalJson.Default.ServiceState);
        RoundTrip(new TaskTarget(@"\Vendor\Task"), JournalJson.Default.TaskTarget);
        RoundTrip(new TaskState(1, true, true, new string('b', 64), "<Task/>"), JournalJson.Default.TaskState);
        RoundTrip(new PowerActiveTarget(), JournalJson.Default.PowerActiveTarget);
        RoundTrip(new PowerActiveState(1, G1), JournalJson.Default.PowerActiveState);
        RoundTrip(new PowerSettingTarget(G1, G2, G3), JournalJson.Default.PowerSettingTarget);
        RoundTrip(new PowerSettingState(1, 100, 0), JournalJson.Default.PowerSettingState);
        RoundTrip(new PowerSchemeTarget(G1, G2), JournalJson.Default.PowerSchemeTarget);
        RoundTrip(new PowerSchemeState(1, true), JournalJson.Default.PowerSchemeState);
    }

    [Fact]
    public void States_start_with_version_field()
    {
        Assert.StartsWith("{\"v\":1,", JsonSerializer.Serialize(new PowerSettingState(1, 1, 2), JournalJson.Default.PowerSettingState));
        Assert.StartsWith("{\"v\":1,", JsonSerializer.Serialize(new RegistryState(1, false, null, null), JournalJson.Default.RegistryState));
        Assert.StartsWith("{\"v\":1,", JsonSerializer.Serialize(new ServiceState(1, 3, false, false, false), JournalJson.Default.ServiceState));
    }

    [Fact]
    public void Unknown_field_in_payload_is_rejected()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"v":1,"exists":true,"extra":1}""", JournalJson.Default.PowerSchemeState));
    }

    [Fact]
    public void Cleanup_and_registry_tree_payloads_roundtrip_and_reject_unknown_fields()
    {
        RoundTrip(new CleanupTarget(1, "temp-user", "user", "S-1-5-21-1-2-3-1001", [@"C:\Users\T\AppData\Local\Temp"], 24), JournalJson.Default.CleanupTarget);
        RoundTrip(new CleanupState(1, false, 10, 2), JournalJson.Default.CleanupState);
        RoundTrip(new CleanupState(1, true, Deleted: 5, FreedBytes: 99, Skipped: new Dictionary<string, int> { ["in-use"] = 1 }, Failed: 0), JournalJson.Default.CleanupState);
        var ev = new OrphanEvidenceJson("uninstall-orphan", [@"C:\Program Files\X"], null);
        RoundTrip(new RegKeyDeleteTarget(1, "HKLM", null, "x64", @"SOFTWARE\X", ev, new RegKeyBackup(@"SOFTWARE\Chistovik\Quarantine\a\1", @"a\1.reg"), false, "X"),
            JournalJson.Default.RegKeyDeleteTarget);
        RoundTrip(new RegKeyState(1, true, 3, 5, new string('a', 64)), JournalJson.Default.RegKeyState);
        RoundTrip(new RegKeyState(1, false), JournalJson.Default.RegKeyState);
        RoundTrip(new ValueRemoveTarget(1, "HKLM", null, "x64", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "V", ev), JournalJson.Default.ValueRemoveTarget);
        Assert.Contains("\"v\":1", JsonSerializer.Serialize(new RegKeyState(1, false), JournalJson.Default.RegKeyState));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"v":1,"done":true,"extra":1}""", JournalJson.Default.CleanupState));
    }

    [Fact]
    public void Step_codes_are_closed()
    {
        foreach (var c in new[] { "evidence-failed", "reg-tree-too-large", "reg-tree-has-link", "backup-mismatch", "backup-missing", "interrupted" })
            Assert.True(StepCodes.IsKnown(c), c);
        Assert.True(StepCodes.IsKnown(StepCodes.NoChange));
        Assert.True(StepCodes.IsKnown(StepCodes.Win32(5)));
        Assert.False(StepCodes.IsKnown("win32:"));
        Assert.False(StepCodes.IsKnown("win32:-1"));
        Assert.False(StepCodes.IsKnown("whatever"));
        Assert.False(StepCodes.IsKnown(null));
    }

    [Fact]
    public void Approved_types_cannot_be_constructed_from_outside()
    {
        // Публичных конструкторов нет: создать Approved* может только Core (и тесты через InternalsVisibleTo).
        foreach (var t in typeof(ApprovedRegistryWrite).Assembly.GetTypes().Where(t => t.Name.StartsWith("Approved", StringComparison.Ordinal)))
            Assert.Empty(t.GetConstructors());
        Assert.Equal(8, typeof(ApprovedRegistryWrite).Assembly.GetTypes().Count(t => t.Name.StartsWith("Approved", StringComparison.Ordinal)));
    }
}
