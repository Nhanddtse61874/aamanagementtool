using Dapper;
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P10 W2: ITeamBootstrapService migration + first-run (architecture §1d, spec §3, R3).
//
// M8.2 (Wave 4): bootstrap no longer takes IAppConfig. It used to end with
// _config.SetActiveTeamId(teamId) — a per-PROCESS write — but the active team is now per-USER
// (Users.active_team_id), and bootstrap runs BEFORE any user is resolved, so it has no "current user"
// to write for. It instead seeds active_team_id for every user that exists at bootstrap time, in the
// same sweep that already joins them to the team. So "sets it active" is now asserted against the DB
// rather than against a mock.
public class TeamBootstrapServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private TeamRepository _teams = null!;
    private Mock<IDbBackupHelper> _backup = null!;
    private FixedClock _clock = null!;
    private TeamBootstrapService _svc = null!;

    private sealed class FixedClock : IClock
    {
        public DateOnly Today => new(2026, 6, 27);
        public DateTimeOffset UtcNow => new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _teams = new TeamRepository(_db);
        _backup = new Mock<IDbBackupHelper>();
        _backup.Setup(b => b.BackupAsync()).ReturnsAsync((string?)null);
        _clock = new FixedClock();
        _svc = new TeamBootstrapService(_teams, _db, _backup.Object, _clock);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private async Task<long> CountAsync(string sql)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long>(sql);
    }

    private async Task<long?> ScalarAsync(string sql)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long?>(sql);
    }

    // ---- Migration path: existing business data -> "Architect Improvement", everything backfilled ----
    [Fact]
    public async Task Migration_assigns_all_rows_to_Architect_Improvement_and_sets_active()
    {
        // Seed a v8 DB with legacy team-less business data: a backlog, a standup entry, two users.
        var backlogId = await _db.SeedRequestAsync("REQ-001", "ARCS");          // team_id NULL
        var u1 = await _db.SeedUserAsync("Alice");
        var u2 = await _db.SeedUserAsync("Bob");
        using (var c = _db.Create())
        {
            await c.ExecuteAsync(
                @"INSERT INTO StandupEntries(user_id, work_date, section, backlog_code, task_text, status, created_at)
                  VALUES(@u, '2026-06-20', 'today', 'REQ-001', 'did work', 'Done', '2026-06-20T00:00:00Z');",
                new { u = u1 });
        }

        await _svc.EnsureBootstrappedAsync();

        var team = await _teams.GetByNameAsync("Architect Improvement");
        Assert.NotNull(team);
        var t = team!.Id;

        // Every backlog (incl. the seeded global DEFAULT) now carries the team.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE team_id IS NULL;"));
        Assert.Equal((long)t, await ScalarAsync($"SELECT team_id FROM Backlogs WHERE id = {backlogId};"));
        // Standup backfilled.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM StandupEntries WHERE team_id IS NULL;"));
        // Both users joined the team.
        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM UserTeams WHERE team_id = {t};"));

        // Active team persisted PER USER (M8.2/W4) — every migrated user gets the bootstrap team on
        // their own row, rather than one process-wide config slot shared by everyone.
        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM Users WHERE active_team_id = {t};"));
        Assert.Equal((long)t, await ScalarAsync($"SELECT active_team_id FROM Users WHERE id = {u1};"));
        Assert.Equal((long)t, await ScalarAsync($"SELECT active_team_id FROM Users WHERE id = {u2};"));

        // Backup taken first (XC-10).
        _backup.Verify(b => b.BackupAsync(), Times.Once);
    }

    [Fact]
    public async Task Migration_is_idempotent_second_run_is_a_noop()
    {
        await _db.SeedRequestAsync("REQ-001", "ARCS");
        var alice = await _db.SeedUserAsync("Alice");

        await _svc.EnsureBootstrappedAsync();
        var teamsAfterFirst = (await _teams.GetAllAsync()).Count;
        var versionAfterFirst = await ScalarAsync($"SELECT row_version FROM Users WHERE id = {alice};");

        await _svc.EnsureBootstrappedAsync(); // guard -> no-op

        Assert.Equal(teamsAfterFirst, (await _teams.GetAllAsync()).Count);
        Assert.Equal(1, teamsAfterFirst);
        // The active-team seed is bump-only but GUARDED (WHERE active_team_id = 0), so a re-run does not
        // touch a user who already has one. This matters: the self-healing path re-runs the backfill on
        // EVERY startup, so an unguarded UPDATE would bump row_version for every user, every launch.
        Assert.Equal(versionAfterFirst, await ScalarAsync($"SELECT row_version FROM Users WHERE id = {alice};"));
    }

    // M8.2 (Wave 4) REGRESSION GUARD: the backfill re-runs on every startup (self-healing, I2). It must
    // never reset a team the user deliberately switched to — otherwise every launch would silently drag
    // everyone back to the bootstrap team. `WHERE active_team_id = 0` is what prevents that.
    [Fact]
    public async Task Rerun_does_not_clobber_a_user_who_already_switched_team()
    {
        await _db.SeedRequestAsync("REQ-001", "ARCS");
        var alice = await _db.SeedUserAsync("Alice");
        await _svc.EnsureBootstrappedAsync();

        // Alice switches to a second team she also belongs to.
        var other = await _db.SeedTeamAsync("Other Team");
        using (var c = _db.Create())
        {
            await c.ExecuteAsync(
                "UPDATE Users SET active_team_id = @t WHERE id = @id;", new { t = other, id = alice });
        }

        await _svc.EnsureBootstrappedAsync(); // self-healing re-run

        Assert.Equal((long)other, await ScalarAsync($"SELECT active_team_id FROM Users WHERE id = {alice};"));
    }

    // ---- First-run path: fresh DB (only the seeded DEFAULT, no business data) -> "My Team" ----
    [Fact]
    public async Task FirstRun_creates_exactly_one_active_My_Team()
    {
        // TestDb.CreateAsync() seeds only the hidden DEFAULT backlog + DefaultTasks -> no business data.
        await _svc.EnsureBootstrappedAsync();

        var all = await _teams.GetAllAsync();
        var single = Assert.Single(all);
        Assert.Equal("My Team", single.Name);
        Assert.True(single.IsActive);
        // No SetActiveTeamId assertion here any more, and deliberately so: a fresh DB has NO users
        // (that is what makes it "fresh"), so there is no user row to seed an active team onto. The
        // first user is auto-provisioned later by MainViewModel, which joins them to this team;
        // CurrentTeamService then resolves it as their active team (first-available fallback).
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Users;"));
        // First-run takes no backup (no migration write needed for the empty-DB repoint).
        _backup.Verify(b => b.BackupAsync(), Times.Never);
    }

    // I1/GAP-1: on a fresh DB, first-run repoints the seeded global DEFAULT to "My Team" so there is
    // no stray team_id=NULL DEFAULT for a later DefaultTaskSync to double up on.
    [Fact]
    public async Task FirstRun_repoints_seeded_DEFAULT_no_orphan_team_id_null()
    {
        await _svc.EnsureBootstrappedAsync();

        var team = await _teams.GetByNameAsync("My Team");
        Assert.NotNull(team);
        var t = team!.Id;

        // No backlog left team-less, and exactly one DEFAULT now belongs to the new team.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE team_id IS NULL;"));
        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM Backlogs WHERE backlog_code = 'DEFAULT' AND team_id = {t};"));
    }

    // I2: a migration interrupted between team-create and backfill (team exists but rows still NULL)
    // is completed on the next startup — the guard re-runs the idempotent backfill instead of bailing.
    [Fact]
    public async Task Partial_migration_is_completed_on_rerun()
    {
        // Simulate the crash state: legacy business data + a team already created, but no backfill ran.
        var backlogId = await _db.SeedRequestAsync("REQ-001", "ARCS");     // team_id NULL
        var u1 = await _db.SeedUserAsync("Alice");
        using (var c = _db.Create())
        {
            await c.ExecuteAsync(
                @"INSERT INTO StandupEntries(user_id, work_date, section, backlog_code, task_text, status, created_at)
                  VALUES(@u, '2026-06-20', 'today', 'REQ-001', 'did work', 'Done', '2026-06-20T00:00:00Z');",
                new { u = u1 });
        }
        var teamId = await _teams.InsertAsync(new Team(0, "Architect Improvement", true, _clock.UtcNow));

        // Rows are still stranded at this point.
        Assert.True(await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE team_id IS NULL;") > 0);

        await _svc.EnsureBootstrappedAsync();   // re-run completes the interrupted migration

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE team_id IS NULL;"));
        Assert.Equal((long)teamId, await ScalarAsync($"SELECT team_id FROM Backlogs WHERE id = {backlogId};"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM StandupEntries WHERE team_id IS NULL;"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM UserTeams WHERE team_id = {teamId};"));
        // Still exactly one team (no duplicate created).
        Assert.Single(await _teams.GetAllAsync());
    }
}
