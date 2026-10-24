using System.Windows;
using Chistovik.App.Views;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private static void Place(Window dlg)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public void ShowStub(string title, string message)
    {
        var dlg = new StubDialog(title, message);
        Place(dlg);
        dlg.ShowDialog();
    }

    public bool Confirm(ConfirmContent content)
    {
        var dlg = new ConfirmDialog(content);
        Place(dlg);
        return dlg.ShowDialog() == true;
    }

    public IReadOnlyList<UndoResolution>? ResolveConflicts(IReadOnlyList<ConflictInfo> conflicts)
    {
        var dlg = new ConflictsDialog(conflicts);
        Place(dlg);
        return dlg.ShowDialog() == true ? dlg.Resolutions : null;
    }
}
