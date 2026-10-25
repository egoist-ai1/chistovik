using System.Runtime.InteropServices;
using System.Text;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Setup;

/// <summary>
/// Запуск деинсталлятора без оболочки: CreateProcess в приостановленном состоянии с чистым окружением (только системные
/// переменные, TEMP и TMP в защищённом каталоге агента), задание без KILL_ON_JOB_CLOSE, затем ожидание, пока в задании не
/// останется процессов (деинсталляторы Inno и NSIS перезапускают себя из временной папки и сразу выходят). Ожидание идёт в
/// фоне: по сроку процессы не убиваются, вызывающий получает задачу и сам решает, ждать ли дальше.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Запускает процесс и возвращает задачу, которая завершится, когда в задании не останется процессов.</summary>
    public static Task<UninstallOutcome> Start(string exePath, string arguments)
    {
        // Путь передаётся и как имя приложения, и в кавычках в командной строке: поиск по PATH и разбор пробелов исключены.
        var commandLine = new StringBuilder("\"" + exePath + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments));
        var directory = Path.GetDirectoryName(exePath);

        var job = IntPtr.Zero;
        var port = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var info = default(Win32Setup.ProcessInformation);
        string? tempDir = null;
        var handedOver = false;
        try
        {
            tempDir = ProtectedTemp.Create();
            environment = CleanEnvironment.Build(tempDir);

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
                    environment, directory, ref si, out info))
                throw Win32.Fail(Marshal.GetLastWin32Error(), "CreateProcess");

            if (!Win32Setup.AssignProcessToJobObject(job, info.Process))
            {
                var code = Marshal.GetLastWin32Error();
                Win32Setup.TerminateProcess(info.Process, 1); // процесс ещё не начал работу: безопасно прервать
                throw Win32.Fail(code, "AssignProcessToJobObject");
            }

            if (Win32Setup.ResumeThread(info.Thread) == uint.MaxValue) throw Win32.Fail(Marshal.GetLastWin32Error(), "ResumeThread");

            // Дальше дескрипторы и каталог принадлежит фоновому ожиданию.
            var (p, j, pr, th, td) = (port, job, info.Process, info.Thread, tempDir);
            handedOver = true;
            return Task.Factory.StartNew(() => WaitAndClean(p, j, pr, th, td),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            CleanEnvironment.Free(environment);
            if (!handedOver)
            {
                if (info.Thread != IntPtr.Zero) Win32Setup.CloseHandle(info.Thread);
                if (info.Process != IntPtr.Zero) Win32Setup.CloseHandle(info.Process);
                if (port != IntPtr.Zero) Win32Setup.CloseHandle(port);
                if (job != IntPtr.Zero) Win32Setup.CloseHandle(job);
                ProtectedTemp.TryDelete(tempDir);
            }
        }
    }

    static UninstallOutcome WaitAndClean(IntPtr port, IntPtr job, IntPtr process, IntPtr thread, string tempDir)
    {
        try
        {
            while (true)
            {
                if (!Win32Setup.GetQueuedCompletionStatus(port, out var message, out _, out _, Win32Setup.Infinite))
                    throw Win32.Fail(Marshal.GetLastWin32Error(), "GetQueuedCompletionStatus");
                if (message == Win32Setup.JobObjectMsgActiveProcessZero) break;
            }

            if (!Win32Setup.GetExitCodeProcess(process, out var exit)) throw Win32.Fail(Marshal.GetLastWin32Error(), "GetExitCodeProcess");
            return new UninstallOutcome(unchecked((int)exit), false);
        }
        finally
        {
            Win32Setup.CloseHandle(thread);
            Win32Setup.CloseHandle(process);
            Win32Setup.CloseHandle(port);
            Win32Setup.CloseHandle(job); // без KILL_ON_JOB_CLOSE: процессы продолжают работу
            ProtectedTemp.TryDelete(tempDir);
        }
    }
}
