using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Request data access (REQ-01..03, DATA-03). SQL + Dapper only; one short connection per method.
// No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
public sealed class RequestRepository : IRequestRepository
{
    private readonly IConnectionFactory _factory;

    public RequestRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Request>> SearchAsync(string? term)
    {
        using var c = _factory.Create();
        if (string.IsNullOrWhiteSpace(term))
        {
            var all = await c.QueryAsync<RequestRaw>(
                "SELECT id, request_code, project, created_at FROM Requests ORDER BY request_code;");
            return all.Select(MapRequest).ToList();
        }

        var like = "%" + term.Trim() + "%";
        var rows = await c.QueryAsync<RequestRaw>(
            @"SELECT id, request_code, project, created_at
              FROM Requests
              WHERE request_code LIKE @q OR project LIKE @q
              ORDER BY request_code;",
            new { q = like });
        return rows.Select(MapRequest).ToList();
    }

    public async Task<Request?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<RequestRaw>(
            "SELECT id, request_code, project, created_at FROM Requests WHERE id = @id;", new { id });
        return row is null ? null : MapRequest(row);
    }

    public async Task<Request?> GetByCodeAsync(string requestCode)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<RequestRaw>(
            "SELECT id, request_code, project, created_at FROM Requests WHERE request_code = @code;",
            new { code = requestCode });
        return row is null ? null : MapRequest(row);
    }

    public async Task<int> InsertAsync(Request request)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Requests(request_code, project, created_at)
              VALUES(@RequestCode, @Project, @CreatedAt);
              SELECT last_insert_rowid();",
            new
            {
                request.RequestCode,
                request.Project,
                CreatedAt = request.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            });
    }

    public async Task UpdateAsync(Request request)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Requests SET request_code = @RequestCode, project = @Project WHERE id = @Id;",
            new { request.RequestCode, request.Project, request.Id });
    }

    private static Request MapRequest(RequestRaw r) => new(
        (int)r.id, r.request_code, r.project,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    // SQLite-native shape (long/string) — DateTimeOffset parsed at the boundary above
    // (Dapper's positional-record path does not parse ISO text into DateTimeOffset). See Task 4 note.
    private sealed class RequestRaw
    {
        public long id { get; set; }
        public string request_code { get; set; } = "";
        public string project { get; set; } = "";
        public string created_at { get; set; } = "";
    }
}
