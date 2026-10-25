using System.Runtime.InteropServices;
using System.Security.Principal;
using Chistovik.Native.Ipc;

namespace Chistovik.App.Services.Agent;

/// <summary>Русские сообщения для кодов запуска агента (ShellExecute runas) и кодов выхода самого агента.</summary>
public static class LaunchMessages
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorElevationRequired = 740;
    public const int ErrorCancelled = 1223;

    public const string AlreadyRunning = "Служба Чистовика уже запущена, возможно в другом сеансе Windows. Закройте её там или подождите несколько секунд и повторите.";
    public const string InstanceNameTaken = "Имя службы Чистовика занято другой программой. Перезапустите компьютер или обратитесь к администратору.";
    public const string InstanceLockError = "Служба Чистовика не смогла занять своё имя в системе.";
    public const string BadArguments = "Служба Чистовика не приняла параметры запуска. Переустановите Чистовик.";
    public const string BadEnvironment = "Служба Чистовика не запустилась: системные каталоги заданы неверно.";
    public static string UnknownExit(int code) => $"Служба Чистовика завершилась с ошибкой (код {code}). Подробности в журнале агента.";
    public const string AccessDenied = "Windows отклонила запрос прав администратора (возможно, действует политика организации).";
    public const string ElevationRequired = "Для запуска нужны права администратора.";
    public const string NoAdminAccount = "Только просмотр: для изменений нужна учётная запись администратора";

    /// <summary>Понятный текст для кода ошибки Win32 при запуске; null — код не знаком, показывается системный текст.</summary>
    public static string? ForNativeError(int code) => code switch
    {
        ErrorAccessDenied => AccessDenied,
        ErrorElevationRequired => ElevationRequired,
        _ => null,
    };

    /// <summary>
    /// Текст для кода выхода агента до подключения; null — для 0 (штатно) и 4 (простой), когда сообщение не нужно.
    /// Любой другой код даёт общий текст с номером.
    /// </summary>
    public static string? ForAgentExit(int exitCode) => exitCode switch
    {
        AgentExitCodes.Ok or AgentExitCodes.Idle => null,
        AgentExitCodes.AlreadyRunning => AlreadyRunning,
        AgentExitCodes.InstanceNameTaken => InstanceNameTaken,
        AgentExitCodes.InstanceLockError => InstanceLockError,
        AgentExitCodes.BadArgs => BadArguments,
        AgentExitCodes.BadEnvironment => BadEnvironment,
        _ => UnknownExit(exitCode),
    };
}

/// <summary>Вид токена процесса приложения (TOKEN_ELEVATION_TYPE).</summary>
public enum TokenElevationKind { Unknown = 0, Default = 1, Full = 2, Limited = 3 }

/// <summary>Факты о текущем пользователе; за интерфейсом, чтобы подставлять в тестах.</summary>
public interface IElevationEnvironment
{
    TokenElevationKind ElevationType { get; }
    bool IsInAdministratorsRole { get; }
}

/// <summary>
/// Может ли текущий пользователь вообще получить права администратора через UAC без чужого пароля.
/// Администратор с включённым UAC: токен Limited (или Full, если приложение уже повышено). Администратор при выключенном UAC:
/// Default и роль Administrators. Обычный пользователь: Default без роли. Ему запрос прав попросил бы пароль другого администратора,
/// агент стартовал бы под другой учётной записью, и приложение не смогло бы проверить доверие к его процессу, поэтому
/// в версии 0.1 такой пользователь работает в режиме просмотра. Если токен прочитать не удалось (Unknown), запрет не вводится.
/// </summary>
public sealed class AdminAccountCheck(IElevationEnvironment env)
{
    public bool CanElevate()
        => env.ElevationType switch
        {
            TokenElevationKind.Limited or TokenElevationKind.Full => true,
            TokenElevationKind.Default => env.IsInAdministratorsRole,
            _ => true,
        };

    public static AdminAccountCheck Windows() => new(new WindowsElevationEnvironment());
}

public sealed class WindowsElevationEnvironment : IElevationEnvironment
{
    const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    public TokenElevationKind ElevationType
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                if (GetTokenInformation(id.AccessToken.DangerousGetHandle(), TokenElevationTypeClass, out var type, sizeof(int), out _)
                    && type is >= 1 and <= 3)
                    return (TokenElevationKind)type;
            }
            catch (Exception e) when (e is System.Security.SecurityException or PlatformNotSupportedException or InvalidOperationException) { }
            return TokenElevationKind.Unknown;
        }
    }

    public bool IsInAdministratorsRole
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception e) when (e is System.Security.SecurityException or PlatformNotSupportedException) { return false; }
        }
    }
}
