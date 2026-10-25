using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Setup;

/// <summary>
/// Удаление программ: MSI через Windows Installer по коду продукта без интерфейса и без перезагрузки, остальное запуском
/// деинсталлятора в задании (<see cref="ProcessRunner"/>). Решения о том, что удалять, принимает Core.
/// </summary>
public sealed class WindowsUninstaller : IUninstaller
{
    static readonly object UiLock = new();
    static bool _uiConfigured;

    public int QueryMsiState(string productCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        return Win32Setup.MsiQueryProductState(productCode);
    }

    public UninstallOutcome RunMsi(string productCode, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(productCode);
        EnsureNoUi();

        // MsiConfigureProductEx блокирует поток и не прерывается; по сроку возвращаем TimedOut, а вызов доработает сам.
        var work = Task.Factory.StartNew(
            () => unchecked((int)Win32Setup.MsiConfigureProductEx(productCode, Win32Setup.InstallLevelDefault,
                Win32Setup.InstallStateAbsent, "REBOOT=ReallySuppress")),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return work.Wait(timeout) ? new UninstallOutcome(work.Result, false) : new UninstallOutcome(0, true);
    }

    public UninstallOutcome RunExe(string exePath, string arguments, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        return ProcessRunner.Run(exePath, arguments, timeout);
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
