using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Chistovik.App.Services;
using Chistovik.App.Views;
using Xunit;

namespace Chistovik.App.Tests;

public class AutomationNameTests
{
    static string PeerName(string name)
    {
        string? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var b = new Button { Content = "x" };
                b.SetValue(AutomationProperties.NameProperty, name);
                result = UIElementAutomationPeer.CreatePeerForElement(b).GetName();
            }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result!;
    }

    [Fact]
    public void Confirm_button_name_keeps_underscore_for_screen_readers()
    {
        var content = new ConfirmContent("Откатить «Автозагрузка: Test_Folder.lnk»?", "", "Откатить");

        Assert.Equal("Откатить: Откатить «Автозагрузка: Test_Folder.lnk»?", PeerName(ConfirmDialog.AccessibleName(content)));
    }
}
