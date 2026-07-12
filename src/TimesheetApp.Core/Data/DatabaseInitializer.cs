using System.Data;
using Dapper;

namespace TimesheetApp.Data;

// DATA-01..05. Schema mirrors architecture spec §2/§3:
//  - Users identity column nullable; is_active on Users/Tasks/DefaultTasks.
//    (v1..v9 called it windows_username; v10 renamed it to username -- see RunMigrations.)
//  - Requests has NO is_active (decision 4).
//  - TimeLogs: single FK task_id, hours REAL, UNIQUE(user_id,task_id,work_date).
//  - work_date / created_at stored as TEXT (ISO-8601), culture-neutral.
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    // Bump SchemaVersion and append a step to Migrations[] for any future additive change.
    private const long SchemaVersion = 10;

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
        EnsureDefaultBacklog(conn, tx);
        SeedDefaultTasksIfEmpty(conn, tx);

        tx.Commit();
        return Task.CompletedTask;
    }

    private static void CreateTables(IDbConnection conn, IDbTransaction tx)
    {
        // 🚨 DO NOT 'TIDY' Users.windows_username TO username BELOW. 🚨
        // This DDL is the *v1* schema, not the current one. RunMigrations replays EVERY step on a
        // brand-new database, and the v10 step does:
        //     ALTER TABLE Users RENAME COLUMN windows_username TO username;
        // so the column must be BORN as windows_username for that rename to have something to rename.
        // Renaming it here would break FRESH INSTALLS ONLY (`no such column: windows_username` at
        // startup) while every existing database keeps working -- the worst kind of bug to find.
        // The same reasoning applies to every other legacy name in this DDL (e.g. Tasks.request_id,
        // renamed to backlog_id by v6). DatabaseInitializerTests.FreshInstall_* guards this.
        const string ddl = @"
CREATE TABLE IF NOT EXISTS Users (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,
    windows_username TEXT,   -- v1 name; v10 renames to username. See the warning above.
    is_active       INTEGER NOT NULL DEFAULT 1
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
);

-- P7 Daily Report (schema v5). Standup rows are per (user, day, section); request_id is
-- nullable (ad-hoc codes typed in a meeting have no Requests row). Issues cascade-delete.
CREATE TABLE IF NOT EXISTS StandupEntries (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id      INTEGER NOT NULL,
    work_date    TEXT    NOT NULL,
    section      TEXT    NOT NULL,
    request_id   INTEGER,
    request_code TEXT    NOT NULL,
    task_text    TEXT    NOT NULL,
    description  TEXT    NOT NULL DEFAULT '',
    deadline     TEXT,
    status       TEXT    NOT NULL,
    order_index  INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT    NOT NULL,
    FOREIGN KEY (user_id) REFERENCES Users(id)
);

CREATE INDEX IF NOT EXISTS ix_standup_user_date ON StandupEntries(user_id, work_date);
CREATE INDEX IF NOT EXISTS ix_standup_date      ON StandupEntries(work_date);

CREATE TABLE IF NOT EXISTS StandupIssues (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id      INTEGER NOT NULL,
    issue_text    TEXT    NOT NULL,
    solution_text TEXT,
    status        TEXT    NOT NULL DEFAULT 'open',
    order_index   INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    FOREIGN KEY (entry_id) REFERENCES StandupEntries(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_standup_issue_entry ON StandupIssues(entry_id);

-- P8 Task List (schema v7). Tags + N:N BacklogTags link, PCA contacts (soft-delete via is_active,
-- mirrors Users), and a manual Holiday calendar. All additive; the v7 Backlog ALTERs live in RunMigrations.
CREATE TABLE IF NOT EXISTS Tags (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    text       TEXT    NOT NULL,
    icon       TEXT    NOT NULL,
    color      TEXT    NOT NULL,
    created_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS BacklogTags (
    backlog_id INTEGER NOT NULL,
    tag_id     INTEGER NOT NULL,
    PRIMARY KEY (backlog_id, tag_id)
);

CREATE TABLE IF NOT EXISTS PcaContacts (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    name      TEXT    NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS Holidays (
    holiday_date TEXT PRIMARY KEY,
    description  TEXT
);

-- P10 Multi-Team (schema v8). Team is a new top-level org entity; Users<->Teams is many-to-many via
-- UserTeams. No inline FK on UserTeams (matches BacklogTags). team_id columns on Backlogs/StandupEntries
-- are added by the v8 migration step in RunMigrations.
CREATE TABLE IF NOT EXISTS Teams (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,
    is_active  INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS UserTeams (
    user_id INTEGER NOT NULL,
    team_id INTEGER NOT NULL,
    PRIMARY KEY (user_id, team_id)
);

-- P13 Task List Operations & History (schema v9). TaskTags is the N:N task<->tag link (no inline FK,
-- mirrors BacklogTags). TaskAudit is the per-task field history (mirrors BacklogAudit). The v9 Backlog/
-- Tasks ALTERs (type, assignee_user_id, BacklogAudit.note) live in the v9 migration step in RunMigrations.
CREATE TABLE IF NOT EXISTS TaskTags (
    task_id INTEGER NOT NULL,
    tag_id  INTEGER NOT NULL,
    PRIMARY KEY (task_id, tag_id)
);

CREATE TABLE IF NOT EXISTS TaskAudit (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id            INTEGER NOT NULL,
    field              TEXT    NOT NULL,
    old_value          TEXT,
    new_value          TEXT,
    changed_by_user_id INTEGER,
    changed_by_name    TEXT,
    changed_at         TEXT    NOT NULL
);";
        conn.Execute(ddl, transaction: tx);

        // Requests + RequestAudit are RENAMED to Backlogs / BacklogAudit by the v6 migration. Re-creating
        // them with IF NOT EXISTS is only correct for a pre-v6 DB (so the v6 ALTER ... RENAME has a table
        // to rename); on a v6+ DB the rename already ran, so re-creating the legacy names would leave
        // stray empty tables on every relaunch. Gate on user_version. (SQLite allows the forward FK
        // reference from Tasks -> Requests created above, so ordering here is fine.)
        var version = conn.ExecuteScalar<long>("PRAGMA user_version;", transaction: tx);
        if (version < 6)
        {
            conn.Execute(@"
CREATE TABLE IF NOT EXISTS Requests (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    request_code TEXT    NOT NULL,
    project      TEXT    NOT NULL,
    created_at   TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS RequestAudit (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id          INTEGER NOT NULL,
    field               TEXT    NOT NULL,
    old_value           TEXT,
    new_value           TEXT,
    changed_by_user_id  INTEGER,
    changed_by_name     TEXT,
    changed_at          TEXT    NOT NULL,
    FOREIGN KEY (request_id) REFERENCES Requests(id)
);", transaction: tx);
        }
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
            // v2 -> Requests gains start_date / end_date / period_month / status (RequestAudit table
            // is created idempotently in CreateTables). ADD COLUMN is not idempotent, so this step is
            // gated on user_version and runs exactly once.
            static (c, t) => c.Execute(
                @"ALTER TABLE Requests ADD COLUMN start_date   TEXT;
                  ALTER TABLE Requests ADD COLUMN end_date     TEXT;
                  ALTER TABLE Requests ADD COLUMN period_month TEXT;
                  ALTER TABLE Requests ADD COLUMN status       TEXT;", transaction: t),
            // v3 -> normalize legacy free-text project values onto the fixed enum
            // (ARCS / PlusArcs / ARMS / Other). The hidden DEFAULT request keeps its 'DEFAULT' project.
            // Each step only touches rows not yet on the enum, so order is safe (PlusArcs before ARCS).
            static (c, t) => c.Execute(
                @"UPDATE Requests SET project='PlusArcs'
                    WHERE request_code<>'DEFAULT' AND project NOT IN ('ARCS','PlusArcs','ARMS','Other')
                      AND lower(project) LIKE '%plus%';
                  UPDATE Requests SET project='ARMS'
                    WHERE request_code<>'DEFAULT' AND project NOT IN ('ARCS','PlusArcs','ARMS','Other')
                      AND lower(project) LIKE '%arms%';
                  UPDATE Requests SET project='ARCS'
                    WHERE request_code<>'DEFAULT' AND project NOT IN ('ARCS','PlusArcs','ARMS','Other')
                      AND lower(project) LIKE '%arc%';
                  UPDATE Requests SET project='Other'
                    WHERE request_code<>'DEFAULT' AND project NOT IN ('ARCS','PlusArcs','ARMS','Other');",
                transaction: t),
            // v4 -> Requests gains assignee_user_id (the user responsible for the ticket). Nullable
            // (a request may be unassigned); not an FK constraint so deactivating a user never blocks.
            static (c, t) => c.Execute(
                "ALTER TABLE Requests ADD COLUMN assignee_user_id INTEGER;", transaction: t),
            // v5 -> Daily Report (StandupEntries + StandupIssues + indexes). The tables are created
            // idempotently in CreateTables (CREATE TABLE IF NOT EXISTS), so this step only needs to
            // exist to gate the user_version bump to 5 — no extra ALTER required.
            static (_, _) => { },
            // v6 -> Rename Request -> Backlog (tables + columns), rename request status -> type,
            // add task status column. StandupEntries request_id/request_code also renamed.
            static (c, t) => c.Execute(
                @"ALTER TABLE Requests RENAME TO Backlogs;
                  ALTER TABLE Backlogs RENAME COLUMN request_code TO backlog_code;
                  ALTER TABLE Backlogs RENAME COLUMN status TO type;
                  ALTER TABLE RequestAudit RENAME TO BacklogAudit;
                  ALTER TABLE BacklogAudit RENAME COLUMN request_id TO backlog_id;
                  ALTER TABLE Tasks RENAME COLUMN request_id TO backlog_id;
                  ALTER TABLE Tasks ADD COLUMN status TEXT NOT NULL DEFAULT 'Todo';
                  ALTER TABLE StandupEntries RENAME COLUMN request_id TO backlog_id;
                  ALTER TABLE StandupEntries RENAME COLUMN request_code TO backlog_code;",
                transaction: t),
            // v7 -> P8 Task List tracking metadata on Backlogs (all nullable; existing rows unaffected).
            // Dual deadlines, dual estimates, manual progress %, note, and the PCA contact id (no inline
            // FK, mirroring assignee_user_id). The 4 supporting tables are created in CreateTables.
            // assignee_user_id (v4) is reused as the PCT person-in-charge — no new PCT column.
            static (c, t) => c.Execute(
                @"ALTER TABLE Backlogs ADD COLUMN deadline_internal       TEXT;
                  ALTER TABLE Backlogs ADD COLUMN deadline_external       TEXT;
                  ALTER TABLE Backlogs ADD COLUMN rough_estimate_hours    REAL;
                  ALTER TABLE Backlogs ADD COLUMN official_estimate_hours REAL;
                  ALTER TABLE Backlogs ADD COLUMN progress_percent        INTEGER;
                  ALTER TABLE Backlogs ADD COLUMN note                    TEXT;
                  ALTER TABLE Backlogs ADD COLUMN pca_contact_id          INTEGER;", transaction: t),
            // v8 -> P10 Multi-Team. team_id on Backlogs + StandupEntries (nullable, no inline FK,
            // mirroring assignee_user_id / pca_contact_id). Teams + UserTeams tables are created
            // idempotently in CreateTables. The DATA MIGRATION (create "Architect Improvement",
            // repoint existing rows) is NOT here — it runs as a post-init bootstrap (W2).
            static (c, t) => c.Execute(
                @"ALTER TABLE Backlogs       ADD COLUMN team_id INTEGER;
                  ALTER TABLE StandupEntries ADD COLUMN team_id INTEGER;", transaction: t),
            // v9 -> P13 Task List Operations & History. task-level type/assignee on Tasks (nullable, no
            // inline FK, mirroring the Backlog columns) + note on BacklogAudit (deadline-change reason from
            // the B2 Note popup). TaskTags + TaskAudit tables are created idempotently in CreateTables.
            static (c, t) => c.Execute(
                @"ALTER TABLE Tasks        ADD COLUMN type             TEXT;
                  ALTER TABLE Tasks        ADD COLUMN assignee_user_id INTEGER;
                  ALTER TABLE BacklogAudit ADD COLUMN note             TEXT;", transaction: t),
            // v10 -> M8.2 web foundation: optimistic concurrency + auth + per-user team scope.
            //
            // row_version goes on the 8 tables a second user can concurrently edit. Two UPDATE
            // templates consume it: check-and-bump (user edits carrying an expectedVersion) and
            // bump-only (reorders, soft-deletes, system writes). A write that bumps without
            // checking is safe; a write that checks without bumping is a lost update.
            //
            // Deliberately NOT versioned: Holidays and Settings (key/date-keyed -- last-write-wins
            // IS the correct semantics there), and StandupEntries (owner-gated inside StandupService,
            // so two users cannot reach the same row in the first place).
            //
            // The Users rename is a pure column rename -- values are preserved, so no history is
            // orphaned and people keep logging in with the name they already use. NOTE this is the
            // step that requires CreateTables' DDL to keep saying windows_username forever; see the
            // warning there.
            static (c, t) => c.Execute(
                @"ALTER TABLE Backlogs      ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE Tasks         ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE TimeLogs      ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE StandupIssues ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE Users         ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE Teams         ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE Tags          ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;
                  ALTER TABLE PcaContacts   ADD COLUMN row_version INTEGER NOT NULL DEFAULT 1;

                  ALTER TABLE Users RENAME COLUMN windows_username TO username;
                  ALTER TABLE Users ADD COLUMN password_hash  TEXT;
                  ALTER TABLE Users ADD COLUMN is_admin       INTEGER NOT NULL DEFAULT 0;
                  ALTER TABLE Users ADD COLUMN active_team_id INTEGER NOT NULL DEFAULT 0;

                  -- Never leave the system with zero admins. On a fresh database Users is empty,
                  -- so MIN(id) is NULL and this matches no row -- correct, there is nobody to promote.
                  UPDATE Users SET is_admin = 1 WHERE id = (SELECT MIN(id) FROM Users);",
                transaction: t),
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

    private static void EnsureDefaultBacklog(IDbConnection conn, IDbTransaction tx)
    {
        // DATA-03: exactly one hidden DEFAULT backlog, idempotent.
        // Note: DDL creates table as 'Requests' (for v2-v4 compat); v6 migration renames to 'Backlogs'.
        // This runs AFTER migrations, so the table is always 'Backlogs' here.
        conn.Execute(
            @"INSERT INTO Backlogs(backlog_code, project, created_at)
              SELECT 'DEFAULT', 'DEFAULT', @now
              WHERE NOT EXISTS (SELECT 1 FROM Backlogs WHERE backlog_code = 'DEFAULT');",
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
