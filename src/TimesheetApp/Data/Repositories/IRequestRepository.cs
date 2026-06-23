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
    // Edit name/project + v2 start/end/month/status. changedBy is recorded in RequestAudit when the
    // four audited fields change (optional so existing callers/tests keep compiling).
    Task UpdateAsync(Request request, int? changedByUserId = null, string? changedByName = null);
    Task<IReadOnlyList<RequestAuditEntry>> GetAuditAsync(int requestId);   // v2 change history
    // No SetActiveAsync — Requests are NOT soft-deletable in v1 (REQ-04, decision 4).
}
