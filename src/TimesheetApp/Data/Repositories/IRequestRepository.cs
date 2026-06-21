using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (RequestRepository)
// is owned by P2 Task 5 (Wave 2).
public interface IRequestRepository
{
    Task<IReadOnlyList<Request>> SearchAsync(string? term);        // null => all; matches code OR project (REQ-01)
    Task<Request?> GetByIdAsync(int id);
    Task<Request?> GetByCodeAsync(string requestCode);             // find hidden 'DEFAULT' (DATA-03)
    Task<int> InsertAsync(Request request);                        // REQ-02
    Task UpdateAsync(Request request);                             // edit name/project (REQ-03)
    // No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
}
