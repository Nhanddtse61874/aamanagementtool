using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Request data access (REQ-01..03, DATA-03). SQL + Dapper only; one short connection per method.
// No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
// v2: start_date / end_date / period_month / status columns + RequestAudit change history.
public sealed class RequestRepository : IRequestRepository
{
    private const string Cols = "id, request_code, project, created_at, start_date, end_date, period_month, status";

    private readonly IConnectionFactory _factory;

    public RequestRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Request>> SearchAsync(string? term)
    {
        using var c = _factory.Create();
        if (string.IsNullOrWhiteSpace(term))
        {
            var all = await c.QueryAsync<RequestRaw>(
                $"SELECT {Cols} FROM Requests ORDER BY request_code;");
            return all.Select(MapRequest).ToList();
        }

        var like = "%" + term.Trim() + "%";
        var rows = await c.QueryAsync<RequestRaw>(
            $@"SELECT {Cols} FROM Requests
               WHERE request_code LIKE @q OR project LIKE @q
               ORDER BY request_code;",
            new { q = like });
        return rows.Select(MapRequest).ToList();
    }

    public async Task<Request?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<RequestRaw>(
            $"SELECT {Cols} FROM Requests WHERE id = @id;", new { id });
        return row is null ? null : MapRequest(row);
    }

    public async Task<Request?> GetByCodeAsync(string requestCode)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<RequestRaw>(
            $"SELECT {Cols} FROM Requests WHERE request_code = @code;",
            new { code = requestCode });
        return row is null ? null : MapRequest(row);
    }

    public async Task<int> InsertAsync(Request request)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Requests(request_code, project, created_at, start_date, end_date, period_month, status)
              VALUES(@RequestCode, @Project, @CreatedAt, @StartDate, @EndDate, @PeriodMonth, @Status);
              SELECT last_insert_rowid();",
            new
            {
                request.RequestCode,
                request.Project,
                CreatedAt = Iso(request.CreatedAt),
                StartDate = Day(request.StartDate),
                EndDate = Day(request.EndDate),
                request.PeriodMonth,
                request.Status,
            });
    }

    public async Task UpdateAsync(Request request, int? changedByUserId = null, string? changedByName = null)
    {
        using var c = _factory.Create();

        // Read the pre-update row so audited field changes (start/end/month/status) can be diffed.
        var before = await c.QuerySingleOrDefaultAsync<RequestRaw>(
            $"SELECT {Cols} FROM Requests WHERE id = @id;", new { id = request.Id });

        await c.ExecuteAsync(
            @"UPDATE Requests SET request_code = @RequestCode, project = @Project,
                start_date = @StartDate, end_date = @EndDate, period_month = @PeriodMonth, status = @Status
              WHERE id = @Id;",
            new
            {
                request.RequestCode,
                request.Project,
                StartDate = Day(request.StartDate),
                EndDate = Day(request.EndDate),
                request.PeriodMonth,
                request.Status,
                request.Id,
            });

        if (before is null) return;

        // Audit the four v2 fields; one history row per actually-changed field.
        var changes = new (string Field, string? Old, string? New)[]
        {
            ("start_date", before.start_date, Day(request.StartDate)),
            ("end_date", before.end_date, Day(request.EndDate)),
            ("period_month", before.period_month, request.PeriodMonth),
            ("status", before.status, request.Status),
        };

        var now = Iso(DateTimeOffset.UtcNow);
        foreach (var (field, oldV, newV) in changes)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) continue;
            await c.ExecuteAsync(
                @"INSERT INTO RequestAudit(request_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@rid, @field, @old, @new, @uid, @uname, @at);",
                new { rid = request.Id, field, old = oldV, @new = newV,
                      uid = changedByUserId, uname = changedByName, at = now });
        }
    }

    public async Task<IReadOnlyList<RequestAuditEntry>> GetAuditAsync(int requestId)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<AuditRaw>(
            @"SELECT id, request_id, field, old_value, new_value,
                     changed_by_user_id, changed_by_name, changed_at
              FROM RequestAudit WHERE request_id = @rid ORDER BY id DESC;",
            new { rid = requestId });
        return rows.Select(a => new RequestAuditEntry(
            (int)a.id, (int)a.request_id, a.field, a.old_value, a.new_value,
            a.changed_by_user_id is { } uid ? (int)uid : null, a.changed_by_name,
            DateTimeOffset.Parse(a.changed_at, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))).ToList();
    }

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Request MapRequest(RequestRaw r) => new(
        (int)r.id, r.request_code, r.project,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        ParseDay(r.start_date), ParseDay(r.end_date), r.period_month, r.status);

    private static DateOnly? ParseDay(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
            : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // SQLite-native shape (long/string) — DateTimeOffset/DateOnly parsed at the boundary above.
    private sealed class RequestRaw
    {
        public long id { get; set; }
        public string request_code { get; set; } = "";
        public string project { get; set; } = "";
        public string created_at { get; set; } = "";
        public string? start_date { get; set; }
        public string? end_date { get; set; }
        public string? period_month { get; set; }
        public string? status { get; set; }
    }

    private sealed class AuditRaw
    {
        public long id { get; set; }
        public long request_id { get; set; }
        public string field { get; set; } = "";
        public string? old_value { get; set; }
        public string? new_value { get; set; }
        public long? changed_by_user_id { get; set; }
        public string? changed_by_name { get; set; }
        public string changed_at { get; set; } = "";
    }
}
