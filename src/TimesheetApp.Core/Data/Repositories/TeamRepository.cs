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

    // Check-and-bump when the caller supplies expectedVersion (throws on a stale version or a
    // deleted row); bump-only when it doesn't, so callers not yet wired to carry a version keep
    // compiling and behaving as before (unconditional success), just with row_version now advancing.
    public async Task UpdateNameAsync(int id, string name, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        if (expectedVersion is null)
        {
            await c.ExecuteAsync(
                "UPDATE Teams SET name = @n, row_version = row_version + 1 WHERE id = @id;",
                new { n = name, id });
            return;
        }

        var rows = await c.ExecuteAsync(
            @"UPDATE Teams SET name = @n, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected;",
            new { n = name, id, expected = expectedVersion.Value });
        if (rows > 0) return;

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
    // field to contend over), so a concurrent membership save is gated on the team's own
    // row_version instead: check-and-bump when expectedVersion is supplied, bump-only when it
    // isn't. Either way Teams.row_version advances, so anything polling the team can see that its
    // membership changed even without itself participating in the check.
    public async Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds, long? expectedVersion = null)
    {
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();

        if (expectedVersion is null)
        {
            await c.ExecuteAsync(
                "UPDATE Teams SET row_version = row_version + 1 WHERE id = @tid;",
                new { tid = teamId }, tx);
        }
        else
        {
            var bumped = await c.ExecuteAsync(
                @"UPDATE Teams SET row_version = row_version + 1
                  WHERE id = @tid AND row_version = @expected;",
                new { tid = teamId, expected = expectedVersion.Value }, tx);
            if (bumped == 0)
            {
                var exists = await c.ExecuteScalarAsync<long>(
                    "SELECT COUNT(1) FROM Teams WHERE id = @tid;", new { tid = teamId }, tx);
                tx.Rollback();
                throw new ConcurrencyConflictException("Teams", teamId, expectedVersion, deleted: exists == 0);
            }
        }

        await c.ExecuteAsync("DELETE FROM UserTeams WHERE team_id = @tid;", new { tid = teamId }, tx);
        foreach (var userId in userIds.Distinct())
        {
            await c.ExecuteAsync(
                "INSERT INTO UserTeams(user_id, team_id) VALUES(@uid, @tid);",
                new { uid = userId, tid = teamId }, tx);
        }

        tx.Commit();
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
