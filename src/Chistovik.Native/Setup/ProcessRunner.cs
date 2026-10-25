using System.Diagnostics;
using System.Runtime.InteropServices;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Setup;

/// <summary>
/// Запуск деинсталлятора без оболочки: CreateProcess в приостановленном состоянии, задание без KILL_ON_JOB_CLOSE, затем
/// ожидание, пока в задании не останется процессов (деинсталляторы Inno и NSIS перезапускают себя из временной папки и сразу
/// выходят). По сроку процессы не убиваются: результат TimedOut, дальше решает журнал и проверка состояния.
/// </summary>
internal static class ProcessRunner
{
    public static UninstallOutcome Run(string exePath, string arguments, TimeSpan timeout)
    {
        // Путь передаётся и как имя приложения, и в кавычках в командной строке: поиск по PATH и разбор пробелов исключены.
        var commandLine = "\"" + exePath + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
        var directory = Path.GetDirectoryName(exePath);

        var job = IntPtr.Zero;
        var port = IntPtr.Zero;
        var info = default(Win32Setup.ProcessInformation);
        try
        {
            job = Win32Setup.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Win32.Fail(Marshal.GetLastWin32Error(), "CreateJobObject");

            port = Win32Setup.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (port == IntPtr.Zero) throw Win32.Fail(Marshal.GetLastWin32Error(), "CreateIoCompletionPort");

            var assoc = new Win32Setup.JobAssociateCompletionPort { CompletionKey = job, CompletionPort = port };
            if (!Win32Setup.SetInformationJobObject(job, Win32Setup.JobObjectAssociateCompletionPortInformation, ref assoc,
                    (uint)Marshal.SizeOf<Win32Setup.JobAssociateCompletionPort>()))
                throw Win32.Fail(Marshal.GetLastWin32Error(), "SetInformationJobObject");

            var si = new Win32Setup.StartupInfo { Cb = (uint)Marshal.SizeOf<Win32Setup.StartupInfo>() };
            if (!Win32Setup.CreateProcess(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Win32Setup.CreateSuspended | Win32Setup.CreateUnicodeEnvironment | Win32Setup.CreateNoWindow,
                    IntPtr.Zero, directory, ref si, out info))
                throw Win32.Fail(Marshal.GetLastWin32Error(), "CreateProcess");

            if (!Win32Setup.AssignProcessToJobObject(job, info.Process))
            {
                var code = Marshal.GetLastWin32Error();
                Win32Setup.TerminateProcess(info.Process, 1); // процесс ещё не начал работу: безопасно прервать
                throw Win32.Fail(code, "AssignProcessToJobObject");
            }

            if (Win32Setup.ResumeThread(info.Thread) == uint.MaxValue) throw Win32.Fail(Marshal.GetLastWin32Error(), "ResumeThread");

            var clock = Stopwatch.StartNew();
            while (true)
            {
                var left = timeout - clock.Elapsed;
                if (left <= TimeSpan.Zero) return new UninstallOutcome(0, true);
                var ms = (uint)Math.Min(left.TotalMilliseconds, int.MaxValue - 1);
                if (!Win32Setup.GetQueuedCompletionStatus(port, out var message, out _, out _, ms))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == 258) return new UninstallOutcome(0, true); // WAIT_TIMEOUT
                    throw Win32.Fail(err, "GetQueuedCompletionStatus");
                }
                if (message == Win32Setup.JobObjectMsgActiveProcessZero) break;
            }

            if (!Win32Setup.GetExitCodeProcess(info.Process, out var exit)) throw Win32.Fail(Marshal.GetLastWin32Error(), "GetExitCodeProcess");
            return new UninstallOutcome(unchecked((int)exit), false);
        }
        finally
        {
            if (info.Thread != IntPtr.Zero) Win32Setup.CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) Win32Setup.CloseHandle(info.Process);
            if (port != IntPtr.Zero) Win32Setup.CloseHandle(port);
            if (job != IntPtr.Zero) Win32Setup.CloseHandle(job); // без KILL_ON_JOB_CLOSE: процессы продолжают работу
        }
    }
}
