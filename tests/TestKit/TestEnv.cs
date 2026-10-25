using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Все подставные порты и собранный из них ExecEnv и OperationHandler. Живую систему не трогает.</summary>
public sealed class TestEnv
{
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    public static readonly Guid Saver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid High = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltTemplate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public FakeRegistry Registry { get; } = new();
    public FakeServiceControl Services { get; } = new();
    public FakeTaskScheduler Tasks { get; } = new();
    public FakePowerControl Power { get; } = new();
    public FakeFileTrust Trust { get; } = new();
    public FakeUserFolders Folders { get; } = new();
    public FakeFileSystem Files { get; } = new();
    public FakeFileSecurity Security { get; } = new();
    public FakeUninstaller Uninstaller { get; } = new();
    public FakeCleanupFiles Cleanup { get; } = new();
    public FakeRecycleBin RecycleBin { get; } = new();
    public FakeProcessList Processes { get; } = new();
    public FakeRegistryTree RegistryTree { get; } = new();
    public FakeClock Clock { get; } = new();
    public SequentialIdGenerator Ids { get; } = new();
    public InMemoryJournal Journal { get; }

    public ExecEnv Env { get; private set; }
    public OperationHandler Handler { get; private set; }
    public ClientContext Client { get; set; } = new(Sid, 1234, 1);

    int _req;

    public TestEnv()
    {
        Journal = new InMemoryJournal(Clock);
        Power.AddScheme(Saver);
        Power.AddScheme(Balanced);
        Power.AddScheme(High);
        Power.Active = Balanced;
        Folders.SetUser(Sid, @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
        Folders.Common = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";
        Env = BuildEnv();
        Handler = new OperationHandler(Env);
    }

    ExecEnv BuildEnv() => new()
    {
        Registry = Registry,
        Services = Services,
        Tasks = Tasks,
        Power = Power,
        Folders = Folders,
        Files = Files,
        Security = Security,
        Uninstaller = Uninstaller,
        Cleanup = Cleanup,
        RecycleBin = RecycleBin,
        Processes = Processes,
        RegistryTree = RegistryTree,
        Clock = Clock,
        Ids = Ids,
        Journal = Journal,
        Gate = new StepGate(TweakCatalog.Shared, PowerCatalog.Shared, PolicyContext.Fixed, Trust),
        AgentVersion = "0.1-test",
        CatalogSha256 = "test",
    };

    /// <summary>Перезапуск агента: новый ExecEnv и обработчик поверх тех же портов и журнала (как после обрыва процесса).</summary>
    public void Restart()
    {
        Env = BuildEnv();
        Handler = new OperationHandler(Env);
    }

    public Guid NextRequestId() => new(0x7000_0000 + ++_req, 1, 1, [1, 1, 1, 1, 1, 1, 1, 1]);

    public AgentResponse Run(AgentRequest request) => Handler.HandleAsync(request, Client, CancellationToken.None).GetAwaiter().GetResult();

    public AgentResponse Tweaks(params (string Id, bool On)[] changes)
        => Run(new ApplyTweaksRequest { Id = NextRequestId(), Changes = changes.Select(c => new TweakChange { TweakId = c.Id, Enabled = c.On }).ToList() });

    public AgentResponse Mode(string id) => Run(new ApplyModeRequest { Id = NextRequestId(), ModeId = id });

    public AgentResponse Startup(string entryId, bool enabled) => Run(new SetStartupEntryRequest { Id = NextRequestId(), EntryId = entryId, Enabled = enabled });

    public AgentResponse Service(string name, Chistovik.Core.Ports.StartMode start) => Run(new SetServiceStartRequest { Id = NextRequestId(), ServiceName = name, Start = start });

    public AgentResponse Task(string path, bool enabled) => Run(new SetTaskEnabledRequest { Id = NextRequestId(), TaskPath = path, Enabled = enabled });

    public AgentResponse Plan(PowerPlan plan) => Run(new SetPowerSchemeRequest { Id = NextRequestId(), Plan = plan });

    public AgentResponse PowerSetting(string id, uint? ac, uint? dc) => Run(new SetPowerSettingRequest { Id = NextRequestId(), SettingId = id, Ac = ac, Dc = dc });

    public AgentResponse Uninstall(string findingId) => Run(new UninstallRequest { Id = NextRequestId(), FindingId = findingId });

    public AgentResponse Undo(Guid operationId, params (long StepId, ConflictAction Action)[] resolutions)
        => Run(new UndoRequest
        {
            Id = NextRequestId(),
            OperationId = operationId,
            Resolutions = resolutions.Length == 0 ? null : resolutions.Select(r => new UndoResolution { StepId = r.StepId, Action = r.Action }).ToList(),
        });

    // ---------- вспомогательное ----------

    public TweakEntry Tweak(string id) => TweakCatalog.Shared.FindTweak(id) ?? throw new ArgumentException("Нет твика " + id);

    /// <summary>Расположение значения твика по правилам каталога (HKCU → HKU\SID клиента).</summary>
    public RegLocation Loc(string tweakId)
    {
        var t = Tweak(tweakId);
        return t.Hive == "HKCU"
            ? new RegLocation(RegRoot.User, Sid, t.Key, t.Value)
            : new RegLocation(RegRoot.Machine, null, t.Key, t.Value);
    }

    public RegValueState TweakValue(string tweakId) => Registry.Get(Loc(tweakId));

    public void SetTweakValue(string tweakId, RegValueState v)
    {
        var l = Loc(tweakId);
        if (v.Exists) Registry.Set(FakeRegistry.Path(l), l.ValueName, v);
        else Registry.Remove(FakeRegistry.Path(l), l.ValueName);
    }

    public void SetTweakDword(string tweakId, uint v) => SetTweakValue(tweakId, new RegValueState(true, RegType.DWord, v));

    public static RegValueState Dword(uint v) => new(true, RegType.DWord, v);

    public static RegValueState Str(string v) => new(true, RegType.String, v);

    public static RegValueState Absent => new(false, RegType.None, null);

    /// <summary>Подходящая по умолчанию служба стороннего производителя в Program Files с подписью.</summary>
    public ServiceRaw AddThirdPartyService(string name, uint start = 2, bool delayed = false, int dependents = 0, string? image = null, uint typeFlags = 0x10)
    {
        var path = image ?? $"\"C:\\Program Files\\Foo\\{name}.exe\" -svc";
        var raw = new ServiceRaw(name, typeFlags, start, delayed, path, false, dependents, 0, false);
        Services.Add(raw);
        Trust.SetSigned($"C:\\Program Files\\Foo\\{name}.exe");
        return raw;
    }

    public TaskRaw AddUserTask(string path, bool enabled = true)
    {
        var t = new TaskRaw(path, enabled, "<Task/>", Sid, "LeastPrivilege", "InteractiveToken",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)], Sid);
        Tasks.Add(t);
        return t;
    }
}
