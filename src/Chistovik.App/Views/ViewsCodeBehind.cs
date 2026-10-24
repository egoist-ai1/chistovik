using System.Windows;
using System.Windows.Controls;

namespace Chistovik.App.Views;

public partial class OverviewView : UserControl { public OverviewView() { InitializeComponent(); } }
public partial class ProgramsView : UserControl { public ProgramsView() { InitializeComponent(); } }
public partial class StartupView : UserControl { public StartupView() { InitializeComponent(); } }
public partial class JournalView : UserControl { public JournalView() { InitializeComponent(); } }
public partial class PlaceholderView : UserControl { public PlaceholderView() { InitializeComponent(); } }

public partial class StubDialog : Window
{
    public StubDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
