using System.Security.Principal;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Native.Ipc;

namespace Chistovik.Agent;

/// <summary>
/// Агент с правами администратора. Запускается приложением через ShellExecute runas:
/// Chistovik.Agent.exe --pipe NAME --parent-pid PID --user-sid SID --app-path PATH
/// Порядок старта: мьютекс → граф (<see cref="AgentComposition"/>: каталог журнала и ACL, журнал, восстановление, очистка)
/// → только затем канал. Если журналом пользоваться нельзя, канал открывается, но кроме Ping отвечает кодом ошибки.
/// </summary>
public static class Program
{
    const int ExitOk = 0, ExitBadArgs = 2, ExitAlreadyRunning = 3, ExitIdle = 4, ExitBadEnvironment = 5;

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var pipe, out var parentPid, out var userSid, out var appPath))
        {
            Console.Error.WriteLine("Использование: Chistovik.Agent.exe --pipe NAME --parent-pid PID --user-sid SID --app-path PATH");
            return ExitBadArgs;
        }

        using var mutex = new Mutex(true, @"Global\Chistovik.Agent." + userSid.Value, out var created);
        if (!created) return ExitAlreadyRunning;

        // Весь граф строится до открытия канала: восстановление после обрыва выполняется раньше, чем придёт первый запрос.
        PolicyContext policy;
        try { policy = PolicyContext.FromEnvironment(); }
        catch (PolicyContextException e)
        {
            // Политику на неверных системных каталогах строить нельзя: безопаснее не стартовать.
            Console.Error.WriteLine("Системные каталоги заданы неверно: " + e.Message);
            return ExitBadEnvironment;
        }

        using var composition = AgentComposition.Build(AgentPorts.Windows(), AgentComposition.DefaultJournalDirectory(),
            policy, msg => Console.Error.WriteLine("[agent] " + msg));

        string hash;
        try { hash = ClientVerifier.Sha256OfFile(appPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Не удалось прочитать образ приложения: " + e.Message);
            return ExitBadArgs;
        }

        var expected = new ExpectedClient(parentPid, appPath, hash, ClientProbe.CurrentSession(), userSid.Value);
        var server = new AgentPipeServer(pipe, userSid, expected, new RequestDispatcher(composition.Handler), TimeSpan.FromMinutes(5));
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
