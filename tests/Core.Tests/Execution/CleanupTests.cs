using System.Text;
using System.Text.Json;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Очистка диска через агента (C2): удаление по хэндлу, ссылки, подмена пути, занятые файлы, измерение, обрыв.</summary>
public class CleanupTests
{
    const string Temp = @"C:\Users\Test\AppData\Local\Temp";
    const string Cbs = @"C:\Windows\Logs\CBS";

    static CleanupState State(AgentResponse r, string keyPart)
    {
        var f = r.Facts!.Single(x => x.Key.Contains(keyPart, StringComparison.Ordinal));
        return JsonSerializer.Deserialize(f.Json, JournalJson.Default.CleanupState)!;
    }

    static StepRecord Step(TestEnv t, AgentResponse r, int ord = 0) => t.Journal.Get(r.OperationId!.Value)!.Steps.Single(s => s.Ord == ord);

    [Fact]
    public void Old_file_is_deleted_and_fresh_file_is_kept()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp", ageHours: 100, length: 1000);
        t.Cleanup.AddFile(Temp + @"\fresh.tmp", ageHours: 1);

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Temp + @"\old.tmp"], t.Cleanup.Deleted);
        Assert.True(t.Cleanup.Exists(Temp + @"\fresh.tmp"));
        Assert.True(t.Cleanup.Exists(Temp));
        var s = Step(t, r);
        Assert.Equal(StepKind.CleanupCategory, s.Kind);
        Assert.Equal(StepState.Done, s.State);
        Assert.False(s.Revertible);
        Assert.Equal(OperationKind.Cleanup, t.Journal.Get(r.OperationId!.Value)!.Summary.Kind);
        var st = State(r, "temp-user");
        Assert.Equal(1, st.Deleted);
        Assert.Equal(1000, st.FreedBytes);
        Assert.Equal(1, st.Skipped!["too-fresh"]);
    }

    [Fact]
    public void Reparse_directory_and_file_are_neither_entered_nor_deleted()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.AddReparseDir(Temp + @"\Junction");
        t.Cleanup.AddFile(Temp + @"\Junction\inner.tmp");
        t.Cleanup.AddReparseFile(Temp + @"\link.tmp");

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Temp + @"\old.tmp"], t.Cleanup.Deleted);
        Assert.DoesNotContain(Temp + @"\Junction", t.Cleanup.Enumerated);
        Assert.DoesNotContain(Temp + @"\Junction", t.Cleanup.Opened);
        Assert.DoesNotContain(Temp + @"\link.tmp", t.Cleanup.Opened);
        Assert.True(t.Cleanup.Exists(Temp + @"\Junction\inner.tmp"));
        Assert.Equal(2, State(r, "temp-user").Skipped!["reparse"]);
    }

    [Fact]
    public void Final_path_outside_root_after_open_leaves_the_file()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\swapped.tmp");
        t.Cleanup.SetFinal(Temp + @"\swapped.tmp", FakeCleanupFiles.Volume + @"\Windows\System32\drivers\etc\hosts");

        var r = t.Clean("temp-user");

        Assert.Empty(t.Cleanup.Deleted);
        Assert.True(t.Cleanup.Exists(Temp + @"\swapped.tmp"));
        Assert.Equal(1, State(r, "temp-user").Skipped!["not-under-root"]);
        Assert.Equal(0, State(r, "temp-user").Deleted);
    }

    [Fact]
    public void Root_that_is_a_reparse_point_denies_the_whole_operation()
    {
        var t = new TestEnv();
        t.Cleanup.AddReparseDir(Temp);
        t.Cleanup.AddFile(Temp + @"\old.tmp");

        var r = t.Clean("temp-user");

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Cleanup.Deleted);
        Assert.Empty(t.Cleanup.Opened);
        Assert.Equal(StepState.Denied, Step(t, r).State);
    }

    [Fact]
    public void Missing_root_is_skipped_not_denied()
    {
        var t = new TestEnv();

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(ResponseCodes.NoChange, r.Code);
    }

    [Fact]
    public void Denial_of_one_category_denies_all_and_deletes_nothing()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.AddReparseDir(@"C:\Users\Test\AppData\Local\D3DSCache");

        var r = t.Clean("temp-user", "d3d-shader-cache");

        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Cleanup.Deleted);
    }

    [Fact]
    public void Locked_file_is_skipped_as_in_use_and_the_rest_is_deleted()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\busy.tmp");
        t.Cleanup.Lock(Temp + @"\busy.tmp");
        t.Cleanup.AddFile(Temp + @"\free.tmp");

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Temp + @"\free.tmp"], t.Cleanup.Deleted);
        var s = Step(t, r);
        Assert.Equal(StepState.Done, s.State);
        Assert.Equal(StepCodes.InUse, s.Code);
        var st = State(r, "temp-user");
        Assert.Equal(1, st.Deleted);
        Assert.Equal(1, st.Skipped!["in-use"]);
    }

    [Fact]
    public void Access_denied_is_a_skip_with_reason()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\locked.tmp");
        t.Cleanup.Deny(Temp + @"\locked.tmp");

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(1, State(r, "temp-user").Skipped!["access-denied"]);
        Assert.True(t.Cleanup.Exists(Temp + @"\locked.tmp"));
    }

    [Fact]
    public void Directory_is_deleted_after_its_children_and_non_empty_directory_stays()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\a\b\old.tmp");
        t.Cleanup.AddFile(Temp + @"\keep\busy.tmp");
        t.Cleanup.Lock(Temp + @"\keep\busy.tmp");

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Temp + @"\a\b\old.tmp", Temp + @"\a\b", Temp + @"\a"], t.Cleanup.Deleted);
        Assert.True(t.Cleanup.Exists(Temp + @"\keep\busy.tmp"));
        Assert.True(t.Cleanup.Exists(Temp + @"\keep"));
        Assert.True(t.Cleanup.Exists(Temp));
        var st = State(r, "temp-user");
        Assert.Equal(3, st.Deleted);
        Assert.Equal(1, st.Skipped!["not-empty"]);
    }

    [Fact]
    public void Keep_names_are_never_deleted()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Cbs + @"\CBS.log", ageHours: 5000);
        t.Cleanup.AddFile(Cbs + @"\old.log", ageHours: 5000);
        t.Cleanup.AddFile(Cbs + @"\young.log", ageHours: 100);

        var r = t.Clean("logs-cbs");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Cbs + @"\old.log"], t.Cleanup.Deleted);
        Assert.True(t.Cleanup.Exists(Cbs + @"\CBS.log"));
        Assert.Equal(1, State(r, "logs-cbs").Skipped!["keep-name"]);
    }

    [Fact]
    public void Patterns_limit_what_is_deleted_and_are_not_counted_as_skips()
    {
        var t = new TestEnv();
        const string dir = @"C:\Users\Test\AppData\Local\Microsoft\Windows\Explorer";
        t.Cleanup.AddFile(dir + @"\thumbcache_32.db");
        t.Cleanup.AddFile(dir + @"\other.dat");

        var r = t.Clean("thumbcache");

        Assert.Equal([dir + @"\thumbcache_32.db"], t.Cleanup.Deleted);
        Assert.Null(State(r, "thumbcache").Skipped);
    }

    [Fact]
    public void Undo_of_cleanup_is_not_revertible()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        var op = t.Clean("temp-user").OperationId!.Value;
        var ops = t.Journal.AllOperations.Count;

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(ResponseCodes.NotRevertible, u.Code);
        Assert.Equal(ops, t.Journal.AllOperations.Count);
    }

    [Fact]
    public void Repeat_run_after_full_cleanup_changes_nothing()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        Assert.True(t.Clean("temp-user").Ok);
        var deleted = t.Cleanup.Deleted.Count;

        var again = t.Clean("temp-user");

        Assert.True(again.Ok, again.Code + again.Message);
        Assert.Equal(ResponseCodes.NoChange, again.Code);
        Assert.Equal(StepState.Skipped, Step(t, again).State);
        Assert.Equal(deleted, t.Cleanup.Deleted.Count);
    }

    [Fact]
    public void Repeat_run_with_leftovers_is_done_with_zero_deleted()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\keep\busy.tmp");
        t.Cleanup.Lock(Temp + @"\keep\busy.tmp");
        Assert.True(t.Clean("temp-user").Ok);

        var again = t.Clean("temp-user");

        Assert.True(again.Ok, again.Code + again.Message);
        Assert.Equal(StepState.Done, Step(t, again).State);
        Assert.Equal(0, State(again, "temp-user").Deleted);
    }

    [Fact]
    public void Measure_counts_without_deleting_or_writing_the_journal()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp", length: 700);
        t.Cleanup.AddFile(Temp + @"\old2.tmp", length: 300);
        t.Cleanup.AddFile(Temp + @"\fresh.tmp", ageHours: 1, length: 9999);
        var ops = t.Journal.AllOperations.Count;
        var steps = t.Journal.AllSteps.Count;

        var r = t.MeasureCleanup("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Null(r.OperationId);
        Assert.Equal(ops, t.Journal.AllOperations.Count);
        Assert.Equal(steps, t.Journal.AllSteps.Count);
        Assert.Empty(t.Cleanup.Deleted);
        Assert.Empty(t.Cleanup.Opened);
        var fact = r.Facts!.Single();
        Assert.StartsWith("cleanup:", fact.Key);
        var st = State(r, "temp-user");
        Assert.Equal(1000, st.Bytes);
        Assert.Equal(2, st.Files);
    }

    [Fact]
    public void Measure_of_unknown_category_is_not_found()
    {
        var t = new TestEnv();

        var r = t.MeasureCleanup("no-such-category");

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
    }

    [Fact]
    public void Unknown_category_is_not_found_and_handler_category_is_denied()
    {
        var t = new TestEnv();

        Assert.Equal(ResponseCodes.NotFound, t.Clean("no-such-category").Code);
        Assert.Equal(ResponseCodes.PolicyDenied, t.Clean("wu-cleanup").Code);
    }

    [Fact]
    public void Running_browser_skips_the_category()
    {
        var t = new TestEnv();
        const string cache = @"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache";
        t.Cleanup.AddFile(cache + @"\f_000001");
        t.Processes.Running.Add("chrome");

        var r = t.Clean("browser-chrome");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Empty(t.Cleanup.Deleted);
        var s = Step(t, r);
        Assert.Equal(StepState.Skipped, s.State);
        Assert.Equal(StepCodes.SkippedByRule, s.Code);
        Assert.Equal("cleanup-browser-running", s.PolicyRule);

        t.Processes.Running.Clear();
        Assert.True(t.Clean("browser-chrome").Ok);
        Assert.Equal([cache + @"\f_000001"], t.Cleanup.Deleted);
    }

    [Fact]
    public void Firefox_profiles_are_expanded_one_level_and_junction_profiles_are_ignored()
    {
        var t = new TestEnv();
        const string profiles = @"C:\Users\Test\AppData\Local\Mozilla\Firefox\Profiles";
        t.Cleanup.AddFile(profiles + @"\abc.default\cache2\entries\e1");
        t.Cleanup.AddReparseDir(profiles + @"\evil.default");
        t.Cleanup.AddFile(@"C:\Elsewhere\cache2\victim");

        var r = t.Clean("browser-firefox");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Contains(profiles + @"\abc.default\cache2\entries\e1", t.Cleanup.Deleted);
        Assert.True(t.Cleanup.Exists(@"C:\Elsewhere\cache2\victim"));
        Assert.DoesNotContain(profiles + @"\evil.default\cache2", t.Cleanup.Enumerated);
    }

    [Fact]
    public void Foreign_profile_owner_denies_user_categories()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.SetOwner(@"C:\Users\Test", TestEnv.OtherSid);

        var r = t.Clean("temp-user");

        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Empty(t.Cleanup.Deleted);
    }

    // ---------- корзина ----------

    [Fact]
    public void Recycle_bin_of_the_client_is_emptied()
    {
        var t = new TestEnv();
        t.RecycleBin.Bytes = 500;

        var r = t.Clean("recycle-bin");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(1, t.RecycleBin.EmptyCalls);
        var s = Step(t, r);
        Assert.Equal(StepKind.RecycleBinEmpty, s.Kind);
        Assert.Equal(StepState.Done, s.State);
        Assert.False(s.Revertible);
        Assert.Equal(500, State(r, "recyclebin").FreedBytes);
    }

    [Fact]
    public void Recycle_bin_of_another_user_is_denied()
    {
        var t = new TestEnv();
        t.RecycleBin.Bytes = 500;
        t.RecycleBin.TokenSid = TestEnv.OtherSid;

        var r = t.Clean("recycle-bin");

        Assert.Equal(ResponseCodes.PolicyDenied, r.Code);
        Assert.Equal(0, t.RecycleBin.EmptyCalls);
        Assert.Equal("recyclebin-foreign-user", Step(t, r).PolicyRule);
    }

    [Fact]
    public void Empty_recycle_bin_is_no_change()
    {
        var t = new TestEnv();

        var r = t.Clean("recycle-bin");

        Assert.Equal(ResponseCodes.NoChange, r.Code);
        Assert.Equal(0, t.RecycleBin.EmptyCalls);
    }

    // ---------- пределы и итог операции ----------

    [Fact]
    public void Timeout_in_one_category_gives_partial_and_the_other_category_still_runs()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.AddFile(@"C:\Users\Test\AppData\Local\D3DSCache\shader");
        // Перечислений: 1 — измерение temp-user, 2 — его запись: на ней проходит 21 минута.
        t.Cleanup.OnEnumerate = _ =>
        {
            if (t.Cleanup.Enumerated.Count == 2) t.Clock.Advance(TimeSpan.FromMinutes(21));
        };

        var r = t.Clean("temp-user", "d3d-shader-cache");

        Assert.Equal(ResponseCodes.Partial, r.Code);
        var a = Step(t, r, 0);
        Assert.Equal(StepState.Failed, a.State);
        Assert.Equal(StepCodes.Timeout, a.Code);
        Assert.Equal(StepState.Done, Step(t, r, 1).State);
        Assert.Equal([@"C:\Users\Test\AppData\Local\D3DSCache\shader"], t.Cleanup.Deleted);
    }

    [Fact]
    public void Root_replaced_by_a_link_during_the_walk_stops_the_category()
    {
        var t = new TestEnv();
        for (var i = 0; i < 1500; i++) t.Cleanup.AddFile(Temp + $@"\f{i:D4}.tmp");
        var inspects = 0;
        // Inspect корня: предпроверка, открытие, затем перепроверки каждые 1000 записей (измерение, запись).
        t.Cleanup.OnInspect = p =>
        {
            if (p == Temp && ++inspects == 4) t.Cleanup.MakeReparse(Temp);
        };

        var r = t.Clean("temp-user");

        Assert.False(r.Ok);
        var s = Step(t, r);
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.PolicyDenied, s.Code);
        Assert.True(t.Cleanup.Deleted.Count < 1500);
        Assert.True(t.Cleanup.Exists(Temp));
    }

    // ---------- журнал очистки ----------

    [Fact]
    public void Log_file_is_written_by_operation_id_and_lists_deleted_files()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.AddFile(Temp + @"\busy.tmp");
        t.Cleanup.Lock(Temp + @"\busy.tmp");

        var r = t.Clean("temp-user");

        var name = r.OperationId!.Value.ToString("D") + ".log";
        var log = Encoding.UTF8.GetString(t.CleanupLog.Files[name]);
        Assert.Contains("D " + FakeCleanupFiles.Final(Temp + @"\old.tmp"), log);
        Assert.Contains("S in-use " + Temp + @"\busy.tmp", log);
    }

    [Fact]
    public void Log_write_failure_does_not_change_the_result()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.CleanupLog.Throw = true;

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal([Temp + @"\old.tmp"], t.Cleanup.Deleted);
    }

    [Fact]
    public void Log_buffer_never_exceeds_the_limit_and_ends_with_a_truncation_mark()
    {
        var log = new CleanupLogBuffer();
        var line = "D " + new string('x', 400);
        for (var i = 0; i < 40_000; i++) log.Line(line);

        var bytes = log.ToBytes();

        Assert.True(bytes.Length <= CleanupLogBuffer.MaxBytes);
        Assert.True(log.Truncated);
        Assert.EndsWith("truncated\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Log_lines_have_control_characters_replaced()
    {
        var log = new CleanupLogBuffer();
        log.Line("D C:\\a\r\nD C:\\fake");

        Assert.Equal("D C:\\a??D C:\\fake\n", Encoding.UTF8.GetString(log.ToBytes()));
    }

    // ---------- обрыв ----------

    [Fact]
    public void Pending_cleanup_step_is_closed_as_interrupted_by_recovery()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Journal.Probe = l =>
        {
            if (l == "Complete:before") throw new SimulatedCrash(l);
        };
        Assert.Throws<SimulatedCrash>(() => t.Clean("temp-user"));
        t.Journal.Probe = null;
        t.Restart();

        var rep = new RecoveryService(t.Env).Run();

        Assert.Equal(1, rep.Failed);
        var s = t.Journal.AllSteps.Single();
        Assert.Equal(StepState.Failed, s.State);
        Assert.Equal(StepCodes.Interrupted, s.Code);
    }

    // ---------- ленивое перечисление, том корня ----------

    [Fact]
    public void Directory_is_read_lazily_in_batches_not_into_one_list()
    {
        var t = new TestEnv();
        for (var i = 0; i < 5000; i++) t.Cleanup.AddFile(Temp + $@"\f{i:D5}.tmp", length: 10);
        var pulledAtFirstDelete = -1;
        var maxPulledAhead = 0;
        var opened = 0;
        var baseline = 0; // Pulled накапливается по всем обходам (замер, затем запись): считаем от начала текущего перечисления
        t.Cleanup.OnEnumerate = _ => baseline = t.Cleanup.Pulled;
        t.Cleanup.OnOpenForDelete = _ =>
        {
            var pulled = t.Cleanup.Pulled - baseline;
            if (pulledAtFirstDelete < 0) pulledAtFirstDelete = pulled;
            opened++;
            maxPulledAhead = Math.Max(maxPulledAhead, pulled - opened);
        };

        var r = t.Clean("temp-user");

        Assert.True(r.Ok, r.Code + r.Message);
        Assert.Equal(5000, t.Cleanup.Deleted.Count);
        Assert.Equal(5000, State(r, "temp-user").Deleted);
        // Первое удаление происходит, когда прочитан только первый блок, а не все 5000 записей.
        Assert.Equal(CleanupSession.BatchSize, pulledAtFirstDelete);
        Assert.True(maxPulledAhead <= CleanupSession.BatchSize, "прочитано вперёд: " + maxPulledAhead);
    }

    [Fact]
    public void Root_whose_volume_differs_from_the_requested_drive_is_not_touched()
    {
        var t = new TestEnv();
        t.Cleanup.AddFile(Temp + @"\old.tmp");
        t.Cleanup.RequestedVolumeOverride = @"\\?\Volume{99999999-8888-7777-6666-555555555555}";

        var r = t.Clean("temp-user");

        Assert.Empty(t.Cleanup.Deleted);
        Assert.Empty(t.Cleanup.Opened);
        Assert.True(t.Cleanup.Exists(Temp + @"\old.tmp"));
        Assert.DoesNotContain(r.Message ?? string.Empty, "удалено 1");
    }
}
