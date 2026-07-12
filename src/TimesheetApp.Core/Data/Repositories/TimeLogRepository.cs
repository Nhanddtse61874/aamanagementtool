using System.Data;
using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// TimeLog data access (XC-06, TS-03/TS-07, SI-05 atomicity, XC-03 range read).
// SQL + Dapper only; one short open->work->close connection per method via IConnectionFactory.
public sealed class TimeLogRepository : ITimeLogRepository
{
    private readonly IConnectionFactory _factory;

    public TimeLogRepository(IConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(TimeLog log)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(UpsertSql, ToParams(log));
    }

    public async Task<long> UpsertCheckedAsync(TimeLog log, long? expectedVersion)
    {
        using var c = _factory.Create();
        // RETURNING emits NO row when the write was a no-op => null means conflict.
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(CheckedUpsertSql, ToParams(log, expectedVersion));
        if (newVersion is null)
            throw await ConflictAsync(c, log.UserId, log.TaskId, log.WorkDate, expectedVersion);
        return newVersion.Value;
    }

    public async Task DeleteAsync(int userId, int taskId, DateOnly workDate)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "DELETE FROM TimeLogs WHERE user_id = @u AND task_id = @t AND work_date = @d;",
            new { u = userId, t = taskId, d = workDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
    }

    public async Task DeleteCheckedAsync(int userId, int taskId, DateOnly workDate, long expectedVersion)
    {
        using var c = _factory.Create();
        var rows = await c.ExecuteAsync(
            @"DELETE FROM TimeLogs
               WHERE user_id = @u AND task_id = @t AND work_date = @d AND row_version = @Expected;",
            new { u = userId, t = taskId, d = Iso(workDate), Expected = expectedVersion });
        if (rows == 0)
            throw await ConflictAsync(c, userId, taskId, workDate, expectedVersion);
    }

    // The conflict path, and ONLY the conflict path, pays for one existence check: rowsAffected == 0
    // conflates "someone edited this" with "someone deleted this", and the user needs different
    // wording for each. Racy only in its wording — the conflict itself is already decided.
    private static async Task<ConcurrencyConflictException> ConflictAsync(
        IDbConnection c, int userId, int taskId, DateOnly workDate, long? expected)
    {
        var exists = await c.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM TimeLogs
               WHERE user_id = @u AND task_id = @t AND work_date = @d;",
            new { u = userId, t = taskId, d = Iso(workDate) }) > 0;
        // id 0: TimeLogs is keyed by the natural (user_id, task_id, work_date) triple, not an id.
        return new ConcurrencyConflictException("TimeLogs", 0, expected, deleted: !exists);
    }

    public async Task<IReadOnlyList<TimeLog>> GetByUserAndRangeAsync(int userId, DateOnly from, DateOnly to)
    {
        using var c = _factory.Create();
        // Read raw (SQLite-native) types, then map decimal/DateOnly at the boundary — Dapper's
        // positional-record path does not narrow long->int / convert double->decimal on its own.
        var rows = await c.QueryAsync<TimeLogRaw>(
            @"SELECT id, user_id, task_id, work_date, hours, created_at
              FROM TimeLogs
              WHERE user_id = @u AND work_date >= @from AND work_date <= @to
              ORDER BY work_date, task_id;",
            new { u = userId, from = Iso(from), to = Iso(to) });
        return rows.Select(MapTimeLog).ToList();
    }

    public async Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to, IReadOnlyList<int>? teamIds = null)
    {
        using var c = _factory.Create();
        // INNER JOIN by id, NO is_active predicate (XC-06): soft-deleted task/user names still resolve.
        // LEFT JOIN Teams so a backlog with no team still returns (team name null). @noTeam short-
        // circuits the team filter when teamIds is null (preserves existing behavior, R6).
        var noTeam = teamIds is null;
        var rows = await c.QueryAsync<ReportRaw>(
            @"SELECT u.id AS user_id, u.name AS user_name,
                     r.backlog_code AS backlog_code, r.project AS project,
                     t.id AS task_id, t.task_name AS task_name,
                     l.work_date AS work_date, l.hours AS hours,
                     r.team_id AS team_id, tm.name AS team_name
              FROM TimeLogs l
              JOIN Tasks    t ON t.id = l.task_id
              JOIN Backlogs r ON r.id = t.backlog_id
              JOIN Users    u ON u.id = l.user_id
              LEFT JOIN Teams tm ON tm.id = r.team_id
              WHERE l.user_id = @u AND l.work_date >= @from AND l.work_date <= @to
                AND (@noTeam OR r.team_id IN @teamIds)
              ORDER BY r.project, r.backlog_code, t.order_index, l.work_date;",
            new { u = userId, from = Iso(from), to = Iso(to), noTeam, teamIds = teamIds ?? Array.Empty<int>() });
        return rows.Select(MapReportRow).ToList();
    }

    public async Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter, IReadOnlyList<int>? teamIds = null)
    {
        using var c = _factory.Create();
        // Same INNER JOIN with NO is_active predicate (XC-06); optional project + team filters.
        // R1 leak fix: without a team predicate this export would surface every team's rows.
        var noTeam = teamIds is null;
        var rows = await c.QueryAsync<ReportRaw>(
            @"SELECT u.id AS user_id, u.name AS user_name,
                     r.backlog_code AS backlog_code, r.project AS project,
                     t.id AS task_id, t.task_name AS task_name,
                     l.work_date AS work_date, l.hours AS hours,
                     r.team_id AS team_id, tm.name AS team_name
              FROM TimeLogs l
              JOIN Tasks    t ON t.id = l.task_id
              JOIN Backlogs r ON r.id = t.backlog_id
              JOIN Users    u ON u.id = l.user_id
              LEFT JOIN Teams tm ON tm.id = r.team_id
              WHERE l.work_date >= @from AND l.work_date <= @to
                AND (@proj IS NULL OR r.project = @proj)
                AND (@noTeam OR r.team_id IN @teamIds)
              ORDER BY u.name, r.project, r.backlog_code, t.order_index, l.work_date;",
            new { from = Iso(from), to = Iso(to), proj = projectFilter, noTeam, teamIds = teamIds ?? Array.Empty<int>() });
        return rows.Select(MapReportRow).ToList();
    }

    public async Task<IReadOnlyList<int>> GetUserIdsWithLogsInRangeAsync(DateOnly from, DateOnly to)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<int>(
            "SELECT DISTINCT user_id FROM TimeLogs WHERE work_date >= @from AND work_date <= @to;",
            new { from = Iso(from), to = Iso(to) });
        return ids.ToList();
    }

    public async Task UpsertBatchAsync(IReadOnlyList<TimeLog> logs)
    {
        // SI-05: all rows in ONE transaction — smart-input apply is all-or-nothing.
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
        foreach (var log in logs)
        {
            await c.ExecuteAsync(UpsertSql, ToParams(log), tx);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyDictionary<int, decimal>> GetLoggedHoursByBacklogAsync()
    {
        using var c = _factory.Create();
        // All-time roll-up: JOIN Tasks only (NO is_active predicate, XC-06) so soft-deleted tasks' hours
        // still count. hours is REAL -> read as double, narrow to decimal at the boundary.
        var rows = await c.QueryAsync<HoursByBacklogRaw>(
            @"SELECT t.backlog_id AS backlog_id, SUM(l.hours) AS hours
              FROM TimeLogs l
              JOIN Tasks    t ON t.id = l.task_id
              GROUP BY t.backlog_id;");
        return rows.ToDictionary(r => (int)r.backlog_id, r => (decimal)r.hours);
    }

    // BUMP-ONLY (M8.2): every write moves row_version, but nothing is compared, so this never conflicts.
    // Used by the legacy/system write path (UpsertAsync) and by Smart Fill (UpsertBatchAsync).
    private const string UpsertSql =
        @"INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at, row_version)
          VALUES(@UserId, @TaskId, @WorkDate, @Hours, @CreatedAt, 1)
          ON CONFLICT(user_id, task_id, work_date) DO UPDATE
            SET hours = excluded.hours, row_version = TimeLogs.row_version + 1;";

    // CHECK-AND-BUMP. Five cases, because an upsert can find the row absent on EITHER side of the
    // version check -- and the fifth one silently destroys data.
    //
    // The obvious statement -- VALUES(...) ON CONFLICT DO UPDATE ... WHERE row_version = @Expected --
    // is WRONG when the caller supplies a version and the row has been DELETED: with no row there is
    // no conflict, so the WHERE never runs, the INSERT succeeds, and the row is resurrected at v1 and
    // reported as success. (Measured: it returns 1 and writes the row back.) The WHERE guards the
    // UPDATE branch. It does not guard the INSERT branch.
    //
    // So the INSERT is made conditional too: its SELECT source yields a row only if the caller either
    // expects no row (@Expected IS NULL) or expects one at exactly @Expected. Case by case:
    //   @Expected NULL, no row  -> SELECT yields    -> no unique clash -> INSERT at 1     -> RETURNS 1
    //   @Expected NULL, row     -> SELECT yields    -> clash -> DO UPDATE WHERE v = NULL is never true
    //                                               -> no-op                              -> RETURNS 0 ROWS
    //   @Expected N,    row @N  -> SELECT yields    -> clash -> DO UPDATE fires           -> RETURNS N+1
    //   @Expected N,    row @M  -> SELECT is EMPTY  -> nothing inserted, nothing updated  -> RETURNS 0 ROWS
    //   @Expected N,    NO row  -> SELECT is EMPTY  -> nothing inserted (NOT resurrected) -> RETURNS 0 ROWS
    //
    // RETURNING emits ZERO rows when the write turns into a no-op -- the SQLite docs are silent on
    // this; it is measured (see TimeLogConcurrencyTests). That is what lets ONE statement both detect
    // the conflict and hand back the new version: a null result means conflict.
    // (The SELECT's WHERE also disambiguates the parse -- SQLite cannot always tell whether ON CONFLICT
    // belongs to a SELECT-sourced INSERT or to the upsert without one.)
    private const string CheckedUpsertSql =
        @"INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at, row_version)
          SELECT @UserId, @TaskId, @WorkDate, @Hours, @CreatedAt, 1
           WHERE @Expected IS NULL
              OR EXISTS (SELECT 1 FROM TimeLogs
                          WHERE user_id = @UserId AND task_id = @TaskId
                            AND work_date = @WorkDate AND row_version = @Expected)
          ON CONFLICT(user_id, task_id, work_date) DO UPDATE
            SET hours = excluded.hours, row_version = TimeLogs.row_version + 1
           WHERE TimeLogs.row_version = @Expected
          RETURNING row_version;";

    private static object ToParams(TimeLog log) => new
    {
        log.UserId,
        log.TaskId,
        WorkDate = Iso(log.WorkDate),
        Hours = (double)log.Hours,   // column is REAL; bind a double, store at 1-decimal precision
        CreatedAt = log.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    };

    private static object ToParams(TimeLog log, long? expectedVersion) => new
    {
        log.UserId,
        log.TaskId,
        WorkDate = Iso(log.WorkDate),
        Hours = (double)log.Hours,
        CreatedAt = log.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        Expected = expectedVersion,
    };

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeLog MapTimeLog(TimeLogRaw r) => new(
        (int)r.id, (int)r.user_id, (int)r.task_id,
        DateOnly.ParseExact(r.work_date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        (decimal)r.hours,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    private static TimeLogReportRow MapReportRow(ReportRaw r) => new(
        (int)r.user_id, r.user_name,
        r.backlog_code, r.project,
        (int)r.task_id, r.task_name,
        DateOnly.ParseExact(r.work_date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        (decimal)r.hours,
        r.team_id is { } team ? (int)team : null, r.team_name);

    // SQLite-native shapes (long/double/string) Dapper maps cleanly; converted at the boundary above.
    private sealed class TimeLogRaw
    {
        public long id { get; set; }
        public long user_id { get; set; }
        public long task_id { get; set; }
        public string work_date { get; set; } = "";
        public double hours { get; set; }
        public string created_at { get; set; } = "";
    }

    private sealed class HoursByBacklogRaw
    {
        public long backlog_id { get; set; }
        public double hours { get; set; }
    }

    private sealed class ReportRaw
    {
        public long user_id { get; set; }
        public string user_name { get; set; } = "";
        public string backlog_code { get; set; } = "";
        public string project { get; set; } = "";
        public long task_id { get; set; }
        public string task_name { get; set; } = "";
        public string work_date { get; set; } = "";
        public double hours { get; set; }
        public long? team_id { get; set; }
        public string? team_name { get; set; }
    }
}
