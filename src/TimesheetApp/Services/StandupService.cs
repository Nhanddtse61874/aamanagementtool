using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Daily Report orchestration (DR-05..08). Pure orchestration over the repo + current-user + backlog/task
// repos; no SQL here. Edit-lock (today/yesterday) + owner gate entry writes; issues are collaborative.
public sealed class StandupService : IStandupService
{
    private readonly IStandupRepository _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentTeamService _currentTeam;
    private readonly IUserRepository _users;
    private readonly ITeamRepository _teams;
    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly IClock _clock;

    public StandupService(
        IStandupRepository repo, ICurrentUserService currentUser, ICurrentTeamService currentTeam,
        IUserRepository users, ITeamRepository teams,
        IBacklogRepository backlogs, ITaskRepository tasks, IClock clock)
    {
        _repo = repo;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _users = users;
        _teams = teams;
        _backlogs = backlogs;
        _tasks = tasks;
        _clock = clock;
    }

    public bool CanEditDay(DateOnly workDate)
    {
        var today = _clock.Today;
        return workDate == today || workDate == today.AddDays(-1);
    }

    public async Task<UserStandup> GetMyStandupAsync(DateOnly workDate)
    {
        var me = _currentUser.Current;
        if (me is null) return new UserStandup(0, "", Array.Empty<StandupEntryView>(), Array.Empty<StandupEntryView>());

        // Input tab is active-team only (TM-06).
        var entries = await _repo.GetEntriesAsync(me.Id, workDate, _currentTeam.ActiveTeamId);
        var issues = await LoadIssuesAsync(entries);
        var editable = CanEditDay(workDate); // owner is always the current user here
        return BuildUserStandup(me.Id, me.Name, entries, issues, editable);
    }

    // Overload kept so the W7-unmodified DailyReportViewModel still compiles: defaults the board to the
    // active team only. W7 will switch the call site to the (workDate, teamIds) overload below.
    public Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate) =>
        GetTeamStandupAsync(workDate, new[] { _currentTeam.ActiveTeamId });

    public async Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate, IReadOnlyList<int> teamIds)
    {
        // teamId 0 / empty set => empty board (no match-all leak — TM-07).
        var effectiveTeams = teamIds.Where(t => t > 0).Distinct().ToList();
        if (effectiveTeams.Count == 0) return Array.Empty<UserStandup>();

        var all = await _repo.GetEntriesForDayAsync(workDate, effectiveTeams);
        var issues = await LoadIssuesAsync(all);
        var currentId = _currentUser.Current?.Id ?? 0;

        // Cards = the MEMBERS of the checked teams (union of UserTeams), not all active users.
        var memberIds = new HashSet<int>();
        foreach (var teamId in effectiveTeams)
            foreach (var uid in await _teams.GetUserIdsForTeamAsync(teamId))
                memberIds.Add(uid);

        var active = (await _users.GetActiveAsync() ?? Array.Empty<User>())
            .Where(u => memberIds.Contains(u.Id))
            .ToList();

        var byUser = all.GroupBy(e => e.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<StandupEntry>)g.ToList());
        var result = new List<UserStandup>();
        foreach (var u in active)
        {
            var entries = byUser.TryGetValue(u.Id, out var es) ? es : Array.Empty<StandupEntry>();
            var editable = CanEditDay(workDate) && u.Id == currentId;
            result.Add(BuildUserStandup(u.Id, u.Name, entries, issues, editable));
        }
        return result;
    }

    // Input picker is active-team only (TM-06).
    public Task<IReadOnlyList<Backlog>> SearchBacklogsAsync(string? term) =>
        _backlogs.SearchAsync(term, new[] { _currentTeam.ActiveTeamId });

    public Task<IReadOnlyList<TaskItem>> GetTasksForBacklogAsync(int backlogId) =>
        _tasks.GetActiveByBacklogAsync(backlogId);

    public async Task<int> AddEntryAsync(DateOnly workDate, StandupEntryDraft draft)
    {
        ValidateDraft(draft);
        if (!CanEditDay(workDate)) return 0;          // locked day -> no-op
        var me = _currentUser.Current;
        if (me is null) return 0;

        var backlogId = await ResolveBacklogIdAsync(draft.BacklogId);
        var entry = new StandupEntry(
            0, me.Id, workDate, draft.Section, backlogId, draft.BacklogCode.Trim(),
            draft.TaskText.Trim(), draft.Description ?? "", draft.Deadline, draft.Status,
            await NextOrderAsync(me.Id, workDate, draft.Section), _clock.UtcNow,
            TeamId: _currentTeam.ActiveTeamId);   // TM-06: new entry carries the active team
        return await _repo.InsertEntryAsync(entry);
    }

    public async Task<bool> UpdateEntryAsync(int entryId, StandupEntryDraft draft)
    {
        ValidateDraft(draft);
        var existing = await _repo.GetEntryAsync(entryId);
        if (existing is null) return false;
        if (existing.UserId != (_currentUser.Current?.Id ?? 0)) return false;   // owner only
        if (!CanEditDay(existing.WorkDate)) return false;                        // lock

        var backlogId = await ResolveBacklogIdAsync(draft.BacklogId);
        var updated = existing with
        {
            Section = draft.Section,
            BacklogId = backlogId,
            BacklogCode = draft.BacklogCode.Trim(),
            TaskText = draft.TaskText.Trim(),
            Description = draft.Description ?? "",
            Deadline = draft.Deadline,
            Status = draft.Status,
        };
        await _repo.UpdateEntryAsync(updated);
        return true;
    }

    public async Task<bool> DeleteEntryAsync(int entryId)
    {
        var existing = await _repo.GetEntryAsync(entryId);
        if (existing is null) return false;
        if (existing.UserId != (_currentUser.Current?.Id ?? 0)) return false;
        if (!CanEditDay(existing.WorkDate)) return false;
        await _repo.DeleteEntryAsync(entryId);
        return true;
    }

    public async Task ReorderEntryAsync(int draggedId, int targetId)
    {
        if (draggedId == targetId) return;
        var dragged = await _repo.GetEntryAsync(draggedId);
        var target = await _repo.GetEntryAsync(targetId);
        if (dragged is null || target is null) return;

        var me = _currentUser.Current;
        if (me is null || dragged.UserId != me.Id) return;          // owner only
        if (!CanEditDay(dragged.WorkDate)) return;                  // edit-lock
        if (dragged.WorkDate != target.WorkDate) return;           // only within the shown day

        var section = target.Section;   // dropping onto the other section moves it there
        var all = await _repo.GetEntriesAsync(me.Id, dragged.WorkDate);

        // Rebuild the destination section's order with the dragged entry inserted at the target's slot.
        var dest = all.Where(e => e.Section == section && e.Id != draggedId)
                      .OrderBy(e => e.OrderIndex).ThenBy(e => e.Id).ToList();
        var idx = dest.FindIndex(e => e.Id == targetId);
        if (idx < 0) idx = dest.Count;
        dest.Insert(idx, dragged);
        for (var i = 0; i < dest.Count; i++)
            await _repo.UpdateEntryAsync(dest[i] with { Section = section, OrderIndex = i });

        // If it moved out of its old section, re-pack that section's order indexes.
        if (dragged.Section != section)
        {
            var src = all.Where(e => e.Section == dragged.Section && e.Id != draggedId)
                         .OrderBy(e => e.OrderIndex).ThenBy(e => e.Id).ToList();
            for (var i = 0; i < src.Count; i++)
                if (src[i].OrderIndex != i)
                    await _repo.UpdateEntryAsync(src[i] with { OrderIndex = i });
        }
    }

    public async Task<int> AddIssueAsync(int entryId, string issueText, string? solutionText, string status)
    {
        if (string.IsNullOrWhiteSpace(issueText)) throw new ArgumentException("issue text required", nameof(issueText));
        if (!StandupIssueStatus.All.Contains(status)) throw new ArgumentException($"invalid issue status '{status}'", nameof(status));
        return await _repo.InsertIssueAsync(new StandupIssue(
            0, entryId, issueText.Trim(), NullIfBlank(solutionText), status, 0, _clock.UtcNow));
    }

    public async Task UpdateIssueAsync(StandupIssue issue)
    {
        if (string.IsNullOrWhiteSpace(issue.IssueText)) throw new ArgumentException("issue text required", nameof(issue));
        if (!StandupIssueStatus.All.Contains(issue.Status)) throw new ArgumentException($"invalid issue status '{issue.Status}'", nameof(issue));
        await _repo.UpdateIssueAsync(issue with { SolutionText = NullIfBlank(issue.SolutionText), IssueText = issue.IssueText.Trim() });
    }

    public Task DeleteIssueAsync(int issueId) => _repo.DeleteIssueAsync(issueId);

    // ---- helpers ----

    private static void ValidateDraft(StandupEntryDraft d)
    {
        if (!StandupSection.IsValid(d.Section)) throw new ArgumentException($"invalid section '{d.Section}'");
        if (!StandupStatus.All.Contains(d.Status)) throw new ArgumentException($"invalid status '{d.Status}'");
        if (string.IsNullOrWhiteSpace(d.BacklogCode)) throw new ArgumentException("backlog code required");
        if (string.IsNullOrWhiteSpace(d.TaskText)) throw new ArgumentException("task text required");
    }

    // An ad-hoc code carries no backlog id; a provided id is kept only if the backlog still exists (DR-03).
    private async Task<int?> ResolveBacklogIdAsync(int? backlogId)
    {
        if (backlogId is not { } id) return null;
        var bl = await _backlogs.GetByIdAsync(id);
        return bl is null ? null : id;
    }

    private async Task<int> NextOrderAsync(int userId, DateOnly workDate, string section)
    {
        var existing = await _repo.GetEntriesAsync(userId, workDate);
        var inSection = existing.Where(e => e.Section == section).ToList();
        return inSection.Count == 0 ? 0 : inSection.Max(e => e.OrderIndex) + 1;
    }

    private async Task<ILookup<int, StandupIssue>> LoadIssuesAsync(IReadOnlyList<StandupEntry> entries)
    {
        var ids = entries.Select(e => e.Id).ToList();
        var issues = await _repo.GetIssuesForEntriesAsync(ids);
        return issues.ToLookup(i => i.EntryId);
    }

    private static UserStandup BuildUserStandup(
        int userId, string userName, IReadOnlyList<StandupEntry> entries,
        ILookup<int, StandupIssue> issues, bool editable)
    {
        List<StandupEntryView> ViewsFor(string section) => entries
            .Where(e => e.Section == section)
            .OrderBy(e => e.OrderIndex).ThenBy(e => e.Id)
            .Select(e => new StandupEntryView(e, issues[e.Id].OrderBy(i => i.OrderIndex).ThenBy(i => i.Id).ToList(), editable))
            .ToList();

        return new UserStandup(userId, userName, ViewsFor(StandupSection.Yesterday), ViewsFor(StandupSection.Today));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
