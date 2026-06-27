using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// P10 Multi-Team (TM-03). Team CRUD (soft-delete via SetActiveAsync, mirroring IUserRepository) plus
// UserTeams membership (many-to-many). SetMembersAsync replaces the whole member set in one tx
// (mirrors IBacklogRepository.SetTagsAsync). SQL + Dapper only; one short connection per method.
public interface ITeamRepository
{
    Task<IReadOnlyList<Team>> GetAllAsync();          // Settings list (incl. inactive)
    Task<IReadOnlyList<Team>> GetActiveAsync();       // switcher + filter source
    Task<Team?> GetByIdAsync(int id);
    Task<Team?> GetByNameAsync(string name);          // bootstrap idempotency ("Architect Improvement")
    Task<int> InsertAsync(Team team);                 // returns new id
    Task UpdateNameAsync(int id, string name);        // rename
    Task SetActiveAsync(int id, bool isActive);       // soft delete (mirrors Users)

    // membership (UserTeams)
    Task<IReadOnlyList<int>> GetTeamIdsForUserAsync(int userId);   // the user's teams (switcher/filter)
    Task<IReadOnlyList<int>> GetUserIdsForTeamAsync(int teamId);   // Settings membership editor
    Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds);  // replace-all in one tx (like SetTagsAsync)
    Task AddMemberAsync(int userId, int teamId);                   // first-run user join (idempotent)
}
