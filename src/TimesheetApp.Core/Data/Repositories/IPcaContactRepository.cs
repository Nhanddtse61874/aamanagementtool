using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// External (PCA) contacts (TL-11). Soft-delete only (SetActiveAsync), mirroring IUserRepository —
// a deactivated contact may still be referenced by historical backlogs.
//
// M8.2 optimistic concurrency (see IBacklogRepository for the full rationale): UpdateNameAsync is
// BUMP-ONLY; UpdateNameCheckedAsync is CHECK-AND-BUMP and returns the new row_version.
// SetActiveAsync is bump-only with no checked sibling: a deactivation doesn't need to know what the
// caller last saw.
public interface IPcaContactRepository
{
    Task<IReadOnlyList<PcaContact>> GetAllAsync();    // incl. inactive (Settings list)
    Task<IReadOnlyList<PcaContact>> GetActiveAsync(); // active only (editor combo)
    Task<PcaContact?> GetByIdAsync(int id);
    Task<int> InsertAsync(PcaContact contact);        // returns new id
    Task UpdateNameAsync(int id, string name);        // bump-only
    Task<long> UpdateNameCheckedAsync(int id, string name, long expectedVersion);
    Task SetActiveAsync(int id, bool isActive);       // soft delete: bump-only
}
