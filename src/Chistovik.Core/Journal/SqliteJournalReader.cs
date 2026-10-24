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
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    public int SchemaVersion { get { lock (_gate) return SqliteJournalSchema.UserVersion(_conn); } }

    public bool IsNewerSchema => SchemaVersion > SqliteJournalSchema.Latest;

    public IReadOnlyList<OperationSummary> List(int skip, int take) { lock (_gate) return SqliteJournalSchema.List(_conn, skip, take); }

    public OperationView? Get(Guid id) { lock (_gate) return SqliteJournalSchema.Get(_conn, id); }

    public StepRecord? LatestEffective(string targetKey) { lock (_gate) return SqliteJournalSchema.LatestEffective(_conn, targetKey); }

    public void Dispose() { lock (_gate) _conn.Dispose(); }
}
