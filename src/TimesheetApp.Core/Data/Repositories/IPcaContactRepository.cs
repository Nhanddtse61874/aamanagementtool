using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// External (PCA) contacts (TL-11). Soft-delete only (SetActiveAsync), mirroring IUserRepository —
// a deactivated contact may still be referenced by historical backlogs.
//
// M8.2 optimistic concurrency: UpdateNameAsync takes an optional expectedVersion — supplied ->
// check-and-bump (throws ConcurrencyConflictException on mismatch); omitted -> bump-only.
// SetActiveAsync is always bump-only: a deactivation doesn't need to know what the caller last saw.
public interface IPcaContactRepository
{
    Task<IReadOnlyList<PcaContact>> GetAllAsync();    // incl. inactive (Settings list)
    Task<IReadOnlyList<PcaContact>> GetActiveAsync(); // active only (editor combo)
    Task<PcaContact?> GetByIdAsync(int id);
    Task<int> InsertAsync(PcaContact contact);        // returns new id
    Task UpdateNameAsync(int id, string name, long? expectedVersion = null);   // check-and-bump
    Task SetActiveAsync(int id, bool isActive);       // soft delete: bump-only
}
