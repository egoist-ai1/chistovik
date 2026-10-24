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
}

public interface IAgentClient
{
    AgentAccess Access { get; }

    /// <summary>true, пока есть живая связь с агентом, запущенным с правами.</summary>
    bool IsElevated { get; }

    event EventHandler? AccessChanged;

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

public sealed record AgentLaunchResult(AgentLaunchStatus Status, string? Message = null);

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
