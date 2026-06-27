using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Backlog data access (formerly RequestRepository). SQL + Dapper only; one short connection per method.
// No SetActiveAsync — Backlogs are NOT soft-deletable (decision 4).
// v2: start_date / end_date / period_month / type columns + BacklogAudit change history.
public sealed class BacklogRepository : IBacklogRepository
{
    private const string Cols = "id, backlog_code, project, created_at, start_date, end_date, period_month, type, assignee_user_id, " +
        "deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id";

    private readonly IConnectionFactory _factory;

    public BacklogRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Backlog>> SearchAsync(string? term)
    {
        using var c = _factory.Create();
        if (string.IsNullOrWhiteSpace(term))
        {
            var all = await c.QueryAsync<BacklogRaw>(
                $"SELECT {Cols} FROM Backlogs ORDER BY backlog_code;");
            return all.Select(MapBacklog).ToList();
        }

        var like = "%" + term.Trim() + "%";
        var rows = await c.QueryAsync<BacklogRaw>(
            $@"SELECT {Cols} FROM Backlogs
               WHERE backlog_code LIKE @q OR project LIKE @q
               ORDER BY backlog_code;",
            new { q = like });
        return rows.Select(MapBacklog).ToList();
    }

    public async Task<Backlog?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<BacklogRaw>(
            $"SELECT {Cols} FROM Backlogs WHERE id = @id;", new { id });
        return row is null ? null : MapBacklog(row);
    }

    public async Task<Backlog?> GetByCodeAsync(string backlogCode)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<BacklogRaw>(
            $"SELECT {Cols} FROM Backlogs WHERE backlog_code = @code;",
            new { code = backlogCode });
        return row is null ? null : MapBacklog(row);
    }

    public async Task<int> InsertAsync(Backlog backlog)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Backlogs(backlog_code, project, created_at, start_date, end_date, period_month, type, assignee_user_id,
                deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id)
              VALUES(@BacklogCode, @Project, @CreatedAt, @StartDate, @EndDate, @PeriodMonth, @Type, @AssigneeUserId,
                @DeadlineInternal, @DeadlineExternal, @RoughEstimateHours, @OfficialEstimateHours, @ProgressPercent, @Note, @PcaContactId);
              SELECT last_insert_rowid();",
            new
            {
                backlog.BacklogCode,
                backlog.Project,
                CreatedAt = Iso(backlog.CreatedAt),
                StartDate = Day(backlog.StartDate),
                EndDate = Day(backlog.EndDate),
                backlog.PeriodMonth,
                backlog.Type,
                backlog.AssigneeUserId,
                DeadlineInternal = Day(backlog.DeadlineInternal),
                DeadlineExternal = Day(backlog.DeadlineExternal),
                RoughEstimateHours = Real(backlog.RoughEstimateHours),
                OfficialEstimateHours = Real(backlog.OfficialEstimateHours),
                backlog.ProgressPercent,
                backlog.Note,
                backlog.PcaContactId,
            });
    }

    public async Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null)
    {
        using var c = _factory.Create();

        // Read the pre-update row so audited field changes can be diffed.
        var before = await c.QuerySingleOrDefaultAsync<BacklogRaw>(
            $"SELECT {Cols} FROM Backlogs WHERE id = @id;", new { id = backlog.Id });

        await c.ExecuteAsync(
            @"UPDATE Backlogs SET backlog_code = @BacklogCode, project = @Project,
                start_date = @StartDate, end_date = @EndDate, period_month = @PeriodMonth, type = @Type,
                assignee_user_id = @AssigneeUserId,
                deadline_internal = @DeadlineInternal, deadline_external = @DeadlineExternal,
                rough_estimate_hours = @RoughEstimateHours, official_estimate_hours = @OfficialEstimateHours,
                progress_percent = @ProgressPercent, note = @Note, pca_contact_id = @PcaContactId
              WHERE id = @Id;",
            new
            {
                backlog.BacklogCode,
                backlog.Project,
                StartDate = Day(backlog.StartDate),
                EndDate = Day(backlog.EndDate),
                backlog.PeriodMonth,
                backlog.Type,
                backlog.AssigneeUserId,
                DeadlineInternal = Day(backlog.DeadlineInternal),
                DeadlineExternal = Day(backlog.DeadlineExternal),
                RoughEstimateHours = Real(backlog.RoughEstimateHours),
                OfficialEstimateHours = Real(backlog.OfficialEstimateHours),
                backlog.ProgressPercent,
                backlog.Note,
                backlog.PcaContactId,
                backlog.Id,
            });

        if (before is null) return;

        var now = Iso(DateTimeOffset.UtcNow);

        async Task LogAsync(string field, string? oldV, string? newV)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
            await c.ExecuteAsync(
                @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@bid, @field, @old, @new, @uid, @uname, @at);",
                new { bid = backlog.Id, field, old = oldV, @new = newV,
                      uid = changedByUserId, uname = changedByName, at = now });
        }

        // Audit the four v2 fields; one history row per actually-changed field.
        await LogAsync("start_date", before.start_date, Day(backlog.StartDate));
        await LogAsync("end_date", before.end_date, Day(backlog.EndDate));
        await LogAsync("period_month", before.period_month, backlog.PeriodMonth);
        await LogAsync("type", before.type, backlog.Type);

        // v4: audit the assignee by NAME (readable history), gated on the user-id actually changing.
        if (before.assignee_user_id != backlog.AssigneeUserId)
        {
            string? NameOf(int? uid) => uid is null ? null
                : c.QuerySingleOrDefault<string?>("SELECT name FROM Users WHERE id = @id;", new { id = uid });
            await LogAsync("assignee",
                NameOf(before.assignee_user_id is { } b ? (int)b : null), NameOf(backlog.AssigneeUserId));
        }

        // v7: audit the new tracking fields; one history row per actually-changed field.
        await LogAsync("deadline_internal", before.deadline_internal, Day(backlog.DeadlineInternal));
        await LogAsync("deadline_external", before.deadline_external, Day(backlog.DeadlineExternal));
        await LogAsync("rough_estimate_hours", RealStr(before.rough_estimate_hours), RealStr(Real(backlog.RoughEstimateHours)));
        await LogAsync("official_estimate_hours", RealStr(before.official_estimate_hours), RealStr(Real(backlog.OfficialEstimateHours)));
        await LogAsync("progress_percent",
            before.progress_percent?.ToString(CultureInfo.InvariantCulture),
            backlog.ProgressPercent?.ToString(CultureInfo.InvariantCulture));
        await LogAsync("note", before.note, backlog.Note);

        // v7: audit the PCA contact by NAME, gated on the id actually changing (mirrors assignee).
        if (before.pca_contact_id != backlog.PcaContactId)
        {
            string? PcaNameOf(int? id) => id is null ? null
                : c.QuerySingleOrDefault<string?>("SELECT name FROM PcaContacts WHERE id = @id;", new { id });
            await LogAsync("pca_contact",
                PcaNameOf(before.pca_contact_id is { } p ? (int)p : null), PcaNameOf(backlog.PcaContactId));
        }
    }

    // ---- v7 tag links (TAG-02) -----------------------------------------------------------

    public async Task<IReadOnlyList<int>> GetTagIdsAsync(int backlogId)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<long>(
            "SELECT tag_id FROM BacklogTags WHERE backlog_id = @bid ORDER BY tag_id;", new { bid = backlogId });
        return ids.Select(i => (int)i).ToList();
    }

    public async Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds)
    {
        // Replace-all in one tx: clear the existing links, then insert the new set (dedup).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM BacklogTags WHERE backlog_id = @bid;", new { bid = backlogId }, tx);
        foreach (var tagId in tagIds.Distinct())
        {
            await c.ExecuteAsync(
                "INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@bid, @tid);",
                new { bid = backlogId, tid = tagId }, tx);
        }

        tx.Commit();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<(long backlog_id, long tag_id)>(
            "SELECT backlog_id, tag_id FROM BacklogTags ORDER BY backlog_id, tag_id;");
        return rows
            .GroupBy(r => (int)r.backlog_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(r => (int)r.tag_id).ToList());
    }

    public async Task<IReadOnlyList<BacklogAuditEntry>> GetAuditAsync(int backlogId)
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<AuditRaw>(
            @"SELECT id, backlog_id, field, old_value, new_value,
                     changed_by_user_id, changed_by_name, changed_at
              FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id DESC;",
            new { bid = backlogId });
        return rows.Select(a => new BacklogAuditEntry(
            (int)a.id, (int)a.backlog_id, a.field, a.old_value, a.new_value,
            a.changed_by_user_id is { } uid ? (int)uid : null, a.changed_by_name,
            DateTimeOffset.Parse(a.changed_at, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))).ToList();
    }

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // REAL boundary: decimal -> double on write (column is REAL), mirroring the hours mapping (XC-01).
    private static double? Real(decimal? d) => d is null ? null : (double)d.Value;

    // Stable string form of a REAL value for the audit field-diff (avoids float formatting drift).
    private static string? RealStr(double? d) => d?.ToString(CultureInfo.InvariantCulture);

    private static Backlog MapBacklog(BacklogRaw r) => new(
        (int)r.id, r.backlog_code, r.project,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        ParseDay(r.start_date), ParseDay(r.end_date), r.period_month, r.type,
        r.assignee_user_id is { } a ? (int)a : null,
        ParseDay(r.deadline_internal), ParseDay(r.deadline_external),
        r.rough_estimate_hours is { } rough ? (decimal)rough : null,
        r.official_estimate_hours is { } off ? (decimal)off : null,
        r.progress_percent is { } pp ? (int)pp : null,
        r.note,
        r.pca_contact_id is { } pca ? (int)pca : null);

    private static DateOnly? ParseDay(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
            : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // SQLite-native shape (long/string) — DateTimeOffset/DateOnly parsed at the boundary above.
    private sealed class BacklogRaw
    {
        public long id { get; set; }
        public string backlog_code { get; set; } = "";
        public string project { get; set; } = "";
        public string created_at { get; set; } = "";
        public string? start_date { get; set; }
        public string? end_date { get; set; }
        public string? period_month { get; set; }
        public string? type { get; set; }
        public long? assignee_user_id { get; set; }
        // v7
        public string? deadline_internal { get; set; }
        public string? deadline_external { get; set; }
        public double? rough_estimate_hours { get; set; }
        public double? official_estimate_hours { get; set; }
        public long? progress_percent { get; set; }
        public string? note { get; set; }
        public long? pca_contact_id { get; set; }
    }

    private sealed class AuditRaw
    {
        public long id { get; set; }
        public long backlog_id { get; set; }
        public string field { get; set; } = "";
        public string? old_value { get; set; }
        public string? new_value { get; set; }
        public long? changed_by_user_id { get; set; }
        public string? changed_by_name { get; set; }
        public string changed_at { get; set; } = "";
    }
}
