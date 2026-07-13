using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// P10 Multi-Team (TM-03). Team CRUD (soft-delete via SetActiveAsync, mirroring IUserRepository) plus
// UserTeams membership (many-to-many). SetMembersAsync replaces the whole member set in one tx
// (mirrors IBacklogRepository.SetTagsAsync). SQL + Dapper only; one short connection per method.
//
// M8.2 optimistic concurrency: every versioned write comes as a PAIR (see IBacklogRepository for the
// full rationale). <Verb>Async is BUMP-ONLY -- always lands, always bumps row_version, never throws --
// and is what existing callers use, since none of them carries a version. <Verb>CheckedAsync is
// CHECK-AND-BUMP: it lands only at expectedVersion, throws ConcurrencyConflictException otherwise, and
// RETURNS the new row_version so the caller never has to re-read it (a read-back is racy).
// SetActiveAsync is bump-only with no checked sibling: a deactivation doesn't need to know what the
// caller last saw. Membership has no row_version of its own (UserTeams is a pure join table keyed on
// the pair), so the SetMembers pair gates on the team's own row_version.
public interface ITeamRepository
{
    Task<IReadOnlyList<Team>> GetAllAsync();          // Settings list (incl. inactive)
    Task<IReadOnlyList<Team>> GetActiveAsync();       // switcher + filter source
    Task<Team?> GetByIdAsync(int id);
    Task<Team?> GetByNameAsync(string name);          // bootstrap idempotency ("Architect Improvement")
    Task<int> InsertAsync(Team team);                 // returns new id
    Task UpdateNameAsync(int id, string name);        // rename: bump-only
    Task<long> UpdateNameCheckedAsync(int id, string name, long expectedVersion);
    Task SetActiveAsync(int id, bool isActive);       // soft delete (mirrors Users): bump-only

    // membership (UserTeams)
    Task<IReadOnlyList<int>> GetTeamIdsForUserAsync(int userId);   // the user's teams (switcher/filter)
    Task<IReadOnlyList<int>> GetUserIdsForTeamAsync(int teamId);   // Settings membership editor
    Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds);  // replace-all in one tx
    Task<long> SetMembersCheckedAsync(int teamId, IReadOnlyList<int> userIds, long expectedVersion);
    Task AddMemberAsync(int userId, int teamId);                   // first-run user join (idempotent)
}
