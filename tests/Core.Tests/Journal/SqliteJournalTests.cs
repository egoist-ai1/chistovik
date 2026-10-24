using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Core.Tests.Journal;

internal sealed class JournalFakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>Временная папка для базы журнала; удаляется целиком. Системные каталоги не используются.</summary>
internal sealed class TempJournalDir : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "chistovik-w1-" + Guid.NewGuid().ToString("N"));
    public string Db => Path.Combine(Dir, "chistovik.db");

    public TempJournalDir() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Dir, true);
        }
        catch (IOException) { }
    }
}

public class SqliteJournalTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static NewOperation NewOp(Guid? requestId = null, OperationKind kind = OperationKind.Tweaks, string subject = "tweak.x", Guid? reverts = null) =>
        new(Guid.NewGuid(), requestId, kind, subject, reverts, Sid, "0.1.0", "abc");

    private static SqliteConnection Raw(string db, bool readOnly = false)
    {
        var c = new SqliteConnection($"Data Source={db};Mode={(readOnly ? "ReadOnly" : "ReadWrite")};Pooling=False");
        c.Open();
        return c;
    }

    private static object? Scalar(string db, string sql)
    {
        using var c = Raw(db);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static long AddStep(SqliteJournal j, Guid op, int ord, string key, string before, string after, bool done = true)
    {
        var id = j.AddPending(op, ord, StepKind.RegistryValue, key, "{\"v\":1}", before, after);
        if (done) j.Complete(id, StepState.Done, null, null);
        return id;
    }

    [Fact]
    public void Creates_schema_v1_with_pragmas()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        Assert.Equal(1, j.SchemaVersion);
        Assert.Equal(1L, Scalar(t.Db, "PRAGMA user_version"));
        Assert.Equal(2L, Scalar(t.Db, "PRAGMA auto_vacuum")); // INCREMENTAL
        Assert.Equal("wal", Scalar(t.Db, "PRAGMA journal_mode"));
        var tables = new List<string>();
        using (var c = Raw(t.Db))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','index') ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        Assert.Contains("operation", tables);
        Assert.Contains("step", tables);
        Assert.Contains("meta", tables);
        Assert.Contains("ix_step_target", tables);
        Assert.Contains("ix_step_pending", tables);
        Assert.Contains("ix_operation_created", tables);
        Assert.Equal("1", Scalar(t.Db, "SELECT count(*) FROM meta WHERE key='created_utc'")?.ToString());
    }

    [Fact]
    public void Reopen_existing_database_keeps_data()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        var op = NewOp();
        using (var j = new SqliteJournal(t.Db, clock)) j.BeginOrGet(op);
        using var j2 = new SqliteJournal(t.Db, clock);
        Assert.NotNull(j2.Get(op.Id));
    }

    [Fact]
    public void Operation_and_steps_round_trip()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        using var j = new SqliteJournal(t.Db, clock);
        var reqId = Guid.NewGuid();
        var op = NewOp(reqId, OperationKind.Mode, "mode.work");
        var rec = j.BeginOrGet(op);
        Assert.False(rec.AlreadyExisted);
        Assert.Equal(OperationState.Running, rec.State);

        clock.UtcNow = clock.UtcNow.AddSeconds(5);
        var s1 = j.AddPending(op.Id, 0, StepKind.RegistryValue, "HKLM|k|n", "{\"t\":1}", "{\"v\":1,\"exists\":false}", "{\"v\":1,\"exists\":true}", "explorer");
        j.AddTerminal(op.Id, 1, StepKind.ServiceStart, "svc|x", "{\"name\":\"x\"}", null, "{\"v\":1}", StepState.Skipped, StepCodes.NoChange, "rule.a");
        j.Complete(s1, StepState.Done, null, null);
        clock.UtcNow = clock.UtcNow.AddSeconds(5);
        j.Finish(op.Id, OperationState.Done, null);

        var v = j.Get(op.Id)!;
        Assert.Equal(OperationKind.Mode, v.Summary.Kind);
        Assert.Equal("mode.work", v.Summary.Subject);
        Assert.Equal(OperationState.Done, v.Summary.State);
        Assert.Equal(Sid, v.UserSid);
        Assert.Null(v.RevertsOpId);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 10, TimeSpan.Zero), v.Summary.FinishedUtc);
        Assert.Equal(2, v.Steps.Count);
        Assert.Equal(s1, v.Steps[0].Id);
        Assert.Equal("explorer", v.Steps[0].Restart);
        Assert.Equal(StepState.Done, v.Steps[0].State);
        Assert.Equal("{\"v\":1,\"exists\":false}", v.Steps[0].BeforeJson);
        Assert.Equal(StepState.Skipped, v.Steps[1].State);
        Assert.Equal("none", v.Steps[1].Restart);
        Assert.Null(v.Steps[1].BeforeJson);
        Assert.Equal(StepCodes.NoChange, v.Steps[1].Code);
        Assert.Equal("rule.a", v.Steps[1].PolicyRule);
        Assert.Single(j.List(0, 10));
        Assert.Null(j.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Invalid_restart_and_pending_terminal_are_rejected()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var op = NewOp();
        j.BeginOrGet(op);
        Assert.Throws<ArgumentException>(() => j.AddPending(op.Id, 0, StepKind.RegistryValue, "k", "{}", "{}", "{}", "format-c"));
        Assert.Throws<ArgumentException>(() =>
            j.AddTerminal(op.Id, 0, StepKind.RegistryValue, "k", "{}", null, "{}", StepState.Pending, "x", null));
    }

    [Fact]
    public void Pending_becomes_done_and_get_pending_returns_only_pending()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var op = NewOp(kind: OperationKind.Startup);
        j.BeginOrGet(op);
        var p1 = j.AddPending(op.Id, 0, StepKind.StartupApproved, "k1", "{}", "{\"v\":1}", "{\"v\":1}");
        var p2 = j.AddPending(op.Id, 1, StepKind.StartupApproved, "k2", "{}", "{\"v\":1}", "{\"v\":1}");
        j.AddTerminal(op.Id, 2, StepKind.StartupApproved, "k3", "{}", null, "{\"v\":1}", StepState.Denied, StepCodes.PolicyDenied, "r");
        var pend = j.GetPending();
        Assert.Equal([p1, p2], pend.Select(p => p.StepId));
        Assert.All(pend, p => Assert.Equal(OperationKind.Startup, p.OpKind));

        j.Complete(p1, StepState.Done, null, null);
        Assert.Equal([p2], j.GetPending().Select(p => p.StepId));
        Assert.Throws<InvalidOperationException>(() => j.Complete(p1, StepState.Failed, "x", null)); // уже не pending
        j.Complete(p2, StepState.Failed, StepCodes.Win32(5), new string('я', 2000));
        Assert.Empty(j.GetPending());
        var err = j.Get(op.Id)!.Error!;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(err) <= 1024);
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Duplicate_request_id_returns_existing_operation()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var req = Guid.NewGuid();
        var first = NewOp(req);
        j.BeginOrGet(first);
        j.Finish(first.Id, OperationState.Done, "ok");
        var second = NewOp(req, subject: "other");
        var rec = j.BeginOrGet(second);
        Assert.True(rec.AlreadyExisted);
        Assert.Equal(first.Id, rec.Id);
        Assert.Equal(OperationState.Done, rec.State);
        Assert.Equal("ok", rec.Code);
        Assert.Equal("tweak.x", rec.Subject);
        Assert.Single(j.List(0, 10));
        // Без request_id дубликатов не ищем.
        j.BeginOrGet(NewOp());
        j.BeginOrGet(NewOp());
        Assert.Equal(3, j.List(0, 10).Count);
    }

    [Fact]
    public void CompleteRevert_updates_both_steps_in_one_transaction()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var op = NewOp();
        j.BeginOrGet(op);
        var orig = AddStep(j, op.Id, 0, "key", "{\"v\":1,\"n\":0}", "{\"v\":1,\"n\":1}");
        var undoOp = NewOp(kind: OperationKind.Undo, reverts: op.Id);
        j.BeginOrGet(undoOp);
        var undo = j.AddPending(undoOp.Id, 0, StepKind.RegistryValue, "key", "{}", "{\"v\":1,\"n\":1}", "{\"v\":1,\"n\":0}");

        j.CompleteRevert(undo, orig, "ok");

        var o = j.Get(op.Id)!.Steps.Single();
        var u = j.Get(undoOp.Id)!.Steps.Single();
        Assert.Equal(StepState.Undone, o.State);
        Assert.Equal(StepState.Done, u.State);
        Assert.Equal(orig, u.RevertsStepId);
        Assert.Equal(op.Id, j.Get(undoOp.Id)!.RevertsOpId);
    }

    [Fact]
    public void CompleteRevert_failure_between_updates_rolls_back_the_first_update()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var op = NewOp();
        j.BeginOrGet(op);
        var orig = AddStep(j, op.Id, 0, "key", "{\"v\":1}", "{\"v\":2}");
        var undo = j.AddPending(op.Id, 1, StepKind.RegistryValue, "key", "{}", "{\"v\":2}", "{\"v\":1}");

        // Сбой при втором UPDATE (исходный шаг → undone): триггер из отдельного соединения.
        using (var c = Raw(t.Db))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER fail_undone BEFORE UPDATE ON step WHEN NEW.state = 'undone' BEGIN SELECT RAISE(ABORT, 'boom'); END";
            cmd.ExecuteNonQuery();
        }

        Assert.ThrowsAny<SqliteException>(() => j.CompleteRevert(undo, orig, "ok"));

        var steps = j.Get(op.Id)!.Steps;
        Assert.Equal(StepState.Done, steps[0].State);
        Assert.Equal(StepState.Pending, steps[1].State); // первый UPDATE откатился
        Assert.Null(steps[1].RevertsStepId);
        Assert.Equal([undo], j.GetPending().Select(p => p.StepId));
    }

    [Fact]
    public void CompleteRevert_with_wrong_original_state_is_atomic_failure()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var op = NewOp();
        j.BeginOrGet(op);
        var notDone = AddStep(j, op.Id, 0, "key", "{\"v\":1}", "{\"v\":2}", done: false); // pending
        var undo = j.AddPending(op.Id, 1, StepKind.RegistryValue, "key", "{}", "{}", "{}");
        Assert.Throws<InvalidOperationException>(() => j.CompleteRevert(undo, notDone, "ok"));
        Assert.Equal(StepState.Pending, j.Get(op.Id)!.Steps[1].State);
        Assert.ThrowsAny<Exception>(() => j.CompleteRevert(undo, 99999, "ok")); // FK на reverts_step_id
        Assert.Equal(StepState.Pending, j.Get(op.Id)!.Steps[1].State);
    }

    [Fact]
    public void LatestEffective_is_the_last_done_step_not_undone_later()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        Assert.Null(j.LatestEffective("key"));

        var a = NewOp();
        j.BeginOrGet(a);
        var sa = AddStep(j, a.Id, 0, "key", "{\"x\":0}", "{\"x\":1}");
        Assert.Equal(sa, j.LatestEffective("key")!.Id);

        var b = NewOp();
        j.BeginOrGet(b);
        var sb = AddStep(j, b.Id, 0, "key", "{\"x\":1}", "{\"x\":2}");
        j.AddPending(b.Id, 1, StepKind.RegistryValue, "key", "{}", "{}", "{\"x\":3}"); // pending не считается
        j.AddTerminal(b.Id, 2, StepKind.RegistryValue, "key", "{}", null, "{\"x\":4}", StepState.Failed, StepCodes.AccessDenied, null);
        Assert.Equal(sb, j.LatestEffective("key")!.Id);
        Assert.Null(j.LatestEffective("other"));

        // Откат B: шаг отмены становится последним эффективным (after = {"x":1}), B исключается.
        var u = NewOp(kind: OperationKind.Undo, reverts: b.Id);
        j.BeginOrGet(u);
        var su = j.AddPending(u.Id, 0, StepKind.RegistryValue, "key", "{}", "{\"x\":2}", "{\"x\":1}");
        j.CompleteRevert(su, sb, "ok");
        var eff = j.LatestEffective("key")!;
        Assert.Equal(su, eff.Id);
        Assert.Equal("{\"x\":1}", eff.AfterJson);
        Assert.Equal(u.Id, eff.OpId);
    }

    [Fact]
    public void Purge_removes_old_finished_operations_with_steps_but_keeps_running()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        using var j = new SqliteJournal(t.Db, clock);
        var t0 = clock.UtcNow;

        var old = NewOp(subject: "old");
        j.BeginOrGet(old);
        AddStep(j, old.Id, 0, "k-old", "{}", "{}");
        j.Finish(old.Id, OperationState.Done, null);

        var oldRunning = NewOp(subject: "old-running");
        j.BeginOrGet(oldRunning);
        AddStep(j, oldRunning.Id, 0, "k-run", "{}", "{}");

        clock.UtcNow = t0.AddDays(60);
        var recent = NewOp(subject: "recent");
        j.BeginOrGet(recent);
        AddStep(j, recent.Id, 0, "k-new", "{}", "{}");
        j.Finish(recent.Id, OperationState.Partial, "p");

        clock.UtcNow = t0.AddDays(100);
        var removed = j.Purge(clock.UtcNow.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Null(j.Get(old.Id));
        Assert.NotNull(j.Get(oldRunning.Id)); // running не удаляется, как бы стар он ни был
        Assert.Single(j.Get(oldRunning.Id)!.Steps);
        Assert.NotNull(j.Get(recent.Id));
        Assert.Null(j.LatestEffective("k-old")); // шаги ушли каскадом
        Assert.Equal(0L, Scalar(t.Db, "SELECT count(*) FROM step WHERE op_id = '" + old.Id.ToString("D") + "'"));
    }

    [Fact]
    public void Writer_refuses_newer_schema_but_reader_can_read()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        var op = NewOp();
        using (var j = new SqliteJournal(t.Db, clock)) j.BeginOrGet(op);
        using (var c = Raw(t.Db))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<JournalException>(() => new SqliteJournal(t.Db, clock));
        Assert.Equal("journal-newer-schema", ex.Code);

        using var r = new SqliteJournalReader(t.Db);
        Assert.True(r.IsNewerSchema);
        Assert.Equal(99, r.SchemaVersion);
        Assert.NotNull(r.Get(op.Id));
        Assert.Equal(99L, Scalar(t.Db, "PRAGMA user_version")); // писатель базу не тронул
    }

    [Fact]
    public void Reader_reads_file_without_write_permission()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        var op = NewOp();
        using (var j = new SqliteJournal(t.Db, clock))
        {
            j.BeginOrGet(op);
            AddStep(j, op.Id, 0, "k", "{\"v\":1}", "{\"v\":2}");
            j.Finish(op.Id, OperationState.Done, null);
        }
        // После Dispose писатель сделал checkpoint и вернул journal_mode=DELETE: побочных файлов нет (§9).
        Assert.False(File.Exists(t.Db + "-wal"));
        Assert.False(File.Exists(t.Db + "-shm"));
        File.SetAttributes(t.Db, FileAttributes.ReadOnly);

        using var r = new SqliteJournalReader(t.Db);
        Assert.False(r.IsNewerSchema);
        var v = r.Get(op.Id)!;
        Assert.Equal(OperationState.Done, v.Summary.State);
        Assert.Single(v.Steps);
        Assert.Single(r.List(0, 5));
        Assert.NotNull(r.LatestEffective("k"));
    }

    [Fact]
    public void Reader_connection_cannot_write()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        using var c = Raw(t.Db, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM operation";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Reader_reads_in_parallel_while_writer_writes()
    {
        using var t = new TempJournalDir();
        var clock = new JournalFakeClock();
        using var j = new SqliteJournal(t.Db, clock);
        var first = NewOp();
        j.BeginOrGet(first);
        using var r = new SqliteJournalReader(t.Db);

        const int total = 200;
        var stop = false;
        Exception? readerError = null;
        var reads = 0;
        var readerTask = Task.Run(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    var list = r.List(0, 1000);
                    foreach (var s in list) r.Get(s.Id);
                    reads++;
                }
            }
            catch (Exception e) { readerError = e; }
        });

        for (var i = 0; i < total; i++)
        {
            var op = NewOp(subject: "op" + i);
            j.BeginOrGet(op);
            var s = j.AddPending(op.Id, 0, StepKind.RegistryValue, "k" + i, "{}", "{}", "{}");
            j.Complete(s, StepState.Done, null, null);
            j.Finish(op.Id, OperationState.Done, null);
        }
        Volatile.Write(ref stop, true);
        await readerTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(readerError);
        Assert.True(reads > 0);
        Assert.Equal(total + 1, r.List(0, 1000).Count);
    }

    [Fact]
    public void Corrupt_file_gives_clear_exception_and_is_not_recreated()
    {
        using var t = new TempJournalDir();
        var junk = new byte[8192];
        new Random(42).NextBytes(junk);
        File.WriteAllBytes(t.Db, junk);

        var ex = Assert.Throws<JournalException>(() => new SqliteJournal(t.Db, new JournalFakeClock()));
        Assert.Equal("journal-corrupt", ex.Code);
        var ex2 = Assert.Throws<JournalException>(() => new SqliteJournalReader(t.Db));
        Assert.Equal("journal-corrupt", ex2.Code);
        Assert.Equal(junk, File.ReadAllBytes(t.Db)); // файл не перезаписан
    }

    [Fact]
    public void Foreign_database_with_tables_is_not_adopted()
    {
        using var t = new TempJournalDir();
        using (var c = Raw0(t.Db))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE other(a)";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<JournalException>(() => new SqliteJournal(t.Db, new JournalFakeClock()));
        Assert.Equal("journal-corrupt", ex.Code);

        static SqliteConnection Raw0(string db)
        {
            var c = new SqliteConnection($"Data Source={db};Pooling=False");
            c.Open();
            return c;
        }
    }

    [Fact]
    public void Missing_file_for_reader_is_reported()
    {
        using var t = new TempJournalDir();
        var ex = Assert.Throws<JournalException>(() => new SqliteJournalReader(t.Db));
        Assert.Equal("journal-not-found", ex.Code);
        Assert.False(File.Exists(t.Db)); // читатель файл не создаёт
    }

    [Fact]
    public void Hostile_strings_are_stored_as_data_and_do_not_break_schema()
    {
        using var t = new TempJournalDir();
        using var j = new SqliteJournal(t.Db, new JournalFakeClock());
        var evil = "it's \"quoted\" '; DROP TABLE step; -- \\ \n привет 🙂 \u0000x";
        var json = "{\"v\":1,\"key\":\"" + "it's \\\"q\\\" '; DROP TABLE operation; --" + "\",\"u\":\"привет 🙂\"}";
        var op = new NewOperation(Guid.NewGuid(), Guid.NewGuid(), OperationKind.Tweaks, evil, null, evil, evil, evil);
        j.BeginOrGet(op);
        var key = "HKLM|'; DROP TABLE step; --";
        var s = j.AddPending(op.Id, 0, StepKind.RegistryValue, key, json, json, json);
        j.Complete(s, StepState.Failed, StepCodes.NotFound, evil);
        j.Finish(op.Id, OperationState.Failed, evil);

        var v = j.Get(op.Id)!;
        Assert.Equal(evil, v.Summary.Subject);
        Assert.Equal(evil, v.UserSid);
        Assert.Equal(json, v.Steps[0].TargetJson);
        Assert.Equal(json, v.Steps[0].AfterJson);
        Assert.Equal(key, v.Steps[0].TargetKey);
        Assert.Equal(evil, v.Error);
        j.Complete(j.AddPending(op.Id, 1, StepKind.RegistryValue, key, json, json, json), StepState.Done, null, null);
        Assert.Equal(key, j.LatestEffective(key)!.TargetKey);
        Assert.Null(j.LatestEffective("HKLM|' OR '1'='1"));
        Assert.Equal(2L, Scalar(t.Db, "SELECT count(*) FROM step"));
        Assert.Equal(1L, Scalar(t.Db, "SELECT count(*) FROM operation"));
    }

    [Fact]
    public void Dispose_is_idempotent_and_leaves_journal_mode_delete()
    {
        using var t = new TempJournalDir();
        var j = new SqliteJournal(t.Db, new JournalFakeClock());
        j.BeginOrGet(NewOp());
        j.Dispose();
        j.Dispose();
        Assert.Equal("delete", Scalar(t.Db, "PRAGMA journal_mode"));
        // Повторное открытие снова включает WAL.
        using var j2 = new SqliteJournal(t.Db, new JournalFakeClock());
        Assert.Equal("wal", Scalar(t.Db, "PRAGMA journal_mode"));
    }
}
