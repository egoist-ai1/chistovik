using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Setup;

/// <summary>
/// Удаление программ: MSI через Windows Installer по коду продукта без интерфейса и без перезагрузки, остальное запуском
/// деинсталлятора в задании (<see cref="ProcessRunner"/>). Решения о том, что удалять, принимает Core.
/// </summary>
public sealed class WindowsUninstaller : IUninstaller
{
    const int ErrorInstallAlreadyRunning = 1618;

    static readonly object UiLock = new();
    static bool _uiConfigured;

    // Незавершённое удаление (по сроку оно не прерывается): пока оно идёт, новое не запускается, а отвечает «занято».
    static readonly object BusyLock = new();
    static Task? _busy;

    public int QueryMsiState(string productCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        return Win32Setup.MsiQueryProductState(productCode);
    }

    public UninstallOutcome RunMsi(string productCode, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);

        // MsiConfigureProductEx блокирует поток и не прерывается; по сроку возвращаем TimedOut, а вызов доработает сам.
        return Guarded(timeout, () => Task.Factory.StartNew(
            () =>
            {
                EnsureNoUi(); // уровень интерфейса задаётся в том же потоке, что и вызов установщика
                return new UninstallOutcome(unchecked((int)Win32Setup.MsiConfigureProductEx(productCode, Win32Setup.InstallLevelDefault,
                    Win32Setup.InstallStateAbsent, "REBOOT=ReallySuppress")), false);
            },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    public UninstallOutcome RunExe(string exePath, string arguments, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        return Guarded(timeout, () => ProcessRunner.Start(exePath, arguments));
    }

    static UninstallOutcome Guarded(TimeSpan timeout, Func<Task<UninstallOutcome>> start)
    {
        Task<UninstallOutcome> work;
        lock (BusyLock)
        {
            if (_busy is { IsCompleted: false }) return new UninstallOutcome(ErrorInstallAlreadyRunning, false);
            work = start();
            _busy = work;
        }
        return work.Wait(timeout) ? work.GetAwaiter().GetResult() : new UninstallOutcome(0, true);
    }

    // Уровень интерфейса относится ко всему процессу: задаём один раз до первого вызова установщика.
    static void EnsureNoUi()
    {
        lock (UiLock)
        {
            if (_uiConfigured) return;
            Win32Setup.MsiSetInternalUI(Win32Setup.InstallUiLevelNone, IntPtr.Zero);
            _uiConfigured = true;
        }
    }
}
