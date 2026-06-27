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

    public async Task DeleteAsync(int userId, int taskId, DateOnly workDate)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "DELETE FROM TimeLogs WHERE user_id = @u AND task_id = @t AND work_date = @d;",
            new { u = userId, t = taskId, d = workDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
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

    public async Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to)
    {
        using var c = _factory.Create();
        // INNER JOIN by id, NO is_active predicate (XC-06): soft-deleted task/user names still resolve.
        var rows = await c.QueryAsync<ReportRaw>(
            @"SELECT u.id AS user_id, u.name AS user_name,
                     r.backlog_code AS backlog_code, r.project AS project,
                     t.id AS task_id, t.task_name AS task_name,
                     l.work_date AS work_date, l.hours AS hours
              FROM TimeLogs l
              JOIN Tasks    t ON t.id = l.task_id
              JOIN Backlogs r ON r.id = t.backlog_id
              JOIN Users    u ON u.id = l.user_id
              WHERE l.user_id = @u AND l.work_date >= @from AND l.work_date <= @to
              ORDER BY r.project, r.backlog_code, t.order_index, l.work_date;",
            new { u = userId, from = Iso(from), to = Iso(to) });
        return rows.Select(MapReportRow).ToList();
    }

    public async Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter)
    {
        using var c = _factory.Create();
        // Same INNER JOIN with NO is_active predicate (XC-06); optional project filter.
        var rows = await c.QueryAsync<ReportRaw>(
            @"SELECT u.id AS user_id, u.name AS user_name,
                     r.backlog_code AS backlog_code, r.project AS project,
                     t.id AS task_id, t.task_name AS task_name,
                     l.work_date AS work_date, l.hours AS hours
              FROM TimeLogs l
              JOIN Tasks    t ON t.id = l.task_id
              JOIN Backlogs r ON r.id = t.backlog_id
              JOIN Users    u ON u.id = l.user_id
              WHERE l.work_date >= @from AND l.work_date <= @to
                AND (@proj IS NULL OR r.project = @proj)
              ORDER BY u.name, r.project, r.backlog_code, t.order_index, l.work_date;",
            new { from = Iso(from), to = Iso(to), proj = projectFilter });
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

    private const string UpsertSql =
        @"INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at)
          VALUES(@UserId, @TaskId, @WorkDate, @Hours, @CreatedAt)
          ON CONFLICT(user_id, task_id, work_date) DO UPDATE SET hours = excluded.hours;";

    private static object ToParams(TimeLog log) => new
    {
        log.UserId,
        log.TaskId,
        WorkDate = Iso(log.WorkDate),
        Hours = (double)log.Hours,   // column is REAL; bind a double, store at 1-decimal precision
        CreatedAt = log.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
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
        (decimal)r.hours);

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
    }
}
