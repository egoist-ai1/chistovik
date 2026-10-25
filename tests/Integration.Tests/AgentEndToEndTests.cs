using System.IO.Pipes;
using System.Security.Principal;
using Chistovik.Agent;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.Native.Ipc;
using Chistovik.TestKit;
using Microsoft.Data.Sqlite;
using Xunit;
using StartMode = Chistovik.Core.Ports.StartMode;

namespace Chistovik.IntegrationTests;

/// <summary>
/// Сквозные проверки: настоящий именованный канал и проверка клиента (Native), AgentComposition (Agent), SqliteJournal во временной
/// папке и подставные порты из TestKit. Живой реестр, службы, задачи и питание не затрагиваются.
/// </summary>
public sealed class AgentEndToEndTests : IDisposable
{
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    readonly string _root = Path.Combine(Path.GetTempPath(), "chistovik-e2e-" + Guid.NewGuid().ToString("N"));
    readonly List<IDisposable> _disposables = [];

    // Подставные порты общие для «перезапусков» агента, как настоящая система между запусками.
    readonly TestEnv _fakes = new();
    readonly FakeJournalDirectory _dir = new();

    string JournalDir => Path.Combine(_root, "journal");

    string DbPath => Path.Combine(JournalDir, AgentComposition.DatabaseFileName);

    public AgentEndToEndTests() => Directory.CreateDirectory(JournalDir);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    sealed class RandomIds : IIdGenerator
    {
        public Guid NewGuid() => Guid.NewGuid();
    }

    AgentPorts Ports() => new(_fakes.Registry, _fakes.Services, _fakes.Tasks, _fakes.Power, _fakes.Folders, _fakes.Files,
        _fakes.Security, _fakes.Uninstaller, _fakes.Trust, _dir, _fakes.Clock, new RandomIds());

    AgentComposition Build(List<string>? log = null)
    {
        var c = AgentComposition.Build(Ports(), JournalDir, PolicyContext.Fixed, log is null ? null : log.Add);
        _disposables.Add(c);
        return c;
    }

    // ---------- канал ----------

    static ExpectedClient ExpectSelf()
    {
        var image = ClientProbe.SelfImagePath();
        return new ExpectedClient(Environment.ProcessId, image, ClientVerifier.Sha256OfFile(image), ClientProbe.CurrentSession(), Me.Value);
    }

    sealed class Session(NamedPipeClientStream client, Task<bool> run) : IAsyncDisposable
    {
        public async Task<AgentResponse> Send(AgentRequest request)
        {
            await FrameCodec.WriteAsync(client, AgentJson.Serialize(request));
            var frame = await FrameCodec.ReadAsync(client).WaitAsync(TimeSpan.FromSeconds(20));
            return AgentJson.DeserializeResponse(frame ?? throw new IOException("Канал закрыт без ответа"));
        }

        /// <summary>Отправляет запрос и ждёт обрыва канала (агент «упал» посреди операции).</summary>
        public async Task SendExpectingDisconnect(AgentRequest request)
        {
            await FrameCodec.WriteAsync(client, AgentJson.Serialize(request));
            try
            {
                var frame = await FrameCodec.ReadAsync(client).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Null(frame);
            }
            catch (IOException)
            {
                // разрыв канала тоже обрыв
            }
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    static async Task<Session> Connect(AgentComposition composition)
    {
        var name = "Chistovik.e2e." + Guid.NewGuid().ToString("N");
        var server = new AgentPipeServer(name, Me, ExpectSelf(), new RequestDispatcher(composition.Handler), TimeSpan.FromSeconds(30));
        var run = server.RunAsync(CancellationToken.None);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return new Session(client, run);
    }

    // ---------- помощники ----------

    static Guid NewId() => Guid.NewGuid();

    static ApplyTweaksRequest Tweak(string id, bool enabled, Guid? requestId = null)
        => new() { Id = requestId ?? NewId(), Changes = [new TweakChange { TweakId = id, Enabled = enabled }] };

    static RegLocation Loc(string tweakId)
    {
        var t = TweakCatalog.Shared.FindTweak(tweakId)!;
        Assert.Equal("HKCU", t.Hive);
        return new RegLocation(RegRoot.User, Me.Value, t.Key, t.Value); // HKU\<SID проверенного клиента>
    }

    RegValueState Value(string tweakId) => _fakes.Registry.Get(Loc(tweakId));

    void Seed(string tweakId, uint v)
    {
        var l = Loc(tweakId);
        _fakes.Registry.Set(FakeRegistry.Path(l), l.ValueName, TestEnv.Dword(v));
    }

    IReadOnlyList<OperationSummary> Operations()
    {
        using var reader = new SqliteJournalReader(DbPath);
        return reader.List(0, 100);
    }

    OperationView Operation(Guid id)
    {
        using var reader = new SqliteJournalReader(DbPath);
        return reader.Get(id)!;
    }

    // ---------- тесты ----------

    [Fact]
    public async Task Ping_is_answered_and_journal_is_opened_in_a_trusted_directory()
    {
        var comp = Build();
        Assert.Equal(JournalMode.Ready, comp.Mode);
        Assert.True(File.Exists(DbPath));

        await using var s = await Connect(comp);
        var id = NewId();
        var pong = await s.Send(new PingRequest { Id = id });
        Assert.True(pong.Ok);
        Assert.Equal(id, pong.Id);
        Assert.Equal(ResponseCodes.Pong, pong.Code);
    }

    [Fact]
    public async Task ApplyTweaks_writes_value_for_verified_sid_and_journals_steps()
    {
        Seed("ext", 1);
        var comp = Build();
        await using var s = await Connect(comp);

        var r = await s.Send(Tweak("ext", true));

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(ResponseCodes.Ok, r.Code);
        Assert.Equal(0u, Value("ext").Data); // onValue из каталога
        Assert.Equal(1, _fakes.Registry.WriteCount);
        // Запись пошла в HKU\<SID клиента>, а не в чужой куст.
        Assert.All(_fakes.Registry.Writes, w => Assert.Contains(Me.Value, w.ResolvedKey, StringComparison.OrdinalIgnoreCase));

        var step = Assert.Single(r.Steps!);
        Assert.Equal(StepState.Done, step.State);
        var op = Operation(r.OperationId!.Value);
        Assert.Equal(OperationState.Done, op.Summary.State);
        Assert.Equal(Me.Value, op.UserSid);
        var rec = Assert.Single(op.Steps);
        Assert.Equal(StepKind.RegistryValue, rec.Kind);
        Assert.Equal(StepState.Done, rec.State);
    }

    [Fact]
    public async Task Repeated_request_id_returns_the_same_answer_without_second_write()
    {
        Seed("ext", 1);
        var comp = Build();
        await using var s = await Connect(comp);
        var request = Tweak("ext", true);

        var first = await s.Send(request);
        var writes = _fakes.Registry.WriteCount;
        var second = await s.Send(request);

        Assert.True(first.Ok);
        Assert.Equal(first.Ok, second.Ok);
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.Steps!.Select(x => (x.StepId, x.State, x.Code)), second.Steps!.Select(x => (x.StepId, x.State, x.Code)));
        Assert.Equal(writes, _fakes.Registry.WriteCount);
        Assert.Single(Operations());
    }

    [Fact]
    public async Task Undo_restores_the_previous_value()
    {
        Seed("ext", 1);
        var comp = Build();
        await using var s = await Connect(comp);
        var applied = await s.Send(Tweak("ext", true));
        Assert.Equal(0u, Value("ext").Data);

        var undone = await s.Send(new UndoRequest { Id = NewId(), OperationId = applied.OperationId!.Value });

        Assert.True(undone.Ok, undone.Code + undone.Message);
        Assert.Equal(1u, Value("ext").Data);
        Assert.Equal(OperationState.Undone, Operation(applied.OperationId!.Value).Summary.State);
        Assert.Contains(Operations(), o => o.Kind == OperationKind.Undo && o.State == OperationState.Done);
    }

    [Fact]
    public async Task Policy_denial_is_reported_and_system_is_untouched()
    {
        _fakes.AddThirdPartyService("WinDefend", 2);
        var comp = Build();
        await using var s = await Connect(comp);

        var r = await s.Send(new SetServiceStartRequest { Id = NewId(), ServiceName = "WinDefend", Start = StartMode.Disabled });

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Equal(0, _fakes.Services.WriteCount);
        Assert.Equal(2u, _fakes.Services.Get("WinDefend").StartType);
        Assert.Empty(_fakes.Registry.Writes);
        var op = Operation(r.OperationId!.Value);
        Assert.Equal(OperationState.Denied, op.Summary.State);
        Assert.DoesNotContain(op.Steps, x => x.State == StepState.Pending);
    }

    [Theory]
    [InlineData(true, StepState.Done, StepCodes.RecoveredDone, 1, 0)]
    [InlineData(false, StepState.Skipped, StepCodes.RecoveredNotApplied, 0, 1)]
    public async Task Restarted_agent_recovers_a_step_left_pending_by_a_crash(bool writeLanded, StepState expectedStep, string expectedCode, int recoveredDone, int notApplied)
    {
        Seed("ext", 1);
        var first = Build();
        // OperationCanceledException исполнение не перехватывает: шаг остаётся pending, как после обрыва процесса.
        _fakes.Registry.FailOnWrite(1, new OperationCanceledException("crash"), applyFirst: writeLanded);
        await using (var s = await Connect(first))
        {
            await s.SendExpectingDisconnect(Tweak("ext", true));
        }

        first.Dispose();
        Guid opId;
        using (var reader = new SqliteJournalReader(DbPath))
        {
            var op = Assert.Single(reader.List(0, 10));
            opId = op.Id;
            Assert.Equal(OperationState.Running, op.State);
            Assert.Equal(StepState.Pending, Assert.Single(reader.Get(opId)!.Steps).State);
        }

        var second = Build();

        Assert.Equal(JournalMode.Ready, second.Mode);
        Assert.NotNull(second.Recovery);
        Assert.Equal(recoveredDone, second.Recovery!.RecoveredDone);
        Assert.Equal(notApplied, second.Recovery.NotApplied);
        Assert.Equal(0, second.Recovery.Failed);
        Assert.Equal(1, second.Recovery.InterruptedOperations);
        var fixedOp = Operation(opId);
        Assert.Equal(OperationState.Interrupted, fixedOp.Summary.State);
        var step = Assert.Single(fixedOp.Steps);
        Assert.Equal(expectedStep, step.State);
        Assert.Equal(expectedCode, step.Code);
        Assert.Equal(writeLanded ? 0u : 1u, Value("ext").Data); // восстановление ничего не пишет в систему

        // После восстановления агент работает как обычно.
        await using var s2 = await Connect(second);
        var again = await s2.Send(Tweak("ext", true));
        Assert.True(again.Ok, again.Code + again.Message);
        Assert.Equal(0u, Value("ext").Data);
    }

    [Fact]
    public async Task Journal_directory_with_unsafe_acl_is_not_opened_and_requests_get_journal_untrusted()
    {
        _dir.Facts = FakeJournalDirectory.UsersCanWrite;
        Seed("ext", 1);
        var log = new List<string>();
        var comp = Build(log);

        Assert.Equal(JournalMode.Untrusted, comp.Mode);
        Assert.False(File.Exists(DbPath));
        Assert.Contains(log, l => l.StartsWith("journal-untrusted", StringComparison.Ordinal));

        await using var s = await Connect(comp);
        Assert.True((await s.Send(new PingRequest { Id = NewId() })).Ok); // Ping работает
        var r = await s.Send(Tweak("ext", true));

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.JournalUntrusted, r.Code);
        Assert.Equal(1u, Value("ext").Data);
        Assert.Empty(_fakes.Registry.Writes);
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void Missing_directory_is_created_protected_and_rechecked()
    {
        Directory.Delete(JournalDir);
        _dir.Facts = FakeJournalDirectory.Missing;

        var comp = Build();

        Assert.Equal(1, _dir.CreateCount);
        Assert.Equal(JournalMode.Ready, comp.Mode);
        Assert.True(File.Exists(DbPath));
        Assert.True(_dir.InspectCount >= 2); // проверка до и после создания
    }

    [Fact]
    public void Directory_that_is_still_untrusted_after_creation_stays_closed()
    {
        Directory.Delete(JournalDir);
        _dir.Facts = FakeJournalDirectory.Missing;
        _dir.CreatedIsTrusted = false;

        var comp = Build();

        Assert.Equal(JournalMode.Untrusted, comp.Mode);
        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public async Task Newer_journal_schema_disables_changes_with_its_own_code()
    {
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE future(x); PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }

        Seed("ext", 1);
        var comp = Build();

        Assert.Equal(JournalMode.NewerSchema, comp.Mode);
        await using var s = await Connect(comp);
        var r = await s.Send(Tweak("ext", true));
        Assert.Equal(ResponseCodes.JournalNewerSchema, r.Code);
        Assert.Empty(_fakes.Registry.Writes);
    }

    [Fact]
    public async Task Damaged_journal_file_gives_journal_unavailable()
    {
        File.WriteAllText(DbPath, new string('x', 4096));
        var comp = Build();

        Assert.Equal(JournalMode.Unavailable, comp.Mode);
        await using var s = await Connect(comp);
        var r = await s.Send(Tweak("ext", true));
        Assert.Equal(ResponseCodes.JournalUnavailable, r.Code);
    }

    [Fact]
    public void Operations_older_than_90_days_are_purged_at_start()
    {
        Build().Dispose();
        using (var j = new SqliteJournal(DbPath, _fakes.Clock))
        {
            var old = j.BeginOrGet(new NewOperation(Guid.NewGuid(), Guid.NewGuid(), OperationKind.Tweaks, "x", null, Me.Value, "t", "t"));
            j.Finish(old.Id, OperationState.Done, null);
        }

        _fakes.Clock.Advance(TimeSpan.FromDays(91));
        var again = Build();

        Assert.Equal(1, again.PurgedOperations);
        Assert.Empty(Operations());
    }
}
