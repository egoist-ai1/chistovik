using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Chistovik.Core.Ipc;

namespace Chistovik.App.Services.Agent;

public sealed class AgentClientOptions
{
    /// <summary>Сервер агента закрывается через 5 минут тишины; Ping раз в минуту держит связь.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Сколько ждать ответа на обычный запрос (откат режима и твиков может быть долгим).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Удаление программы: деинсталлятор может работать долго (на стороне агента срок запуска 10 минут).</summary>
    public TimeSpan UninstallTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Очистка диска: перебор тысяч файлов (на стороне агента не больше 20 минут на категорию).</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromMinutes(25);

    /// <summary>Чистка реестра: проверка свидетельств, копии ключей и удаление.</summary>
    public TimeSpan FindingsTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Откат операции. Число шагов в запросе не указано (его знает только журнал агента), а откат чистки реестра может возвращать
    /// сотни ключей, поэтому срок фиксированный и увеличенный: короткий срок дал бы «результат неизвестен» при занятом агенте
    /// и повторный UAC с кодом 3.
    /// </summary>
    public TimeSpan UndoTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Ожидание канала после запуска агента (UAC уже пройден).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Шаг опроса мьютекса агента после кода выхода 3 («уже запущен»).</summary>
    public TimeSpan MutexPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Сколько ждать, пока прежний агент отпустит мьютекс; не отпустил, повторный запуск (и UAC) не делается.</summary>
    public TimeSpan MutexPollTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Проверка машинного мьютекса агента без прав администратора.</summary>
    public IAgentInstanceProbe InstanceProbe { get; init; } = new MutexAgentInstanceProbe();

    /// <summary>Быстрая попытка вернуться в тот же канал, прежде чем просить UAC заново.</summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>false — обычный пользователь без прав администратора: только просмотр, runas не запускается (0.1).</summary>
    public bool CanElevate { get; init; } = AdminAccountCheck.Windows().CanElevate();

    public string UserSid { get; init; } = CurrentSid();
    public int ParentPid { get; init; } = Environment.ProcessId;
    public string AppPath { get; init; } = Chistovik.Native.Ipc.ClientProbe.SelfImagePath();

    static string CurrentSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch (Exception e) when (e is System.Security.SecurityException or PlatformNotSupportedException) { return ""; }
    }
}

/// <summary>
/// Клиент агента: ленивый запуск через <see cref="IAgentLauncher"/>, кадры по <see cref="IAgentTransport"/>,
/// последовательные запросы, Ping по таймеру, повтор с тем же Id при обрыве, режим просмотра при отказе UAC.
/// </summary>
public sealed class AgentClient : IAgentClient, IDisposable
{
    readonly IAgentLauncher _launcher;
    readonly IAgentTransport _transport;
    readonly AgentClientOptions _opt;
    readonly Func<string> _pipeNameFactory;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _life = new();

    Stream? _stream;
    string? _pipeName;
    AgentAccess _access = AgentAccess.NotStarted;
    Task? _heartbeat;
    bool _disposed;

    public AgentClient(IAgentLauncher launcher, IAgentTransport transport, AgentClientOptions? options = null,
        Func<string>? pipeNameFactory = null)
    {
        _launcher = launcher;
        _transport = transport;
        _opt = options ?? new AgentClientOptions();
        _pipeNameFactory = pipeNameFactory ?? (() => "Chistovik." + Guid.NewGuid().ToString("N"));
        if (!_opt.CanElevate)
        {
            _access = AgentAccess.ViewOnly;
            _viewOnlyReason = AgentViewOnlyReason.NoAdminAccount;
        }
    }

    AgentViewOnlyReason _viewOnlyReason;

    public event EventHandler? AccessChanged;

    public AgentViewOnlyReason ViewOnlyReason => _access == AgentAccess.ViewOnly ? _viewOnlyReason : AgentViewOnlyReason.None;

    public AgentAccess Access => _access;

    public bool IsElevated => _access == AgentAccess.Connected;

    public void AllowElevationRetry()
    {
        // Обычному пользователю повторный запрос не поможет: нужна другая учётная запись.
        if (_access == AgentAccess.ViewOnly && _viewOnlyReason != AgentViewOnlyReason.NoAdminAccount)
        {
            _viewOnlyReason = AgentViewOnlyReason.None;
            SetAccess(AgentAccess.NotStarted);
        }
    }

    AgentResponse ViewOnlyFail(AgentRequest request)
        => _viewOnlyReason == AgentViewOnlyReason.NoAdminAccount
            ? Fail(request, ClientCodes.NoAdminAccount, LaunchMessages.NoAdminAccount)
            : Fail(request, ClientCodes.Declined, "Права администратора не выданы: доступен только просмотр.");

    void SetAccess(AgentAccess a)
    {
        if (_access == a) return;
        _access = a;
        AccessChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken ct = default)
    {
        var problem = request.Validate();
        if (problem is not null || request.Id == Guid.Empty)
            return Fail(request, ResponseCodes.BadRequest, problem ?? "Пустой Id запроса");

        if (_access == AgentAccess.ViewOnly)
            return ViewOnlyFail(request);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return Fail(request, ClientCodes.NotRunning, "Связь с агентом закрыта.");
            if (_access == AgentAccess.ViewOnly)
                return ViewOnlyFail(request);

            // Ping без связи ничего не запускает: UAC только по действию пользователя.
            if (request is PingRequest && _stream is null)
                return Fail(request, ClientCodes.NotRunning, "Агент не запущен.");

            var prepared = await EnsureConnectedAsync(request, ct).ConfigureAwait(false);
            if (prepared is not null) return prepared;

            var timeout = TimeoutFor(request);
            var result = await ExchangeAsync(request, timeout, ct).ConfigureAwait(false);
            if (result.Retry)
            {
                // Обрыв до ответа: повтор с тем же Id безопасен, агент вернёт сохранённый результат (DESIGN-0.1 §3).
                prepared = await EnsureConnectedAsync(request, ct).ConfigureAwait(false);
                if (prepared is not null) return prepared;
                result = await ExchangeAsync(request, timeout, ct, allowRetry: false).ConfigureAwait(false);
            }
            return result.Response!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Срок ожидания ответа по типу запроса: удаление программы и очистка диска долгие, чистка реестра средняя;
    /// измерение очистки (<c>MeasureOnly</c>) это обычный короткий запрос.
    /// </summary>
    internal TimeSpan TimeoutFor(AgentRequest request) => request switch
    {
        UninstallRequest => _opt.UninstallTimeout,
        CleanCategoriesRequest { MeasureOnly: false } => _opt.CleanupTimeout,
        ApplyFindingsRequest => _opt.FindingsTimeout,
        UndoRequest => _opt.UndoTimeout,
        _ => _opt.RequestTimeout,
    };

    /// <summary>Один Ping, как по таймеру. Возвращает false, если связь потеряна. Для тестов и таймера.</summary>
    public async Task<bool> HeartbeatOnceAsync(CancellationToken ct = default)
    {
        if (_stream is null || !_gate.Wait(0)) return _stream is not null; // занят настоящим запросом: связь жива
        try
        {
            if (_stream is null) return false;
            var ping = new PingRequest { Id = Guid.NewGuid() };
            var r = await ExchangeAsync(ping, _opt.HeartbeatTimeout, ct, allowRetry: false).ConfigureAwait(false);
            return r.Response is { Ok: true };
        }
        finally { _gate.Release(); }
    }

    sealed record Exchange(AgentResponse? Response, bool Retry);

    /// <summary>Поднимает связь, если её нет. Возвращает готовый отказ-ответ или null, когда можно слать.</summary>
    async Task<AgentResponse?> EnsureConnectedAsync(AgentRequest request, CancellationToken ct)
    {
        if (_stream is not null) return null;

        // Сначала тихо пробуем тот же канал (агент мог пережить обрыв), UAC только если канала уже нет.
        if (_pipeName is not null)
        {
            try
            {
                _stream = await _transport.ConnectAsync(_pipeName, _opt.ReconnectTimeout, ct).ConfigureAwait(false);
                SetConnected();
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AgentUntrustedException e)
            {
                // Имя канала занял чужой процесс: запрос не отправляем и нового агента поверх не запускаем.
                _pipeName = null;
                return Untrusted(request, e);
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException) { }
        }

        // Код 3 (другой агент ещё живёт, например дорабатывает операцию после потери связи) повторяем один раз, когда мьютекс агента освободится.
        for (var attempt = 0; ; attempt++)
        {
            var pipe = _pipeNameFactory();
            AgentLaunchResult launch;
            try
            {
                launch = await _launcher.LaunchAsync(new AgentLaunchArgs(pipe, _opt.ParentPid, _opt.UserSid, _opt.AppPath), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

            switch (launch.Status)
            {
                case AgentLaunchStatus.Declined:
                    _viewOnlyReason = AgentViewOnlyReason.Declined;
                    SetAccess(AgentAccess.ViewOnly);
                    return ViewOnlyFail(request);
                case AgentLaunchStatus.Failed:
                    return Fail(request, ClientCodes.LaunchFailed, launch.Message ?? "Не удалось запустить агент.");
            }

            _pipeName = pipe;
            try
            {
                _stream = await ConnectWatchingExitAsync(pipe, launch.Exited, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AgentExitedException e)
            {
                _pipeName = null;
                if (e.Code == Chistovik.Native.Ipc.AgentExitCodes.AlreadyRunning)
                {
                    // Прежний агент дорабатывает запрос и новых подключений не принимает: второй UAC нужен только когда мьютекс уже свободен.
                    if (attempt == 0 && await WaitForInstanceReleaseAsync(ct).ConfigureAwait(false))
                        continue;
                    return Fail(request, ClientCodes.AlreadyRunning, LaunchMessages.AlreadyRunning);
                }
                return Fail(request, ClientCodes.LaunchFailed, e.Message);
            }
            catch (AgentUntrustedException e)
            {
                _pipeName = null;
                return Untrusted(request, e);
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                _pipeName = null;
                return Fail(request, ClientCodes.LaunchFailed, "Агент запущен, но не отвечает.");
            }
            SetConnected();
            return null;
        }
    }

    /// <summary>true — мьютекса агента больше нет (можно запускать повторно); false — держится дольше <see cref="AgentClientOptions.MutexPollTimeout"/>.</summary>
    async Task<bool> WaitForInstanceReleaseAsync(CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_opt.InstanceProbe.IsHeld())
        {
            if (clock.Elapsed >= _opt.MutexPollTimeout) return false;
            await Task.Delay(_opt.MutexPollInterval, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Процесс агента завершился до подключения; <see cref="Exception.Message"/> это готовый русский текст.</summary>
    sealed class AgentExitedException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>
    /// Подключается к каналу, но если процесс агента вышел с ошибкой (код 3 и любые другие, кроме штатных 0 и 4), не ждёт таймаут подключения.
    /// </summary>
    async Task<Stream> ConnectWatchingExitAsync(string pipe, Task<int>? exited, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Обёртка превращает синхронное исключение транспорта в сбойную задачу: сначала нужно посмотреть на код выхода агента.
        var connect = Task.Run(() => _transport.ConnectAsync(pipe, _opt.ConnectTimeout, cts.Token), CancellationToken.None);
        if (exited is null) return await connect.ConfigureAwait(false);

        await Task.WhenAny(connect, exited).ConfigureAwait(false);
        if (exited.IsCompletedSuccessfully && LaunchMessages.ForAgentExit(exited.Result) is { } text)
        {
            cts.Cancel();
            try { (await connect.ConfigureAwait(false)).Dispose(); } catch (Exception) { /* подключение не удалось или отменено: так и нужно */ }
            throw new AgentExitedException(exited.Result, text);
        }
        return await connect.ConfigureAwait(false);
    }

    static AgentResponse Untrusted(AgentRequest request, AgentUntrustedException e)
        => Fail(request, ClientCodes.Untrusted,
            e.Reason == "not-elevated"
                ? Models.ReasonTexts.Explain(e.Reason)
                : "Канал занят процессом, который не является установленным агентом Чистовика (" + e.Reason + "). Запрос не отправлен.");

    void SetConnected()
    {
        SetAccess(AgentAccess.Connected);
        StartHeartbeat();
    }

    void StartHeartbeat()
    {
        if (_heartbeat is not null) return;
        _heartbeat = Task.Run(async () =>
        {
            var token = _life.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(_opt.HeartbeatInterval, token).ConfigureAwait(false);
                    if (_stream is not null) await HeartbeatOnceAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    async Task<Exchange> ExchangeAsync(AgentRequest request, TimeSpan timeout, CancellationToken ct, bool allowRetry = true)
    {
        var stream = _stream!;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await FrameCodec.WriteAsync(stream, AgentJson.Serialize(request), linked.Token).ConfigureAwait(false);
            var frame = await FrameCodec.ReadAsync(stream, linked.Token).ConfigureAwait(false);
            if (frame is null) return Dropped(request, allowRetry, "Агент закрыл связь.");

            var response = AgentJson.DeserializeResponse(frame);
            if (response.Id != request.Id)
                return Dropped(request, false, "Ответ агента не соответствует запросу.");
            return new Exchange(response, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Состояние кадра неизвестно: канал закрываем, чтобы следующий запрос не прочёл чужой ответ.
            Drop();
            throw;
        }
        catch (OperationCanceledException)
        {
            Drop();
            return new Exchange(Fail(request, ClientCodes.Timeout,
                "Агент не ответил вовремя. Результат неизвестен: проверьте журнал."), false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or JsonException)
        {
            return Dropped(request, allowRetry, "Связь с агентом оборвалась.");
        }
    }

    Exchange Dropped(AgentRequest request, bool retry, string message)
    {
        Drop();
        return retry
            ? new Exchange(null, true)
            : new Exchange(Fail(request, ClientCodes.Disconnected, message), false);
    }

    void Drop()
    {
        var s = _stream;
        _stream = null;
        try { s?.Dispose(); } catch (IOException) { }
        if (_access == AgentAccess.Connected) SetAccess(AgentAccess.Lost);
    }

    static AgentResponse Fail(AgentRequest r, string code, string message) => new(r.Id, false, code, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _life.Cancel();
        var s = _stream;
        _stream = null;
        try { s?.Dispose(); } catch (IOException) { }
    }
}
