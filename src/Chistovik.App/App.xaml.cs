using System.Windows;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // В самопроверке и снимках окон с сообщением быть не должно: запись в журнал есть, диалога нет.
        _silentCrash = e.Args.Contains("--selftest") || e.Args.Contains("--snapshots");
        InstallCrashHandlers();

        // Программная отрисовка (RDP, ВМ без GPU): размытие слоёв свечения там очень медленное, слои скрываем.
        if (System.Windows.Media.RenderCapability.Tier >> 16 == 0)
            Resources["GlowVisibility"] = Visibility.Collapsed;

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

    private readonly CrashLog _crashLog = new();
    private int _crashShown;
    private bool _silentCrash;

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // Состояние интерфейса после необработанной ошибки неизвестно: сообщаем и завершаем работу.
            args.Handled = true;
            ReportCrash("Dispatcher", args.Exception, exit: true);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash("AppDomain", args.ExceptionObject as Exception, exit: false);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Не фатальна: только пишем в журнал и помечаем наблюдённой.
            _crashLog.Write("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    private void ReportCrash(string source, Exception? error, bool exit)
    {
        try
        {
            _crashLog.Write(source, error);
            // Окно показываем один раз: каскад ошибок при закрытии не должен плодить диалоги.
            if (!_silentCrash && Interlocked.Exchange(ref _crashShown, 1) == 0)
                MessageBox.Show(_crashLog.UserMessage(), "Чистовик", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Обработчик ошибок сам не должен бросать.
        }
        if (exit)
        {
            try { Shutdown(1); }
            catch { Environment.Exit(1); }
        }
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
