using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Daily Report orchestration (DR-05..08). Groups entries by section, attaches issues, enforces the
// edit-lock + owner rule on entry writes, and exposes the backlog/task picker. Issues are collaborative
// (anyone, any day) so they are NOT gated.
public interface IStandupService
{
    // The signed-in member's standup for a day, scoped to the active team (Input tab, DR-07 / TM-06).
    Task<UserStandup> GetMyStandupAsync(DateOnly workDate);
    // The multi-team board feed for a day (Board tab, DR-08 / TM-07): entries for the checked teams,
    // one card per MEMBER of those teams. Empty teamIds => empty board.
    Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate, IReadOnlyList<int> teamIds);
    // Convenience overload defaulting to the active team only (kept so the W7-unmodified VM compiles).
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

    // P18 Quick Import: clone the current user's standup for sourceDate (both sections + their issues) into
    // targetDate, APPENDING — new ids/timestamps/order, everything else copied. Scoped to current user +
    // active team. Returns # entries cloned; a locked target / no current user / empty source => 0 (no-op).
    // The source day is never modified.
    Task<int> QuickImportDayAsync(DateOnly sourceDate, DateOnly targetDate);

    // Issue writes — collaborative, not gated by owner/lock (DR-04).
    Task<int> AddIssueAsync(int entryId, string issueText, string? solutionText, string status);
    Task UpdateIssueAsync(StandupIssue issue);
    Task DeleteIssueAsync(int issueId);
}
