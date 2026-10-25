using Chistovik.App.Services.Changes;
using Chistovik.App.ViewModels;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;
using Xunit;

namespace Chistovik.App.Tests;

public class SubjectTextTests
{
    [Theory]
    [InlineData("folder:user:Test_Folder.lnk", "Test_Folder.lnk")]
    [InlineData("run:hklm64:OneDrive", "OneDrive")]
    public void Startup_subject_becomes_entry_name(string subject, string expected)
    {
        Assert.Equal(expected, SubjectText.OfOperation(OperationKind.Startup, subject));
        Assert.Equal(expected, SubjectText.OfOperation(OperationKind.Undo, subject));
    }

    [Fact]
    public void Tweak_ids_become_titles()
    {
        var tweak = TweakCatalog.Shared.Tweaks[0];

        Assert.Equal(tweak.Title, SubjectText.OfOperation(OperationKind.Tweaks, tweak.Id));
    }

    [Fact]
    public void Unknown_subject_stays_as_is()
    {
        Assert.Equal("что-то", SubjectText.OfOperation(OperationKind.Tweaks, "что-то"));
        Assert.Equal("", SubjectText.OfOperation(OperationKind.Tweaks, " "));
    }

    [Theory]
    [InlineData("startup:hku:s-1-5-21-1:startupfolder:test_folder.lnk", "test_folder.lnk")]
    [InlineData("svc:wuauserv", "служба wuauserv")]
    [InlineData("task:\\microsoft\\x", "задача \\microsoft\\x")]
    [InlineData("power-active", "power-active")]
    public void Step_target_is_readable(string key, string expected)
        => Assert.Equal(expected, SubjectText.OfTarget(key));

    [Fact]
    public void Per_user_service_suffix_is_cleaned_and_instance_collapsed()
    {
        Assert.Equal("Платформа", PerUser.CleanDisplayName("CDPUserSvc_4b92f", "Платформа_4b92f"));
        Assert.Equal("Служба_beef", PerUser.CleanDisplayName("Foo", "Служба_beef"));
        Assert.Equal("CDPUserSvc", PerUser.TemplateName("CDPUserSvc_4b92f"));
        Assert.Null(PerUser.TemplateName("wuauserv"));
    }
}
