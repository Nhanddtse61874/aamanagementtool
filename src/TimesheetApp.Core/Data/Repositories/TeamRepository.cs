using System.Globalization;
using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Team data access (TM-03). SQL + Dapper only; one short connection per method (OneDrive-safe, XC-01).
// Soft-delete only (SetActiveAsync) — mirrors UserRepository. Membership lives in UserTeams.
public sealed class TeamRepository : ITeamRepository
{
    private const string Cols = "id, name, is_active, created_at";

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

    public async Task UpdateNameAsync(int id, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync("UPDATE Teams SET name = @n WHERE id = @id;", new { n = name, id });
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Teams SET is_active = @a WHERE id = @id;",
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

    public async Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds)
    {
        // Replace-all in one tx: clear this team's links, then insert the new set (dedup).
        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
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
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

    // SQLite-native shape (long/string) — narrowed at the boundary above.
    private sealed class TeamRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public long is_active { get; set; }
        public string created_at { get; set; } = "";
    }
}
