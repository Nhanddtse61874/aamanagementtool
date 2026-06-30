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
        "deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id, team_id";

    private readonly IConnectionFactory _factory;

    public BacklogRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Backlog>> SearchAsync(string? term, IReadOnlyList<int>? teamIds = null)
    {
        using var c = _factory.Create();
        // teamIds == null => no team filter (all teams). A non-null list (incl. empty) filters via
        // `team_id IN @teamIds`; an empty list matches nothing (teamId 0 == empty, R6). @noTeam short-
        // circuits the predicate when null so existing callers keep returning every backlog.
        var noTeam = teamIds is null;
        if (string.IsNullOrWhiteSpace(term))
        {
            var all = await c.QueryAsync<BacklogRaw>(
                $@"SELECT {Cols} FROM Backlogs
                   WHERE (@noTeam OR team_id IN @teamIds)
                   ORDER BY backlog_code;",
                new { noTeam, teamIds = teamIds ?? Array.Empty<int>() });
            return all.Select(MapBacklog).ToList();
        }

        var like = "%" + term.Trim() + "%";
        var rows = await c.QueryAsync<BacklogRaw>(
            $@"SELECT {Cols} FROM Backlogs
               WHERE (backlog_code LIKE @q OR project LIKE @q)
                 AND (@noTeam OR team_id IN @teamIds)
               ORDER BY backlog_code;",
            new { q = like, noTeam, teamIds = teamIds ?? Array.Empty<int>() });
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

    public async Task<Backlog?> GetDefaultForTeamAsync(int teamId)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<BacklogRaw>(
            $"SELECT {Cols} FROM Backlogs WHERE backlog_code = 'DEFAULT' AND team_id = @t;",
            new { t = teamId });
        return row is null ? null : MapBacklog(row);
    }

    public async Task<int> InsertAsync(Backlog backlog)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Backlogs(backlog_code, project, created_at, start_date, end_date, period_month, type, assignee_user_id,
                deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id, team_id)
              VALUES(@BacklogCode, @Project, @CreatedAt, @StartDate, @EndDate, @PeriodMonth, @Type, @AssigneeUserId,
                @DeadlineInternal, @DeadlineExternal, @RoughEstimateHours, @OfficialEstimateHours, @ProgressPercent, @Note, @PcaContactId, @TeamId);
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
                backlog.TeamId,
            });
    }

    public async Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null,
        string? auditNote = null)
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
                progress_percent = @ProgressPercent, note = @Note, pca_contact_id = @PcaContactId,
                team_id = @TeamId
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
                backlog.TeamId,
                backlog.Id,
            });

        if (before is null) return;

        var now = Iso(DateTimeOffset.UtcNow);

        // v9 (B2): note is written only for deadline fields; all other fields receive NULL.
        async Task LogAsync(string field, string? oldV, string? newV, string? note = null)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
            await c.ExecuteAsync(
                @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at, note)
                  VALUES(@bid, @field, @old, @new, @uid, @uname, @at, @note);",
                new { bid = backlog.Id, field, old = oldV, @new = newV,
                      uid = changedByUserId, uname = changedByName, at = now, note });
        }

        // Audit the four v2 fields; one history row per actually-changed field.
        await LogAsync("start_date", before.start_date, Day(backlog.StartDate));
        await LogAsync("end_date", before.end_date, Day(backlog.EndDate));
        await LogAsync("period_month", before.period_month, backlog.PeriodMonth);
        await LogAsync("type", before.type, backlog.Type);

        // v4: audit the assignee by NAME (readable history), gated on the user-id actually changing.
        if (before.assignee_user_id != backlog.AssigneeUserId)
        {
            async Task<string?> NameOfAsync(int? uid) => uid is null ? null
                : await c.QuerySingleOrDefaultAsync<string?>("SELECT name FROM Users WHERE id = @id;", new { id = uid });
            await LogAsync("assignee",
                await NameOfAsync(before.assignee_user_id is { } b ? (int)b : null),
                await NameOfAsync(backlog.AssigneeUserId));
        }

        // v7: audit the new tracking fields; one history row per actually-changed field.
        // v9 (B2): deadline rows carry auditNote; all other fields leave note NULL.
        await LogAsync("deadline_internal", before.deadline_internal, Day(backlog.DeadlineInternal), note: auditNote);
        await LogAsync("deadline_external", before.deadline_external, Day(backlog.DeadlineExternal), note: auditNote);
        await LogAsync("rough_estimate_hours", RealStr(before.rough_estimate_hours), RealStr(Real(backlog.RoughEstimateHours)));
        await LogAsync("official_estimate_hours", RealStr(before.official_estimate_hours), RealStr(Real(backlog.OfficialEstimateHours)));
        await LogAsync("progress_percent",
            before.progress_percent?.ToString(CultureInfo.InvariantCulture),
            backlog.ProgressPercent?.ToString(CultureInfo.InvariantCulture));
        await LogAsync("note", before.note, backlog.Note);

        // v7: audit the PCA contact by NAME, gated on the id actually changing (mirrors assignee).
        if (before.pca_contact_id != backlog.PcaContactId)
        {
            async Task<string?> PcaNameOfAsync(int? id) => id is null ? null
                : await c.QuerySingleOrDefaultAsync<string?>("SELECT name FROM PcaContacts WHERE id = @id;", new { id });
            await LogAsync("pca_contact",
                await PcaNameOfAsync(before.pca_contact_id is { } p ? (int)p : null),
                await PcaNameOfAsync(backlog.PcaContactId));
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

    public async Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null)
    {
        // Replace-all in one tx: capture old set for audit diff, then clear + insert new set (dedup).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();

        // v9 (B1): read old tag ids before the replace so we can diff for the audit row.
        var oldIds = (await c.QueryAsync<long>(
            "SELECT tag_id FROM BacklogTags WHERE backlog_id = @bid ORDER BY tag_id;",
            new { bid = backlogId }, tx)).Select(i => (int)i).ToHashSet();
        var newIds = tagIds.Distinct().ToHashSet();

        await c.ExecuteAsync("DELETE FROM BacklogTags WHERE backlog_id = @bid;", new { bid = backlogId }, tx);
        foreach (var tagId in newIds)
        {
            await c.ExecuteAsync(
                "INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@bid, @tid);",
                new { bid = backlogId, tid = tagId }, tx);
        }

        // v9 (B1): write exactly one 'tags' audit row when the set actually changed.
        if (!oldIds.SetEquals(newIds))
        {
            var now = Iso(DateTimeOffset.UtcNow);
            await c.ExecuteAsync(
                @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value,
                    changed_by_user_id, changed_by_name, changed_at)
                  VALUES(@bid, 'tags', @old, @new, @uid, @uname, @at);",
                new
                {
                    bid = backlogId,
                    old = string.Join(",", oldIds.OrderBy(x => x)),
                    @new = string.Join(",", newIds.OrderBy(x => x)),
                    uid = changedByUserId,
                    uname = changedByName,
                    at = now,
                }, tx);
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
                     changed_by_user_id, changed_by_name, changed_at, note
              FROM BacklogAudit WHERE backlog_id = @bid ORDER BY id DESC;",
            new { bid = backlogId });
        return rows.Select(a => new BacklogAuditEntry(
            (int)a.id, (int)a.backlog_id, a.field, a.old_value, a.new_value,
            a.changed_by_user_id is { } uid ? (int)uid : null, a.changed_by_name,
            DateTimeOffset.Parse(a.changed_at, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            a.note)).ToList();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<BacklogAuditEntry>>> GetAuditForBacklogsAsync(IReadOnlyList<int> backlogIds)
    {
        using var c = _factory.Create();
        // One IN-query for all backlogs (avoids N+1 vs. calling GetAuditAsync per id), grouped by
        // backlog_id. ORDER BY id DESC keeps each group newest-first like the single-backlog query;
        // includes the v9 note column and maps each row exactly like GetAuditAsync.
        var rows = await c.QueryAsync<AuditRaw>(
            @"SELECT id, backlog_id, field, old_value, new_value,
                     changed_by_user_id, changed_by_name, changed_at, note
              FROM BacklogAudit WHERE backlog_id IN @ids ORDER BY id DESC;",
            new { ids = backlogIds });
        return rows
            .GroupBy(a => (int)a.backlog_id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BacklogAuditEntry>)g.Select(a => new BacklogAuditEntry(
                (int)a.id, (int)a.backlog_id, a.field, a.old_value, a.new_value,
                a.changed_by_user_id is { } uid ? (int)uid : null, a.changed_by_name,
                DateTimeOffset.Parse(a.changed_at, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                a.note)).ToList());
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
        r.pca_contact_id is { } pca ? (int)pca : null,
        r.team_id is { } team ? (int)team : null);

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
        // v8
        public long? team_id { get; set; }
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
        public string? note { get; set; }   // v9 (B2): deadline-change reason
    }
}
