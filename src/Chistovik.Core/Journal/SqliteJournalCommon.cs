using System.Globalization;
using System.Text;
using Chistovik.Core.Ports;
using Microsoft.Data.Sqlite;

namespace Chistovik.Core.Journal;

/// <summary>Ошибка журнала с закрытым кодом: journal-newer-schema, journal-corrupt, journal-not-found.</summary>
public sealed class JournalException : Exception
{
    public const string NewerSchema = "journal-newer-schema";
    public const string Corrupt = "journal-corrupt";
    public const string NotFound = "journal-not-found";

    public string Code { get; }

    public JournalException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>Схема v1, миграции, преобразование перечислений и чтение строк. Любые значения только параметрами.</summary>
internal static class SqliteJournalSchema
{
    public const int ErrorMaxBytes = 1024;

    // Миграции вперёд, по одной: индекс i приводит базу от версии i к версии i+1. Выражения разделены на отдельные команды.
    private static readonly string[][] Migrations =
    [
        [
            "CREATE TABLE meta(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)",
            """
            CREATE TABLE operation(
                id TEXT PRIMARY KEY NOT NULL,
                request_id TEXT UNIQUE NULL,
                kind TEXT NOT NULL,
                subject TEXT NOT NULL,
                reverts_op_id TEXT NULL REFERENCES operation(id) ON DELETE SET NULL,
                user_sid TEXT NOT NULL,
                agent_version TEXT NOT NULL,
                catalog_sha256 TEXT NOT NULL,
                created_utc INTEGER NOT NULL,
                finished_utc INTEGER NULL,
                state TEXT NOT NULL,
                code TEXT NULL,
                error TEXT NULL)
            """,
            """
            CREATE TABLE step(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                op_id TEXT NOT NULL REFERENCES operation(id) ON DELETE CASCADE,
                ord INTEGER NOT NULL,
                kind TEXT NOT NULL,
                target_key TEXT NOT NULL,
                target_json TEXT NOT NULL,
                before_json TEXT NULL,
                after_json TEXT NOT NULL,
                state TEXT NOT NULL,
                code TEXT NULL,
                policy_rule TEXT NULL,
                reverts_step_id INTEGER NULL REFERENCES step(id) ON DELETE SET NULL,
                restart TEXT NOT NULL DEFAULT 'none',
                created_utc INTEGER NOT NULL,
                updated_utc INTEGER NOT NULL,
                UNIQUE(op_id, ord))
            """,
            "CREATE INDEX ix_step_target ON step(target_key, id DESC)",
            "CREATE INDEX ix_step_pending ON step(state) WHERE state = 'pending'",
            "CREATE INDEX ix_operation_created ON operation(created_utc)",
        ],
        // v2: признак «шаг вернуть нельзя» (удаление программ). Старые шаги возвращаемые.
        [
            "ALTER TABLE step ADD COLUMN revertible INTEGER NOT NULL DEFAULT 1",
        ],
    ];

    /// <summary>Первая версия схемы, где есть колонка step.revertible.</summary>
    public const int RevertibleSince = 2;

    public static int Latest => Migrations.Length;

    public static SqliteCommand Cmd(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, tx, ps);
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] ps)
    {
        using var cmd = Cmd(c, sql, tx, ps);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static int UserVersion(SqliteConnection c) => Convert.ToInt32(Scalar(c, "PRAGMA user_version"), CultureInfo.InvariantCulture);

    /// <summary>Применяет миграции от текущей версии вперёд; каждая в своей транзакции. Вызывающий уже проверил, что версия не новее.</summary>
    public static void MigrateForward(SqliteConnection c, long nowMs)
    {
        for (var v = UserVersion(c); v < Latest; v++)
        {
            using var tx = c.BeginTransaction();
            foreach (var stmt in Migrations[v]) Exec(c, stmt, tx);
            if (v == 0)
                Exec(c, "INSERT INTO meta(key, value) VALUES ('created_utc', @t)", tx, ("@t", nowMs.ToString(CultureInfo.InvariantCulture)));
            // user_version не принимает параметров; значение это наша константа-int.
            Exec(c, "PRAGMA user_version = " + (v + 1).ToString(CultureInfo.InvariantCulture), tx);
            tx.Commit();
        }
    }

    public static bool IsDamage(SqliteException e) => e.SqliteErrorCode is 11 or 26; // CORRUPT, NOTADB

    public static string TruncateError(string s)
    {
        if (Encoding.UTF8.GetByteCount(s) <= ErrorMaxBytes) return s;
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var r in s.EnumerateRunes())
        {
            var n = r.Utf8SequenceLength;
            if (bytes + n > ErrorMaxBytes) break;
            sb.Append(r.ToString());
            bytes += n;
        }
        return sb.ToString();
    }

    // ---- перечисления <-> строки колонок ----
    public static string OpState(OperationState s) => s switch
    {
        OperationState.Running => "running",
        OperationState.Done => "done",
        OperationState.Partial => "partial",
        OperationState.Failed => "failed",
        OperationState.Denied => "denied",
        OperationState.Interrupted => "interrupted",
        OperationState.Undone => "undone",
        OperationState.PartiallyUndone => "partially_undone",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static OperationState ParseOpState(string s) => s switch
    {
        "running" => OperationState.Running,
        "done" => OperationState.Done,
        "partial" => OperationState.Partial,
        "failed" => OperationState.Failed,
        "denied" => OperationState.Denied,
        "interrupted" => OperationState.Interrupted,
        "undone" => OperationState.Undone,
        "partially_undone" => OperationState.PartiallyUndone,
        _ => throw new InvalidDataException("Unknown operation state in journal: " + s)
    };

    public static string StepStateText(StepState s) => s switch
    {
        StepState.Pending => "pending",
        StepState.Done => "done",
        StepState.Failed => "failed",
        StepState.Skipped => "skipped",
        StepState.Denied => "denied",
        StepState.Undone => "undone",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static StepState ParseStepState(string s) => s switch
    {
        "pending" => StepState.Pending,
        "done" => StepState.Done,
        "failed" => StepState.Failed,
        "skipped" => StepState.Skipped,
        "denied" => StepState.Denied,
        "undone" => StepState.Undone,
        _ => throw new InvalidDataException("Unknown step state in journal: " + s)
    };

    public static T ParseEnum<T>(string s) where T : struct, Enum =>
        s.Length > 0 && !char.IsDigit(s[0]) && Enum.TryParse<T>(s, ignoreCase: false, out var v) && Enum.IsDefined(v)
            ? v
            : throw new InvalidDataException("Unknown " + typeof(T).Name + " in journal: " + s);

    public static string ValidRestart(string restart) => restart is "none" or "explorer" or "logoff" or "reboot"
        ? restart
        : throw new ArgumentException("restart must be none|explorer|logoff|reboot", nameof(restart));

    // ---- чтение (общее для писателя и читателя) ----
    private const string OpColumns = "id, kind, subject, state, code, created_utc, finished_utc, reverts_op_id, user_sid, error";
    private const string StepColumnsBase = "id, op_id, ord, kind, target_key, target_json, before_json, after_json, state, code, policy_rule, reverts_step_id, restart, created_utc, updated_utc";

    /// <summary>Читатель App может открыть ещё не мигрированный файл v1: колонки revertible там нет, шаги возвращаемые.</summary>
    private static string StepColumns(SqliteConnection c)
        => StepColumnsBase + (UserVersion(c) >= RevertibleSince ? ", revertible" : ", 1 AS revertible");

    private static DateTimeOffset Ms(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static IReadOnlyList<OperationSummary> List(SqliteConnection c, int skip, int take)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 0) throw new ArgumentOutOfRangeException(nameof(take));
        var res = new List<OperationSummary>();
        using var cmd = Cmd(c, "SELECT " + OpColumns + " FROM operation ORDER BY created_utc DESC, rowid DESC LIMIT @take OFFSET @skip",
            null, ("@take", take), ("@skip", skip));
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(ReadSummary(r));
        return res;
    }

    private static OperationSummary ReadSummary(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), ParseEnum<OperationKind>(r.GetString(1)), r.GetString(2), ParseOpState(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetString(4), Ms(r.GetInt64(5)), r.IsDBNull(6) ? null : Ms(r.GetInt64(6)));

    public static OperationView? Get(SqliteConnection c, Guid id)
    {
        OperationSummary summary;
        Guid? reverts;
        string sid;
        string? error;
        using (var cmd = Cmd(c, "SELECT " + OpColumns + " FROM operation WHERE id = @id", null, ("@id", id.ToString("D"))))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            summary = ReadSummary(r);
            reverts = r.IsDBNull(7) ? null : Guid.Parse(r.GetString(7));
            sid = r.GetString(8);
            error = r.IsDBNull(9) ? null : r.GetString(9);
        }
        var steps = new List<StepRecord>();
        using (var cmd = Cmd(c, "SELECT " + StepColumns(c) + " FROM step WHERE op_id = @id ORDER BY ord", null, ("@id", id.ToString("D"))))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) steps.Add(ReadStep(r));
        return new OperationView(summary, reverts, sid, error, steps);
    }

    /// <summary>Эффективное значение цели = последний (по id) шаг со state 'done'. См. XML-комментарий SqliteJournal.LatestEffective.</summary>
    public static StepRecord? LatestEffective(SqliteConnection c, string targetKey)
    {
        using var cmd = Cmd(c, "SELECT " + StepColumns(c) + " FROM step WHERE target_key = @k AND state = 'done' ORDER BY id DESC LIMIT 1",
            null, ("@k", targetKey));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadStep(r) : null;
    }

    private static StepRecord ReadStep(SqliteDataReader r) => new(
        r.GetInt64(0), Guid.Parse(r.GetString(1)), r.GetInt32(2), ParseEnum<StepKind>(r.GetString(3)), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), ParseStepState(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetInt64(11), r.GetString(12), Ms(r.GetInt64(13)), Ms(r.GetInt64(14)), r.GetInt64(15) != 0);
}
