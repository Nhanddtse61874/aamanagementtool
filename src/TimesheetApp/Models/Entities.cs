namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

// Extra fields (start/end/period month/status) added in schema v2. Optional with defaults so existing
// constructors keep compiling; PeriodMonth is "yyyy-MM" (the fixed month a ticket belongs to).
public sealed record Backlog(
    int Id, string BacklogCode, string Project, DateTimeOffset CreatedAt,
    DateOnly? StartDate = null, DateOnly? EndDate = null,
    string? PeriodMonth = null, string? Type = null,
    int? AssigneeUserId = null);   // v4: the user responsible for this ticket (null = unassigned)

// Allowed ticket types (v2, formerly "status"). Order is the display order.
public static class BacklogType
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Continue", "Implement", "Investigate", "IT", "Estimate" };
}

// Allowed projects (v2) — a fixed enum-like set chosen from a dropdown.
public static class BacklogProjects
{
    public static readonly IReadOnlyList<string> All =
        new[] { "ARCS", "PlusArcs", "ARMS", "Other" };
}

// One audit-history row for a Backlog field change (v2): who changed what, old -> new, when.
public sealed record BacklogAuditEntry(
    int Id, int BacklogId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt);

// Allowed task statuses. Backlog status is derived from its tasks at runtime.
public static class TaskStatus
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Todo", "In-process", "Done", "Pending" };
}

public sealed record TaskItem(int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive, string Status = "Todo");

public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt);

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);
