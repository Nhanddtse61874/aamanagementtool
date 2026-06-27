using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// DR-05..08: StandupService orchestration, validation, edit-lock + owner gating.
public class StandupServiceTests
{
    private static readonly DateOnly Today = new(2026, 6, 25);   // a Thursday
    private static readonly DateOnly Yesterday = new(2026, 6, 24);
    private static readonly DateOnly TwoDaysAgo = new(2026, 6, 23);

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public User? Current { get; set; }
        public Task<CurrentUserResult> ResolveAsync() => throw new NotSupportedException();
        public Task SetWindowsUsernameAsync(int userId, string windowsUsername) => Task.CompletedTask;
    }

    private sealed class FakeCurrentTeam : ICurrentTeamService
    {
        public int ActiveTeamId { get; set; } = 7;
        public Team? ActiveTeam => null;
        public IReadOnlyList<Team> AvailableTeams => Array.Empty<Team>();
        public event EventHandler? ActiveTeamChanged { add { } remove { } }
        public Task InitializeAsync(int currentUserId) => Task.CompletedTask;
        public Task SetActiveTeamAsync(int teamId) => Task.CompletedTask;
    }

    private sealed class Ctx
    {
        public Mock<IStandupRepository> Repo = new();
        public Mock<IUserRepository> Users = new();
        public Mock<ITeamRepository> Teams = new();
        public Mock<IBacklogRepository> Backlogs = new();
        public Mock<ITaskRepository> Tasks = new();
        public FakeCurrentUser Current = new() { Current = new User(1, "Alice", "alice", true) };
        public FakeCurrentTeam CurrentTeam = new();
        public StandupService Make(DateOnly today) =>
            new(Repo.Object, Current, CurrentTeam, Users.Object, Teams.Object,
                Backlogs.Object, Tasks.Object, new FakeClock { Today = today });
    }

    private static StandupEntryDraft Draft(
        string section = StandupSection.Today, int? requestId = null, string code = "REQ-1",
        string task = "Build", string desc = "did work", string status = "Todo", DateOnly? deadline = null) =>
        new(section, requestId, code, task, desc, deadline, status);

    private static StandupEntry Entry(int id, int userId, DateOnly date, string section = StandupSection.Today) =>
        new(id, userId, date, section, null, "REQ-1", "Build", "x", null, "Todo", 0,
            new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero));

    // DR-06
    [Fact]
    public void CanEditDay_only_today_and_yesterday()
    {
        var svc = new Ctx().Make(Today);
        Assert.True(svc.CanEditDay(Today));
        Assert.True(svc.CanEditDay(Yesterday));
        Assert.False(svc.CanEditDay(TwoDaysAgo));
        Assert.False(svc.CanEditDay(Today.AddDays(1)));
    }

    [Fact]
    public async Task AddEntry_on_locked_day_is_noop_returns_zero()
    {
        var ctx = new Ctx();
        var svc = ctx.Make(Today);
        var id = await svc.AddEntryAsync(TwoDaysAgo, Draft());
        Assert.Equal(0, id);
        ctx.Repo.Verify(r => r.InsertEntryAsync(It.IsAny<StandupEntry>()), Times.Never);
    }

    [Fact]
    public async Task AddEntry_stamps_current_user_and_date_and_inserts()
    {
        var ctx = new Ctx();
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(Array.Empty<StandupEntry>());
        ctx.Repo.Setup(r => r.InsertEntryAsync(It.IsAny<StandupEntry>())).ReturnsAsync(55);
        var svc = ctx.Make(Today);

        var id = await svc.AddEntryAsync(Today, Draft(code: "ADHOC", task: "spike", status: "In-process"));

        Assert.Equal(55, id);
        ctx.Repo.Verify(r => r.InsertEntryAsync(It.Is<StandupEntry>(e =>
            e.UserId == 1 && e.WorkDate == Today && e.BacklogCode == "ADHOC" &&
            e.TaskText == "spike" && e.Status == "In-process" && e.BacklogId == null)), Times.Once);
    }

    [Fact]
    public async Task AddEntry_rejects_invalid_status()
    {
        var svc = new Ctx().Make(Today);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddEntryAsync(Today, Draft(status: "Bogus")));
    }

    [Fact]
    public async Task AddEntry_drops_backlog_id_when_request_missing()
    {
        var ctx = new Ctx();
        ctx.Backlogs.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Backlog?)null);
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(Array.Empty<StandupEntry>());
        ctx.Repo.Setup(r => r.InsertEntryAsync(It.IsAny<StandupEntry>())).ReturnsAsync(1);
        var svc = ctx.Make(Today);

        await svc.AddEntryAsync(Today, Draft(requestId: 99));

        ctx.Repo.Verify(r => r.InsertEntryAsync(It.Is<StandupEntry>(e => e.BacklogId == null)), Times.Once);
    }

    [Fact]
    public async Task UpdateEntry_rejects_non_owner()
    {
        var ctx = new Ctx();
        ctx.Repo.Setup(r => r.GetEntryAsync(7)).ReturnsAsync(Entry(7, userId: 2, Today)); // owned by user 2
        var svc = ctx.Make(Today);

        var ok = await svc.UpdateEntryAsync(7, Draft());
        Assert.False(ok);
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.IsAny<StandupEntry>()), Times.Never);
    }

    [Fact]
    public async Task UpdateEntry_rejects_locked_day()
    {
        var ctx = new Ctx();
        ctx.Repo.Setup(r => r.GetEntryAsync(7)).ReturnsAsync(Entry(7, userId: 1, TwoDaysAgo));
        var svc = ctx.Make(Today);

        var ok = await svc.UpdateEntryAsync(7, Draft());
        Assert.False(ok);
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.IsAny<StandupEntry>()), Times.Never);
    }

    [Fact]
    public async Task UpdateEntry_applies_for_owner_on_editable_day()
    {
        var ctx = new Ctx();
        ctx.Repo.Setup(r => r.GetEntryAsync(7)).ReturnsAsync(Entry(7, userId: 1, Yesterday));
        var svc = ctx.Make(Today);

        var ok = await svc.UpdateEntryAsync(7, Draft(status: "Done"));
        Assert.True(ok);
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.Is<StandupEntry>(e => e.Id == 7 && e.Status == "Done")), Times.Once);
    }

    [Fact] // drag-reorder: moving an entry onto the front slot reassigns 0..n within that section
    public async Task ReorderEntry_within_section_reassigns_order()
    {
        var ctx = new Ctx();
        var a = Entry(10, 1, Today, StandupSection.Today) with { OrderIndex = 0 };
        var b = Entry(11, 1, Today, StandupSection.Today) with { OrderIndex = 1 };
        var c = Entry(12, 1, Today, StandupSection.Today) with { OrderIndex = 2 };
        ctx.Repo.Setup(r => r.GetEntryAsync(12)).ReturnsAsync(c);   // dragged
        ctx.Repo.Setup(r => r.GetEntryAsync(10)).ReturnsAsync(a);   // target (front)
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(new[] { a, b, c });
        var svc = ctx.Make(Today);

        await svc.ReorderEntryAsync(draggedId: 12, targetId: 10);

        // new order: 12(0), 10(1), 11(2)
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.Is<StandupEntry>(e => e.Id == 12 && e.OrderIndex == 0)), Times.Once);
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.Is<StandupEntry>(e => e.Id == 10 && e.OrderIndex == 1)), Times.Once);
        ctx.Repo.Verify(r => r.UpdateEntryAsync(It.Is<StandupEntry>(e => e.Id == 11 && e.OrderIndex == 2)), Times.Once);
    }

    // DR-07: grouping + issue attach + editable flag
    [Fact]
    public async Task GetMyStandup_groups_sections_and_attaches_issues()
    {
        var ctx = new Ctx();
        var entries = new[]
        {
            Entry(10, 1, Today, StandupSection.Yesterday),
            Entry(11, 1, Today, StandupSection.Today),
        };
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(entries);
        ctx.Repo.Setup(r => r.GetIssuesForEntriesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(new[] { new StandupIssue(1, 11, "blocked", null, "open", 0, default) });
        var svc = ctx.Make(Today);

        var standup = await svc.GetMyStandupAsync(Today);

        Assert.Equal("Alice", standup.UserName);
        Assert.Single(standup.Yesterday);
        var today = Assert.Single(standup.Today);
        Assert.True(today.Editable);                  // today is editable
        Assert.Equal("blocked", Assert.Single(today.Issues).IssueText);
    }

    // DR-08 / TM-07: one card per MEMBER of the checked teams, empty sections allowed.
    // The convenience overload defaults to the active team (id 7).
    [Fact]
    public async Task GetTeamStandup_returns_one_per_team_member_even_when_empty()
    {
        var ctx = new Ctx();
        ctx.Users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true), new User(2, "Bob", null, true),
        });
        ctx.Teams.Setup(t => t.GetUserIdsForTeamAsync(7)).ReturnsAsync(new[] { 1, 2 });
        ctx.Repo.Setup(r => r.GetEntriesForDayAsync(Today, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(new[] { Entry(10, 1, Today, StandupSection.Today) });
        ctx.Repo.Setup(r => r.GetIssuesForEntriesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(Array.Empty<StandupIssue>());
        var svc = ctx.Make(Today);

        var team = await svc.GetTeamStandupAsync(Today);

        Assert.Equal(2, team.Count);
        var alice = team.Single(t => t.UserId == 1);
        Assert.Single(alice.Today);
        var bob = team.Single(t => t.UserId == 2);
        Assert.Empty(bob.Today);
        Assert.Empty(bob.Yesterday);
    }

    // TM-06: a new entry carries the active team's id.
    [Fact]
    public async Task AddEntry_stamps_active_team_id()
    {
        var ctx = new Ctx();
        ctx.CurrentTeam.ActiveTeamId = 42;
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(Array.Empty<StandupEntry>());
        ctx.Repo.Setup(r => r.InsertEntryAsync(It.IsAny<StandupEntry>())).ReturnsAsync(9);
        var svc = ctx.Make(Today);

        await svc.AddEntryAsync(Today, Draft());

        ctx.Repo.Verify(r => r.InsertEntryAsync(It.Is<StandupEntry>(e => e.TeamId == 42)), Times.Once);
    }

    // TM-06: the Input tab passes the active team to the day query (active-team only).
    [Fact]
    public async Task GetMyStandup_queries_only_active_team()
    {
        var ctx = new Ctx();
        ctx.CurrentTeam.ActiveTeamId = 42;
        ctx.Repo.Setup(r => r.GetEntriesAsync(1, Today, It.IsAny<int?>())).ReturnsAsync(Array.Empty<StandupEntry>());
        ctx.Repo.Setup(r => r.GetIssuesForEntriesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(Array.Empty<StandupIssue>());
        var svc = ctx.Make(Today);

        await svc.GetMyStandupAsync(Today);

        ctx.Repo.Verify(r => r.GetEntriesAsync(1, Today, 42), Times.Once);
    }

    // TM-07: the board feed filters entries by the checked teams and cards = those teams' members.
    [Fact]
    public async Task GetTeamStandup_with_teamIds_filters_by_checked_teams_and_members()
    {
        var ctx = new Ctx();
        ctx.Users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true), new User(2, "Bob", null, true), new User(3, "Cara", null, true),
        });
        // team 10 -> {1}, team 20 -> {2}; user 3 is in neither checked team.
        ctx.Teams.Setup(t => t.GetUserIdsForTeamAsync(10)).ReturnsAsync(new[] { 1 });
        ctx.Teams.Setup(t => t.GetUserIdsForTeamAsync(20)).ReturnsAsync(new[] { 2 });
        ctx.Repo.Setup(r => r.GetEntriesForDayAsync(Today, It.IsAny<IReadOnlyList<int>?>()))
            .ReturnsAsync(new[] { Entry(10, 1, Today, StandupSection.Today) });
        ctx.Repo.Setup(r => r.GetIssuesForEntriesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(Array.Empty<StandupIssue>());
        var svc = ctx.Make(Today);

        var team = await svc.GetTeamStandupAsync(Today, new[] { 10, 20 });

        Assert.Equal(2, team.Count);                       // members of the checked teams only (no Cara)
        Assert.Contains(team, t => t.UserId == 1);
        Assert.Contains(team, t => t.UserId == 2);
        Assert.DoesNotContain(team, t => t.UserId == 3);
        ctx.Repo.Verify(r => r.GetEntriesForDayAsync(Today,
            It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 10, 20 }))), Times.Once);
    }

    // TM-07: empty teamIds (or teamId 0) => empty board, no queries leak match-all.
    [Fact]
    public async Task GetTeamStandup_with_empty_teamIds_returns_empty()
    {
        var ctx = new Ctx();
        var svc = ctx.Make(Today);

        var team = await svc.GetTeamStandupAsync(Today, Array.Empty<int>());

        Assert.Empty(team);
        ctx.Repo.Verify(r => r.GetEntriesForDayAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<int>?>()), Times.Never);
        ctx.Teams.Verify(t => t.GetUserIdsForTeamAsync(It.IsAny<int>()), Times.Never);
    }

    // DR-04: issues are collaborative — adding succeeds regardless of owner/day; bad status rejected
    [Fact]
    public async Task AddIssue_validates_status_and_inserts()
    {
        var ctx = new Ctx();
        ctx.Repo.Setup(r => r.InsertIssueAsync(It.IsAny<StandupIssue>())).ReturnsAsync(3);
        var svc = ctx.Make(Today);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.AddIssueAsync(10, "x", null, "bogus"));

        var id = await svc.AddIssueAsync(10, "blocked", null, "open");
        Assert.Equal(3, id);
        ctx.Repo.Verify(r => r.InsertIssueAsync(It.Is<StandupIssue>(i =>
            i.EntryId == 10 && i.IssueText == "blocked" && i.SolutionText == null && i.Status == "open")), Times.Once);
    }
}
