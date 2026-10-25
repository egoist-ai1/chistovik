using System.Security.Principal;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Native.Ipc;

namespace Chistovik.Agent;

/// <summary>
/// Агент с правами администратора. Запускается приложением через ShellExecute runas:
/// Chistovik.Agent.exe --pipe NAME --parent-pid PID --user-sid SID --app-path PATH
/// Порядок старта: машинный мьютекс Global\Chistovik.Agent (занят — выход с кодом 3) → граф (<see cref="AgentComposition"/>: каталог журнала и ACL, журнал, восстановление, очистка)
/// → только затем канал. Если журналом пользоваться нельзя, канал открывается, но кроме Ping отвечает кодом ошибки.
/// </summary>
public static class Program
{
    const int ExitOk = AgentExitCodes.Ok, ExitBadArgs = AgentExitCodes.BadArgs, ExitAlreadyRunning = AgentExitCodes.AlreadyRunning,
        ExitIdle = AgentExitCodes.Idle, ExitBadEnvironment = AgentExitCodes.BadEnvironment,
        ExitNameTaken = AgentExitCodes.InstanceNameTaken, ExitLockError = AgentExitCodes.InstanceLockError;

    public static async Task<int> Main(string[] args)
    {
        // Файловый журнал подключается позже, когда каталог данных проверен; до тех пор строки копятся в памяти. stderr остаётся как был.
        var fileLog = new AgentFileLog();
        void Log(string line) { Console.Error.WriteLine(line); fileLog.Write(line); }

        if (!TryParse(args, out var pipe, out var parentPid, out var userSid, out var appPath))
        {
            Log("Использование: Chistovik.Agent.exe --pipe NAME --parent-pid PID --user-sid SID --app-path PATH");
            return ExitBadArgs;
        }

        // Один агент на компьютер (не на пользователя): второй выходит до восстановления журнала и любых записей.
        using var instance = AgentInstanceLock.TryAcquire(AgentInstanceLock.MachineName, out var lockOutcome);
        if (instance is null)
        {
            switch (lockOutcome)
            {
                case InstanceLockOutcome.NameTaken:
                    Log("[agent] instance-name-taken");
                    return ExitNameTaken;
                case InstanceLockOutcome.Error:
                    Log("[agent] instance-lock-error");
                    return ExitLockError;
                default:
                    Log("[agent] already-running");
                    return ExitAlreadyRunning;
            }
        }

        // Весь граф строится до открытия канала: восстановление после обрыва выполняется раньше, чем придёт первый запрос.
        PolicyContext policy;
        try { policy = PolicyContext.FromEnvironment(); }
        catch (PolicyContextException e)
        {
            // Политику на неверных системных каталогах строить нельзя: безопаснее не стартовать.
            Log("Системные каталоги заданы неверно: " + e.Message);
            return ExitBadEnvironment;
        }

        var journalDir = AgentComposition.DefaultJournalDirectory();
        using var composition = AgentComposition.Build(AgentPorts.Windows(), journalDir, policy, msg => Log("[agent] " + msg));

        // Писать в каталог можно только если он доверенный (ACL проверен при сборке); иначе файловый журнал отключается.
        if (composition.Mode is JournalMode.Ready or JournalMode.NewerSchema) fileLog.Attach(journalDir);
        else fileLog.Close();

        string hash;
        try { hash = ClientVerifier.Sha256OfFile(appPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log("Не удалось прочитать образ приложения: " + e.GetType().Name);
            return ExitBadArgs;
        }

        var expected = new ExpectedClient(parentPid, appPath, hash, ClientProbe.CurrentSession(), userSid.Value);
        // Причина отказа клиенту (client-rejected:pid и т. п.) идёт в stderr и agent.log: только фиксированное слово, без путей и SID.
        var server = new AgentPipeServer(pipe, userSid, expected, new RequestDispatcher(composition.Handler), TimeSpan.FromMinutes(5),
            msg => Log("[agent] " + msg));
        var served = await server.RunAsync(CancellationToken.None);
        return served ? ExitOk : ExitIdle;
    }

    static bool TryParse(string[] args, out string pipe, out int pid, out SecurityIdentifier sid, out string appPath)
    {
        pipe = appPath = string.Empty; pid = 0; sid = new SecurityIdentifier("S-1-0-0");
        string? sidText = null, pidText = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe": pipe = args[i + 1]; break;
                case "--parent-pid": pidText = args[i + 1]; break;
                case "--user-sid": sidText = args[i + 1]; break;
                case "--app-path": appPath = args[i + 1]; break;
                default: return false;
            }
        }
        if (args.Length % 2 != 0 || pipe.Length == 0 || appPath.Length == 0) return false;
        if (!pipe.StartsWith("Chistovik.", StringComparison.Ordinal) || pipe.Contains('\\')) return false;
        if (!int.TryParse(pidText, out pid) || pid <= 0) return false;
        try { sid = new SecurityIdentifier(sidText ?? string.Empty); }
        catch (ArgumentException) { return false; }
        return true;
    }
}
