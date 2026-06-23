namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

// Extra fields (start/end/period month/status) added in schema v2. Optional with defaults so existing
// constructors keep compiling; PeriodMonth is "yyyy-MM" (the fixed month a ticket belongs to).
public sealed record Request(
    int Id, string RequestCode, string Project, DateTimeOffset CreatedAt,
    DateOnly? StartDate = null, DateOnly? EndDate = null,
    string? PeriodMonth = null, string? Status = null);

// Allowed ticket statuses (v2). Order is the display order.
public static class RequestStatus
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Continue", "Implement", "Investigate", "IT", "Estimate" };
}

// One audit-history row for a Request field change (v2): who changed what, old -> new, when.
public sealed record RequestAuditEntry(
    int Id, int RequestId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt);

public sealed record TaskItem(int Id, int RequestId, string TaskName, int OrderIndex, bool IsActive);

public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt);

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);
