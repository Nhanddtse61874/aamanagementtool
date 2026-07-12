using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Daily Report (Standup) data access — DR-02..04, DR-09. Dates stored as TEXT (ISO, culture-neutral);
// DateOnly/DateTimeOffset parsed at the boundary. One short connection per method (OneDrive-safe policy).
public sealed class StandupRepository : IStandupRepository
{
    private const string EntryCols =
        "id, user_id, work_date, section, backlog_id, backlog_code, task_text, description, deadline, status, order_index, created_at, team_id";
    private const string IssueCols =
        "id, entry_id, issue_text, solution_text, status, order_index, created_at";

    private readonly IConnectionFactory _factory;

    public StandupRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesAsync(int userId, DateOnly workDate, int? teamId = null)
    {
        using var c = _factory.Create();
        // @noTeam short-circuits the team filter when teamId is null (preserves existing tests, R6);
        // teamId 0 yields nothing (empty).
        var noTeam = teamId is null;
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE user_id = @uid AND work_date = @day
                 AND (@noTeam OR team_id = @teamId)
               ORDER BY section, order_index, id;",
            new { uid = userId, day = Day(workDate), noTeam, teamId = teamId ?? 0 });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesForDayAsync(DateOnly workDate, IReadOnlyList<int>? teamIds = null)
    {
        using var c = _factory.Create();
        var noTeam = teamIds is null;
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE work_date = @day
                 AND (@noTeam OR team_id IN @teamIds)
               ORDER BY user_id, section, order_index, id;",
            new { day = Day(workDate), noTeam, teamIds = teamIds ?? Array.Empty<int>() });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<IReadOnlyList<StandupEntry>> GetEntriesForRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<int>? teamIds = null)
    {
        using var c = _factory.Create();
        var noTeam = teamIds is null;
        var rows = await c.QueryAsync<EntryRaw>(
            $@"SELECT {EntryCols} FROM StandupEntries
               WHERE work_date >= @from AND work_date <= @to
                 AND (@noTeam OR team_id IN @teamIds)
               ORDER BY work_date, user_id, section, order_index, id;",
            new { from = Day(from), to = Day(to), noTeam, teamIds = teamIds ?? Array.Empty<int>() });
        return rows.Select(MapEntry).ToList();
    }

    public async Task<StandupEntry?> GetEntryAsync(int entryId)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<EntryRaw>(
            $"SELECT {EntryCols} FROM StandupEntries WHERE id = @id;", new { id = entryId });
        return row is null ? null : MapEntry(row);
    }

    public async Task<int> InsertEntryAsync(StandupEntry e)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO StandupEntries(user_id, work_date, section, backlog_id, backlog_code,
                  task_text, description, deadline, status, order_index, created_at, team_id)
              VALUES(@UserId, @WorkDate, @Section, @BacklogId, @BacklogCode,
                  @TaskText, @Description, @Deadline, @Status, @OrderIndex, @CreatedAt, @TeamId);
              SELECT last_insert_rowid();",
            new
            {
                e.UserId,
                WorkDate = Day(e.WorkDate),
                e.Section,
                e.BacklogId,
                e.BacklogCode,
                e.TaskText,
                e.Description,
                Deadline = DayN(e.Deadline),
                e.Status,
                e.OrderIndex,
                CreatedAt = Iso(e.CreatedAt),
                e.TeamId,
            });
    }

    public async Task UpdateEntryAsync(StandupEntry e)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            @"UPDATE StandupEntries SET
                  section = @Section, backlog_id = @BacklogId, backlog_code = @BacklogCode,
                  task_text = @TaskText, description = @Description, deadline = @Deadline,
                  status = @Status, order_index = @OrderIndex, team_id = @TeamId
              WHERE id = @Id;",
            new
            {
                e.Section,
                e.BacklogId,
                e.BacklogCode,
                e.TaskText,
                e.Description,
                Deadline = DayN(e.Deadline),
                e.Status,
                e.OrderIndex,
                e.TeamId,
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
        r.backlog_id is { } rid ? (int)rid : null, r.backlog_code, r.task_text, r.description,
        ParseDayN(r.deadline), r.status, (int)r.order_index, ParseIso(r.created_at),
        r.team_id is { } tid ? (int)tid : null);

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
        public long? backlog_id { get; set; }
        public string backlog_code { get; set; } = "";
        public string task_text { get; set; } = "";
        public string description { get; set; } = "";
        public string? deadline { get; set; }
        public string status { get; set; } = "";
        public long order_index { get; set; }
        public string created_at { get; set; } = "";
        public long? team_id { get; set; }
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
