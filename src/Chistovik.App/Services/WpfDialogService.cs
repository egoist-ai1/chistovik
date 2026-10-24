using System.Windows;
using Chistovik.App.Views;

namespace Chistovik.App.Services;

public sealed class WpfDialogService : IDialogService
{
    public void ShowStub(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        var dlg = new StubDialog(title, message);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }
}
