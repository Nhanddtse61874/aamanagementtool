using System.IO;
using Microsoft.Data.Sqlite;

namespace TimesheetApp.Data;

/// <summary>
/// M8.2: snapshot a LIVE SQLite database through SQLite's online backup API
/// (<c>sqlite3_backup_*</c>, exposed as <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>).
///
/// A file-level <c>File.Copy</c> of a live .db is only safe under the pre-M8.2 profile that
/// <see cref="SqliteConnectionFactory"/> used to guarantee: short connections + journal_mode=DELETE +
/// no pooling. Under WAL those premises are gone — committed pages sit in the <c>-wal</c> sidecar until
/// a checkpoint, so copying the .db alone yields, per https://www.sqlite.org/wal.html, a file whose
/// "transactions that were previously committed ... might be lost, or the database file might become
/// corrupted".
///
/// The backup API reads THROUGH a connection, so it sees the WAL content and takes a consistent
/// snapshot of the last committed state — even while another connection holds an open write
/// transaction (WAL readers do not block on the writer).
///
/// Chosen over <c>VACUUM INTO</c>: VACUUM INTO refuses to write a file that already exists, and cannot
/// write into a database another connection has open. Backup-with-overwrite needs the first; restoring
/// onto the live .db needs the second.
/// </summary>
public static class SqliteOnlineBackup
{
    /// <summary>
    /// Snapshot <paramref name="sourceDbPath"/> onto <paramref name="destDbPath"/>, replacing the
    /// destination and any stale <c>-wal</c>/<c>-shm</c> sidecar beside it. Safe while the source is
    /// live, in WAL, and being written to.
    /// </summary>
    /// <remarks>
    /// Used for restore too (backup file -> live .db). Deleting the destination's sidecars is the
    /// load-bearing half of that: a <c>-wal</c> left over from the REPLACED database is replayed by
    /// SQLite over the newly restored file on the next open. Callers must ensure no handle is still
    /// open on the destination (see <see cref="ClearPools"/>) — otherwise this throws rather than
    /// silently corrupting it.
    /// </remarks>
    public static void Copy(string sourceDbPath, string destDbPath)
    {
        DeleteWithSidecars(destDbPath);

        using var source = Open(sourceDbPath, SqliteOpenMode.ReadWrite);
        using var dest = Open(destDbPath, SqliteOpenMode.ReadWriteCreate);

        source.BackupDatabase(dest);

        // The backup copies the source's header verbatim, so a WAL source produces a WAL-MARKED
        // artifact. Convert it back to DELETE so the artifact stays ONE self-contained file: a
        // WAL-marked database must be able to create -wal/-shm beside itself to be reopened, which a
        // read-only share or a synced backup folder cannot always allow — exactly where these files go.
        Exec(dest, "PRAGMA journal_mode=DELETE;");
    }

    /// <summary>
    /// SQLite's own verdict on <paramref name="dbPath"/>: true only when <c>PRAGMA integrity_check</c>
    /// answers exactly "ok".
    /// </summary>
    /// <remarks>
    /// "Exists &amp;&amp; Length &gt; 0" is not evidence that a file is a usable database — six
    /// arbitrary bytes pass it. RetentionService PERMANENTLY DELETES the original rows on the strength
    /// of this answer, so it has to be a real one.
    /// </remarks>
    public static bool IsIntact(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return false;

        try
        {
            using var conn = Open(dbPath, SqliteOpenMode.ReadOnly);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            return cmd.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException)
        {
            return false; // not a database / unreadable -> certainly not a recovery artifact
        }
    }

    /// <summary>Release every pooled connection handle. With pooling on, a closed <c>SqliteConnection</c>
    /// keeps the file open; the .db cannot be replaced until the pool lets go of it.</summary>
    public static void ClearPools() => SqliteConnection.ClearAllPools();

    private static void DeleteWithSidecars(string dbPath)
    {
        File.Delete(dbPath);          // no-op when absent
        File.Delete(dbPath + "-wal");
        File.Delete(dbPath + "-shm");
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            // Pooling OFF: these are one-shot maintenance connections. A pooled handle would hold the
            // file open past Dispose — the artifact could not be moved, synced or pruned, and on restore
            // the live .db would stay locked at precisely the moment every handle must be gone.
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
