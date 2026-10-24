using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;

namespace Chistovik.App.Snapshots;

/// <summary>
/// Рисует каждый экран в PNG на демонстрационных данных. Окно создаётся, но не получает фокус и не видно:
/// за пределами экрана (Left = -32000), без кнопки на панели задач, с флагом WS_EX_NOACTIVATE и Show без активации.
/// Реальная система не читается и не меняется.
/// </summary>
internal static class SnapshotRunner
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const double OffScreen = -32000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>0 — все снимки сохранены; 1 — хотя бы один не получился.</summary>
    public static async Task<int> RunAsync(string directory, Action<string> log)
    {
        Directory.CreateDirectory(directory);
        var failures = 0;
        var saved = 0;
        foreach (var group in SnapshotPlan.Build().GroupBy(s => (s.Screen, s.State)))
        {
            var specs = group.ToList();
            try
            {
                saved += await RenderGroupAsync(directory, specs);
            }
            catch (Exception ex)
            {
                failures++;
                log($"FAIL {specs[0].Screen}{specs[0].StateSuffix}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        log(failures == 0 ? $"SNAPSHOTS OK: {saved} файлов в {directory}" : $"SNAPSHOTS FAILED: {failures}, сохранено {saved}");
        return failures == 0 ? 0 : 1;
    }

    private static Task<int> RenderGroupAsync(string directory, List<SnapshotSpec> specs)
        => specs[0].IsDialog ? RenderDialogAsync(directory, specs) : RenderScreenAsync(directory, specs);

    // ----- экраны главного окна -----

    private static async Task<int> RenderScreenAsync(string directory, List<SnapshotSpec> specs)
    {
        var first = specs[0];
        var screen = SnapshotPlan.Screens.First(s => s.Id == first.Screen);
        var services = SnapshotServices.Create(first.State);
        var vm = new MainViewModel(services);
        vm.SelectedNav = vm.AllNavItems.First(n => n.Title == screen.NavTitle);
        var page = vm.Current ?? throw new InvalidOperationException("раздел не создан");

        var window = new MainWindow { DataContext = vm, SkipTransitions = true };
        Prepare(window, first.Width, first.Height);
        try
        {
            ShowWithoutFocus(window);
            await SettleAsync(page, first.State);
            if (first.State == SnapshotState.Partial)
                ((StatefulPageViewModel)page).ShowDemoOutcome(new Services.Changes.ChangeOutcome(
                    Services.Changes.OutcomeKind.Partial,
                    "Демо: выполнено 2 из 3 шагов, остальные не удались. На этом компьютере ничего не изменено.",
                    StepsDone: 2, StepsTotal: 3,
                    RestartHint: "Демо: что получилось, видно в журнале; остальное можно повторить."));

            var count = 0;
            foreach (var spec in specs)
            {
                window.Width = spec.Width;
                window.Height = spec.Height;
                await FlushAsync(window);
                var root = (FrameworkElement)window.Content;
                if (Math.Abs(root.ActualWidth - spec.Width) > 1 || Math.Abs(root.ActualHeight - spec.Height) > 1)
                    throw new InvalidOperationException($"размер содержимого {root.ActualWidth}x{root.ActualHeight}, нужен {spec.Width}x{spec.Height}");
                Save(directory, spec, Render(root, root.ActualWidth, root.ActualHeight, spec.Dpi));
                count++;
            }
            return count;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Ждёт, пока экран загрузится (кроме состояния «загрузка», где он нарочно не отвечает).</summary>
    private static async Task SettleAsync(PageViewModel page, SnapshotState state)
    {
        if (page is StatefulPageViewModel s && state != SnapshotState.Loading)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (s.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (s.IsLoading) throw new TimeoutException("экран не загрузился за 5 секунд");
        }
    }

    // ----- диалоги -----

    private static async Task<int> RenderDialogAsync(string directory, List<SnapshotSpec> specs)
    {
        Window dialog = specs[0].Screen switch
        {
            "dialog-confirm" => new Views.ConfirmDialog(new ConfirmContent(
                "Включить режим «Тишина»? (демо)",
                "Демо: будет изменено 3 значения реестра. Каждое можно вернуть из журнала, ничего не применяется на этом компьютере.",
                "Включить")),
            "dialog-confirm-danger" => new Views.ConfirmDialog(new ConfirmContent(
                "Отключить службу «Демо-служба»? (демо)",
                "Демо: служба перестанет запускаться при старте Windows. Вернуть можно в журнале: «Откатить». Ничего не применяется на этом компьютере.",
                "Отключить", Danger: true)),
            "dialog-conflicts" => new Views.ConflictsDialog(new[]
            {
                new ConflictInfo(1, StepKind.RegistryValue, "значение 1 (демо)", null),
                new ConflictInfo(2, StepKind.ServiceStart, "Вручную (демо)", Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000")),
            }),
            var other => throw new InvalidOperationException("неизвестный диалог " + other),
        };
        Prepare(dialog, 0, 0);
        try
        {
            ShowWithoutFocus(dialog);
            var count = 0;
            foreach (var spec in specs)
            {
                await FlushAsync(dialog);
                var root = (FrameworkElement)dialog.Content;
                Save(directory, spec, Render(root, root.ActualWidth, root.ActualHeight, spec.Dpi));
                count++;
            }
            return count;
        }
        finally
        {
            dialog.Close();
        }
    }

    // ----- окно без фокуса -----

    private static void Prepare(Window w, int width, int height)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = OffScreen;
        w.Top = OffScreen;
        if (width > 0) w.Width = width;
        if (height > 0) w.Height = height;
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            _ = SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
    }

    private static void ShowWithoutFocus(Window w)
    {
        w.Show();
        // Страховка: если окно всё же стало активным или передним у нашего процесса, прерываем работу.
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == new WindowInteropHelper(w).Handle)
            throw new InvalidOperationException("окно снимка забрало фокус");
        if (fg != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(fg, out var pid);
            if (pid == (uint)Environment.ProcessId)
                throw new InvalidOperationException("передним стало окно процесса снимков");
        }
    }

    private static async Task FlushAsync(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        w.UpdateLayout();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        w.UpdateLayout();
    }

    // ----- рисунок -----

    private static BitmapSource Render(FrameworkElement root, double width, double height, int dpi)
    {
        var scale = dpi / 96.0;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        var background = (Brush)Application.Current.FindResource("BgBrush");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(background, null, rect);
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void Save(string directory, SnapshotSpec spec, BitmapSource bmp)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(Path.Combine(directory, spec.FileName));
        encoder.Save(stream);
    }
}
