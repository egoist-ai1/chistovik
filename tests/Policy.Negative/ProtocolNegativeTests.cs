using System.Text;
using System.Text.Json;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>Негативные тесты протокола агента (DESIGN-0.1 §7 «Протокол»): запрос не должен дойти до обработчика.</summary>
public class ProtocolNegativeTests
{
    const string Id = "11111111-2222-3333-4444-555555555555";
    static readonly string Tweak = TweakCatalog.Shared.Tweaks[0].Id;
    static readonly string Mode = TweakCatalog.Shared.Modes[0].Id;
    static readonly ClientContext Client = new("S-1-5-21-1-2-3-1001", 1, 1);

    sealed class Spy : IOperationHandler
    {
        public int Calls;

        public Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AgentResponse(request.Id, true, ResponseCodes.Ok));
        }
    }

    static async Task AssertRejected(string json)
    {
        var spy = new Spy();
        var r = await new RequestDispatcher(spy).HandleAsync(Encoding.UTF8.GetBytes(json), Client);
        Assert.False(r.Ok, json);
        Assert.Equal(ResponseCodes.BadRequest, r.Code);
        Assert.Equal(0, spy.Calls);
    }

    static async Task AssertAccepted(string json)
    {
        var spy = new Spy();
        var r = await new RequestDispatcher(spy).HandleAsync(Encoding.UTF8.GetBytes(json), Client);
        Assert.True(r.Ok, json + " -> " + r.Message);
        Assert.Equal(1, spy.Calls);
    }

    static string Svc(string name, string start = "\"Manual\"") => $$"""{"type":"SetServiceStart","id":"{{Id}}","serviceName":{{JsonSerializer.Serialize(name)}},"start":{{start}}}""";
    static string TaskReq(string path) => $$"""{"type":"SetTaskEnabled","id":"{{Id}}","taskPath":{{JsonSerializer.Serialize(path)}},"enabled":false}""";
    static string Power(string setting, string values) => $$"""{"type":"SetPowerSetting","id":"{{Id}}","settingId":"{{setting}}",{{values}}}""";

    // Контрольные положительные запросы: проверяют, что тест отвергает по делу, а не из-за опечатки в шаблоне.
    [Fact]
    public async Task Control_requests_are_accepted()
    {
        await AssertAccepted(Svc("Spooler"));
        await AssertAccepted(TaskReq(@"\Vendor\Updater"));
        await AssertAccepted(Power("min", "\"ac\":100,\"dc\":0"));
        await AssertAccepted($$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":[{"tweakId":"{{Tweak}}","enabled":true}]}""");
        await AssertAccepted($$"""{"type":"ApplyMode","id":"{{Id}}","modeId":"{{Mode}}"}""");
        await AssertAccepted($$"""{"type":"Undo","id":"{{Id}}","operationId":"{{Guid.NewGuid()}}","resolutions":[{"stepId":3,"action":"Skip"}]}""");
    }

    [Theory]
    [InlineData("""{"type":"DeleteFile","id":"11111111-2222-3333-4444-555555555555","path":"C:\\Windows"}""")]
    [InlineData("""{"type":"RunCommand","id":"11111111-2222-3333-4444-555555555555","command":"cmd"}""")]
    [InlineData("""{"type":"ApplyRegistryValue","id":"11111111-2222-3333-4444-555555555555"}""")]
    [InlineData("""{"type":"apply_tweaks","id":"11111111-2222-3333-4444-555555555555","changes":[]}""")]
    [InlineData("""{"id":"11111111-2222-3333-4444-555555555555","modeId":"x"}""")]
    public async Task Unknown_or_missing_type(string json) => await AssertRejected(json);

    [Fact]
    public async Task Extra_fields_key_path_command_are_rejected()
    {
        await AssertRejected($$"""{"type":"ApplyMode","id":"{{Id}}","modeId":"{{Mode}}","key":"HKLM\\SOFTWARE"}""");
        await AssertRejected($$"""{"type":"SetTaskEnabled","id":"{{Id}}","taskPath":"\\A","enabled":true,"path":"C:\\x"}""");
        await AssertRejected($$"""{"type":"SetServiceStart","id":"{{Id}}","serviceName":"Spooler","start":"Manual","command":"sc stop"}""");
        await AssertRejected($$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":[{"tweakId":"{{Tweak}}","enabled":true,"key":"X"}]}""");
        await AssertRejected($$"""{"type":"SetPowerScheme","id":"{{Id}}","plan":"bal","guid":"381b4222-f694-41f0-9685-ff5bb260df2e"}""");
        await AssertRejected($$"""{"type":"SetStartupEntry","id":"{{Id}}","entryId":"run:hku:X","enabled":true,"sid":"S-1-5-18"}""");
        await AssertRejected($$"""{"type":"Undo","id":"{{Id}}","operationId":"{{Guid.NewGuid()}}","expert":true}""");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("\"Boot\"")]
    [InlineData("\"System\"")]
    [InlineData("\"0\"")]
    [InlineData("\"99\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("\"Manual,Disabled\"")]
    public async Task Start_must_be_one_of_four_names(string start) => await AssertRejected(Svc("Spooler", start));

    [Fact]
    public async Task Changes_count_and_duplicates()
    {
        string Item(string id) => $$"""{"tweakId":"{{id}}","enabled":true}""";
        var ids = TweakCatalog.Shared.Tweaks.Select(t => t.Id).ToList();
        Assert.True(ids.Count >= 24);

        string Req(IEnumerable<string> items) => $$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":[{{string.Join(",", items)}}]}""";

        await AssertRejected(Req([]));
        await AssertRejected(Req(Enumerable.Range(0, 33).Select(i => Item(ids[i % ids.Count]))));   // 33 элемента
        await AssertRejected(Req([Item(ids[0]), Item(ids[1]), Item(ids[0])]));                       // повтор
        await AssertRejected($$"""{"type":"ApplyTweaks","id":"{{Id}}"}""");                           // нет Changes
        await AssertRejected($$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":null}""");
        await AssertRejected($$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":[null]}""");
        await AssertRejected(Req([$$"""{"tweakId":"{{ids[0]}}"}"""]));                              // нет Enabled
        await AssertRejected(Req([$$"""{"tweakId":"{{ids[0]}}","enabled":"true"}"""]));              // строка вместо bool

        // Граница: ровно 32 разных (если в каталоге их хватает) принимаются форматом.
        if (ids.Count >= 32)
            await AssertAccepted(Req(ids.Take(32).Select(Item)));
    }

    [Theory]
    [InlineData("not-in-catalog")]
    [InlineData("")]
    [InlineData("ext ")]
    [InlineData("a/b")]
    [InlineData("ext\\u0000")]
    public async Task TweakId_and_ModeId_must_come_from_catalog(string id)
    {
        await AssertRejected($$"""{"type":"ApplyTweaks","id":"{{Id}}","changes":[{"tweakId":"{{id}}","enabled":true}]}""");
        await AssertRejected($$"""{"type":"ApplyMode","id":"{{Id}}","modeId":"{{id}}"}""");
    }

    public static IEnumerable<object[]> BadServiceNames()
    {
        yield return new object[] { @"Evil\Svc" };
        yield return new object[] { "Svc\0" };
        yield return new object[] { "Svc " };
        yield return new object[] { " Svc" };
        yield return new object[] { "Svc\n" };
        yield return new object[] { "" };
        yield return new object[] { new string('a', 257) };
        yield return new object[] { new string('a', 300) };
        yield return new object[] { "Svc/x" };
        yield return new object[] { "Сервис" };
        yield return new object[] { "Svc;sc stop" };
    }

    [Theory]
    [MemberData(nameof(BadServiceNames))]
    public async Task ServiceName_is_strict(string name) => await AssertRejected(Svc(name));

    [Fact]
    public async Task ServiceName_length_limit_is_inclusive()
    {
        await AssertAccepted(Svc(new string('a', 256)));
        await AssertRejected(Svc(new string('a', 257)));
    }

    public static IEnumerable<object[]> BadTaskPaths()
    {
        yield return new object[] { @"\Microsoft\..\Windows\X" };
        yield return new object[] { @"\.." };
        yield return new object[] { @"\A\.\B" };
        yield return new object[] { "/A/B" };
        yield return new object[] { @"\A/B" };
        yield return new object[] { @"\\server\share\task" };
        yield return new object[] { @"A\B" };            // без ведущего \
        yield return new object[] { "" };
        yield return new object[] { @"\" };
        yield return new object[] { @"\A\" };            // пустой хвостовой сегмент
        yield return new object[] { @"\A\\B" };
        yield return new object[] { @"\A:B" };
        yield return new object[] { "\\A\\B*" };
        yield return new object[] { "\\A\\B?" };
        yield return new object[] { "\\A\\B|C" };
        yield return new object[] { "\\A\\B\"C" };
        yield return new object[] { "\\A\\B<C>" };
        yield return new object[] { "\\A\\B\0" };
        yield return new object[] { "\\A\\B\n" };
        yield return new object[] { "\\A\\B " };
        yield return new object[] { "\\A\\B." };
        yield return new object[] { "\\" + new string('a', 256) };
        yield return new object[] { "\\" + string.Join("\\", Enumerable.Repeat(new string('a', 200), 6)) }; // длиннее общего предела
    }

    [Theory]
    [MemberData(nameof(BadTaskPaths))]
    public async Task TaskPath_is_strict(string path) => await AssertRejected(TaskReq(path));

    [Theory]
    [InlineData("run:hklm64:")]
    [InlineData("run:hkcu:X")]
    [InlineData("run:hku")]
    [InlineData("folder:other:x.lnk")]
    [InlineData("HKLM\\Software\\Run")]
    [InlineData("C:\\Users\\x\\Startup\\a.lnk")]
    [InlineData("")]
    [InlineData("run:hklm64:a\nb")]
    [InlineData("run:hklm64:a\0b")]
    [InlineData("run:hklm64:a\n")]
    public async Task EntryId_format(string entry)
        => await AssertRejected($$"""{"type":"SetStartupEntry","id":"{{Id}}","entryId":{{JsonSerializer.Serialize(entry)}},"enabled":true}""");

    [Fact]
    public async Task EntryId_length_limit()
    {
        string E(int len) => "run:hku:" + new string('x', len - 8);
        await AssertAccepted($$"""{"type":"SetStartupEntry","id":"{{Id}}","entryId":"{{E(300)}}","enabled":true}""");
        await AssertRejected($$"""{"type":"SetStartupEntry","id":"{{Id}}","entryId":"{{E(301)}}","enabled":true}""");
    }

    [Theory]
    [InlineData("min", "\"ac\":101")]
    [InlineData("max", "\"dc\":101")]
    [InlineData("min", "\"ac\":4294967295")]
    [InlineData("min", "\"ac\":-1")]
    [InlineData("min", "\"ac\":1.5")]
    [InlineData("min", "\"ac\":\"50\"")]
    [InlineData("min", "\"ac\":null,\"dc\":null")]
    [InlineData("min", "\"x\":1")]
    [InlineData("boost", "\"ac\":7")]
    [InlineData("aspm", "\"ac\":3")]
    [InlineData("usb", "\"dc\":2")]
    [InlineData("display", "\"ac\":86401")]
    [InlineData("sleep", "\"dc\":4294967295")]
    [InlineData("nope", "\"ac\":1")]
    [InlineData("MIN", "\"ac\":1")]
    [InlineData("", "\"ac\":1")]
    public async Task PowerSetting_range_and_id(string setting, string values) => await AssertRejected(Power(setting, values));

    [Fact]
    public async Task PowerSetting_without_values_and_boundaries()
    {
        await AssertRejected($$"""{"type":"SetPowerSetting","id":"{{Id}}","settingId":"min"}""");
        await AssertAccepted(Power("min", "\"ac\":0"));
        await AssertAccepted(Power("min", "\"dc\":100"));
        await AssertAccepted(Power("boost", "\"ac\":6,\"dc\":0"));
    }

    [Theory]
    [InlineData("balanced")]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("\"4\"")]
    [InlineData("null")]
    public async Task PowerScheme_plan_is_closed(string plan)
    {
        var value = plan.StartsWith('"') || plan == "0" || plan == "4" || plan == "null" ? plan : $"\"{plan}\"";
        await AssertRejected($$"""{"type":"SetPowerScheme","id":"{{Id}}","plan":{{value}}}""");
    }

    [Fact]
    public async Task PowerScheme_accepts_only_four_names()
    {
        foreach (var p in new[] { "saver", "bal", "high", "ult" })
            await AssertAccepted($$"""{"type":"SetPowerScheme","id":"{{Id}}","plan":"{{p}}"}""");
    }

    [Fact]
    public async Task Undo_resolutions_shape()
    {
        var op = Guid.NewGuid();
        string Req(string resolutions) => $$"""{"type":"Undo","id":"{{Id}}","operationId":"{{op}}","resolutions":{{resolutions}}}""";

        await AssertAccepted(Req("[]"));
        await AssertAccepted(Req("""[{"stepId":1,"action":"Overwrite"},{"stepId":2,"action":"Skip"}]"""));
        await AssertAccepted($$"""{"type":"Undo","id":"{{Id}}","operationId":"{{op}}"}""");

        await AssertRejected(Req("""[{"stepId":1,"action":"Overwrite"},{"stepId":1,"action":"Skip"}]"""));   // повтор шага
        await AssertRejected(Req("""[{"stepId":0,"action":"Skip"}]"""));
        await AssertRejected(Req("""[{"stepId":-5,"action":"Skip"}]"""));
        await AssertRejected(Req("""[{"stepId":1,"action":"Delete"}]"""));
        await AssertRejected(Req("""[{"stepId":1,"action":0}]"""));
        await AssertRejected(Req("""[{"stepId":1}]"""));
        await AssertRejected(Req("""[{"action":"Skip"}]"""));
        await AssertRejected(Req("""[{"stepId":1,"action":"Skip","sql":"x"}]"""));
        await AssertRejected(Req("[null]"));
        await AssertRejected(Req("\"all\""));
        await AssertRejected(Req("[" + string.Join(",", Enumerable.Range(1, 257).Select(i => $$"""{"stepId":{{i}},"action":"Skip"}""")) + "]"));
        await AssertRejected($$"""{"type":"Undo","id":"{{Id}}","operationId":"00000000-0000-0000-0000-000000000000"}""");
        await AssertRejected($$"""{"type":"Undo","id":"{{Id}}","operationId":"not-a-guid"}""");
        await AssertRejected($$"""{"type":"Undo","id":"{{Id}}"}""");
    }

    [Fact]
    public void Validate_never_throws_on_hostile_deserialized_values()
    {
        // Объекты, собранные в обход JSON (null в обязательных полях), тоже не должны ронять проверку.
        var hostile = new AgentRequest[]
        {
            new ApplyTweaksRequest { Id = Guid.NewGuid(), Changes = null! },
            new ApplyModeRequest { Id = Guid.NewGuid(), ModeId = null! },
            new SetStartupEntryRequest { Id = Guid.NewGuid(), EntryId = null!, Enabled = true },
            new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = null!, Start = StartMode.Manual },
            new SetServiceStartRequest { Id = Guid.NewGuid(), ServiceName = "Spooler", Start = (StartMode)99 },
            new SetTaskEnabledRequest { Id = Guid.NewGuid(), TaskPath = null!, Enabled = true },
            new SetPowerSchemeRequest { Id = Guid.NewGuid(), Plan = (PowerPlan)42 },
            new SetPowerSettingRequest { Id = Guid.NewGuid(), SettingId = null! },
            new UndoRequest { Id = Guid.NewGuid(), OperationId = Guid.Empty },
        };
        foreach (var r in hostile) Assert.NotNull(r.Validate());
    }

    [Theory]
    [InlineData("""{"type":"Ping","id":"11111111-2222-3333-4444-555555555555","extra":1}""")]
    [InlineData("""{"type":"Ping"}""")]
    [InlineData("""{"type":"Ping","id":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task Ping_stays_strict(string json) => await AssertRejected(json);
}
