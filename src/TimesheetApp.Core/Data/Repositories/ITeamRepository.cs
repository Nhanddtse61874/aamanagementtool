using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// P10 Multi-Team (TM-03). Team CRUD (soft-delete via SetActiveAsync, mirroring IUserRepository) plus
// UserTeams membership (many-to-many). SetMembersAsync replaces the whole member set in one tx
// (mirrors IBacklogRepository.SetTagsAsync). SQL + Dapper only; one short connection per method.
//
// M8.2 optimistic concurrency: UpdateNameAsync and SetMembersAsync take an optional expectedVersion.
// Supplied -> check-and-bump (throws ConcurrencyConflictException on mismatch). Omitted -> bump-only
// (row_version still advances, but nothing is checked) -- the shape existing callers keep compiling
// against until they're wired to carry a version. SetActiveAsync is always bump-only: a deactivation
// doesn't need to know what the caller last saw. Membership has no row_version of its own (UserTeams
// is a pure join table keyed on the pair) so SetMembersAsync gates on the team's own row_version.
public interface ITeamRepository
{
    Task<IReadOnlyList<Team>> GetAllAsync();          // Settings list (incl. inactive)
    Task<IReadOnlyList<Team>> GetActiveAsync();       // switcher + filter source
    Task<Team?> GetByIdAsync(int id);
    Task<Team?> GetByNameAsync(string name);          // bootstrap idempotency ("Architect Improvement")
    Task<int> InsertAsync(Team team);                 // returns new id
    Task UpdateNameAsync(int id, string name, long? expectedVersion = null);   // rename: check-and-bump
    Task SetActiveAsync(int id, bool isActive);       // soft delete (mirrors Users): bump-only

    // membership (UserTeams)
    Task<IReadOnlyList<int>> GetTeamIdsForUserAsync(int userId);   // the user's teams (switcher/filter)
    Task<IReadOnlyList<int>> GetUserIdsForTeamAsync(int teamId);   // Settings membership editor
    Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds, long? expectedVersion = null); // replace-all in one tx, gated on Teams.row_version
    Task AddMemberAsync(int userId, int teamId);                   // first-run user join (idempotent)
}
