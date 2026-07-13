using Moq;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// M8.3 W0-T2 (standup half). StandupIssues is the ONE standup table two people can genuinely race: issues
// are collaborative by design (DR-04 — anyone may edit anyone's issue, no owner gate), so a lost update is
// reachable there and nowhere else in the feature.
//
// StandupEntries is deliberately the opposite: it has no row_version column at all and is protected by an
// OWNER GATE, so two users cannot reach the same row. That is why UpdateEntryAsync gets no checked sibling
// here — last-write-wins is correct BY DESIGN, not by omission.
public class StandupServiceCheckedTests
{
    private static readonly DateOnly Today = new(2026, 6, 25);

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
        public int ActiveTeamId { get; set; } = 1;
        public Team? ActiveTeam => null;
        public IReadOnlyList<Team> AvailableTeams => Array.Empty<Team>();
        public event EventHandler? ActiveTeamChanged { add { } remove { } }
        public Task InitializeAsync(int currentUserId) => Task.CompletedTask;
        public Task SetActiveTeamAsync(int teamId) => Task.CompletedTask;
    }

    // Real repository + real service on a real temp-file DB — the only way a version conflict can occur.
    // (:memory: would give each connection its own database, so the race under test could not happen.)
    private sealed class Harness : IDisposable
    {
        public TestDb Db = null!;
        public StandupRepository Repo = null!;
        public StandupService Svc = null!;
        public int EntryId;

        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            h.Db = await TestDb.CreateAsync();
            h.Repo = new StandupRepository(h.Db);

            var userId = await h.Db.SeedUserAsync("Alice", "alice");
            var teamId = await h.Db.SeedTeamAsync();

            h.Svc = new StandupService(
                h.Repo,
                new FakeCurrentUser { Current = new User(userId, "Alice", "alice", true) },
                new FakeCurrentTeam { ActiveTeamId = teamId },
                new Mock<IUserRepository>().Object, new Mock<ITeamRepository>().Object,
                new Mock<IBacklogRepository>().Object, new Mock<ITaskRepository>().Object,
                new FakeClock { Today = Today });

            h.EntryId = await h.Repo.InsertEntryAsync(new StandupEntry(
                0, userId, Today, StandupSection.Today, null, "REQ-001", "Some task", "",
                null, "Todo", 0, new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero), teamId));
            return h;
        }

        public async Task<StandupIssue> IssueAsync(int issueId) =>
            (await Repo.GetIssuesForEntriesAsync(new[] { EntryId })).Single(i => i.Id == issueId);

        public void Dispose() => Db.Dispose();
    }

    // The observable truth, standup side: two people edit the same issue from the same version; the second
    // one must LOSE — through the service.
    [Fact]
    public async Task Two_clients_editing_one_issue_from_the_same_version__the_second_conflicts()
    {
        using var h = await Harness.CreateAsync();
        var issueId = await h.Svc.AddIssueAsync(h.EntryId, "Blocked on the API", null, "open");

        // Both clients read the issue and hold the same version.
        var issue = await h.IssueAsync(issueId);
        var v = issue.RowVersion;

        // Client A edits at v -> wins.
        var newVersion = await h.Svc.UpdateIssueCheckedAsync(issue with { IssueText = "A's edit" }, v);
        Assert.NotEqual(v, newVersion);

        // Client B edits at the SAME v -> loses.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.UpdateIssueCheckedAsync(issue with { IssueText = "B's edit" }, v));
        Assert.Equal("StandupIssues", ex.Table);

        // A's edit stands; B's was not silently applied.
        Assert.Equal("A's edit", (await h.IssueAsync(issueId)).IssueText);
    }

    // The returned version chains straight into the next edit — no re-read (which would be racy anyway).
    [Fact]
    public async Task The_returned_RowVersion_chains_straight_into_the_next_edit()
    {
        using var h = await Harness.CreateAsync();
        var issueId = await h.Svc.AddIssueAsync(h.EntryId, "First", null, "open");
        var issue = await h.IssueAsync(issueId);

        var v1 = await h.Svc.UpdateIssueCheckedAsync(issue with { IssueText = "Second" }, issue.RowVersion);
        var v2 = await h.Svc.UpdateIssueCheckedAsync(issue with { IssueText = "Third" }, v1);

        Assert.True(v2 > v1);
        Assert.Equal("Third", (await h.IssueAsync(issueId)).IssueText);
    }

    // Editing an issue somebody else DELETED must conflict, not silently resurrect it.
    [Fact]
    public async Task Editing_a_deleted_issue_is_a_conflict()
    {
        using var h = await Harness.CreateAsync();
        var issueId = await h.Svc.AddIssueAsync(h.EntryId, "Doomed", null, "open");
        var issue = await h.IssueAsync(issueId);

        await h.Svc.DeleteIssueAsync(issueId);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Svc.UpdateIssueCheckedAsync(issue with { IssueText = "edit" }, issue.RowVersion));
        Assert.True(ex.Deleted);
    }

    // Validation still runs FIRST on the checked path, and still throws (matching UpdateIssueAsync): bad
    // input is the caller's fault (-> 400), a stale version is not (-> 409). Two failures, two answers.
    [Fact]
    public async Task Validation_still_runs_first_and_reaches_no_repository_write()
    {
        var repo = new Mock<IStandupRepository>();
        var svc = new StandupService(
            repo.Object, new FakeCurrentUser { Current = new User(1, "Alice", "alice", true) },
            new FakeCurrentTeam(), new Mock<IUserRepository>().Object, new Mock<ITeamRepository>().Object,
            new Mock<IBacklogRepository>().Object, new Mock<ITaskRepository>().Object,
            new FakeClock { Today = Today });

        var blank = new StandupIssue(1, 1, "   ", null, "open", 0, DateTimeOffset.UtcNow, 3);
        var badStatus = new StandupIssue(1, 1, "text", null, "not-a-status", 0, DateTimeOffset.UtcNow, 3);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateIssueCheckedAsync(blank, 3));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateIssueCheckedAsync(badStatus, 3));

        repo.Verify(r => r.UpdateIssueCheckedAsync(It.IsAny<StandupIssue>(), It.IsAny<long>()), Times.Never);
        repo.Verify(r => r.UpdateIssueAsync(It.IsAny<StandupIssue>()), Times.Never);
    }

    // Same normalization as UpdateIssueAsync (trim + blank solution => null), and the version travels
    // SEPARATELY from the record — never read off issue.RowVersion, which a caller that rebuilt the record
    // from edited fields would have left at the default 0.
    [Fact]
    public async Task Checked_update_normalizes_like_the_unchecked_one_and_passes_the_version_separately()
    {
        var repo = new Mock<IStandupRepository>();
        repo.Setup(r => r.UpdateIssueCheckedAsync(It.IsAny<StandupIssue>(), It.IsAny<long>())).ReturnsAsync(9L);
        var svc = new StandupService(
            repo.Object, new FakeCurrentUser { Current = new User(1, "Alice", "alice", true) },
            new FakeCurrentTeam(), new Mock<IUserRepository>().Object, new Mock<ITeamRepository>().Object,
            new Mock<IBacklogRepository>().Object, new Mock<ITaskRepository>().Object,
            new FakeClock { Today = Today });

        // RowVersion on the record is the default 0 — a rebuilt-from-fields record. The real version is 5.
        var rebuilt = new StandupIssue(1, 1, "  needs trimming  ", "   ", "open", 0, DateTimeOffset.UtcNow);
        Assert.Equal(0, rebuilt.RowVersion);

        var result = await svc.UpdateIssueCheckedAsync(rebuilt, expectedVersion: 5L);

        Assert.Equal(9L, result);
        repo.Verify(r => r.UpdateIssueCheckedAsync(
            It.Is<StandupIssue>(i => i.IssueText == "needs trimming" && i.SolutionText == null), 5L), Times.Once);
        repo.Verify(r => r.UpdateIssueAsync(It.IsAny<StandupIssue>()), Times.Never);   // never the bump-only write
    }
}
