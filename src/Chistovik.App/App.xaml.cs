using System.Windows;
using Chistovik.App.Mocks;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Сейчас везде моки; позже здесь подключается Chistovik.Core.
        var vm = new MainViewModel(
            new MockProgramsSource(), new MockStartupSource(), new MockJournalSource(), new WpfDialogService());
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        if (e.Args.Contains("--selftest"))
        {
            var code = SelfTest.Run(window, vm);
            Environment.ExitCode = code;
            Shutdown(code);
            return;
        }

        window.Show();
    }
}
