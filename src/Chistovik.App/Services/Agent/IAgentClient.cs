using System.IO;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Services.Agent;

/// <summary>Что известно о привилегированном агенте.</summary>
public enum AgentAccess
{
    /// <summary>Агент не запускался: он стартует при первом изменении (UAC).</summary>
    NotStarted,
    Connected,
    /// <summary>Связь оборвалась; следующий запрос подключится заново.</summary>
    Lost,
    /// <summary>Пользователь отказал в правах: только просмотр, пока не запросить снова.</summary>
    ViewOnly,
}

/// <summary>Коды ответов, которые формирует сам клиент (в протоколе агента их нет).</summary>
public static class ClientCodes
{
    public const string Declined = "agent-declined";
    public const string NotRunning = "agent-not-running";
    public const string LaunchFailed = "agent-launch-failed";
    public const string Timeout = "client-timeout";
    public const string Disconnected = "client-disconnected";
    /// <summary>На другом конце канала не наш установленный Chistovik.Agent.exe: запрос не отправлялся.</summary>
    public const string Untrusted = "agent-untrusted";
    /// <summary>Агент вышел с кодом 3: другой агент уже работает на компьютере (другой сеанс Windows).</summary>
    public const string AlreadyRunning = "agent-already-running";    /// <summary>Пользователь без прав администратора: режим просмотра, запрос прав не показывается.</summary>
    public const string NoAdminAccount = "agent-no-admin-account";
}

/// <summary>Почему включён режим просмотра.</summary>
public enum AgentViewOnlyReason { None, Declined, NoAdminAccount }

/// <summary>Транспорт не принял сервер канала: образ процесса не совпал с установленным агентом. Данные по каналу не передавались.</summary>
public sealed class AgentUntrustedException(string reason) : Exception("Сервер канала не прошёл проверку: " + reason)
{
    /// <summary>Код причины из ServerVerifier (pid, image-path, image-hash, not-elevated) или agent-missing.</summary>
    public string Reason { get; } = reason;
}

public interface IAgentClient
{
    AgentAccess Access { get; }

    /// <summary>true, пока есть живая связь с агентом, запущенным с правами.</summary>
    bool IsElevated { get; }

    event EventHandler? AccessChanged;

    /// <summary>Причина режима просмотра (имеет смысл при <see cref="AgentAccess.ViewOnly"/>).</summary>
    AgentViewOnlyReason ViewOnlyReason => AgentViewOnlyReason.None;

    /// <summary>
    /// Отправляет запрос и ждёт ответ. Агент запускается лениво, при первом запросе на изменение. Отказ UAC даёт
    /// ответ с кодом <see cref="ClientCodes.Declined"/> и режим просмотра; в этом режиме запросы не отправляются.
    /// Ошибки связи возвращаются ответом (Ok=false), отмена — OperationCanceledException.
    /// </summary>
    Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default);

    /// <summary>Снимает режим просмотра, чтобы следующий запрос снова показал UAC.</summary>
    void AllowElevationRetry();
}

public sealed record AgentLaunchArgs(string PipeName, int ParentPid, string UserSid, string AppPath);

public enum AgentLaunchStatus { Started, Declined, Failed }

/// <param name="Exited">Завершится кодом выхода процесса агента, если процесс запущен и за ним удалось следить; иначе null.</param>
public sealed record AgentLaunchResult(AgentLaunchStatus Status, string? Message = null, Task<int>? Exited = null);

/// <summary>Запуск процесса агента с правами (ShellExecute runas). За интерфейсом, чтобы тестировать без процесса.</summary>
public interface IAgentLauncher
{
    Task<AgentLaunchResult> LaunchAsync(AgentLaunchArgs args, CancellationToken ct);
}

/// <summary>Подключение к каналу агента. Тесты подставляют потоки в памяти.</summary>
public interface IAgentTransport
{
    /// <summary>Возвращает двунаправленный поток или бросает TimeoutException/IOException, если канал не открылся.</summary>
    Task<Stream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct);
}
