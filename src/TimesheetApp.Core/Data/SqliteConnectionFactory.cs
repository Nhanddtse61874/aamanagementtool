using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;

namespace TimesheetApp.Data;

// M8.2 (design §6.3): Core is shared by WPF (SQLite on a synced folder) and the future API
// (SQLite on the host's local disk), and the two need OPPOSITE settings. The profile below
// travels as an optional constructor parameter -- never IOptions<T> (breaks the 3 direct
// `new SqliteConnectionFactory(cfg)` call sites) and never IAppConfig (frozen: 4 test files
// hand-implement it, so any new member fails compilation on the whole suite).
public enum SqliteProfile
{
    // XC-01: short open->work->close connections, journal_mode=DELETE (NOT WAL -> no -wal/-shm
    // sidecars to sync out of band over OneDrive), Pooling=False (Dispose truly releases the
    // file handle so OneDrive can upload). Default: every unregistered (WPF) call site and
    // every existing test fixture gets this profile with zero code changes.
    Desktop,

    // Single writer process on the host's local disk -> WAL is safe and wanted. Per design
    // §8.4, busy_timeout alone does not bound a blocked writer: Microsoft.Data.Sqlite
    // auto-retries SQLITE_BUSY up to CommandTimeout (default 30s). DefaultTimeout=5 is what
    // actually lowers that ceiling; busy_timeout=1000 is SQLite's own retry window underneath.
    Server,
}

public sealed class SqliteConnectionFactory : IConnectionFactory
{
    private readonly IAppConfig _config;
    private readonly SqliteProfile _profile;

    public SqliteConnectionFactory(IAppConfig config, SqliteProfile profile = SqliteProfile.Desktop)
    {
        _config = config;
        _profile = profile;
    }

    public IDbConnection Create()
    {
        // First-run safety: SQLite ReadWriteCreate creates the .db FILE but NOT missing parent
        // directories (e.g. %APPDATA%\TimesheetApp on a fresh machine) -> "unable to open database
        // file". Ensure the directory exists before opening. Idempotent; no-op for bare filenames.
        var dir = Path.GetDirectoryName(_config.DbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _config.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        };

        string pragmaSql;
        if (_profile == SqliteProfile.Server)
        {
            builder.Pooling = true;
            // "Default Timeout" is a real SqliteConnectionStringBuilder keyword: it becomes the
            // default CommandTimeout for every command created off this connection, so it flows
            // into every Dapper call without touching a single repository.
            builder.DefaultTimeout = 5;
            pragmaSql = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; " +
                        "PRAGMA busy_timeout=1000; PRAGMA synchronous=NORMAL;";
        }
        else
        {
            builder.Pooling = false;
            // DELETE journal (rollback) + belt-and-suspenders FK pragma on the live handle.
            pragmaSql = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
        }

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // PRAGMA journal_mode cannot be set inside a transaction, so this batch runs here,
            // immediately after Open() and before any caller can BEGIN one, for both profiles.
            cmd.CommandText = pragmaSql;
            cmd.ExecuteNonQuery();
        }

        return conn;
    }
}
