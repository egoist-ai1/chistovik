using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Core.Tests.Journal;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Один сценарий на InMemoryJournal и на SqliteJournal: итоговые состояния операций и шагов совпадают (подставной журнал не врёт).</summary>
public class JournalContractTests
{
    static (OperationHandler Handler, ExecEnv Env) WithJournal(TestEnv t, IJournalWriter journal)
    {
        var env = new ExecEnv
        {
            Registry = t.Registry, Services = t.Services, Tasks = t.Tasks, Power = t.Power, Folders = t.Folders, Files = t.Files,
            Security = t.Security, Uninstaller = t.Uninstaller,
            Cleanup = t.Cleanup, RecycleBin = t.RecycleBin, Processes = t.Processes, RegistryTree = t.RegistryTree, Quarantine = t.Quarantine, CleanupLog = t.CleanupLog,
            Clock = t.Clock, Ids = t.Ids, Journal = journal,
            Gate = new StepGate(TweakCatalog.Shared, PowerCatalog.Shared, PolicyContext.Fixed, t.Trust),
            AgentVersion = "0.1-test", CatalogSha256 = "test",
        };
        return (new OperationHandler(env), env);
    }

    static List<string> Scenario(TestEnv t, OperationHandler h, IJournalWriter j)
    {
        AgentResponse Run(AgentRequest r) => h.HandleAsync(r, t.Client, CancellationToken.None).GetAwaiter().GetResult();
        t.SetTweakDword("ext", 1);
        var a = Run(new ApplyTweaksRequest { Id = t.NextRequestId(), Changes = [new TweakChange { TweakId = "ext", Enabled = true }] });
        t.SetTweakDword("ext", 9);
        var c = Run(new UndoRequest { Id = t.NextRequestId(), OperationId = a.OperationId!.Value });
        var stepId = j.Get(a.OperationId!.Value)!.Steps[0].Id;
        var u = Run(new UndoRequest
        {
            Id = t.NextRequestId(), OperationId = a.OperationId!.Value,
            Resolutions = [new UndoResolution { StepId = stepId, Action = ConflictAction.Overwrite }],
        });
        var m = Run(new ApplyModeRequest { Id = t.NextRequestId(), ModeId = "game" });
        var undoM = Run(new UndoRequest { Id = t.NextRequestId(), OperationId = m.OperationId!.Value });

        var lines = new List<string> { a.Code, c.Code, u.Code, m.Code, undoM.Code };
        foreach (var op in j.List(0, 100).Reverse())
        {
            var v = j.Get(op.Id)!;
            lines.Add($"{op.Kind}/{op.State}/{op.Code}");
            lines.AddRange(v.Steps.Select(s => $"  {s.Ord}:{s.Kind}:{s.State}:{s.Code}:{(s.RevertsStepId is null ? "-" : "rev")}"));
        }
        return lines;
    }

    [Fact]
    public void Sqlite_and_in_memory_journals_agree_on_a_full_scenario()
    {
        var t1 = new TestEnv();
        var mem = Scenario(t1, t1.Handler, t1.Journal);

        using var dir = new TempJournalDir();
        var t2 = new TestEnv();
        using var sql = new SqliteJournal(dir.Db, t2.Clock);
        var (h, _) = WithJournal(t2, sql);
        var real = Scenario(t2, h, sql);

        Assert.Equal(mem, real);
    }

    static List<string> CleanupScenario(TestEnv t, OperationHandler h, IJournalWriter j)
    {
        AgentResponse Run(AgentRequest r) => h.HandleAsync(r, t.Client, CancellationToken.None).GetAwaiter().GetResult();
        const string cv = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion";
        t.Security.Trust(@"C:\");
        t.Registry.SetString(cv + @"\Uninstall\Foo", "DisplayName", "Foo App");
        t.Registry.SetString(cv + @"\Uninstall\Foo", "UninstallString", "\"C:\\Program Files\\Foo\\unins000.exe\" /S");
        t.Registry.SetString(cv + @"\Run", "Foo", "\"C:\\Gone\\foo.exe\"");
        t.Registry.SetDword(cv + @"\SharedDLLs", @"C:\Gone\a.dll", 3);
        t.RecycleBin.Bytes = 500;
        var ids = new[] { "arp:machine:x64:Foo", "run:hklm64:Foo", "shareddll:x64:" + OrphanId.SharedDllHash(@"C:\Gone\a.dll") };
        var reg = Run(FindingsHarness.Request(t, ids));
        var clean = Run(new CleanCategoriesRequest { Id = t.NextRequestId(), Categories = ["recycle-bin"], MeasureOnly = false });
        var undoClean = Run(new UndoRequest { Id = t.NextRequestId(), OperationId = clean.OperationId!.Value });
        var undoReg = Run(new UndoRequest { Id = t.NextRequestId(), OperationId = reg.OperationId!.Value });
        var again = Run(new UndoRequest { Id = t.NextRequestId(), OperationId = reg.OperationId!.Value });

        var lines = new List<string> { reg.Code, clean.Code, undoClean.Code, undoReg.Code, again.Code };
        foreach (var op in j.List(0, 100).Reverse())
        {
            var v = j.Get(op.Id)!;
            lines.Add($"{op.Kind}/{op.State}/{op.Code}");
            lines.AddRange(v.Steps.Select(s => $"  {s.Ord}:{s.Kind}:{s.State}:{s.Code}:{(s.RevertsStepId is null ? "-" : "rev")}"));
        }
        lines.Add("ids:" + j.ExistingOperationIds().Count);
        return lines;
    }

    [Fact]
    public void Sqlite_and_in_memory_journals_agree_on_cleanup_and_registry_operations()
    {
        var t1 = new TestEnv();
        var mem = CleanupScenario(t1, t1.Handler, t1.Journal);

        using var dir = new TempJournalDir();
        var t2 = new TestEnv();
        using var sql = new SqliteJournal(dir.Db, t2.Clock);
        var (h, _) = WithJournal(t2, sql);
        var real = CleanupScenario(t2, h, sql);

        Assert.Equal(mem, real);
        Assert.Contains(mem, l => l.Contains("RegistryKeyDelete:Undone"));
        Assert.Contains(ResponseCodes.NotRevertible, mem);
    }

    [Fact]
    public void Existing_operation_ids_follow_purge_on_both_journals()
    {
        var t1 = new TestEnv();
        t1.SetTweakDword("ext", 1);
        t1.Tweaks(("ext", true));
        Assert.Single(t1.Journal.ExistingOperationIds());
        t1.Clock.Advance(TimeSpan.FromDays(91));
        t1.Journal.Purge(t1.Clock.UtcNow - TimeSpan.FromDays(90));
        Assert.Empty(t1.Journal.ExistingOperationIds());

        using var dir = new TempJournalDir();
        var t2 = new TestEnv();
        using var sql = new SqliteJournal(dir.Db, t2.Clock);
        var (h, _) = WithJournal(t2, sql);
        t2.SetTweakDword("ext", 1);
        AgentResponse Run(AgentRequest q) => h.HandleAsync(q, t2.Client, CancellationToken.None).GetAwaiter().GetResult();
        var r = Run(new ApplyTweaksRequest { Id = t2.NextRequestId(), Changes = [new TweakChange { TweakId = "ext", Enabled = true }] });
        Assert.Equal(r.OperationId!.Value, Assert.Single(sql.ExistingOperationIds()));
        t2.Clock.Advance(TimeSpan.FromDays(91));
        sql.Purge(t2.Clock.UtcNow - TimeSpan.FromDays(90));
        Assert.Empty(sql.ExistingOperationIds());
    }

    [Fact]
    public void Recovery_on_sqlite_closes_a_pending_step()
    {
        using var dir = new TempJournalDir();
        var t = new TestEnv();
        using var sql = new SqliteJournal(dir.Db, t.Clock);
        var (h, env) = WithJournal(t, sql);
        t.SetTweakDword("ext", 1);
        var op = Guid.NewGuid();
        sql.BeginOrGet(new NewOperation(op, Guid.NewGuid(), OperationKind.Tweaks, "tweak.ext", null, TestEnv.Sid, "0.1", "x"));
        var loc = t.Loc("ext");
        var before = StateJson.RegistryJson(TestEnv.Dword(1));
        var after = StateJson.RegistryJson(TestEnv.Dword(0));
        sql.AddPending(op, 0, StepKind.RegistryValue, StateJson.RegistryKey(loc), StateJson.RegistryTargetJson(loc), before, after);
        t.SetTweakDword("ext", 0);

        var rep = new RecoveryService(env).Run();

        Assert.Equal(1, rep.RecoveredDone);
        Assert.Equal(StepState.Done, sql.Get(op)!.Steps.Single().State);
        Assert.Equal(OperationState.Interrupted, sql.Get(op)!.Summary.State);
        Assert.NotNull(h);
    }
}
