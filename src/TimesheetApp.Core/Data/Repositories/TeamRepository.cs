using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Team data access (TM-03). SQL + Dapper only; one short connection per method (OneDrive-safe, XC-01).
// Soft-delete only (SetActiveAsync) — mirrors UserRepository. Membership lives in UserTeams.
public sealed class TeamRepository : ITeamRepository
{
    private const string Cols = "id, name, is_active, created_at, row_version";

    private readonly IConnectionFactory _factory;

    public TeamRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Team>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TeamRaw>(
            $"SELECT {Cols} FROM Teams ORDER BY is_active DESC, name;");
        return rows.Select(MapTeam).ToList();
    }

    public async Task<IReadOnlyList<Team>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<TeamRaw>(
            $"SELECT {Cols} FROM Teams WHERE is_active = 1 ORDER BY name;");
        return rows.Select(MapTeam).ToList();
    }

    public async Task<Team?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TeamRaw>(
            $"SELECT {Cols} FROM Teams WHERE id = @id;", new { id });
        return row is null ? null : MapTeam(row);
    }

    public async Task<Team?> GetByNameAsync(string name)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<TeamRaw>(
            $"SELECT {Cols} FROM Teams WHERE name = @name;", new { name });
        return row is null ? null : MapTeam(row);
    }

    public async Task<int> InsertAsync(Team team)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Teams(name, is_active, created_at)
              VALUES(@Name, @IsActive, @CreatedAt);
              SELECT last_insert_rowid();",
            new { team.Name, IsActive = team.IsActive ? 1 : 0, CreatedAt = Iso(team.CreatedAt) });
    }

    // BUMP-ONLY: always lands, always bumps, never throws (every existing WPF call site).
    public async Task UpdateNameAsync(int id, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Teams SET name = @n, row_version = row_version + 1 WHERE id = @id;",
            new { n = name, id });
    }

    // CHECK-AND-BUMP. RETURNING hands back the post-write version from the SAME statement that wrote
    // it, and emits zero rows when the WHERE matched nothing — so one round-trip both detects the
    // conflict and supplies the caller's next expectedVersion, with no racy read-back in between.
    public async Task<long> UpdateNameCheckedAsync(int id, string name, long expectedVersion)
    {
        using var c = _factory.Create();
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE Teams SET name = @n, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected
              RETURNING row_version;",
            new { n = name, id, expected = expectedVersion });
        if (newVersion is not null) return newVersion.Value;

        var exists = await c.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM Teams WHERE id = @id;", new { id });
        throw new ConcurrencyConflictException("Teams", id, expectedVersion, deleted: exists == 0);
    }

    // Bump-only: a deactivation doesn't carry a version from the client, and always succeeds.
    public async Task SetActiveAsync(int id, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Teams SET is_active = @a, row_version = row_version + 1 WHERE id = @id;",
            new { a = isActive ? 1 : 0, id });
    }

    // ---- membership (UserTeams) ----

    public async Task<IReadOnlyList<int>> GetTeamIdsForUserAsync(int userId)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<long>(
            "SELECT team_id FROM UserTeams WHERE user_id = @uid ORDER BY team_id;", new { uid = userId });
        return ids.Select(i => (int)i).ToList();
    }

    public async Task<IReadOnlyList<int>> GetUserIdsForTeamAsync(int teamId)
    {
        using var c = _factory.Create();
        var ids = await c.QueryAsync<long>(
            "SELECT user_id FROM UserTeams WHERE team_id = @tid ORDER BY user_id;", new { tid = teamId });
        return ids.Select(i => (int)i).ToList();
    }

    // Replace-all in one tx: clear this team's links, then insert the new set (dedup). UserTeams
    // carries no row_version of its own (pure join table, PK is the (user_id, team_id) pair -- no
    // field to contend over), so a concurrent membership save is gated on the team's own row_version
    // instead. Either way Teams.row_version advances, so anything polling the team can see that its
    // membership changed even without itself participating in the check.
    public Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds) =>
        SetMembersCoreAsync(teamId, userIds, null);

    public Task<long> SetMembersCheckedAsync(int teamId, IReadOnlyList<int> userIds, long expectedVersion) =>
        SetMembersCoreAsync(teamId, userIds, expectedVersion);

    private async Task<long> SetMembersCoreAsync(int teamId, IReadOnlyList<int> userIds, long? expectedVersion)
    {
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();

        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE Teams SET row_version = row_version + 1
              WHERE id = @tid AND (@expected IS NULL OR row_version = @expected)
              RETURNING row_version;",
            new { tid = teamId, expected = expectedVersion }, tx);

        if (newVersion is null && expectedVersion is not null)
        {
            var exists = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM Teams WHERE id = @tid;", new { tid = teamId }, tx);
            tx.Rollback();
            throw new ConcurrencyConflictException("Teams", teamId, expectedVersion, deleted: exists == 0);
        }

        await c.ExecuteAsync("DELETE FROM UserTeams WHERE team_id = @tid;", new { tid = teamId }, tx);
        foreach (var userId in userIds.Distinct())
        {
            await c.ExecuteAsync(
                "INSERT INTO UserTeams(user_id, team_id) VALUES(@uid, @tid);",
                new { uid = userId, tid = teamId }, tx);
        }

        tx.Commit();
        return newVersion ?? 0;
    }

    public async Task AddMemberAsync(int userId, int teamId)
    {
        using var c = _factory.Create();
        // Idempotent: PK(user_id, team_id) means a re-add is a no-op.
        await c.ExecuteAsync(
            "INSERT OR IGNORE INTO UserTeams(user_id, team_id) VALUES(@uid, @tid);",
            new { uid = userId, tid = teamId });
    }

    private static string Iso(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static Team MapTeam(TeamRaw r) => new(
        (int)r.id, r.name, r.is_active != 0,
        DateTimeOffset.Parse(r.created_at, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        r.row_version);

    // SQLite-native shape (long/string) — narrowed at the boundary above.
    private sealed class TeamRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public long is_active { get; set; }
        public string created_at { get; set; } = "";
        public long row_version { get; set; }
    }
}
