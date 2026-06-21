using System.Data;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;

namespace TimesheetApp.Data;

// XC-01: short open->work->close connections, journal_mode=DELETE (NOT WAL -> no -wal/-shm
// sidecars to sync out of band over OneDrive), Pooling=False (Dispose truly releases the
// file handle so OneDrive can upload). DATA-06: PRAGMA foreign_keys=ON every connection.
public sealed class SqliteConnectionFactory : IConnectionFactory
{
    private readonly IAppConfig _config;

    public SqliteConnectionFactory(IAppConfig config)
    {
        _config = config;
    }

    public IDbConnection Create()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _config.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            // DELETE journal (rollback) + belt-and-suspenders FK pragma on the live handle.
            cmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
        }

        return conn;
    }
}
