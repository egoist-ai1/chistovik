using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.PolicyNegative;

/// <summary>Шаблонные пользовательские службы без суффикса и определение служебных учётных записей задач по SID.</summary>
public class UserServiceAndTaskSidTests
{
    // ---------- службы ----------

    [Theory]
    [InlineData("CDPUserSvc")]
    [InlineData("OneSyncSvc")]
    [InlineData("PimIndexMaintenanceSvc")]
    [InlineData("UnistoreSvc")]
    [InlineData("UserDataSvc")]
    [InlineData("WpnUserService")]
    [InlineData("cbdhsvc")]
    [InlineData("BcastDVRUserService")]
    [InlineData("CaptureService")]
    [InlineData("ConsentUxUserSvc")]
    [InlineData("DevicesFlowUserSvc")]
    [InlineData("MessagingService")]
    [InlineData("PrintWorkflowUserSvc")]
    [InlineData("NPSMSvc")]
    [InlineData("webthreatdefusersvc")]
    public void User_service_templates_are_hard_listed_with_and_without_suffix(string template)
    {
        Assert.True(ServicePolicy.IsHardListed(template));
        Assert.True(ServicePolicy.IsHardListed(template + "_1a2b3"));
        Assert.True(ServicePolicy.IsHardListed(template + "_ABCD"));
        Assert.True(ServicePolicy.IsHardListed(template + "_12345678"));
        Assert.True(ServicePolicy.IsHardListed(template.ToUpperInvariant() + "_1a2b3"));
    }

    [Theory]
    [InlineData("CDPUserSvcX")]
    [InlineData("MyMessagingService")]
    [InlineData("MessagingServices")]
    [InlineData("CaptureService_xyz1")]
    [InlineData("OneSyncSvc_123")]       // суффикс короче 4 hex-символов
    [InlineData("OneSyncSvc_123456789")] // суффикс длиннее 8
    public void Names_that_only_resemble_templates_are_not_hard_listed_by_the_template_rule(string name)
    {
        Assert.False(ServicePolicy.IsHardListed(name));
    }

    // ---------- задачи: SID важнее имени ----------

    static readonly TaskPolicy Policy = new(PolicyContext.Fixed);

    static TaskRaw Raw(string? user, string? sid)
        => new(@"\Foo\Job", true, "<Task/>", user, "LeastPrivilege", "InteractiveToken",
            [new TaskAction("Exec", @"C:\Program Files\Foo\foo.exe", null)], sid);

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    public void Service_sid_makes_task_revert_only_even_when_name_looks_like_a_user(string sid)
    {
        // Локализованное имя («СИСТЕМА») не распознаётся по имени, но SID выдаёт служебную запись.
        var raw = Raw(@"PC\Egoist", sid);
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(raw).Class);
        Assert.True(Policy.CheckSetEnabled(raw, false).IsAllowed); // выключение разрешено, включение только откатом
        Assert.True(Policy.CheckSetEnabled(raw, true).IsDenied);
        Assert.False(Policy.CheckSetEnabled(raw, true, isRevertOfOwnStep: true).IsDenied);
    }

    [Fact]
    public void Service_sid_without_user_name_is_still_recognised()
    {
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(Raw(null, "S-1-5-18")).Class);
    }

    [Theory]
    [InlineData(@"NT AUTHORITY\SYSTEM")]
    [InlineData(@"NT AUTHORITY\LOCAL SERVICE")]
    [InlineData("NETWORK SERVICE")]
    public void Name_is_the_fallback_when_sid_is_missing(string user)
    {
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(Raw(user, null)).Class);
    }

    // UserId, похожий на SID, но SID не являющийся, не доверяется: Native для таких строк SID не возвращает, значит это ReadOnly.
    [Theory]
    [InlineData("S-1-xyz")]
    [InlineData("S-1-")]
    [InlineData("S-1-5-")]
    [InlineData("S-1-5")]                    // нет подавторитета
    [InlineData("S-1-5--21")]
    [InlineData("S-1-5-21-1-x")]
    [InlineData("S-1-5-21-1-2-3 x")]
    [InlineData("S-1-5-21-1 2-3")]
    [InlineData("s-1-5-21-1-2-3")]           // SID пишется с заглавной S
    [InlineData("S-1-٥-21")]            // арабско-индийская цифра не ASCII
    [InlineData("S-1-5-21-1-2-3\n-4")]
    [InlineData("S-1-5-21-1-2-3\0")]
    public void User_id_that_only_starts_like_a_sid_is_read_only(string user)
    {
        var d = Policy.CheckSetEnabled(Raw(user, null), false);

        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Raw(user, null)).Class);
        Assert.Equal("task-unresolved-principal", Policy.Classify(Raw(user, null)).Rule);
        Assert.True(d.IsDenied);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-21-1-2-3")]
    [InlineData("  S-1-12-1-1-2-3-4  ")]    // пробелы по краям срезаются
    public void Well_formed_unprivileged_sid_in_user_id_is_still_modifiable(string user)
    {
        Assert.Equal(ChangeClass.Modifiable, Policy.Classify(Raw(user, null)).Class);
    }

    [Theory]
    [InlineData("s-1-5-18 ")]
    [InlineData(" S-1-5-18")]
    [InlineData("S-1-5-80-1-2-3-4-5")]
    public void Service_sid_with_stray_spaces_or_case_is_still_privileged(string user)
    {
        Assert.Equal(ChangeClass.RevertOnly, Policy.Classify(Raw(user, null)).Class);
    }

    [Fact]
    public void Unrecognised_account_stays_read_only_and_ordinary_user_stays_modifiable()
    {
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Raw(null, null)).Class);
        Assert.Equal(ChangeClass.ReadOnly, Policy.Classify(Raw(null, "S-1-5-21-1-2-3-1001")).Class);
        Assert.Equal(ChangeClass.Modifiable, Policy.Classify(Raw(@"PC\Egoist", "S-1-5-21-1-2-3-1001")).Class);
    }
}
