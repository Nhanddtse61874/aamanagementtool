using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Daily Report orchestration (DR-05..08). Pure orchestration over the repo + current-user + request/task
// repos; no SQL here. Edit-lock (today/yesterday) + owner gate entry writes; issues are collaborative.
public sealed class StandupService : IStandupService
{
    private readonly IStandupRepository _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserRepository _users;
    private readonly IRequestRepository _requests;
    private readonly ITaskRepository _tasks;
    private readonly IClock _clock;

    public StandupService(
        IStandupRepository repo, ICurrentUserService currentUser, IUserRepository users,
        IRequestRepository requests, ITaskRepository tasks, IClock clock)
    {
        _repo = repo;
        _currentUser = currentUser;
        _users = users;
        _requests = requests;
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

        var entries = await _repo.GetEntriesAsync(me.Id, workDate);
        var issues = await LoadIssuesAsync(entries);
        var editable = CanEditDay(workDate); // owner is always the current user here
        return BuildUserStandup(me.Id, me.Name, entries, issues, editable);
    }

    public async Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate)
    {
        var active = await _users.GetActiveAsync() ?? Array.Empty<User>();
        var all = await _repo.GetEntriesForDayAsync(workDate);
        var issues = await LoadIssuesAsync(all);
        var currentId = _currentUser.Current?.Id ?? 0;

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

    public Task<IReadOnlyList<Request>> SearchRequestsAsync(string? term) => _requests.SearchAsync(term);

    public Task<IReadOnlyList<TaskItem>> GetTasksForRequestAsync(int requestId) =>
        _tasks.GetActiveByRequestAsync(requestId);

    public async Task<int> AddEntryAsync(DateOnly workDate, StandupEntryDraft draft)
    {
        ValidateDraft(draft);
        if (!CanEditDay(workDate)) return 0;          // locked day -> no-op
        var me = _currentUser.Current;
        if (me is null) return 0;

        var requestId = await ResolveRequestIdAsync(draft.RequestId);
        var entry = new StandupEntry(
            0, me.Id, workDate, draft.Section, requestId, draft.RequestCode.Trim(),
            draft.TaskText.Trim(), draft.Description ?? "", draft.Deadline, draft.Status,
            await NextOrderAsync(me.Id, workDate, draft.Section), _clock.UtcNow);
        return await _repo.InsertEntryAsync(entry);
    }

    public async Task<bool> UpdateEntryAsync(int entryId, StandupEntryDraft draft)
    {
        ValidateDraft(draft);
        var existing = await _repo.GetEntryAsync(entryId);
        if (existing is null) return false;
        if (existing.UserId != (_currentUser.Current?.Id ?? 0)) return false;   // owner only
        if (!CanEditDay(existing.WorkDate)) return false;                        // lock

        var requestId = await ResolveRequestIdAsync(draft.RequestId);
        var updated = existing with
        {
            Section = draft.Section,
            RequestId = requestId,
            RequestCode = draft.RequestCode.Trim(),
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
        if (string.IsNullOrWhiteSpace(d.RequestCode)) throw new ArgumentException("request code required");
        if (string.IsNullOrWhiteSpace(d.TaskText)) throw new ArgumentException("task text required");
    }

    // An ad-hoc code carries no request id; a provided id is kept only if the request still exists (DR-03).
    private async Task<int?> ResolveRequestIdAsync(int? requestId)
    {
        if (requestId is not { } id) return null;
        var req = await _requests.GetByIdAsync(id);
        return req is null ? null : id;
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
