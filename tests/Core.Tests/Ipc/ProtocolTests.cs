using System.Text;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.CoreTests.Ipc;

public class ProtocolRoundTripTests
{
    static readonly Guid Rid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static IEnumerable<object[]> Requests()
    {
        var firstTweak = TweakCatalog.Shared.Tweaks[0].Id;
        var secondTweak = TweakCatalog.Shared.Tweaks[1].Id;
        var mode = TweakCatalog.Shared.Modes[0].Id;
        yield return new object[] { new PingRequest { Id = Rid } };
        yield return new object[]
        {
            new ApplyTweaksRequest
            {
                Id = Rid,
                Changes = [new TweakChange { TweakId = firstTweak, Enabled = true }, new TweakChange { TweakId = secondTweak, Enabled = false }],
            },
        };
        yield return new object[] { new ApplyModeRequest { Id = Rid, ModeId = mode } };
        yield return new object[] { new SetStartupEntryRequest { Id = Rid, EntryId = "run:hklm64:OneDrive", Enabled = false } };
        yield return new object[] { new SetStartupEntryRequest { Id = Rid, EntryId = "folder:user:Мой ярлык.lnk", Enabled = true } };
        yield return new object[] { new SetServiceStartRequest { Id = Rid, ServiceName = "Spooler", Start = StartMode.AutomaticDelayed } };
        yield return new object[] { new SetTaskEnabledRequest { Id = Rid, TaskPath = @"\Vendor\Updater\Check", Enabled = false } };
        foreach (var plan in Enum.GetValues<PowerPlan>())
            yield return new object[] { new SetPowerSchemeRequest { Id = Rid, Plan = plan } };
        yield return new object[] { new SetPowerSettingRequest { Id = Rid, SettingId = "min", Ac = 100, Dc = 5 } };
        yield return new object[] { new SetPowerSettingRequest { Id = Rid, SettingId = "usb", Ac = 0 } };
        yield return new object[] { new UndoRequest { Id = Rid, OperationId = Guid.NewGuid() } };
        yield return new object[]
        {
            new UndoRequest
            {
                Id = Rid,
                OperationId = Guid.NewGuid(),
                Resolutions = [new UndoResolution { StepId = 7, Action = ConflictAction.Overwrite }, new UndoResolution { StepId = 9, Action = ConflictAction.Skip }],
            },
        };
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Request_roundtrips_and_is_valid(AgentRequest request)
    {
        Assert.Null(request.Validate());
        var back = AgentJson.DeserializeRequest(AgentJson.Serialize(request));
        Assert.Equal(request.GetType(), back.GetType());
        // Сравниваем по повторной сериализации: записи со списками не равны по ссылке.
        Assert.Equal(Encoding.UTF8.GetString(AgentJson.Serialize(request)), Encoding.UTF8.GetString(AgentJson.Serialize(back)));
        Assert.Null(back.Validate());
    }

    [Fact]
    public void Enums_are_written_as_names()
    {
        var json = Encoding.UTF8.GetString(AgentJson.Serialize(new SetServiceStartRequest { Id = Rid, ServiceName = "Spooler", Start = StartMode.Disabled }));
        Assert.Contains("\"start\":\"Disabled\"", json);
        json = Encoding.UTF8.GetString(AgentJson.Serialize(new SetPowerSchemeRequest { Id = Rid, Plan = PowerPlan.Ult }));
        Assert.Contains("\"plan\":\"ult\"", json);
    }

    [Fact]
    public void Response_roundtrips_with_steps_and_conflicts()
    {
        var op = Guid.NewGuid();
        var other = Guid.NewGuid();
        var r = new AgentResponse(Rid, false, ResponseCodes.Conflict, "есть конфликты", op,
            [new StepResult(1, 0, StepKind.RegistryValue, StepState.Done, null), new StepResult(2, 1, StepKind.ServiceStart, StepState.Failed, "win32:5")],
            [new ConflictInfo(2, StepKind.ServiceStart, "Вручную", other)]);
        var back = AgentJson.DeserializeResponse(AgentJson.Serialize(r));
        Assert.Equal(r.Id, back.Id);
        Assert.Equal(r.Code, back.Code);
        Assert.Equal(op, back.OperationId);
        Assert.Equal(StepKind.ServiceStart, back.Steps![1].Kind);
        Assert.Equal(StepState.Failed, back.Steps[1].State);
        Assert.Equal("win32:5", back.Steps[1].Code);
        Assert.Equal(other, back.Conflicts![0].ChangedByOperationId);
        Assert.Equal("Вручную", back.Conflicts[0].CurrentDisplay);
    }

    [Fact]
    public void Simple_response_roundtrips()
    {
        var back = AgentJson.DeserializeResponse(AgentJson.Serialize(new AgentResponse(Rid, true, ResponseCodes.Pong)));
        Assert.True(back.Ok);
        Assert.Null(back.Steps);
        Assert.Null(back.Conflicts);
        Assert.Null(back.OperationId);
    }

    [Fact]
    public void Response_codes_are_the_closed_spec_list()
    {
        var codes = typeof(ResponseCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] spec =
        [
            "ok", "no-change", "partial", "policy-denied", "not-found", "conflict", "busy", "journal-unavailable",
            "journal-untrusted", "journal-newer-schema", "bad-request", "unknown-operation", "internal", "pong",
        ];
        Assert.Equal(spec.OrderBy(x => x, StringComparer.Ordinal).ToArray(), codes);
    }

    [Fact]
    public void Power_setting_ids_match_power_json()
    {
        Assert.Equal(PowerSettingIds.All.OrderBy(x => x), PowerCatalog.Shared.Settings.Select(s => s.Id).OrderBy(x => x));
    }
}

public class DispatcherHandlerTests
{
    sealed class Recorder : IOperationHandler
    {
        public List<AgentRequest> Seen { get; } = [];
        public Func<AgentRequest, AgentResponse>? Reply { get; set; }

        public Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(Reply?.Invoke(request) ?? new AgentResponse(request.Id, true, ResponseCodes.Ok));
        }
    }

    static readonly ClientContext Client = new("S-1-5-21-1-2-3-1001", 1, 1);
    static readonly string ValidApply = $$"""{"type":"ApplyMode","id":"11111111-2222-3333-4444-555555555555","modeId":"{{TweakCatalog.Shared.Modes[0].Id}}"}""";

    [Fact]
    public async Task Valid_request_reaches_handler_with_client()
    {
        var h = new Recorder();
        var r = await new RequestDispatcher(h).HandleAsync(Encoding.UTF8.GetBytes(ValidApply), Client);
        Assert.True(r.Ok);
        Assert.Single(h.Seen);
        Assert.IsType<ApplyModeRequest>(h.Seen[0]);
    }

    [Fact]
    public async Task Invalid_request_never_reaches_handler()
    {
        var h = new Recorder();
        var r = await new RequestDispatcher(h).HandleAsync(
            """{"type":"ApplyMode","id":"11111111-2222-3333-4444-555555555555","modeId":"nope"}"""u8.ToArray(), Client);
        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.BadRequest, r.Code);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Without_handler_privileged_operations_are_unknown()
    {
        var r = await new RequestDispatcher().HandleAsync(Encoding.UTF8.GetBytes(ValidApply), Client);
        Assert.Equal(ResponseCodes.UnknownOperation, r.Code);
    }

    [Fact]
    public async Task Ping_is_answered_without_handler()
    {
        var h = new Recorder();
        var r = await new RequestDispatcher(h).HandleAsync(
            """{"type":"Ping","id":"11111111-2222-3333-4444-555555555555"}"""u8.ToArray(), Client);
        Assert.Equal(ResponseCodes.Pong, r.Code);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Handler_exception_becomes_internal_without_details()
    {
        var h = new Recorder { Reply = _ => throw new InvalidOperationException("секрет из исключения") };
        var r = await new RequestDispatcher(h).HandleAsync(Encoding.UTF8.GetBytes(ValidApply), Client);
        Assert.Equal(ResponseCodes.Internal, r.Code);
        Assert.DoesNotContain("секрет", r.Message);
    }

    [Fact]
    public async Task Missing_client_context_is_internal()
    {
        var r = await new RequestDispatcher(new Recorder()).HandleAsync(Encoding.UTF8.GetBytes(ValidApply));
        Assert.Equal(ResponseCodes.Internal, r.Code);
    }

    [Fact]
    public async Task Empty_request_id_is_rejected()
    {
        var r = await new RequestDispatcher().HandleAsync(
            """{"type":"Ping","id":"00000000-0000-0000-0000-000000000000"}"""u8.ToArray());
        Assert.Equal(ResponseCodes.BadRequest, r.Code);
    }
}
