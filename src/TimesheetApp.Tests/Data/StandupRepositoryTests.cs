using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Data;

// DR-01..04, DR-09: StandupRepository integration tests on the real v5 schema (TestDb).
public class StandupRepositoryTests
{
    private static StandupEntry NewEntry(
        int userId, DateOnly date, string section = StandupSection.Today,
        int? requestId = null, string code = "REQ-9", string task = "Build",
        string desc = "did work", DateOnly? deadline = null, string status = "Todo", int order = 0) =>
        new(0, userId, date, section, requestId, code, task, desc, deadline, status, order,
            new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Day = new(2026, 6, 25);

    [Fact]
    public async Task Insert_then_get_round_trips_all_fields_including_adhoc_and_null_deadline()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var userId = await db.SeedUserAsync("Alice");

        var id = await repo.InsertEntryAsync(NewEntry(
            userId, Day, section: StandupSection.Yesterday,
            requestId: null, code: "ADHOC-1", task: "spike", desc: "looked into X",
            deadline: null, status: "In-process"));

        var rows = await repo.GetEntriesAsync(userId, Day);
        var e = Assert.Single(rows);
        Assert.True(e.Id > 0);
        Assert.Equal(id, e.Id);
        Assert.Equal(userId, e.UserId);
        Assert.Equal(Day, e.WorkDate);
        Assert.Equal(StandupSection.Yesterday, e.Section);
        Assert.Null(e.BacklogId);            // ad-hoc (DR-03)
        Assert.Equal("ADHOC-1", e.BacklogCode);
        Assert.Equal("spike", e.TaskText);
        Assert.Equal("looked into X", e.Description);
        Assert.Null(e.Deadline);             // nullable (DR-02)
        Assert.Equal("In-process", e.Status);
    }

    [Fact]
    public async Task Deadline_and_backlog_id_persist_when_present()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var userId = await db.SeedUserAsync();
        var requestId = await db.SeedRequestAsync("REQ-100", "ARCS");

        await repo.InsertEntryAsync(NewEntry(
            userId, Day, requestId: requestId, code: "REQ-100",
            deadline: new DateOnly(2026, 7, 1)));

        var e = Assert.Single(await repo.GetEntriesAsync(userId, Day));
        Assert.Equal(requestId, e.BacklogId);
        Assert.Equal(new DateOnly(2026, 7, 1), e.Deadline);
    }

    [Fact]
    public async Task GetEntriesForDay_returns_all_users()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var a = await db.SeedUserAsync("Alice");
        var b = await db.SeedUserAsync("Bob");
        await repo.InsertEntryAsync(NewEntry(a, Day));
        await repo.InsertEntryAsync(NewEntry(b, Day));
        await repo.InsertEntryAsync(NewEntry(a, Day.AddDays(-1))); // other day, excluded

        var rows = await repo.GetEntriesForDayAsync(Day);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.UserId == a);
        Assert.Contains(rows, r => r.UserId == b);
    }

    [Fact]
    public async Task GetEntriesForRange_filters_by_date()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var u = await db.SeedUserAsync();
        await repo.InsertEntryAsync(NewEntry(u, new DateOnly(2026, 6, 22)));
        await repo.InsertEntryAsync(NewEntry(u, new DateOnly(2026, 6, 26)));
        await repo.InsertEntryAsync(NewEntry(u, new DateOnly(2026, 6, 29))); // outside

        var rows = await repo.GetEntriesForRangeAsync(new DateOnly(2026, 6, 22), new DateOnly(2026, 6, 26));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.InRange(r.WorkDate, new DateOnly(2026, 6, 22), new DateOnly(2026, 6, 26)));
    }

    [Fact]
    public async Task Update_entry_persists_changes()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var u = await db.SeedUserAsync();
        var id = await repo.InsertEntryAsync(NewEntry(u, Day, status: "Todo", desc: "old"));

        var current = Assert.Single(await repo.GetEntriesAsync(u, Day));
        await repo.UpdateEntryAsync(current with { Status = "Done", Description = "new", TaskText = "edited" });

        var e = Assert.Single(await repo.GetEntriesAsync(u, Day));
        Assert.Equal("Done", e.Status);
        Assert.Equal("new", e.Description);
        Assert.Equal("edited", e.TaskText);
    }

    [Fact]
    public async Task Issues_crud_and_cascade_on_entry_delete()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var u = await db.SeedUserAsync();
        var entryId = await repo.InsertEntryAsync(NewEntry(u, Day));

        var i1 = await repo.InsertIssueAsync(new StandupIssue(0, entryId, "blocked on API", null, "open", 0,
            new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero)));
        await repo.InsertIssueAsync(new StandupIssue(0, entryId, "flaky test", "rerun", "resolved", 1,
            new DateTimeOffset(2026, 6, 25, 9, 1, 0, TimeSpan.Zero)));

        var issues = await repo.GetIssuesForEntriesAsync(new[] { entryId });
        Assert.Equal(2, issues.Count);
        var open = issues.Single(x => x.Id == i1);
        Assert.Equal("blocked on API", open.IssueText);
        Assert.Null(open.SolutionText);            // pending (DR-04)
        Assert.Equal("open", open.Status);

        // update an issue (anyone can edit — collaborative)
        await repo.UpdateIssueAsync(open with { SolutionText = "vendor fixed", Status = "resolved" });
        var updated = (await repo.GetIssuesForEntriesAsync(new[] { entryId })).Single(x => x.Id == i1);
        Assert.Equal("vendor fixed", updated.SolutionText);
        Assert.Equal("resolved", updated.Status);

        // delete one issue directly
        await repo.DeleteIssueAsync(i1);
        Assert.Single(await repo.GetIssuesForEntriesAsync(new[] { entryId }));

        // deleting the entry cascades its remaining issues (DR-04)
        await repo.DeleteEntryAsync(entryId);
        Assert.Empty(await repo.GetEntriesAsync(u, Day));
        Assert.Empty(await repo.GetIssuesForEntriesAsync(new[] { entryId }));
    }

    [Fact]
    public async Task GetIssues_for_empty_id_list_is_empty()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        Assert.Empty(await repo.GetIssuesForEntriesAsync(Array.Empty<int>()));
    }

    // v10/M8.2: StandupIssues is check-and-bump (collaborative, DR-04 -- anybody may edit an issue).
    // Deterministic, no threads: the conflict window IS the stale version number (M8.2 plan §Wave 3).
    // Both "clients" read row_version 1; A saves first (succeeds); B saves second still holding 1
    // (conflicts); A's write must survive untouched.
    [Fact]
    public async Task UpdateIssue_TwoConcurrentEdits_FirstWinsSecondConflicts()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var u = await db.SeedUserAsync();
        var entryId = await repo.InsertEntryAsync(NewEntry(u, Day));
        var issueId = await repo.InsertIssueAsync(new StandupIssue(0, entryId, "blocked on API", null, "open", 0,
            new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero)));

        var aliceSaw = (await repo.GetIssuesForEntriesAsync(new[] { entryId })).Single(x => x.Id == issueId);
        var bobSaw = (await repo.GetIssuesForEntriesAsync(new[] { entryId })).Single(x => x.Id == issueId);
        Assert.Equal(1, aliceSaw.RowVersion);
        Assert.Equal(1, bobSaw.RowVersion);

        await repo.UpdateIssueAsync(aliceSaw with { SolutionText = "alice's fix", Status = "resolved" });

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repo.UpdateIssueAsync(bobSaw with { SolutionText = "bob's fix", Status = "resolved" }));
        Assert.False(ex.Deleted);
        Assert.Equal("StandupIssues", ex.Table);

        var final = (await repo.GetIssuesForEntriesAsync(new[] { entryId })).Single(x => x.Id == issueId);
        Assert.Equal("alice's fix", final.SolutionText);   // Bob did NOT silently overwrite it
        Assert.Equal(2, final.RowVersion);                  // bumped exactly once
    }

    // OT-2: a client holding a stale version must not be able to silently resurrect an issue someone
    // else deleted -- rowsAffected == 0 alone conflates "changed" and "deleted"; the existence check
    // on the conflict path must tell them apart via ConcurrencyConflictException.Deleted.
    [Fact]
    public async Task UpdateIssue_OnDeletedIssue_ConflictsWithDeletedTrue()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var u = await db.SeedUserAsync();
        var entryId = await repo.InsertEntryAsync(NewEntry(u, Day));
        var issueId = await repo.InsertIssueAsync(new StandupIssue(0, entryId, "blocked on API", null, "open", 0,
            new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero)));

        var loaded = (await repo.GetIssuesForEntriesAsync(new[] { entryId })).Single(x => x.Id == issueId);
        await repo.DeleteIssueAsync(issueId);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repo.UpdateIssueAsync(loaded with { Status = "resolved" }));
        Assert.True(ex.Deleted);
        Assert.Equal("StandupIssues", ex.Table);
    }

    // P10 (TM-06/07): team_id round-trips on the entry, and GetEntriesForDayAsync filters by team.
    [Fact]
    public async Task TeamId_roundtrips_and_GetEntriesForDay_filters_by_team()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new StandupRepository(db);
        var teamA = await db.SeedTeamAsync("A");
        var teamB = await db.SeedTeamAsync("B");
        var u = await db.SeedUserAsync();
        await repo.InsertEntryAsync(NewEntry(u, Day) with { TeamId = teamA });
        await repo.InsertEntryAsync(NewEntry(u, Day) with { TeamId = teamB });

        // round-trip: the team id persists.
        var mine = await repo.GetEntriesAsync(u, Day);
        Assert.Contains(mine, e => e.TeamId == teamA);
        Assert.Contains(mine, e => e.TeamId == teamB);

        // null => all teams (existing behavior); a team filter scopes the board feed.
        Assert.Equal(2, (await repo.GetEntriesForDayAsync(Day)).Count);
        var onlyA = await repo.GetEntriesForDayAsync(Day, new[] { teamA });
        var single = Assert.Single(onlyA);
        Assert.Equal(teamA, single.TeamId);
    }
}
