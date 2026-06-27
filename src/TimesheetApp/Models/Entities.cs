namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive);

// P10 Multi-Team (schema v8). A top-level org entity; soft-deletable via IsActive (mirrors User).
public sealed record Team(int Id, string Name, bool IsActive, DateTimeOffset CreatedAt);

// Extra fields (start/end/period month/status) added in schema v2. Optional with defaults so existing
// constructors keep compiling; PeriodMonth is "yyyy-MM" (the fixed month a ticket belongs to).
public sealed record Backlog(
    int Id, string BacklogCode, string Project, DateTimeOffset CreatedAt,
    DateOnly? StartDate = null, DateOnly? EndDate = null,
    string? PeriodMonth = null, string? Type = null,
    int? AssigneeUserId = null,   // v4: the PCT person-in-charge / user responsible (null = unassigned)
    // v7 (Task List tracking). All nullable-defaulted so existing ctors keep compiling.
    DateOnly? DeadlineInternal = null, DateOnly? DeadlineExternal = null,
    decimal? RoughEstimateHours = null, decimal? OfficialEstimateHours = null,
    int? ProgressPercent = null, string? Note = null,
    int? PcaContactId = null,     // v7: external (PCA) contact (null = unassigned)
    int? TeamId = null);          // v8: owning team (null = unassigned, backfilled by bootstrap)

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

// --- P8 Task List entities (schema v7) ---

// User-defined tag (TAG-01): free-text label with an icon glyph/emoji and a hex color. Hard-deletable.
public sealed record Tag(int Id, string Text, string Icon, string Color, DateTimeOffset CreatedAt);

// External (PCA) contact (TL-11). Soft-deletable via IsActive, mirroring User.
public sealed record PcaContact(int Id, string Name, bool IsActive);

// A manually-marked non-working day (HOL-01). Date is the natural key.
public sealed record Holiday(DateOnly Date, string? Description);
