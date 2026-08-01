using System.IO;
using Microsoft.Data.Sqlite;
using Saurus.Core;

namespace Saurus.Storage;

/// <summary>
/// Single SQLite connection, WAL mode, serialised behind a lock. The write volume here is
/// a handful of rows per query, so a connection pool would be pure overhead — but the
/// budget path does read-modify-write under a transaction, so access must be serialised.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public Database(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "saurus.db");

        // Checked before the main connection is opened, because quarantining a bad file
        // means moving it, and you cannot move a file you have open.
        var quarantined = CheckAndQuarantine(path);

        // No Cache=Shared. Shared-cache mode is explicitly not recommended by the
        // Microsoft.Data.Sqlite documentation and interacts badly with a connection used
        // from several threads; it bought nothing here, since access is already serialised
        // behind a lock.
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");

        // FULL, not NORMAL. NORMAL lets the OS decide when WAL frames reach the disk, which
        // is fine for a database you can rebuild and wrong for one holding the spend record.
        // The write volume here is a handful of rows per query — the durability is free.
        Exec("PRAGMA synchronous=FULL;");
        Exec("PRAGMA busy_timeout=3000;");
        Migrate();
        AddColumnIfMissing("threads", "source_key", "TEXT");
        AddColumnIfMissing("threads", "source_label", "TEXT");
        AddColumnIfMissing("threads", "source_kind", "TEXT");
        Exec("CREATE INDEX IF NOT EXISTS ix_threads_source ON threads(source_key, last_utc DESC);");

        // root_id groups a lookup with its follow-ups into one conversation. A root row
        // points at itself, so "is this a root" is root_id = id and needs no null handling
        // in every query that touches it.
        AddColumnIfMissing("queries", "root_id", "INTEGER");
        Exec("CREATE INDEX IF NOT EXISTS ix_queries_root ON queries(root_id, created_utc);");
        Exec("UPDATE queries SET root_id = id WHERE root_id IS NULL");

        if (quarantined is not null) Salvage(quarantined);
        Log.Info($"db ready at {path}");
    }

    /// <summary>
    /// Verifies the database is readable and moves it aside if it is not.
    ///
    /// A corrupt SQLite file opens perfectly happily and then throws on the first query that
    /// touches a damaged page. Every caller here wraps its reads in a try/catch and shows an
    /// empty list, so corruption presents to the user as "all my data vanished" with nothing
    /// in the UI to say otherwise. Failing loudly at startup and rebuilding is far better
    /// than looking like a persistence bug.
    ///
    /// Returns the path the damaged file was moved to, or null when all is well.
    /// </summary>
    private static string? CheckAndQuarantine(string path)
    {
        if (!File.Exists(path)) return null;

        string verdict;
        try
        {
            using var probe = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
            probe.Open();

            // Fold the WAL into the database first. quick_check on a database whose recent
            // writes are still in a sidecar reports on an incomplete picture, and it leaves
            // the main file incomplete if it then has to be quarantined.
            using (var chk = probe.CreateCommand())
            {
                chk.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                try { chk.ExecuteNonQuery(); } catch { /* no WAL yet, or busy */ }
            }

            using var cmd = probe.CreateCommand();
            // quick_check catches the damage that matters at a fraction of the cost of
            // integrity_check, which walks every page and would be felt at startup.
            cmd.CommandText = "PRAGMA quick_check";
            verdict = cmd.ExecuteScalar()?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            verdict = $"unreadable ({ex.Message})";
        }

        if (string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase)) return null;

        // Try to repair before discarding. Index corruption — "wrong # of entries in index
        // X" — is by far the most common form and is completely recoverable: indexes are
        // derived data, and REINDEX rebuilds them from the tables. Quarantining on an index
        // fault would throw away perfectly good rows.
        Log.Warn($"database failed quick_check ({verdict}); attempting REINDEX");

        try
        {
            using var repair = new SqliteConnection($"Data Source={path};Mode=ReadWrite");
            repair.Open();

            using (var cmd = repair.CreateCommand())
            {
                cmd.CommandText = "REINDEX";
                cmd.ExecuteNonQuery();
            }

            using var recheck = repair.CreateCommand();
            recheck.CommandText = "PRAGMA quick_check";
            var after = recheck.ExecuteScalar()?.ToString() ?? "unknown";

            if (string.Equals(after, "ok", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("REINDEX repaired the database; no data lost");
                SqliteConnection.ClearAllPools();
                return null;
            }

            Log.Error($"REINDEX did not repair it (quick_check still says: {after})");
        }
        catch (Exception ex)
        {
            Log.Error("REINDEX failed", ex);
        }

        SqliteConnection.ClearAllPools();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var quarantine = $"{path}.corrupt-{stamp}";

        try
        {
            File.Move(path, quarantine);

            // The WAL is moved with the database, never deleted. In WAL mode the most recent
            // writes live in the sidecar — it is routinely larger than the .db itself — so
            // deleting it destroys precisely the data quarantine exists to preserve. Renaming
            // it alongside keeps the quarantined database complete and salvageable.
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                if (!File.Exists(path + ext)) continue;
                try { File.Move(path + ext, quarantine + ext); }
                catch (Exception ex) { Log.Warn($"could not preserve {ext}: {ex.Message}"); }
            }

            Log.Error($"database failed quick_check ({verdict}); moved to {quarantine} and starting fresh");
            return quarantine;
        }
        catch (Exception ex)
        {
            Log.Error("database is corrupt and could not be moved aside", ex);
            return null;
        }
    }

    /// <summary>
    /// Best-effort copy of whatever still reads out of a quarantined database. Each table is
    /// attempted separately: corruption is usually confined to some pages, so losing the
    /// query log does not have to mean losing the spend ledger as well.
    /// </summary>
    private void Salvage(string quarantinePath)
    {
        var recovered = new List<string>();

        try
        {
            Exec($"ATTACH DATABASE '{quarantinePath.Replace("'", "''")}' AS old");
        }
        catch (Exception ex)
        {
            Log.Error("could not attach the quarantined database to salvage it", ex);
            return;
        }

        // Ledger first: it is the spend record, the smallest table, and the one whose loss
        // would silently reset a guardrail.
        TrySalvage("usage_ledger",
            "INSERT INTO main.usage_ledger(id, ts_utc, day_local, reserved, input_tokens, output_tokens, settled) " +
            "SELECT id, ts_utc, day_local, reserved, input_tokens, output_tokens, settled FROM old.usage_ledger",
            recovered);

        TrySalvage("threads",
            "INSERT INTO main.threads(id, url, process, window_title, created_utc, last_utc) " +
            "SELECT id, url, process, window_title, created_utc, last_utc FROM old.threads",
            recovered);

        TrySalvage("queries",
            "INSERT INTO main.queries(id, thread_id, created_utc, selection, context, question, answer, " +
            "model, input_tokens, output_tokens, from_cache) " +
            "SELECT id, thread_id, created_utc, selection, context, question, answer, " +
            "model, input_tokens, output_tokens, from_cache FROM old.queries",
            recovered);

        try { Exec("DETACH DATABASE old"); } catch { }

        // Columns added by later migrations will not have come across.
        try { Exec("UPDATE queries SET root_id = id WHERE root_id IS NULL"); } catch { }

        Log.Info(recovered.Count > 0
            ? $"salvaged from the corrupt database: {string.Join(", ", recovered)}"
            : "nothing could be salvaged from the corrupt database");
    }

    private void TrySalvage(string table, string sql, List<string> recovered)
    {
        try
        {
            Exec(sql);
            var n = Scalar<long>($"SELECT COUNT(*) FROM main.{table}");
            recovered.Add($"{table} ({n} rows)");
        }
        catch (Exception ex)
        {
            Log.Warn($"could not salvage {table}: {ex.Message}");
        }
    }

    /// <summary>
    /// SQLite has no ADD COLUMN IF NOT EXISTS, and running a bare ALTER on every start would
    /// throw once the column exists. Checked against the schema instead of caught, so a real
    /// failure is not mistaken for the benign case.
    /// </summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        var exists = Query($"PRAGMA table_info({table})", r => r.GetString(1))
            .Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

        if (exists) return;

        Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
        Log.Info($"schema: added {table}.{column}");
    }

    /// <summary>
    /// Wipes conversation history. Deliberately leaves usage_ledger alone: that is the spend
    /// record the daily budget is enforced against, and silently resetting a spend guard
    /// because someone tidied their history would be a hole in the guardrails.
    /// </summary>
    public void ClearHistory()
    {
        Exec("DELETE FROM queries");
        Exec("DELETE FROM threads");
        Exec("DELETE FROM cache");
        Exec("VACUUM");
        Log.Info("history cleared (queries, threads, cache); usage ledger retained");
    }

    private void Migrate() => Exec("""
        -- A thread is a document, not a time window. source_key is the canonical identity
        -- (a URL, an arXiv id, a file path); url/process/window_title are kept as the raw
        -- evidence it was derived from, so improved identity rules can re-key old threads
        -- instead of orphaning them.
        CREATE TABLE IF NOT EXISTS threads (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            url           TEXT,
            process       TEXT,
            window_title  TEXT,
            created_utc   TEXT NOT NULL,
            last_utc      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_threads_last ON threads(last_utc DESC);

        CREATE TABLE IF NOT EXISTS queries (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            thread_id     INTEGER NOT NULL,
            created_utc   TEXT NOT NULL,
            selection     TEXT,
            context       TEXT,
            question      TEXT,
            answer        TEXT,
            model         TEXT,
            input_tokens  INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0,
            from_cache    INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_queries_thread ON queries(thread_id, created_utc);

        -- One row per API call. Written as a reservation before the call and settled with
        -- real token counts after, so a crash mid-call still counts against the budget.
        CREATE TABLE IF NOT EXISTS usage_ledger (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_utc         TEXT NOT NULL,
            day_local      TEXT NOT NULL,
            reserved       INTEGER NOT NULL DEFAULT 0,
            input_tokens   INTEGER NOT NULL DEFAULT 0,
            output_tokens  INTEGER NOT NULL DEFAULT 0,
            settled        INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_ledger_day ON usage_ledger(day_local);
        CREATE INDEX IF NOT EXISTS ix_ledger_ts  ON usage_ledger(ts_utc);

        CREATE TABLE IF NOT EXISTS cache (
            key            TEXT PRIMARY KEY,
            answer         TEXT NOT NULL,
            created_utc    TEXT NOT NULL,
            input_tokens   INTEGER NOT NULL DEFAULT 0,
            output_tokens  INTEGER NOT NULL DEFAULT 0
        );
        """);

    public void Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var o = cmd.ExecuteScalar();
            if (o is null || o is DBNull) return default;

            // Nullable<T> is not a valid Convert.ChangeType target — unwrap it first so
            // callers can use Scalar<long?> to mean "may be absent".
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(o, target);
        }
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside a transaction with the connection lock held.
    /// Microsoft.Data.Sqlite requires every command created during a pending local
    /// transaction to have its Transaction assigned, so the transaction is handed to the
    /// body rather than being implicit — use <see cref="Command"/> to build commands.
    /// </summary>
    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            var result = body(_conn, tx);
            tx.Commit();
            return result;
        }
    }

    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql,
                                        params (string, object?)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public void Dispose()
    {
        // Fold the WAL back into the database on the way out, so the .db file is
        // self-contained. Without this the newest writes only exist in a sidecar, and
        // anything that copies, backs up or inspects saurus.db on its own sees stale data.
        try { Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
        _conn.Dispose();
    }
}
