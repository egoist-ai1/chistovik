using System.Security.Cryptography;
using Chistovik.Core.Catalog;
using Chistovik.Core.Execution;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Chistovik.Native.Cleanup;
using Chistovik.Native.Env;
using Chistovik.Native.Journal;
using Chistovik.Native.Power;
using Chistovik.Native.Registry;
using Chistovik.Native.Services;
using Chistovik.Native.Setup;
using Chistovik.Native.Tasks;
using Chistovik.Native.Trust;
using Microsoft.Data.Sqlite;

namespace Chistovik.Agent;

/// <summary>Все порты агента. В боевой сборке это Native-реализации, в тестах подставные.</summary>
public sealed record AgentPorts(
    IRegistry Registry,
    IServiceControl Services,
    ITaskScheduler Tasks,
    IPowerControl Power,
    IUserFolders Folders,
    IFileSystem Files,
    IFileSecurity Security,
    IUninstaller Uninstaller,
    IFileTrust Trust,
    IJournalDirectory JournalDirectory,
    IClock Clock,
    IIdGenerator Ids,
    ICleanupFiles Cleanup,
    IRecycleBin RecycleBin,
    IProcessList Processes,
    IRegistryTree RegistryTree)
{
    /// <summary>Реальные порты Windows. Создание ничего не читает и не пишет.</summary>
    public static AgentPorts Windows() => new(
        new WindowsRegistry(),
        new WindowsServiceControl(),
        new WindowsTaskScheduler(),
        new WindowsPowerControl(),
        new WindowsUserFolders(),
        new WindowsFileSystem(),
        new WindowsFileSecurity(),
        new WindowsUninstaller(),
        new WindowsFileTrust(),
        new WindowsJournalDirectory(),
        new SystemClock(),
        new GuidGenerator(),
        new WindowsCleanupFiles(),
        new WindowsRecycleBin(),
        new WindowsProcessList(),
        new WindowsRegistryTree());
}

/// <summary>Состояние журнала после старта агента (DESIGN-0.1 §1, §4).</summary>
public enum JournalMode
{
    /// <summary>Журнал открыт, восстановление выполнено, операции работают.</summary>
    Ready,

    /// <summary>Каталог журнала не доверенный (JournalAclPolicy): журнал не открывался, отвечаем journal-untrusted.</summary>
    Untrusted,

    /// <summary>Схема журнала новее известной: только чтение, на запросы отвечаем journal-newer-schema.</summary>
    NewerSchema,

    /// <summary>Журнал повреждён, не создаётся или не читается: отвечаем journal-unavailable (или internal при сбое сборки графа).</summary>
    Unavailable,
}

/// <summary>Обработчик на случай, когда операции выполнять нельзя: на каждый запрос один и тот же понятный код. Ping отвечает диспетчер.</summary>
public sealed class UnavailableOperationHandler(string code, string message) : IOperationHandler
{
    public string Code => code;

    public Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct)
        => Task.FromResult(new AgentResponse(request.Id, false, code, message));
}

/// <summary>
/// Граф объектов агента (DESIGN-0.1 §4, §6 W4). Порядок: проверка каталога журнала (Inspect + JournalAclPolicy; нет каталога
/// это CreateProtected и повторная проверка) → открытие журнала → RecoveryService → очистка старше 90 дней → обработчик.
/// Канал открывает вызывающий только после Build. Если безопасно работать нельзя, Handler это <see cref="UnavailableOperationHandler"/>
/// с кодом journal-untrusted / journal-newer-schema / journal-unavailable / internal, система не трогается.
/// </summary>
public sealed class AgentComposition : IDisposable
{
    /// <summary>Срок хранения завершённых операций.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public const string DatabaseFileName = "chistovik.db";

    AgentComposition(JournalMode mode, IOperationHandler handler)
    {
        Mode = mode;
        Handler = handler;
    }

    public JournalMode Mode { get; private init; }

    public IOperationHandler Handler { get; private init; }

    /// <summary>Итог восстановления; null, если журнал не открывался.</summary>
    public RecoveryReport? Recovery { get; private set; }

    /// <summary>Сколько операций старше срока удалено при старте.</summary>
    public int PurgedOperations { get; private set; }

    /// <summary>Причина, по которой Handler это UnavailableOperationHandler (для лога); пусто в режиме Ready.</summary>
    public string Reason { get; private set; } = string.Empty;

    IDisposable? _journal;

    /// <summary>Стандартный каталог журнала: %ProgramData%\Chistovik.</summary>
    public static string DefaultJournalDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Chistovik");

    public static AgentComposition Build(AgentPorts ports, string journalDirectory, PolicyContext policy, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrEmpty(journalDirectory);
        ArgumentNullException.ThrowIfNull(policy);
        try
        {
            return BuildCore(ports, journalDirectory, policy, log);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Подробности клиенту не отдаются: только код и общий текст; причина идёт в лог.
            log?.Invoke("build-failed:" + e.GetType().Name + ": " + e.Message);
            return Unavailable(JournalMode.Unavailable, ResponseCodes.Internal, "Агент не смог подготовиться к работе", e.GetType().Name);
        }
    }

    static AgentComposition BuildCore(AgentPorts ports, string dir, PolicyContext policy, Action<string>? log)
    {
        // 1. Каталог журнала. Права сами не чиним: недоверенный существующий каталог остаётся как есть.
        JournalAclResult acl;
        try
        {
            var facts = ports.JournalDirectory.Inspect(dir);
            if (!facts.Exists)
            {
                ports.JournalDirectory.CreateProtected(dir);
                facts = ports.JournalDirectory.Inspect(dir);
            }
            acl = JournalAclPolicy.Evaluate(facts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke("journal-dir-error:" + e.GetType().Name + ": " + e.Message);
            return Unavailable(JournalMode.Unavailable, ResponseCodes.JournalUnavailable, "Журнал недоступен", "journal-dir-error");
        }

        if (!acl.IsTrusted)
        {
            log?.Invoke("journal-untrusted:" + acl.Rule);
            return Unavailable(JournalMode.Untrusted, ResponseCodes.JournalUntrusted,
                "Каталог журнала небезопасен, изменения отключены: " + acl.Reason, acl.Rule);
        }

        // 2. Открытие журнала.
        SqliteJournal journal;
        try
        {
            journal = new SqliteJournal(Path.Combine(dir, DatabaseFileName), ports.Clock);
        }
        catch (JournalException e) when (e.Code == JournalException.NewerSchema)
        {
            log?.Invoke("journal-newer-schema");
            return Unavailable(JournalMode.NewerSchema, ResponseCodes.JournalNewerSchema,
                "Журнал создан более новой версией Чистовика: изменения отключены, журнал доступен только для чтения", e.Code);
        }
        catch (Exception e) when (e is JournalException or SqliteException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke("journal-open-failed:" + e.GetType().Name + ": " + e.Message);
            return Unavailable(JournalMode.Unavailable, ResponseCodes.JournalUnavailable, "Журнал недоступен", e.GetType().Name);
        }

        // 3. Граф исполнения, восстановление, очистка. При сбое журнал закрывается, канал не открывается для операций.
        try
        {
            var env = new ExecEnv
            {
                Registry = ports.Registry,
                Services = ports.Services,
                Tasks = ports.Tasks,
                Power = ports.Power,
                Folders = ports.Folders,
                Files = ports.Files,
                Security = ports.Security,
                Uninstaller = ports.Uninstaller,
                Cleanup = ports.Cleanup,
                RecycleBin = ports.RecycleBin,
                Processes = ports.Processes,
                RegistryTree = ports.RegistryTree,
                Clock = ports.Clock,
                Ids = ports.Ids,
                Journal = journal,
                Gate = new StepGate(TweakCatalog.Shared, PowerCatalog.Shared, policy, ports.Trust),
                CatalogSha256 = CatalogSha256(),
            };

            var recovery = new RecoveryService(env).Run();
            log?.Invoke($"recovery: done={recovery.RecoveredDone} not-applied={recovery.NotApplied} failed={recovery.Failed} interrupted={recovery.InterruptedOperations}");

            var purged = 0;
            try { purged = journal.Purge(ports.Clock.UtcNow - Retention); }
            catch (Exception e) when (e is JournalException or SqliteException)
            {
                log?.Invoke("purge-failed:" + e.GetType().Name); // очистка не обязательна для работы
            }

            return new AgentComposition(JournalMode.Ready, new OperationHandler(env))
            {
                Recovery = recovery,
                PurgedOperations = purged,
                _journal = journal,
            };
        }
        catch (Exception e) when (e is JournalException or SqliteException)
        {
            journal.Dispose();
            log?.Invoke("recovery-failed:" + e.GetType().Name + ": " + e.Message);
            return Unavailable(JournalMode.Unavailable, ResponseCodes.JournalUnavailable, "Журнал недоступен", e.GetType().Name);
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    static AgentComposition Unavailable(JournalMode mode, string code, string message, string reason)
        => new(mode, new UnavailableOperationHandler(code, message)) { Reason = reason };

    /// <summary>Хэш каталогов, зашитых в сборку (tweaks, modes, power): записывается в каждую операцию журнала.</summary>
    public static string CatalogSha256()
    {
        var asm = typeof(TweakCatalog).Assembly;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "catalog/tweaks.json", "catalog/modes.json", "catalog/power.json" })
        {
            using var s = asm.GetManifestResourceStream(name) ?? throw new InvalidDataException("Ресурс каталога не найден: " + name);
            var buf = new byte[8192];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) hash.AppendData(buf, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        _journal?.Dispose();
        _journal = null;
    }
}
