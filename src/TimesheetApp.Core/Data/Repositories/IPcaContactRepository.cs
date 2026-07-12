using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// External (PCA) contacts (TL-11). Soft-delete only (SetActiveAsync), mirroring IUserRepository —
// a deactivated contact may still be referenced by historical backlogs.
public interface IPcaContactRepository
{
    Task<IReadOnlyList<PcaContact>> GetAllAsync();    // incl. inactive (Settings list)
    Task<IReadOnlyList<PcaContact>> GetActiveAsync(); // active only (editor combo)
    Task<PcaContact?> GetByIdAsync(int id);
    Task<int> InsertAsync(PcaContact contact);        // returns new id
    Task UpdateNameAsync(int id, string name);
    Task SetActiveAsync(int id, bool isActive);       // soft delete
}
