using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (TimeLogRepository)
// is owned by P2 Task 4 (Wave 2). Report/export joins use INNER JOIN by id with NO is_active
// filter (XC-06) so soft-deleted names still resolve; UpsertBatchAsync is one transaction (SI-05).
public interface ITimeLogRepository
{
    // Upsert on UNIQUE(user_id, task_id, work_date) via INSERT ... ON CONFLICT DO UPDATE (TS-07/SI-05).
    Task UpsertAsync(TimeLog log);
    Task DeleteAsync(int userId, int taskId, DateOnly workDate);   // empty cell => remove row (TS-03)
    Task<IReadOnlyList<TimeLog>> GetByUserAndRangeAsync(int userId, DateOnly from, DateOnly to);  // week grid + 8h day-total read (XC-03)
    Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to); // RPT-01..03
    Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter); // EXP-01..04
    Task<IReadOnlyList<int>> GetUserIdsWithLogsInRangeAsync(DateOnly from, DateOnly to);  // RPT-04 single-range scan
    // Batch upsert in one transaction for smart-input apply (SI-05 atomicity).
    Task UpsertBatchAsync(IReadOnlyList<TimeLog> logs);
}
