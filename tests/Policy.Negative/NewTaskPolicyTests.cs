using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

public class NewTaskPolicyTests
{
    static readonly TaskPolicy Policy = new(PolicyContext.Fixed);

    const string GoodExe = "C:\\Program Files\\Foo\\up.exe";

    static TaskRaw Raw(
        string path = "\\Foo\\Updater",
        string? exe = GoodExe,
        string? user = "PC\\Egoist",
        string runLevel = "LeastPrivilege",
        string logon = "InteractiveToken",
        string xml = "<Task/>",
        TaskAction[]? extra = null,
        string? sid = "S-1-5-21-1-2-3-1001")
    {
        var actions = new List<TaskAction>();
        if (exe is not null) actions.Add(new TaskAction("Exec", exe, null));
        if (extra is not null) actions.AddRange(extra);
        return new TaskRaw(path, true, xml, user, runLevel, logon, actions, sid);
    }

    // ---------- путь ----------

    [Theory]
    [InlineData("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag")]
    [InlineData("\\Microsoft")]
    [InlineData("\\microsoft\\x")]
    [InlineData("\\MICROSOFT\\Windows\\UpdateOrchestrator\\Schedule Scan")]
    [InlineData("\\Chistovik\\Anything")]
    [InlineData("\\chistovik")]
    public void Microsoft_and_chistovik_subtrees_are_forbidden(string path)
    {
        var raw = Raw(path: path);
        Assert.Equal(ChangeClass.Forbidden, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Foo")]
    [InlineData("\\")]
    [InlineData("\\Foo\\")]
    [InlineData("\\Foo\\\\Bar")]
    [InlineData("\\Foo\\..\\Microsoft\\x")]
    [InlineData("\\Foo\\.\\x")]
    [InlineData("\\Foo/Bar")]
    [InlineData("\\Foo:Bar")]
    [InlineData("\\Foo*")]
    [InlineData("\\Foo?")]
    [InlineData("\\Foo|Bar")]
    [InlineData("\\Foo.\\x")]
    [InlineData("\\Foo \\x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("\\Foo\nBar")]
    [InlineData("\\Foo\0Bar")]
    public void Malformed_paths_are_forbidden(string path)
    {
        Assert.False(TaskPolicy.TryParsePath(path, out _));
        var raw = Raw(path: path);
        Assert.Equal(ChangeClass.Forbidden, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
    }

    [Fact]
    public void Segment_longer_than_255_is_rejected()
    {
        Assert.False(TaskPolicy.TryParsePath("\\" + new string('a', 256), out _));
        Assert.True(TaskPolicy.TryParsePath("\\" + new string('a', 255), out _));
    }

    // ---------- действия ----------

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("%SystemRoot%\\system32\\cmd.exe")]
    [InlineData("%windir%\\notepad.exe")]
    [InlineData("\"C:\\Windows\\System32\\wscript.exe\"")]
    [InlineData("C:\\Program Files\\..\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\?\\C:\\Windows\\System32\\cmd.exe")]
    [InlineData("c:/windows/system32/cmd.exe")]
    [InlineData("C:\\Program Files\\Windows Defender\\MpCmdRun.exe")]
    [InlineData("C:\\ProgramData\\Microsoft\\Windows Defender\\x.exe")]
    [InlineData("C:\\Program Files\\WindowsApps\\Foo\\a.exe")]
    public void Exec_in_windows_or_system_zone_is_forbidden(string exe)
    {
        var raw = Raw(exe: exe);
        Assert.Equal(ChangeClass.Forbidden, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData("D:\\Games\\Foo\\up.exe")]
    [InlineData("foo.exe")]
    [InlineData("\\\\srv\\share\\up.exe")]
    [InlineData("%APPDATA%\\Foo\\up.exe")]
    [InlineData("%windir%\\..\\Foo\\up.exe")]
    [InlineData("\"C:\\Foo\\up.exe")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\Progra~1\\Foo\\up.exe")]
    public void Exec_that_cannot_be_resolved_or_is_outside_roots_is_read_only(string? exe)
    {
        var raw = Raw(exe: null, extra: [new TaskAction("Exec", exe, null)]);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData("ComHandler")]
    [InlineData("Other")]
    [InlineData("Email")]
    [InlineData("")]
    public void Non_exec_action_is_read_only(string kind)
    {
        var raw = Raw(exe: null, extra: [new TaskAction(kind, GoodExe, null)]);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Fact]
    public void One_bad_action_taints_the_whole_task()
    {
        var withCom = Raw(extra: [new TaskAction("ComHandler", null, null)]);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(withCom).Class);
        var withWindows = Raw(extra: [new TaskAction("Exec", "C:\\Windows\\System32\\cmd.exe", "/c x")]);
        Assert.Equal(ChangeClass.Forbidden, Policy.Classify(withWindows).Class);
    }

    [Fact]
    public void Task_without_actions_is_read_only()
        => Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Raw(exe: null)).Class);

    [Fact]
    public void Com_handler_in_xml_is_read_only_even_if_actions_look_fine()
        => Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Raw(xml: "<Task><Actions><ComHandler><ClassId>{x}</ClassId></ComHandler></Actions></Task>")).Class);

    // ---------- учётная запись и права ----------

    public static IEnumerable<object[]> PrivilegedUsers() =>
        new[]
        {
            "SYSTEM", "system", "NT AUTHORITY\\SYSTEM", "LocalSystem", "S-1-5-18", "S-1-5-19", "S-1-5-20", "LOCAL SERVICE",
            "NT AUTHORITY\\LOCAL SERVICE", "NetworkService", "NT AUTHORITY\\NETWORK SERVICE", "NT SERVICE\\TrustedInstaller",
            "BUILTIN\\Administrators", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
        }.Select(u => new object[] { u });

    [Theory]
    [MemberData(nameof(PrivilegedUsers))]
    public void System_and_service_accounts_are_changed_only_by_reverting_own_step(string user)
    {
        var raw = Raw(user: user);
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed); // выключение разрешено (DESIGN-0.1 §7), включение только откатом
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsAllowed);
        Assert.True(Policy.CheckSetEnabled(raw, false, isRevertOfOwnStep: true).IsAllowed);
    }

    [Theory]
    [InlineData("HighestAvailable")]
    [InlineData("Highest")]
    [InlineData("highestavailable")]
    public void Elevated_run_level_is_changed_only_by_reverting_own_step(string level)
    {
        var raw = Raw(runLevel: level);
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed); // выключение разрешено (DESIGN-0.1 §7), включение только откатом
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsAllowed);
    }

    [Theory]
    [InlineData("SYSTEM")]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    [InlineData(@"NT AUTHORITY\LOCAL SERVICE")]
    public void Elevated_task_direct_matrix_disable_allowed_enable_denied_revert_allowed(string user)
    {
        var raw = Raw(user: user);
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsAllowed);
        var hi = Raw(runLevel: "HighestAvailable");
        Assert.True(Policy.CheckSetEnabled(hi, false).IsAllowed);
        Assert.True(Policy.CheckSetEnabled(hi, true).IsDenied);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Foo")]
    [InlineData(@"\Chistovik\Foo")]
    public void Forbidden_subtrees_stay_denied_in_both_directions_even_for_system(string path)
    {
        var raw = Raw(path: path, user: "SYSTEM");
        foreach (var enable in new[] { true, false })
        foreach (var revert in new[] { true, false })
            Assert.True(Policy.CheckSetEnabled(raw, enable, revert).IsDenied);
    }

    [Fact]
    public void Service_account_logon_type_is_revert_only()
    {
        var raw = Raw(logon: "ServiceAccount");
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Weird")]
    [InlineData("TASK_RUNLEVEL_HIGHEST")]
    public void Unknown_run_level_is_read_only_even_for_reverts(string level)
    {
        var raw = Raw(runLevel: level);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unknown_principal_is_read_only(string? user)
    {
        var raw = Raw(user: user);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false, isRevertOfOwnStep: true).IsDenied);
    }

    [Theory]
    [InlineData(@"PC\Egoist")]
    [InlineData("Egoist")]
    [InlineData(@"DOMAIN\ghost")]
    [InlineData("S-9-9-9")]
    public void Principal_name_that_did_not_resolve_to_sid_is_read_only(string user)
    {
        var raw = Raw(user: user, sid: null);
        var (cls, _, rule) = Policy.Classify(raw);
        Assert.Equal(ChangeClass.ReadOnly, cls);
        Assert.Equal("task-unresolved-principal", rule);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
        Assert.True(Policy.CheckSetEnabled(raw, false, isRevertOfOwnStep: true).IsDenied);
    }

    [Fact]
    public void Principal_given_as_sid_without_resolution_is_not_read_only()
        => Assert.Equal(ChangeClass.Modifiable, Policy.Classify(Raw(user: "S-1-5-21-1-2-3-1001", sid: null)).Class);

    [Fact]
    public void Carve_out_task_with_unresolved_principal_is_still_decided_by_the_carve_out()
    {
        // Карв-аут проверяется до принципала: имя пользователя и SID на него не влияют (только выключение, включение откатом).
        foreach (var path in TaskPolicy.CarveOutTasks)
        {
            var raw = Raw(path: path, user: @"PC\ghost", sid: null, exe: null);
            Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
            Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed);
            Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        }
    }

    [Fact]
    public void Worst_class_wins_over_revert_only()
    {
        // SYSTEM и неразобранное действие одновременно: откат не поможет.
        var raw = Raw(user: "SYSTEM", exe: null, extra: [new TaskAction("ComHandler", null, null)]);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    // ---------- обычная сторонняя задача ----------

    [Theory]
    [InlineData("C:\\Program Files\\Foo\\up.exe")]
    [InlineData("\"C:\\Program Files\\Foo Bar\\up.exe\"")]
    [InlineData("C:\\Program Files (x86)\\Foo\\up.exe")]
    [InlineData("%ProgramFiles%\\Foo\\up.exe")]
    [InlineData("%ProgramFiles(x86)%\\Foo\\up.exe")]
    [InlineData("C:\\ProgramData\\Foo\\up.exe")]
    [InlineData("C:\\Users\\Egoist\\AppData\\Local\\Foo\\up.exe")]
    public void Ordinary_third_party_task_is_modifiable(string exe)
    {
        var raw = Raw(exe: exe);
        Assert.Equal(ChangeClass.Modifiable, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, true).IsAllowed);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed);
    }

    [Fact]
    public void Task_with_args_and_nested_folder_is_modifiable_and_target_is_normalized()
    {
        var raw = Raw(path: "\\Foo\\Sub\\Updater", extra: [new TaskAction("Exec", "C:\\Program Files\\Foo\\b.exe", "--silent")]);
        var d = Policy.CheckSetEnabled(raw, false);
        Assert.True(d.IsAllowed);
        Assert.Equal("task:\\Foo\\Sub\\Updater", d.Target);
    }

    [Fact]
    public void Task_named_like_microsoft_but_not_under_it_is_not_vetoed_by_the_tree_rule()
        => Assert.Equal(ChangeClass.Modifiable, Policy.Classify(Raw(path: "\\MicrosoftEdgeUpdateFoo")).Class);

    [Fact]
    public void Default_decision_is_deny_and_unknown_class_never_allows()
        => Assert.True(default(PolicyDecision).IsDenied);
}
