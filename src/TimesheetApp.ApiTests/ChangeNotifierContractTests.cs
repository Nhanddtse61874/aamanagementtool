using System.Net;
using System.Net.Http.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>The <c>IChangeNotifier</c> CONTRACT, pinned across all four endpoint files.
///
/// <para><b>None of this was testable before W2.5.</b> <c>Program.cs</c> registers
/// <c>NoopChangeNotifier</c>, whose <c>DataChangedAsync</c> is <c>Task.CompletedTask</c> — so in the test
/// host all 44 notify call sites were unobservable BY CONSTRUCTION. A missing notify, a wrong
/// <c>DataKind</c>, a wrong <c>teamId</c> or a dropped <c>exceptConnectionId</c> would all have merged
/// green. <see cref="RecordingChangeNotifier"/> replaces the Noop through <c>ConfigureTestServices</c>.</para>
///
/// <para>Not one test per call site — one test per distinct SHAPE of the contract:</para>
/// <list type="number">
/// <item>a TEAM-SCOPED entity notifies the entity's REAL owning team id (not the caller's active team);</item>
/// <item>a GLOBAL entity (no team column) notifies the reserved <c>teamId: 0</c> broadcast sentinel;</item>
/// <item>the caller's connection id is threaded through as <c>exceptConnectionId</c>, so the editor does
///       not receive their own echo and clobber the conflict dialog a 409 just raised;</item>
/// <item>the two DELIBERATE non-notifies — active-team switch and password change — stay silent;</item>
/// <item>a REJECTED mutation notifies nothing (rule 7 is about SUCCESSFUL mutations).</item>
/// </list></summary>
public sealed class ChangeNotifierContractTests
{
    // ===== 1. Team-scoped: the entity's REAL owning team ==================================================

    /// <summary>A timesheet cell belongs to the team that owns the TASK — which is NOT necessarily the
    /// caller's active team.
    ///
    /// <para><b>The arrangement is the assertion.</b> Alice is a member of two teams; her ACTIVE team
    /// resolves to "Team A" (<c>ApiCurrentTeamService.InitializeAsync</c> takes the first available, and
    /// <c>TeamRepository.GetActiveAsync</c> orders by name), while the task she logs against lives under
    /// "Team B". A handler that reached for <c>ICurrentTeamService.ActiveTeamId</c> instead of the task's
    /// real owning team — the single easiest mistake to make here, and one no other test can see — would
    /// announce the change to Team A, and Team B's board would never refresh. With one team seeded, the
    /// two ids coincide and this test would pass while asserting nothing.</para></summary>
    [Fact]
    public async Task A_team_scoped_write_notifies_the_entitys_real_owning_team_not_the_callers_active_team()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B", aliceId);

        // The task lives in Team B...
        var backlogId = await factory.SeedBacklogAsync(teamB, "REQ-900");
        var taskId = await factory.SeedTaskAsync(backlogId, "Log against me");

        using var client = await factory.ClientAsync("alice");

        // ...while Alice's ACTIVE team is Team A. Proven, not assumed — the whole test rests on it.
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");
        Assert.Equal(teamA, me!.ActiveTeamId);
        Assert.NotEqual(teamA, teamB);

        factory.Notifier.Clear();

        var response = await client.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskId, Weekday(), 4m, ExpectedVersion: null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(factory.Notifier.Calls);
        Assert.Equal(DataKind.Logs, call.Kind);
        Assert.Equal(teamB, call.TeamId);          // the TASK's team, not the caller's active team
    }

    /// <summary>A standup entry is stamped with <c>ICurrentTeamService.ActiveTeamId</c> by
    /// <c>StandupService.AddEntryAsync</c>, and the endpoint notifies with that same value. Those two are
    /// only equal by construction — so assert against the team id the row ACTUALLY landed with, read back
    /// out of the database, rather than against the value the endpoint happened to pass.</summary>
    [Fact]
    public async Task A_standup_entry_notifies_the_team_the_row_actually_landed_in()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);

        using var client = await factory.ClientAsync("alice");
        factory.Notifier.Clear();

        // StandupSection.IsValid is case-SENSITIVE ("today", not "Today"), and StandupStatus.All is
        // { Todo, In-process, Done, Pending }. Both are validated by throwing ArgumentException, which the
        // ExceptionMapper turns into a 400 — so a wrong literal here fails as a rejected write, not as a
        // missing notification, and would look exactly like the bug this test hunts.
        var response = await client.PostAsJsonAsync("/api/standup/entries",
            new SettingsStandupEntryCreateRequest(
                Today(), "today", null, "REQ-1", "Ship the notifier", null, null, "In-process"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entryId = await response.Content.ReadFromJsonAsync<int>();

        using var db = factory.OpenDb();
        var storedTeamId = await db.ExecuteScalarAsync<int?>(
            "SELECT team_id FROM StandupEntries WHERE id = @id;", new { id = entryId });

        // 0 would mean ClientContextFilter never initialized the team — the "poisoned data" case its own
        // doc warns about — and the notify would be broadcasting to the global sentinel by accident.
        Assert.Equal(teamId, storedTeamId);

        var call = Assert.Single(factory.Notifier.Calls);
        Assert.Equal(DataKind.Standup, call.Kind);
        Assert.Equal(storedTeamId, call.TeamId);
    }

    // ===== 2. Global entities: teamId 0 = broadcast ======================================================

    /// <summary>Tag has no team column, so there is no team to send to: <c>teamId: 0</c> is the reserved
    /// "broadcast to every connected client" sentinel. A real team id here would announce a global change to
    /// exactly one team and leave every other team's tag picker stale.</summary>
    [Fact]
    public async Task A_global_entity_write_notifies_team_zero_the_broadcast_sentinel()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();
        factory.Notifier.Clear();

        var response = await admin.PostAsJsonAsync("/api/tags",
            new SettingsTagCreateRequest("Urgent", "flame", "#ff0000"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(factory.Notifier.Calls);
        Assert.Equal(DataKind.Tags, call.Kind);
        Assert.Equal(0, call.TeamId);
    }

    /// <summary>Every global entity, one route each, so the <c>teamId: 0</c> convention cannot be applied to
    /// six of the seven and quietly forgotten on the last one. These are the entities with NO team column:
    /// Tag · PcaContact · User · Team · TaskTemplate · DefaultTask · Holiday.</summary>
    [Fact]
    public async Task Every_global_entity_uses_the_same_broadcast_sentinel()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        await AssertGlobalNotifyAsync(factory, DataKind.Tags, () =>
            admin.PostAsJsonAsync("/api/tags", new SettingsTagCreateRequest("T", "i", "#fff")));

        await AssertGlobalNotifyAsync(factory, DataKind.PcaContacts, () =>
            admin.PostAsJsonAsync("/api/pca-contacts", new SettingsNameRequest("Contact")));

        await AssertGlobalNotifyAsync(factory, DataKind.Users, () =>
            admin.PostAsJsonAsync("/api/users", new SettingsNameRequest("New Person")));

        await AssertGlobalNotifyAsync(factory, DataKind.Teams, () =>
            admin.PostAsJsonAsync("/api/teams", new SettingsNameRequest("New Team")));

        await AssertGlobalNotifyAsync(factory, DataKind.Templates, () =>
            admin.PostAsJsonAsync("/api/templates", new SettingsTemplateCreateRequest("Tpl", "Task", 0)));

        await AssertGlobalNotifyAsync(factory, DataKind.Holidays, () =>
            admin.PostAsJsonAsync("/api/holidays",
                new SettingsHolidayRequest(new DateOnly(2026, 12, 25), "Christmas")));

        await AssertGlobalNotifyAsync(factory, DataKind.DefaultTasks, () =>
            admin.PostAsJsonAsync("/api/default-tasks", new SettingsDefaultTaskCreateRequest("Standup", 0)));
    }

    // ===== 3. exceptConnectionId — the caller must not get their own echo ================================

    /// <summary>The caller's <c>X-Connection-Id</c> must reach <c>exceptConnectionId</c> on EVERY mutation.
    /// Drop it and the editing user receives their own echo, which re-fetches and CLOBBERS the very conflict
    /// dialog a 409 just raised. Asserted on one team-scoped and one global route, because they resolve
    /// <c>teamId</c> along completely different paths and only share <c>ctx.ConnectionId</c>.</summary>
    [Fact]
    public async Task The_callers_connection_id_is_threaded_into_the_notification_as_except()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-901");
        var taskId = await factory.SeedTaskAsync(backlogId, "Log against me");

        using var alice = await factory.ClientAsync("alice", connectionId: "conn-alice-1");
        factory.Notifier.Clear();

        // Team-scoped.
        var cell = await alice.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskId, Weekday(), 2m, ExpectedVersion: null));
        Assert.Equal(HttpStatusCode.OK, cell.StatusCode);
        Assert.Equal("conn-alice-1", Assert.Single(factory.Notifier.Calls).ExceptConnectionId);

        // Global.
        using var admin = await factory.AdminClientAsync(connectionId: "conn-admin-9");
        factory.Notifier.Clear();

        var tag = await admin.PostAsJsonAsync("/api/tags", new SettingsTagCreateRequest("T", "i", "#fff"));
        Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
        Assert.Equal("conn-admin-9", Assert.Single(factory.Notifier.Calls).ExceptConnectionId);
    }

    /// <summary>No header => null, and Wave 3 must read that as "this caller has no live hub connection",
    /// never as "exclude nobody" — the two are indistinguishable at the call site if this is not pinned.</summary>
    [Fact]
    public async Task A_caller_with_no_hub_connection_excludes_nobody()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();       // no X-Connection-Id
        factory.Notifier.Clear();

        await admin.PostAsJsonAsync("/api/tags", new SettingsTagCreateRequest("T", "i", "#fff"));

        Assert.Null(Assert.Single(factory.Notifier.Calls).ExceptConnectionId);
    }

    // ===== 4. The two DELIBERATE non-notifies ============================================================

    /// <summary><c>PUT /api/me/active-team</c> is the SINGLE mutating route that announces nothing, and it
    /// is a decision, not an omission — so it is pinned as one, to stop a later reader "helpfully" adding a
    /// notify back. <c>DataChangedAsync</c> sends to a team group MINUS the caller: when Alice switches from
    /// Team A to Team B, nobody else's view changes, and the one client that cares (hers) is the one it
    /// would exclude. Anything sent here is noise aimed at exactly the wrong set of people.</summary>
    [Fact]
    public async Task Switching_my_own_active_team_notifies_nobody()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B", aliceId);

        using var client = await factory.ClientAsync("alice", connectionId: "conn-alice-1");
        factory.Notifier.Clear();

        var response = await client.PutAsJsonAsync("/api/me/active-team", new SettingsActiveTeamRequest(teamB));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The write really happened — otherwise "notified nothing" would be trivially true for the wrong reason.
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");
        Assert.Equal(teamB, me!.ActiveTeamId);
        Assert.NotEqual(teamA, me.ActiveTeamId);

        Assert.Empty(factory.Notifier.Calls);
    }

    /// <summary>A password change alters no client-visible state: <c>password_hash</c> is never serialized
    /// onto any DTO, so there is nothing for a notification to refresh — and <c>Users</c> is global, so
    /// there would be no non-guessed team id to hand the frozen signature either. Both the self-service and
    /// the admin-reset routes stay silent.</summary>
    [Fact]
    public async Task Changing_a_password_notifies_nobody()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);

        // Self-service.
        using var alice = await factory.ClientAsync("alice", connectionId: "conn-alice-1");
        factory.Notifier.Clear();

        var self = await alice.PostAsJsonAsync("/api/auth/set-password",
            new AuthSetPasswordRequest(ApiFactory.DefaultPassword, "Newer-Pa55w0rd!"));
        Assert.Equal(HttpStatusCode.NoContent, self.StatusCode);
        Assert.Empty(factory.Notifier.Calls);

        // It really changed — a no-op write would make the empty assertion above meaningless.
        using var reloggedIn = await factory.ClientAsync("alice", "Newer-Pa55w0rd!");

        // Admin reset of someone else.
        using var admin = await factory.AdminClientAsync(connectionId: "conn-admin-9");
        factory.Notifier.Clear();

        var reset = await admin.PostAsJsonAsync($"/api/auth/users/{targetId}/set-password",
            new AuthAdminSetPasswordRequest("Reset-Pa55w0rd!"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Empty(factory.Notifier.Calls);

        using var target = await factory.ClientAsync("target", "Reset-Pa55w0rd!");
    }

    // ===== 5. A rejected mutation announces nothing ======================================================

    /// <summary>Rule 7 is about SUCCESSFUL mutations. A write rejected by the team gate wrote nothing, so
    /// there is nothing to announce — and a notification sent anyway would tell an entire team to re-fetch
    /// on the strength of an attacker's rejected request.</summary>
    [Fact]
    public async Task A_write_rejected_by_the_team_gate_notifies_nothing()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);
        var bobsTeam = await factory.SeedTeamAsync("Team B", bobId);

        // A task that belongs to BOB's team, which Alice is not a member of.
        var backlogId = await factory.SeedBacklogAsync(bobsTeam, "REQ-902");
        var taskId = await factory.SeedTaskAsync(backlogId, "Not yours");

        using var alice = await factory.ClientAsync("alice");
        factory.Notifier.Clear();

        var response = await alice.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskId, Weekday(), 4m, ExpectedVersion: null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Notifier.Calls);
    }

    // ===== helpers =======================================================================================

    private static async Task AssertGlobalNotifyAsync(
        ApiFactory factory, DataKind expected, Func<Task<HttpResponseMessage>> act)
    {
        factory.Notifier.Clear();

        var response = await act();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{expected}: the write itself failed with {(int)response.StatusCode}; the notify assertion " +
            "below would then be vacuously true.");

        var call = Assert.Single(factory.Notifier.Calls);
        Assert.Equal(expected, call.Kind);
        Assert.Equal(0, call.TeamId);
    }

    /// <summary>TimeLogService rejects a weekend outright (and a holiday), so a cell test that happens to
    /// run on a Saturday would fail for a reason that has nothing to do with notifications.</summary>
    private static DateOnly Weekday()
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            day = day.AddDays(-1);
        return day;
    }

    /// <summary>Standup entries are edit-locked to today/yesterday, so the date cannot be a fixed literal.</summary>
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
}
