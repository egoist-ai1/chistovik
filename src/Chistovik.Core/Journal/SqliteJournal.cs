using System.Globalization;
using Chistovik.Core.Ports;
using Microsoft.Data.Sqlite;
using static Chistovik.Core.Journal.SqliteJournalSchema;

namespace Chistovik.Core.Journal;

/// <summary>
/// Писатель журнала (DESIGN-0.1 §1). Одно соединение на объект, все вызовы под замком: агент работает строго последовательно.
/// При создании файла ставит auto_vacuum=INCREMENTAL; при открытии журнал в режиме DELETE (не WAL) и
/// synchronous=FULL, чтобы читатель без права записи в каталог мог открыть файл, пока писатель жив (§9).
/// </summary>
public sealed class SqliteJournal : IJournalWriter, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private bool _disposed;

    /// <exception cref="JournalException">journal-newer-schema (схема новее известной: писать нельзя, читайте через SqliteJournalReader),
    /// journal-corrupt (файл повреждён: молча не пересоздаётся).</exception>
    public SqliteJournal(string dbPath, IClock clock)
    {
        _clock = clock;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            _conn.Open();
            Exec(_conn, "PRAGMA busy_timeout = 5000");
            Exec(_conn, "PRAGMA trusted_schema = OFF");
            Exec(_conn, "PRAGMA foreign_keys = ON");
            var version = UserVersion(_conn);
            var tables = Convert.ToInt64(Scalar(_conn, "SELECT count(*) FROM sqlite_master"), CultureInfo.InvariantCulture);
            if (version > Latest)
                throw new JournalException(JournalException.NewerSchema,
                    $"Journal schema {version} is newer than supported {Latest}; read-only.");
            if (version == 0 && tables > 0)
                throw new JournalException(JournalException.Corrupt, "Database file has tables but no journal schema version.");
            // auto_vacuum действует только до создания первой таблицы.
            if (version == 0) Exec(_conn, "PRAGMA auto_vacuum = INCREMENTAL");
            MigrateForward(_conn, NowMs());
            // Не WAL: читатель App работает без права записи в каталог и не может открыть -shm живой WAL-базы.
            // База, оставленная прежней версией в WAL, возвращается в DELETE; если она занята, продолжаем как есть.
            // Результат PRAGMA читаем: режим мог не смениться (база занята). Повторяем один раз; итог виден в JournalMode.
            if (!TrySetDelete()) TrySetDelete();
            Exec(_conn, "PRAGMA synchronous = FULL");
        }
        catch (SqliteException e) when (IsDamage(e))
        {
            _conn.Dispose();
            throw new JournalException(JournalException.Corrupt, "Journal file is damaged or is not a journal database.", e);
        }
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Режим журнала SQLite после открытия (ожидается "delete"). Другое значение значит, что базу не удалось вернуть из WAL
    /// (она занята); хост агента может записать предупреждение. Логгера в Core нет, поэтому состояние отдаётся свойством.
    /// </summary>
    public string JournalMode { get; private set; } = "unknown";

    private bool TrySetDelete()
    {
        try
        {
            JournalMode = Convert.ToString(Scalar(_conn, "PRAGMA journal_mode = DELETE"), CultureInfo.InvariantCulture)?.ToLowerInvariant() ?? "unknown";
        }
        catch (SqliteException e) when (!IsDamage(e))
        {
            JournalMode = "unknown";
        }
        return JournalMode == "delete";
    }

    private long NowMs() => _clock.UtcNow.ToUnixTimeMilliseconds();

    public int SchemaVersion { get { lock (_gate) return UserVersion(_conn); } }

    public IReadOnlyList<OperationSummary> List(int skip, int take) { lock (_gate) return SqliteJournalSchema.List(_conn, skip, take); }

    public OperationView? Get(Guid id) { lock (_gate) return SqliteJournalSchema.Get(_conn, id); }

    /// <summary>
    /// Эффективное значение цели: последний (по id) шаг с этим target_key в состоянии <c>done</c>. Шаг, который откатили,
    /// получает state=<c>undone</c> (<see cref="CompleteRevert"/>), поэтому он выпадает из выборки, а шаг отмены (тоже done,
    /// after = исходный before) становится последним. Шаги pending/failed/skipped/denied значения не меняют и не учитываются.
    /// </summary>
    public StepRecord? LatestEffective(string targetKey) { lock (_gate) return SqliteJournalSchema.LatestEffective(_conn, targetKey); }

    public OperationRecord BeginOrGet(NewOperation op)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            if (op.RequestId is { } rid)
            {
                using var q = Cmd(_conn, "SELECT id, kind, subject, state, code FROM operation WHERE request_id = @r", tx, ("@r", rid.ToString("D")));
                using var r = q.ExecuteReader();
                if (r.Read())
                    return new OperationRecord(Guid.Parse(r.GetString(0)), ParseEnum<OperationKind>(r.GetString(1)), r.GetString(2),
                        ParseOpState(r.GetString(3)), r.IsDBNull(4) ? null : r.GetString(4), true);
            }
            Exec(_conn,
                "INSERT INTO operation(id, request_id, kind, subject, reverts_op_id, user_sid, agent_version, catalog_sha256, created_utc, state) " +
                "VALUES (@id, @rid, @kind, @subj, @rev, @sid, @ver, @cat, @t, 'running')", tx,
                ("@id", op.Id.ToString("D")), ("@rid", op.RequestId?.ToString("D")), ("@kind", op.Kind.ToString()),
                ("@subj", op.Subject), ("@rev", op.RevertsOpId?.ToString("D")), ("@sid", op.UserSid),
                ("@ver", op.AgentVersion), ("@cat", op.CatalogSha256), ("@t", NowMs()));
            tx.Commit();
            return new OperationRecord(op.Id, op.Kind, op.Subject, OperationState.Running, null, false);
        }
    }

    public long AddPending(Guid op, int ord, StepKind k, string targetKey, string target, string before, string after,
        string restart = "none", bool revertible = true)
    {
        lock (_gate) return InsertStep(op, ord, k, targetKey, target, before, after, StepState.Pending, null, null, restart, revertible);
    }

    public void AddTerminal(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after,
        StepState s, string code, string? rule, string restart = "none", bool revertible = true)
    {
        if (s == StepState.Pending) throw new ArgumentException("AddTerminal takes a terminal state, not pending.", nameof(s));
        lock (_gate) InsertStep(op, ord, k, targetKey, target, before, after, s, code, rule, restart, revertible);
    }

    private long InsertStep(Guid op, int ord, StepKind k, string targetKey, string target, string? before, string after,
        StepState s, string? code, string? rule, string restart, bool revertible)
    {
        ValidRestart(restart);
        var t = NowMs();
        var v = Scalar(_conn,
            "INSERT INTO step(op_id, ord, kind, target_key, target_json, before_json, after_json, state, code, policy_rule, restart, revertible, created_utc, updated_utc) " +
            "VALUES (@op, @ord, @kind, @tk, @tj, @bj, @aj, @st, @code, @rule, @rs, @rv, @t, @t) RETURNING id", null,
            ("@op", op.ToString("D")), ("@ord", ord), ("@kind", k.ToString()), ("@tk", targetKey), ("@tj", target), ("@bj", before),
            ("@aj", after), ("@st", StepStateText(s)), ("@code", code), ("@rule", rule), ("@rs", restart), ("@rv", revertible ? 1 : 0), ("@t", t));
        return Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Переводит шаг из pending в терминальное состояние. Текст ошибки (≤1 КБ) в шаге не хранится (в схеме нет колонки): он записывается в operation.error родительской операции, если там ещё пусто.</summary>
    public void Complete(long stepId, StepState s, string? code, string? error)
    {
        if (s == StepState.Pending) throw new ArgumentException("Complete takes a terminal state, not pending.", nameof(s));
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var n = ExecCount("UPDATE step SET state = @s, code = @c, updated_utc = @t WHERE id = @id AND state = 'pending'", tx,
                ("@s", StepStateText(s)), ("@c", code), ("@t", NowMs()), ("@id", stepId));
            if (n != 1) throw new InvalidOperationException($"Step {stepId} is not pending or does not exist.");
            if (error is not null)
                Exec(_conn, "UPDATE operation SET error = @e WHERE error IS NULL AND id = (SELECT op_id FROM step WHERE id = @id)", tx,
                    ("@e", TruncateError(error)), ("@id", stepId));
            tx.Commit();
        }
    }

    /// <summary>Одна транзакция: шаг отмены pending → done (reverts_step_id = исходный), исходный done → undone. Любое несоответствие откатывает обе правки.</summary>
    public void CompleteRevert(long undoStepId, long originalStepId, string code)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var t = NowMs();
            var n1 = ExecCount("UPDATE step SET state = 'done', code = @c, reverts_step_id = @o, updated_utc = @t WHERE id = @u AND state = 'pending'", tx,
                ("@c", code), ("@o", originalStepId), ("@t", t), ("@u", undoStepId));
            if (n1 != 1) throw new InvalidOperationException($"Undo step {undoStepId} is not pending or does not exist.");
            var n2 = ExecCount("UPDATE step SET state = 'undone', updated_utc = @t WHERE id = @o AND state = 'done'", tx,
                ("@t", t), ("@o", originalStepId));
            if (n2 != 1) throw new InvalidOperationException($"Original step {originalStepId} is not done or does not exist.");
            tx.Commit(); // при исключении выше Dispose транзакции откатывает обе правки
        }
    }

    public void Finish(Guid op, OperationState s, string? code)
    {
        lock (_gate)
        {
            var n = ExecCount("UPDATE operation SET state = @s, code = @c, finished_utc = @t WHERE id = @id", null,
                ("@s", OpState(s)), ("@c", code), ("@t", NowMs()), ("@id", op.ToString("D")));
            if (n != 1) throw new InvalidOperationException($"Operation {op} does not exist.");
        }
    }

    public IReadOnlyList<PendingStep> GetPending()
    {
        lock (_gate)
        {
            var res = new List<PendingStep>();
            using var cmd = Cmd(_conn,
                "SELECT s.id, s.op_id, o.kind, s.ord, s.kind, s.target_key, s.target_json, s.before_json, s.after_json, s.reverts_step_id " +
                "FROM step s JOIN operation o ON o.id = s.op_id WHERE s.state = 'pending' ORDER BY s.id");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                res.Add(new PendingStep(r.GetInt64(0), Guid.Parse(r.GetString(1)), ParseEnum<OperationKind>(r.GetString(2)), r.GetInt32(3),
                    ParseEnum<StepKind>(r.GetString(4)), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9)));
            return res;
        }
    }

    /// <summary>Удаляет завершённые операции с finished_utc &lt; cutoff (running не трогает, шаги уходят каскадом), затем incremental_vacuum. Возвращает число удалённых операций.</summary>
    public int Purge(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            // Операции с действующим шагом создания нашей схемы питания не стираем: без них журнал забыл бы, что схема наша
            // (повторное создание и откат). Откатанная схема (шаг undone) очищается как обычно; шаг удаления схемы не считается.
            var n = ExecCount("DELETE FROM operation WHERE state <> 'running' AND finished_utc IS NOT NULL AND finished_utc < @c " +
                "AND NOT EXISTS (SELECT 1 FROM step s WHERE s.op_id = operation.id AND s.kind = 'PowerSchemeCreate' AND s.state = 'done' " +
                "AND json_extract(s.after_json, '$.exists') = 1 " +
                "AND s.id = (SELECT MAX(x.id) FROM step x WHERE x.target_key = s.target_key AND x.state = 'done'))", null,
                ("@c", cutoff.ToUnixTimeMilliseconds()));
            Exec(_conn, "PRAGMA incremental_vacuum");
            return n;
        }
    }

    public IReadOnlyList<Guid> ExistingOperationIds()
    {
        lock (_gate)
        {
            var res = new List<Guid>();
            using var cmd = Cmd(_conn, "SELECT id FROM operation");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (Guid.TryParse(r.GetString(0), out var id)) res.Add(id);
            return res;
        }
    }

    private int ExecCount(string sql, SqliteTransaction? tx, params (string, object?)[] ps)
    {
        using var cmd = Cmd(_conn, sql, tx, ps);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Режим DELETE с открытия: checkpoint не нужен. Если открыть в DELETE не удалось, пробуем ещё раз (как раньше).
            if (JournalMode != "delete") TrySetDelete();
            _conn.Dispose();
        }
    }
}
