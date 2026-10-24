using System.Windows;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Демонстрационные данные только для самопроверки и предпросмотра дизайна (--demo).
        // Обычный запуск читает реальную систему; агент стартует лишь при первом изменении.
        var selfTest = e.Args.Contains("--selftest");
        var demo = selfTest || e.Args.Contains("--demo");
        var dialogs = new WpfDialogService();
        var services = demo ? CompositionRoot.CreateDemo(dialogs) : CompositionRoot.CreateReal(dialogs);

        var vm = new MainViewModel(services);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        if (selfTest)
        {
            var code = SelfTest.Run(window, vm);
            Environment.ExitCode = code;
            Shutdown(code);
            return;
        }

        window.Show();
    }
}
