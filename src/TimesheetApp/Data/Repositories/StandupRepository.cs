using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Daily Report (Standup) data access — DR-02..04, DR-09. Dates stored as TEXT (ISO, culture-neutral);
// DateOnly/DateTimeOffset parsed at the boundary. One short connection per method (OneDrive-safe policy).
public sealed class StandupRepository : IStandupRepository
{
    private const string EntryCols =
        "id, user_id, work_date, section, request_id, request_code, task_text, description, deadline, status, order_index, created_at";
    private const string IssueCols =
        "id, entry_id, issue_text, solution_text, status, order_index, created_at";

    private readonly IConnectionFactory _factory;

    public StandupRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesAsync(int userId, DateOnly workDate)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE user_id = @uid AND work_date = @day
               ORDER BY section, order_index, id;",
            new { uid = userId, day = Day(workDate) });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesForDayAsync(DateOnly workDate)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE work_date = @day
               ORDER BY user_id, section, order_index, id;",
            new { day = Day(workDate) });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesForRangeAsync(DateOnly from, DateOnly to)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE work_date >= @from AND work_date <= @to
               ORDER BY work_date, user_id, section, order_index, id;",
            new { from = Day(from), to = Day(to) });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<int> InsertEntryAsync(StandupEntry e)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO StandupEntries(user_id, work_date, section, request_id, request_code,
                  task_text, description, deadline, status, order_index, created_at)
              VALUES(@UserId, @WorkDate, @Section, @RequestId, @RequestCode,
                  @TaskText, @Description, @Deadline, @Status, @OrderIndex, @CreatedAt);
              SELECT last_insert_rowid();",
            new
            {
                e.UserId,
                WorkDate = Day(e.WorkDate),
                e.Section,
                e.RequestId,
                e.RequestCode,
                e.TaskText,
                e.Description,
                Deadline = DayN(e.Deadline),
                e.Status,
                e.OrderIndex,
                CreatedAt = Iso(e.CreatedAt),
            });
    }

    public async Task UpdateEntryAsync(StandupEntry e)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            @"UPDATE StandupEntries SET
                  section = @Section, request_id = @RequestId, request_code = @RequestCode,
                  task_text = @TaskText, description = @Description, deadline = @Deadline,
                  status = @Status, order_index = @OrderIndex
              WHERE id = @Id;",
            new
            {
                e.Section,
                e.RequestId,
                e.RequestCode,
                e.TaskText,
                e.Description,
                Deadline = DayN(e.Deadline),
                e.Status,
                e.OrderIndex,
                e.Id,
            });
    }

    public async Task DeleteEntryAsync(int entryId)
    {
        using var c = _factory.Create();
        // FK ON DELETE CASCADE removes the entry's issues (foreign_keys is ON per connection).
        await c.ExecuteAsync("DELETE FROM StandupEntries WHERE id = @id;", new { id = entryId });
    }

    public async Task<IReadOnlyList<StandupIssue>> GetIssuesForEntriesAsync(IReadOnlyList<int> entryIds)
    {
        if (entryIds.Count == 0) return Array.Empty<StandupIssue>();
        using var c = _factory.Create();
        var rows = await c.QueryAsync<IssueRaw>(
            $@"SELECT {IssueCols} FROM StandupIssues
               WHERE entry_id IN @ids
               ORDER BY entry_id, order_index, id;",
            new { ids = entryIds });
        return rows.Select(MapIssue).ToList();
    }

    public async Task<int> InsertIssueAsync(StandupIssue i)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO StandupIssues(entry_id, issue_text, solution_text, status, order_index, created_at)
              VALUES(@EntryId, @IssueText, @SolutionText, @Status, @OrderIndex, @CreatedAt);
              SELECT last_insert_rowid();",
            new
            {
                i.EntryId,
                i.IssueText,
                i.SolutionText,
                i.Status,
                i.OrderIndex,
                CreatedAt = Iso(i.CreatedAt),
            });
    }

    public async Task UpdateIssueAsync(StandupIssue i)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            @"UPDATE StandupIssues SET
                  issue_text = @IssueText, solution_text = @SolutionText,
                  status = @Status, order_index = @OrderIndex
              WHERE id = @Id;",
            new { i.IssueText, i.SolutionText, i.Status, i.OrderIndex, i.Id });
    }

    public async Task DeleteIssueAsync(int issueId)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync("DELETE FROM StandupIssues WHERE id = @id;", new { id = issueId });
    }

    // ---- boundary helpers ----
    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string? DayN(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static DateOnly ParseDay(string s) =>
        DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? ParseDayN(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseDay(s);
    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static StandupEntry MapEntry(EntryRaw r) => new(
        (int)r.id, (int)r.user_id, ParseDay(r.work_date), r.section,
        r.request_id is { } rid ? (int)rid : null, r.request_code, r.task_text, r.description,
        ParseDayN(r.deadline), r.status, (int)r.order_index, ParseIso(r.created_at));

    private static StandupIssue MapIssue(IssueRaw r) => new(
        (int)r.id, (int)r.entry_id, r.issue_text, r.solution_text, r.status,
        (int)r.order_index, ParseIso(r.created_at));

    // SQLite-native shapes (long/string) — typed records mapped at the boundary above.
    private sealed class EntryRaw
    {
        public long id { get; set; }
        public long user_id { get; set; }
        public string work_date { get; set; } = "";
        public string section { get; set; } = "";
        public long? request_id { get; set; }
        public string request_code { get; set; } = "";
        public string task_text { get; set; } = "";
        public string description { get; set; } = "";
        public string? deadline { get; set; }
        public string status { get; set; } = "";
        public long order_index { get; set; }
        public string created_at { get; set; } = "";
    }

    private sealed class IssueRaw
    {
        public long id { get; set; }
        public long entry_id { get; set; }
        public string issue_text { get; set; } = "";
        public string? solution_text { get; set; }
        public string status { get; set; } = "";
        public long order_index { get; set; }
        public string created_at { get; set; } = "";
    }
}
