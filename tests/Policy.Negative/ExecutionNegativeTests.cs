using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>
/// Негативные тесты слоя исполнения (DESIGN-0.1 §7) на подставных портах: каждый отказ проверяется по ответу, по журналу
/// и по тому, что в систему (подставные порты) не записано ничего.
/// </summary>
public class ExecutionNegativeTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static int Writes(TestEnv t) => t.Registry.WriteCount + t.Services.WriteCount + t.Tasks.WriteCount + t.Power.WriteCount;

    static void AssertDenied(TestEnv t, AgentResponse r, string code = ResponseCodes.PolicyDenied)
    {
        Assert.False(r.Ok, r.Code + r.Message);
        Assert.Equal(code, r.Code);
        Assert.Equal(0, Writes(t));
        Assert.Empty(t.Journal.GetPending());
    }

    // ---------- реестр: ссылки между кустами ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hku_key_that_is_a_symlink_to_hklm_is_refused(bool openAsLink)
    {
        var t = new TestEnv();
        var loc = t.Loc("ext");
        var target = @"HKLM\SOFTWARE\Evil";
        t.Registry.EnsureKey(target);
        t.Registry.AddSymlink(FakeRegistry.HkuPath(TestEnv.Sid, loc.KeyPath), target, openAsLink);

        var r = t.Tweaks(("ext", true));

        AssertDenied(t, r);
        Assert.False(t.Registry.Get(target, loc.ValueName).Exists);
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    [Fact]
    public void Symlink_in_the_hku_approved_key_blocks_startup_toggle()
    {
        var t = new TestEnv();
        t.Registry.EnsureKey(@"HKLM\SOFTWARE\Evil\Run");
        t.Registry.AddSymlink(FakeRegistry.HkuPath(TestEnv.Sid, ApprovedRunKey), @"HKLM\SOFTWARE\Evil\Run");
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "Foo", "\"C:\\Program Files\\Foo\\foo.exe\"");

        var r = t.Startup("run:hku:Foo", false);

        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
        Assert.False(t.Registry.Get(@"HKLM\SOFTWARE\Evil\Run", "Foo").Exists);
    }

    [Fact]
    public void Foreign_sid_cannot_reach_hku_of_system_or_another_user()
    {
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-19", "S-1-5-20", ".DEFAULT", "S-1-5-21-1-2-3-1001_Classes" })
        {
            var t = new TestEnv { Client = new ClientContext(sid, 1, 1) };
            var r = t.Tweaks(("ext", true));
            Assert.False(r.Ok, sid);
            Assert.Equal(0, Writes(t));
        }
    }

    [Fact]
    public void Registry_access_denied_on_open_is_a_denial_not_a_crash()
    {
        var t = new TestEnv();
        var loc = t.Loc("killsvc");
        t.Registry.DenyOpen(FakeRegistry.Path(loc));
        var r = t.Tweaks(("killsvc", true));
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
        Assert.Equal(0, t.Registry.OpenHandles);
    }

    // ---------- службы ----------

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("wscsvc")]
    [InlineData("EventLog")]
    [InlineData("RpcSs")]
    public void Protected_services_are_refused_in_every_direction(string name)
    {
        var t = new TestEnv();
        t.AddThirdPartyService(name, 2);
        foreach (var mode in new[] { StartMode.Disabled, StartMode.Manual, StartMode.Automatic })
            AssertDenied(t, t.Service(name, mode));
        Assert.Equal(2u, t.Services.Get(name).StartType);
    }

    [Fact]
    public void Per_user_template_service_in_shared_svchost_is_refused()
    {
        var t = new TestEnv();
        // Так CDPUserSvc_1a2b3 выглядит в системе: общий svchost из System32, пользовательский экземпляр.
        t.Services.Add(new ServiceRaw("CDPUserSvc_1a2b3", 0xE0, 2, false, @"C:\Windows\system32\svchost.exe -k UnistackSvcGroup", false, 0, 0, true));
        t.Trust.SetSigned(@"C:\Windows\system32\svchost.exe", microsoft: true);
        foreach (var mode in new[] { StartMode.Disabled, StartMode.Manual, StartMode.Automatic })
            AssertDenied(t, t.Service("CDPUserSvc_1a2b3", mode));
        Assert.Equal(2u, t.Services.Get("CDPUserSvc_1a2b3").StartType);
    }

    [Fact]
    public void Service_with_dependents_is_refused()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", 2, dependents: 2);
        AssertDenied(t, t.Service("FooSvc", StartMode.Disabled));
    }

    [Fact]
    public void Service_outside_program_files_cannot_be_elevated_but_its_own_disable_can_be_undone()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("ToolSvc", 4, image: "\"C:\\Tools\\tool.exe\"");
        t.Trust.SetSigned(@"C:\Tools\tool.exe");
        AssertDenied(t, t.Service("ToolSvc", StartMode.Automatic));
        AssertDenied(t, t.Service("ToolSvc", StartMode.Manual));

        t.Services.Mutate("ToolSvc", s => s with { StartType = 2 });
        var off = t.Service("ToolSvc", StartMode.Disabled);
        Assert.True(off.Ok, off.Code + off.Message);

        var undo = t.Undo(off.OperationId!.Value);
        Assert.True(undo.Ok, undo.Code + undo.Message);
        Assert.Equal(2u, t.Services.Get("ToolSvc").StartType);
    }

    [Fact]
    public void Service_unsigned_or_in_windows_dir_is_refused()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("WinSvc", 2, image: "\"C:\\Windows\\System32\\foo.exe\"");
        AssertDenied(t, t.Service("WinSvc", StartMode.Disabled));

        var t2 = new TestEnv();
        t2.AddThirdPartyService("UnsignedSvc", 2);
        t2.Trust.Set(@"C:\Program Files\Foo\UnsignedSvc.exe", new SignatureFacts(false, false));
        AssertDenied(t2, t2.Service("UnsignedSvc", StartMode.Disabled));
    }

    [Fact]
    public void Boot_and_system_start_services_are_refused()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("DrvSvc", 0, typeFlags: 1);
        AssertDenied(t, t.Service("DrvSvc", StartMode.Disabled));
    }

    [Fact]
    public void Image_path_changed_between_precheck_and_execution_is_refused_without_a_write()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", 2);
        t.Services.QueryHook = (name, n) =>
        {
            if (n == 2) t.Services.Mutate(name, s => s with { ImagePath = "\"C:\\Windows\\System32\\evil.exe\"" });
        };

        var r = t.Service("FooSvc", StartMode.Disabled);

        Assert.False(r.Ok);
        Assert.Equal(0, t.Services.WriteCount);
        Assert.Equal(2u, t.Services.Get("FooSvc").StartType);
        var s = Assert.Single(t.Journal.AllSteps);
        Assert.Equal(StepState.Denied, s.State);
        Assert.Equal(0, t.Services.OpenHandles);
    }

    [Fact]
    public void Service_that_gained_dependents_between_precheck_and_execution_is_not_written()
    {
        var t = new TestEnv();
        t.AddThirdPartyService("FooSvc", 2);
        t.Services.QueryHook = (name, n) =>
        {
            if (n == 2) t.Services.Mutate(name, s => s with { DependentCount = 3 });
        };
        var r = t.Service("FooSvc", StartMode.Disabled);
        Assert.False(r.Ok);
        Assert.Equal(0, t.Services.WriteCount);
    }

    [Fact]
    public void Unknown_service_is_not_found()
    {
        var t = new TestEnv();
        var r = t.Service("NoSuchSvc", StartMode.Disabled);
        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Equal(0, Writes(t));
    }

    // ---------- задачи ----------

    [Fact]
    public void Microsoft_tasks_are_refused()
    {
        var t = new TestEnv();
        t.Tasks.Add(new TaskRaw(@"\Microsoft\Windows\Defrag\ScheduledDefrag", true, "<Task/>", TestEnv.Sid, "LeastPrivilege", "InteractiveToken",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)]));
        AssertDenied(t, t.Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag", false));
        Assert.True(t.Tasks.Get(@"\Microsoft\Windows\Defrag\ScheduledDefrag").Enabled);
    }

    [Fact]
    public void Chistovik_own_tasks_subtree_is_refused()
    {
        var t = new TestEnv();
        t.AddUserTask(@"\Chistovik\Agent");
        AssertDenied(t, t.Task(@"\Chistovik\Agent", false));
    }

    [Fact]
    public void Enabling_a_system_or_highest_task_is_refused_directly()
    {
        var t = new TestEnv();
        t.Tasks.Add(new TaskRaw(@"\Foo\SysTask", false, "<Task/>", "NT AUTHORITY\\SYSTEM", "LeastPrivilege", "ServiceAccount",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)]));
        t.Tasks.Add(new TaskRaw(@"\Foo\AdminTask", false, "<Task/>", TestEnv.Sid, "HighestAvailable", "InteractiveToken",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)]));
        AssertDenied(t, t.Task(@"\Foo\SysTask", true));
        AssertDenied(t, t.Task(@"\Foo\AdminTask", true));
    }

    [Fact]
    public void Task_action_in_windows_dir_is_refused()
    {
        var t = new TestEnv();
        t.Tasks.Add(new TaskRaw(@"\Foo\Sys", true, "<Task/>", TestEnv.Sid, "LeastPrivilege", "InteractiveToken",
            [new TaskAction("Exec", @"C:\Windows\System32\cmd.exe", "/c evil")]));
        AssertDenied(t, t.Task(@"\Foo\Sys", false));
    }

    [Fact]
    public void Task_changed_between_precheck_and_execution_is_not_written()
    {
        var t = new TestEnv();
        t.AddUserTask(@"\Foo\Update");
        t.Tasks.QueryHook = (path, n) =>
        {
            if (n == 2) t.Tasks.Mutate(path, x => x with { Actions = [new TaskAction("Exec", @"C:\Windows\System32\cmd.exe", null)] });
        };
        var r = t.Task(@"\Foo\Update", false);
        Assert.False(r.Ok);
        Assert.Equal(0, t.Tasks.WriteCount);
        Assert.True(t.Tasks.Get(@"\Foo\Update").Enabled);
    }

    [Fact]
    public void Unknown_task_is_not_found()
    {
        var t = new TestEnv();
        var r = t.Task(@"\Foo\Nothing", false);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Equal(0, Writes(t));
    }

    // ---------- автозагрузка ----------

    [Fact]
    public void Startup_entry_with_a_file_in_windows_dir_is_refused()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "Evil", "\"C:\\Windows\\System32\\evil.exe\"");
        t.Trust.SetSigned(@"C:\Windows\System32\evil.exe", microsoft: true);
        AssertDenied(t, t.Startup("run:hku:Evil", false));
    }

    [Fact]
    public void Startup_entry_with_unexpanded_variable_command_is_refused()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.Sid, RunKey), "Evil", "%EVILVAR%\\x.exe");
        AssertDenied(t, t.Startup("run:hku:Evil", false));
    }

    [Fact]
    public void Startup_entry_that_does_not_exist_is_not_found()
    {
        var t = new TestEnv();
        var r = t.Startup("run:hku:Missing", false);
        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Equal(0, Writes(t));
    }

    [Theory]
    [InlineData("")]
    [InlineData("run:hku:")]
    [InlineData("run:hkcr:Foo")]
    [InlineData("folder:user:..\\..\\evil.lnk")]
    [InlineData("run:hku:..\\Foo")]
    public void Startup_malformed_entry_id_never_writes(string id)
    {
        var t = new TestEnv();
        var r = t.Startup(id, false);
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
    }

    [Fact]
    public void Startup_entry_of_another_users_hive_is_not_reachable()
    {
        var t = new TestEnv();
        t.Registry.SetString(FakeRegistry.HkuPath(TestEnv.OtherSid, RunKey), "Foo", "\"C:\\Program Files\\Foo\\foo.exe\"");
        var r = t.Startup("run:hku:Foo", false);
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
    }

    // ---------- питание ----------

    [Fact]
    public void Journal_pointing_the_scheme_delete_at_a_builtin_scheme_is_rejected()
    {
        var t = new TestEnv();
        var op = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var create = t.Journal.Get(op)!.Steps[0];
        var copy = t.Power.ListSchemes().Last();
        var tampered = create.TargetJson.Replace(copy.ToString("D"), TestEnv.High.ToString("D"), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(create.TargetJson, tampered);
        t.Journal.Tamper(create.Id, targetJson: tampered);
        var writes = Writes(t);

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(writes, Writes(t));
        Assert.Equal(0, t.Power.DeleteCount);
        Assert.Contains(TestEnv.High, t.Power.ListSchemes());
    }

    [Fact]
    public void Deleting_the_active_scheme_is_refused()
    {
        var t = new TestEnv();
        var op = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var copy = t.Power.Active;
        var activeStep = t.Journal.Get(op)!.Steps.Single(s => s.Kind == StepKind.PowerActiveScheme);
        // Журнал велит «вернуть» активной саму копию, после чего её удаление пришлось бы на активную схему.
        t.Journal.Tamper(activeStep.Id, beforeJson: StateJson.PowerActiveJson(copy));

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(0, t.Power.DeleteCount);
        Assert.Contains(copy, t.Power.ListSchemes());
    }

    [Fact]
    public void Journal_pointing_the_scheme_delete_at_a_foreign_user_scheme_is_rejected()
    {
        var t = new TestEnv();
        var foreign = Guid.Parse("11111111-2222-3333-4444-555555555555");
        t.Power.AddScheme(foreign, "Схема владельца");
        var op = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var create = t.Journal.Get(op)!.Steps[0];
        var copy = t.Power.ListSchemes().Single(g => g != TestEnv.Saver && g != TestEnv.Balanced && g != TestEnv.High && g != foreign);
        t.Journal.Tamper(create.Id, targetJson: create.TargetJson.Replace(copy.ToString("D"), foreign.ToString("D"), StringComparison.OrdinalIgnoreCase));

        var u = t.Undo(op);

        Assert.Equal(0, t.Power.DeleteCount);
        Assert.Contains(foreign, t.Power.ListSchemes());
        Assert.False(u.Ok && t.Power.DeleteCount > 0);
    }

    [Fact]
    public void Scheme_renamed_by_the_owner_is_not_deleted_by_undo()
    {
        var t = new TestEnv();
        var op = t.Plan(PowerPlan.Ult).OperationId!.Value;
        var copy = t.Power.Active;
        t.Power.Names[copy] = "Моя схема, переименована владельцем";

        var u = t.Undo(op);

        Assert.False(u.Ok);
        Assert.Equal(0, t.Power.DeleteCount);
        Assert.Contains(copy, t.Power.ListSchemes());
    }

    [Fact]
    public void Power_setting_outside_catalog_range_never_writes()
    {
        var t = new TestEnv();
        var r = t.PowerSetting("min", 1000, 1000);
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
    }

    [Fact]
    public void Power_setting_with_unknown_id_never_writes()
    {
        var t = new TestEnv();
        var r = t.PowerSetting("no-such-setting", 1, 1);
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
    }

    [Fact]
    public void Operation_with_one_denied_step_writes_nothing_at_all()
    {
        var t = new TestEnv();
        t.SetTweakDword("ext", 1);
        var r = t.Tweaks(("ext", true), ("no-such-tweak", true));
        Assert.False(r.Ok);
        Assert.Equal(0, Writes(t));
        Assert.Equal(TestEnv.Dword(1), t.TweakValue("ext"));
    }

    // ---------- каталог: ни один твик не должен упираться в политику ----------

    [Fact]
    public void Every_catalog_tweak_plans_and_applies_without_a_policy_refusal_in_both_directions()
    {
        var disputed = new List<string>();
        foreach (var tw in TweakCatalog.Shared.Tweaks)
        {
            foreach (var on in new[] { true, false })
            {
                var t = new TestEnv();
                var r = t.Tweaks((tw.Id, on));
                if (!(r.Ok && r.Code is ResponseCodes.Ok or ResponseCodes.NoChange)) disputed.Add($"{tw.Id}/{on}: {r.Code} {r.Message}");
                var u = r.OperationId is { } op && r.Code == ResponseCodes.Ok ? t.Undo(op) : r;
                if (!u.Ok) disputed.Add($"{tw.Id}/{on}/undo: {u.Code} {u.Message}");
            }
        }
        Assert.True(disputed.Count == 0, string.Join("\n", disputed));
    }

    [Fact]
    public void Every_catalog_mode_applies_and_undoes_without_a_policy_refusal()
    {
        var disputed = new List<string>();
        foreach (var m in TweakCatalog.Shared.Modes)
        {
            var t = new TestEnv();
            var r = t.Mode(m.Id);
            if (!r.Ok) disputed.Add($"{m.Id}: {r.Code} {r.Message}");
            else if (r.Code == ResponseCodes.Ok)
            {
                var u = t.Undo(r.OperationId!.Value);
                if (!u.Ok) disputed.Add($"{m.Id}/undo: {u.Code} {u.Message}");
            }
            Assert.Equal(0, t.Registry.OpenHandles);
        }
        Assert.True(disputed.Count == 0, string.Join("\n", disputed));
    }

    [Fact]
    public void Every_power_plan_applies_and_undoes()
    {
        foreach (var p in Enum.GetValues<PowerPlan>())
        {
            var t = new TestEnv();
            var r = t.Plan(p);
            Assert.True(r.Ok, p + " " + r.Code + r.Message);
            if (r.Code == ResponseCodes.Ok) Assert.True(t.Undo(r.OperationId!.Value).Ok, p.ToString());
            Assert.Equal(TestEnv.Balanced, t.Power.Active);
        }
    }
}
