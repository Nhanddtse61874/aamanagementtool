using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Daily Report orchestration (DR-05..08). Groups entries by section, attaches issues, enforces the
// edit-lock + owner rule on entry writes, and exposes the backlog/task picker. Issues are collaborative
// (anyone, any day) so they are NOT gated.
public interface IStandupService
{
    // The signed-in member's standup for a day (Input tab, DR-07).
    Task<UserStandup> GetMyStandupAsync(DateOnly workDate);
    // Every active user's standup for a day (Board tab, DR-08).
    Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate);

    // Picker support (DR-07).
    Task<IReadOnlyList<Backlog>> SearchBacklogsAsync(string? term);
    Task<IReadOnlyList<TaskItem>> GetTasksForBacklogAsync(int backlogId);

    // Edit-lock: true only when workDate is today or yesterday (DR-06).
    bool CanEditDay(DateOnly workDate);

    // Entry writes (owner = current user + CanEditDay; rejected => returns 0 / no-op).
    Task<int> AddEntryAsync(DateOnly workDate, StandupEntryDraft draft);
    Task<bool> UpdateEntryAsync(int entryId, StandupEntryDraft draft);
    Task<bool> DeleteEntryAsync(int entryId);
    // Drag-reorder: move the dragged entry to the target entry's position (and section), same day + owner.
    Task ReorderEntryAsync(int draggedId, int targetId);

    // Issue writes — collaborative, not gated by owner/lock (DR-04).
    Task<int> AddIssueAsync(int entryId, string issueText, string? solutionText, string status);
    Task UpdateIssueAsync(StandupIssue issue);
    Task DeleteIssueAsync(int issueId);
}
