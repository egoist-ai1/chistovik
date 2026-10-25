using System.Security.AccessControl;
using System.Security.Principal;

namespace Chistovik.Native.Ipc;

/// <summary>Коды выхода Chistovik.Agent.exe. Приложение по ним понимает, почему агент не запустился.</summary>
public static class AgentExitCodes
{
    public const int Ok = 0;
    public const int BadArgs = 2;
    /// <summary>Другой агент уже работает на этом компьютере (возможно, в другом сеансе Windows).</summary>
    public const int AlreadyRunning = 3;
    public const int Idle = 4;
    public const int BadEnvironment = 5;
    /// <summary>Имя мьютекса службы занято чужим объектом (другой DACL или другой тип объекта): стартовать безопаснее не будем.</summary>
    public const int InstanceNameTaken = 6;
    /// <summary>Мьютекс службы создать не удалось по иной причине (ошибка ввода-вывода, недопустимое имя).</summary>
    public const int InstanceLockError = 7;
}

/// <summary>Итог попытки занять «место» агента.</summary>
public enum InstanceLockOutcome
{
    /// <summary>Место занято нами.</summary>
    Acquired,
    /// <summary>Мьютекс уже существует и доступен нам: работает другой агент.</summary>
    Busy,
    /// <summary>Мьютекс с этим именем есть, но открыть его нельзя (чужой DACL или объект другого типа).</summary>
    NameTaken,
    /// <summary>Создать мьютекс не удалось (IOException и подобное).</summary>
    Error,
}

/// <summary>
/// Один агент на весь компьютер: именованный мьютекс в глобальном пространстве имён. Второй агент (другой пользователь при быстром
/// переключении, второй сеанс) обязан выйти до восстановления журнала и до любых записей, иначе он закроет чужую идущую операцию
/// как прерванную и оба процесса будут писать в HKLM и журнал одновременно.
/// Доступ к мьютексу только у администраторов и SYSTEM: агент всегда запущен с повышением, обычному пользователю открывать его не нужно,
/// а лишние права (SYNCHRONIZE) позволили бы ему занять мьютекс и удерживать его. Если мьютекс уже есть, но открыть его нельзя
/// (чужой DACL), это считается «занято»: безопаснее не стартовать.
/// </summary>
public sealed class AgentInstanceLock : IDisposable
{
    public const string MachineName = @"Global\Chistovik.Agent";

    readonly Mutex _mutex;

    AgentInstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>Захватывает «место» агента. null — не получилось (причину даёт перегрузка с <c>outcome</c>).</summary>
    public static AgentInstanceLock? TryAcquire(string name = MachineName) => TryAcquire(name, out _);

    /// <summary>Как <see cref="TryAcquire(string)"/>, но сообщает причину отказа: «занято» (<see cref="InstanceLockOutcome.Busy"/>) отличается от «имя занято чужим» и от ошибки.</summary>
    public static AgentInstanceLock? TryAcquire(string name, out InstanceLockOutcome outcome) => TryAcquire(name, null, out outcome);

    /// <summary>Шов для тестов: <paramref name="securityOverride"/> заменяет DACL (по умолчанию администраторы и SYSTEM).</summary>
    internal static AgentInstanceLock? TryAcquire(string name, MutexSecurity? securityOverride, out InstanceLockOutcome outcome)
    {
        var security = securityOverride ?? DefaultSecurity();
        try
        {
            // Владение не берём: мьютекс служит именем-замком, а не блокировкой потока (Main агента асинхронный).
            var mutex = MutexAcl.Create(false, name, out var created, security);
            if (created)
            {
                outcome = InstanceLockOutcome.Acquired;
                return new AgentInstanceLock(mutex);
            }
            mutex.Dispose();
            outcome = InstanceLockOutcome.Busy;
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Имя в Global\ может создать кто угодно, поэтому «нет доступа» означает чужой мьютекс (или токен без прав): стартовать нельзя.
            outcome = InstanceLockOutcome.NameTaken;
            return null;
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            // MutexAcl.Create документированно бросает IOException (в том числе DirectoryNotFoundException) и ArgumentException.
            outcome = InstanceLockOutcome.Error;
            return null;
        }
    }

    static MutexSecurity DefaultSecurity()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public void Dispose() => _mutex.Dispose();
}
