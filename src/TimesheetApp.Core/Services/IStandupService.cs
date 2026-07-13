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

    /// <summary>M8.3: the version-checked sibling of <see cref="UpdateIssueAsync"/> — the web API's write
    /// path. Issues are the one standup table two people can genuinely race: they are collaborative by
    /// design (DR-04, no owner gate), so anyone may edit anyone's issue and a lost update is reachable.
    ///
    /// Validation still runs first and still throws <see cref="ArgumentException"/> (matching
    /// <see cref="UpdateIssueAsync"/>) — bad input is the caller's fault; a stale version is not.</summary>
    /// <param name="expectedVersion">Travels SEPARATELY from the record on purpose: a caller that rebuilt
    /// a StandupIssue from edited fields carries the record's default RowVersion of 0, and a write that
    /// trusted the record would reject it outright.</param>
    /// <returns>The row_version AFTER the write — the caller's next expectedVersion.</returns>
    /// <exception cref="Data.ConcurrencyConflictException">Version moved on, or the issue is gone.</exception>
    Task<long> UpdateIssueCheckedAsync(StandupIssue issue, long expectedVersion);

    // NO checked sibling for UpdateEntryAsync, and this is deliberate — do not add one. StandupEntry is
    // NOT versioned (the row has no row_version column at all; any write that tried to bump it would be a
    // SQL error, not a no-op). It is protected by an OWNER GATE instead — only the entry's owner may touch
    // it — so two users cannot reach the same row, and last-write-wins is correct BY DESIGN rather than by
    // omission. Inventing a version for it would add a mechanism to a race that cannot happen.
    //
    // Smart Fill (ITimeLogService.ApplySmartFillAsync / ApplySmartInputAsync) has no checked sibling for a
    // different reason: it BUMPS but does not CHECK. It is a server-side computation, not an echo of a
    // version a client read, so it has nothing to check against — but it must still bump, or a client
    // holding the pre-fill version would still match and silently overwrite Smart Fill's result: the exact
    // lost update this whole mechanism exists to kill, reintroduced by its own exception.
}
