using System.Data;
using Dapper;

namespace TimesheetApp.Data;

// DATA-01..05. Schema mirrors architecture spec §2/§3:
//  - Users.windows_username nullable; is_active on Users/Tasks/DefaultTasks.
//  - Requests has NO is_active (decision 4).
//  - TimeLogs: single FK task_id, hours REAL, UNIQUE(user_id,task_id,work_date).
//  - work_date / created_at stored as TEXT (ISO-8601), culture-neutral.
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    // Bump SchemaVersion and append a step to Migrations[] for any future additive change.
    private const long SchemaVersion = 1;

    private readonly IConnectionFactory _factory;

    public DatabaseInitializer(IConnectionFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        using var conn = _factory.Create();
        using var tx = conn.BeginTransaction();

        CreateTables(conn, tx);
        RunMigrations(conn, tx);
        EnsureDefaultRequest(conn, tx);
        SeedDefaultTasksIfEmpty(conn, tx);

        tx.Commit();
        return Task.CompletedTask;
    }

    private static void CreateTables(IDbConnection conn, IDbTransaction tx)
    {
        const string ddl = @"
CREATE TABLE IF NOT EXISTS Users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,
    windows_username TEXT,
    is_active       INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Requests (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    request_code TEXT    NOT NULL,
    project      TEXT    NOT NULL,
    created_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS Tasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id  INTEGER NOT NULL,
    task_name   TEXT    NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (request_id) REFERENCES Requests(id)
);

CREATE TABLE IF NOT EXISTS TaskTemplates (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    template_name TEXT    NOT NULL,
    task_name     TEXT    NOT NULL,
    order_index   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS TimeLogs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    task_id    INTEGER NOT NULL,
    work_date  TEXT    NOT NULL,
    hours      REAL    NOT NULL,
    created_at TEXT    NOT NULL,
    FOREIGN KEY (user_id) REFERENCES Users(id),
    FOREIGN KEY (task_id) REFERENCES Tasks(id),
    UNIQUE (user_id, task_id, work_date)
);

CREATE TABLE IF NOT EXISTS DefaultTasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_name   TEXT    NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);";
        conn.Execute(ddl, transaction: tx);
    }

    private static void RunMigrations(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-05: forward-only additive migrations gated on PRAGMA user_version.
        // Step index N runs when current user_version < N+1. All steps additive (ADD COLUMN /
        // CREATE TABLE) so an old client opening a newer DB still works.
        var migrations = new Action<IDbConnection, IDbTransaction>[]
        {
            // v1 -> baseline schema already created above; nothing extra to alter.
            static (_, _) => { },
        };

        var current = conn.ExecuteScalar<long>("PRAGMA user_version;", transaction: tx);
        for (var step = current; step < migrations.Length; step++)
        {
            migrations[step](conn, tx);
        }

        if (current < SchemaVersion)
        {
            // PRAGMA cannot be parameterized; SchemaVersion is a compile-time constant.
            conn.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
        }
    }

    private static void EnsureDefaultRequest(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-03: exactly one hidden DEFAULT request, idempotent.
        conn.Execute(
            @"INSERT INTO Requests(request_code, project, created_at)
              SELECT 'DEFAULT', 'DEFAULT', @now
              WHERE NOT EXISTS (SELECT 1 FROM Requests WHERE request_code = 'DEFAULT');",
            new { now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
            transaction: tx);
    }

    private static void SeedDefaultTasksIfEmpty(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-04: seed the default set ONLY when the table is empty, so a user who has
        // renamed/hidden defaults is never overwritten on relaunch.
        var any = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM DefaultTasks;", transaction: tx);
        if (any > 0) return;

        var seeds = new[] { "Annual Leave", "Meeting", "Other" };
        for (var i = 0; i < seeds.Length; i++)
        {
            conn.Execute(
                "INSERT INTO DefaultTasks(task_name, order_index, is_active) VALUES(@name, @order, 1);",
                new { name = seeds[i], order = i },
                transaction: tx);
        }
    }
}
