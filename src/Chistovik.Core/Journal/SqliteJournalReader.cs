using Chistovik.Core.Ports;
using Microsoft.Data.Sqlite;

namespace Chistovik.Core.Journal;

/// <summary>
/// Читатель журнала для App: база открыта <c>Mode=ReadOnly</c> и <c>PRAGMA query_only=1</c>. Записать через него нельзя.
/// Базу с более новой схемой открывает (<see cref="IsNewerSchema"/> = true): колонки только добавляются, известные читаются как раньше.
/// </summary>
public sealed class SqliteJournalReader : IJournalReader, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SqliteJournalReader(string dbPath)
    {
        if (!File.Exists(dbPath)) throw new JournalException(JournalException.NotFound, "Journal file not found: " + dbPath);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            _conn.Open();
            SqliteJournalSchema.Exec(_conn, "PRAGMA query_only = 1");
            SqliteJournalSchema.Exec(_conn, "PRAGMA busy_timeout = 5000");
            SqliteJournalSchema.Exec(_conn, "PRAGMA trusted_schema = OFF");
            SqliteJournalSchema.UserVersion(_conn);
        }
        catch (SqliteException e) when (SqliteJournalSchema.IsDamage(e))
        {
            _conn.Dispose();
            throw new JournalException(JournalException.Corrupt, "Journal file is damaged or is not a journal database.", e);
        }
        catch (SqliteException e) when (SqliteJournalSchema.IsHotJournal(e))
        {
            _conn.Dispose();
            throw SqliteJournalSchema.HotJournal(e);
        }
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>Читает под замком; «горячий» журнал отката превращается в JournalException(Recovering).</summary>
    private T Read<T>(Func<SqliteConnection, T> f)
    {
        lock (_gate)
        {
            try { return f(_conn); }
            catch (SqliteException e) when (SqliteJournalSchema.IsHotJournal(e)) { throw SqliteJournalSchema.HotJournal(e); }
        }
    }

    public int SchemaVersion => Read(SqliteJournalSchema.UserVersion);

    public bool IsNewerSchema => SchemaVersion > SqliteJournalSchema.Latest;

    public IReadOnlyList<OperationSummary> List(int skip, int take) => Read(c => SqliteJournalSchema.List(c, skip, take));

    public OperationView? Get(Guid id) => Read(c => SqliteJournalSchema.Get(c, id));

    public StepRecord? LatestEffective(string targetKey) => Read(c => SqliteJournalSchema.LatestEffective(c, targetKey));

    public void Dispose() { lock (_gate) _conn.Dispose(); }
}
