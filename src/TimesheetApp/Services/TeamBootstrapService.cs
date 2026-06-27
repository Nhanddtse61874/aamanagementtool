using System.Data;
using Dapper;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Post-init bootstrap (architecture §1d, spec §3). On an EXISTING DB with business data it
/// creates team "Architect Improvement", backfills team_id on every legacy backlog/standup row and
/// joins every user to it, then sets it active. On a FRESH DB it creates an active "My Team". Backup
/// first (XC-10); bulk writes run in their own transaction, OUTSIDE the initializer's transaction.
/// Idempotent + self-healing: when a team already exists it still re-runs the (no-op-when-done)
/// backfill so a migration interrupted between team-create and backfill completes on next startup
/// (I2). On a fresh DB it also repoints the initializer's seeded global DEFAULT to the new team so a
/// later per-team DefaultTaskSync doesn't strand a team_id=NULL DEFAULT (I1, R3).</summary>
public sealed class TeamBootstrapService : ITeamBootstrapService
{
    private const string MigratedTeamName = "Architect Improvement";
    private const string FreshTeamName = "My Team";

    private readonly ITeamRepository _teams;
    private readonly IConnectionFactory _factory;
    private readonly IDbBackupHelper _backup;
    private readonly IClock _clock;
    private readonly IAppConfig _config;

    public TeamBootstrapService(
        ITeamRepository teams, IConnectionFactory factory,
        IDbBackupHelper backup, IClock clock, IAppConfig config)
    {
        _teams = teams;
        _factory = factory;
        _backup = backup;
        _clock = clock;
        _config = config;
    }

    public async Task EnsureBootstrappedAsync()
    {
        // A team already exists => bootstrap was at least started. It may have crashed between
        // team-create and the backfill (separate transactions), stranding legacy team_id=NULL rows
        // forever (I2). Resolve the bootstrap team and ALWAYS re-run the idempotent backfill so an
        // interrupted migration completes; a fully-done DB makes every statement a no-op. (R3)
        var existing = (await _teams.GetAllAsync())
            .OrderBy(t => t.Id)
            .FirstOrDefault();
        if (existing is not null)
        {
            await BackfillTeamAsync(existing.Id, backupFirst: false);
            return;
        }

        if (await HasBusinessDataAsync())
            await MigrateExistingDbAsync();
        else
            await FirstRunAsync();
    }

    // Existing v8 DB carrying legacy team-less data: migrate everything to "Architect Improvement".
    private async Task MigrateExistingDbAsync()
    {
        var teamId = await EnsureTeamAsync(MigratedTeamName);
        await BackfillTeamAsync(teamId, backupFirst: true);
        _config.SetActiveTeamId(teamId);
    }

    // Fresh DB (no business data): create the renamable default team and make it active. The
    // initializer already seeded a global DEFAULT (team_id NULL); repoint it to the new team so a
    // later per-team DefaultTaskSync doesn't create a SECOND DEFAULT and strand the NULL one (I1).
    private async Task FirstRunAsync()
    {
        var teamId = await EnsureTeamAsync(FreshTeamName);
        await BackfillTeamAsync(teamId, backupFirst: false);
        _config.SetActiveTeamId(teamId);
    }

    // Idempotent backfill: repoint every team-less backlog/standup row to the bootstrap team and
    // join every user. Re-running it is a no-op (WHERE team_id IS NULL / INSERT OR IGNORE), which is
    // what makes an interrupted migration self-heal on the next startup (I2). Bulk writes run in one
    // transaction, OUTSIDE the initializer's transaction.
    private async Task BackfillTeamAsync(int teamId, bool backupFirst)
    {
        if (teamId <= 0) return;

        if (backupFirst)
            await _backup.BackupAsync(); // XC-10: bulk write -> backup first (no-op when DB absent).

        using var c = _factory.Create();
        using var tx = c.BeginTransaction();
        // Repoint every team-less backlog (incl. the seeded global DEFAULT -> becomes this team's DEFAULT).
        await c.ExecuteAsync(
            "UPDATE Backlogs SET team_id = @t WHERE team_id IS NULL;", new { t = teamId }, tx);
        await c.ExecuteAsync(
            "UPDATE StandupEntries SET team_id = @t WHERE team_id IS NULL;", new { t = teamId }, tx);
        // Every existing user becomes a member (idempotent via PK).
        await c.ExecuteAsync(
            "INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;",
            new { t = teamId }, tx);
        tx.Commit();
    }

    // Idempotent create-by-name (GetByNameAsync guard); returns the team id.
    private async Task<int> EnsureTeamAsync(string name)
    {
        var existing = await _teams.GetByNameAsync(name);
        if (existing is not null) return existing.Id;
        return await _teams.InsertAsync(new Team(0, name, true, _clock.UtcNow));
    }

    // Business data = any non-DEFAULT backlog, or any standup entry, or any user.
    private async Task<bool> HasBusinessDataAsync()
    {
        using var c = _factory.Create();
        var backlogs = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Backlogs WHERE backlog_code <> 'DEFAULT';");
        if (backlogs > 0) return true;
        var standup = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM StandupEntries;");
        if (standup > 0) return true;
        var users = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users;");
        return users > 0;
    }
}
