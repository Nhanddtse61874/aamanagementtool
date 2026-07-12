using System.Data;
using Dapper;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Post-init bootstrap (architecture §1d, spec §3). On an EXISTING DB with business data it
/// creates team "Architect Improvement", backfills team_id on every legacy backlog/standup row and
/// joins every user to it, then makes it each of those users' active team (Users.active_team_id —
/// per-user since M8.2/W4). On a FRESH DB it creates an active "My Team". Backup
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

    // M8.2 (Wave 4): IAppConfig is gone from this service. It used to end bootstrap with
    // _config.SetActiveTeamId(teamId) — a per-PROCESS write. The active team is now per-USER
    // (Users.active_team_id), and bootstrap runs in App.OnStartup BEFORE any user is resolved, so it
    // has no "current user" to write for and could not do this correctly even if it wanted to. What it
    // CAN do — and now does, inside BackfillTeamAsync — is seed active_team_id for every user that
    // exists at bootstrap time, in the same sweep that already joins them to the team.
    public TeamBootstrapService(
        ITeamRepository teams, IConnectionFactory factory,
        IDbBackupHelper backup, IClock clock)
    {
        _teams = teams;
        _factory = factory;
        _backup = backup;
        _clock = clock;
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
    }

    // Fresh DB (no business data): create the renamable default team and make it active. The
    // initializer already seeded a global DEFAULT (team_id NULL); repoint it to the new team so a
    // later per-team DefaultTaskSync doesn't create a SECOND DEFAULT and strand the NULL one (I1).
    private async Task FirstRunAsync()
    {
        var teamId = await EnsureTeamAsync(FreshTeamName);
        await BackfillTeamAsync(teamId, backupFirst: false);
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
        //
        // M8.2: this is a raw-SQL write to a VERSIONED table, so it must bump row_version — bump-only,
        // since a backfill carries no client version to check. Per the project rule: bumping without
        // checking is safe; checking without bumping is the lost update this mechanism exists to
        // prevent. Without the bump, a client that read a backlog before bootstrap would still match on
        // version afterwards and could silently overwrite the team_id this just assigned.
        await c.ExecuteAsync(
            "UPDATE Backlogs SET team_id = @t, row_version = row_version + 1 WHERE team_id IS NULL;",
            new { t = teamId }, tx);
        // StandupEntries deliberately has NO row_version column (schema v10 versions 8 tables and this
        // is not one of them — entries are owner-gated in StandupService, so two users cannot reach the
        // same row). Adding a bump here is not a no-op, it is a hard SQLite error: "no such column:
        // row_version". Measured, not assumed.
        await c.ExecuteAsync(
            "UPDATE StandupEntries SET team_id = @t WHERE team_id IS NULL;", new { t = teamId }, tx);
        // Every existing user becomes a member (idempotent via PK).
        await c.ExecuteAsync(
            "INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;",
            new { t = teamId }, tx);
        // M8.2 (Wave 4): this REPLACES the old `_config.SetActiveTeamId(teamId)` — the same intent
        // ("the bootstrap team is the active team"), moved from a per-PROCESS home to a per-USER one.
        // Bootstrap has no current user, so it seeds every user that exists right now; a user
        // auto-provisioned LATER is joined + resolved by MainViewModel/CurrentTeamService instead.
        //
        // WHERE active_team_id = 0 is what makes this idempotent AND non-destructive: it seeds only
        // users who have never had an active team, so a re-run (the self-healing path above calls this
        // on EVERY startup) never clobbers a team a user deliberately switched to, and never churns
        // row_version. Users is a versioned table -> bump; system write with no client-supplied
        // version -> bump-only, nothing to check.
        await c.ExecuteAsync(
            "UPDATE Users SET active_team_id = @t, row_version = row_version + 1 WHERE active_team_id = 0;",
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
