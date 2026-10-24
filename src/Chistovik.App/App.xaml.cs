using System.Windows;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Анимации и контраст по настройкам Windows: до создания окон, чтобы шаблоны взяли нужные токены.
        Motion.Apply(this);
        Motion.Watch(this);

        // Снимки экранов: окна не показываются и не берут фокус, реальная система не читается.
        if (!Snapshots.SnapshotPlan.TryParseArgs(e.Args, out var snapshots, out var snapshotDir, out var snapshotError))
        {
            Console.Error.WriteLine(snapshotError);
            Shutdown(2);
            return;
        }
        if (snapshots)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunSnapshots(snapshotDir!);
            return;
        }

        // Демонстрационные данные только для самопроверки и предпросмотра дизайна (--demo).
        // Обычный запуск читает реальную систему; агент стартует лишь при первом изменении.
        var selfTest = e.Args.Contains("--selftest");
        if (selfTest) Services.Motion.ForceOff = true;
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

    private async void RunSnapshots(string directory)
    {
        var code = 1;
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Services.Motion.ForceOff = true;
            var logFile = System.IO.Path.Combine(directory, "snapshots.log");
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(logFile, "");
            code = await Snapshots.SnapshotRunner.RunAsync(directory, line =>
            {
                Console.WriteLine(line);
                System.IO.File.AppendAllText(logFile, line + Environment.NewLine);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SNAPSHOTS FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        Environment.ExitCode = code;
        Shutdown(code);
    }
}
