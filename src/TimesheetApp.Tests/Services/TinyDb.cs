using Microsoft.Data.Sqlite;

namespace TimesheetApp.Tests.Services;

/// <summary>
/// Real (tiny) SQLite databases for the backup/restore/snapshot tests.
///
/// The pre-M8.2 backup tests used TEXT files as stand-ins for the .db ("LIVE-DB", or six 0x09 bytes).
/// That fixture is exactly what let <c>File.Copy</c> look correct: a text file has no pages, no header
/// and — the point of all this — no <c>-wal</c> sidecar. These helpers build databases SQLite actually
/// recognises, so the tests exercise what SQLite actually does.
/// </summary>
internal static class TinyDb
{
    private const string Table = "T";

    /// <summary>A real, self-contained (journal_mode=DELETE) database holding one row per value.</summary>
    public static void Create(string path, params string[] values)
    {
        using var conn = Open(path);
        Exec(conn, "PRAGMA journal_mode=DELETE;");
        Seed(conn, values);
    }

    /// <summary>A real database in WAL mode. The returned connection is OPEN and the caller must keep
    /// it open: under WAL the committed rows sit in the <c>-wal</c> sidecar until the last connection
    /// closes and checkpoints them into the .db. Holding it open is what reproduces the state a pooled
    /// WAL server is in all day long — and the state in which copying the .db alone loses data.</summary>
    public static SqliteConnection OpenWal(string path)
    {
        var conn = Open(path);
        Exec(conn, "PRAGMA journal_mode=WAL;");
        return conn;
    }

    public static void Seed(SqliteConnection conn, params string[] values)
    {
        Exec(conn, $"CREATE TABLE IF NOT EXISTS {Table}(id INTEGER PRIMARY KEY, v TEXT NOT NULL);");
        foreach (var v in values) Insert(conn, v);
    }

    public static void Insert(SqliteConnection conn, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {Table}(v) VALUES($v);";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every value in the table, in insertion order. Opens its own connection, so it reads
    /// the file as a restored/copied artifact would be read — not through the writer's cache.</summary>
    public static IReadOnlyList<string> ReadAll(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT v FROM {Table} ORDER BY id;";
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>SQLite's own verdict on the file: "ok" when the database is intact.</summary>
    public static string IntegrityCheck(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string)cmd.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // Dispose must really release the handle — the temp dir gets deleted.
        }.ToString());
        conn.Open();
        return conn;
    }
}
