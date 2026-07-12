namespace TimesheetApp.Models;

// --- Entities (1:1 with tables). Names are VERBATIM from architecture spec §2. ---
// 'Task' collides with System.Threading.Tasks.Task -> the entity is named TaskItem.

// RowVersion: v10 (M8.2) optimistic-concurrency token, carried on every versioned record.
//
// It arrives from the SELECT (each repository projects row_version into its Raw DTO and hands it to
// the record) and travels back to a checked write as an EXPLICIT expectedVersion argument. No write
// path ever reads it off the record — see the note on the *CheckedAsync methods. That is what makes
// it safe to put here: a caller that builds a record from editor fields rather than from a read
// (RequestsViewModel.SaveEditAsync does exactly that) gets the default and is unaffected.
//
// The default is 0 on EVERY versioned record, and 0 is a fail-CLOSED sentinel: the schema declares
// row_version INTEGER NOT NULL DEFAULT 1, so no row in the database is ever at 0. A 0 that leaks
// into a check therefore matches nothing and raises a loud conflict. A default of 1 would fail OPEN —
// it would silently match a freshly-inserted row.
public sealed record User(int Id, string Name, string? WindowsUsername, bool IsActive, long RowVersion = 0);

// P10 Multi-Team (schema v8). A top-level org entity; soft-deletable via IsActive (mirrors User).
public sealed record Team(int Id, string Name, bool IsActive, DateTimeOffset CreatedAt, long RowVersion = 0);

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
    int? TeamId = null,           // v8: owning team (null = unassigned, backfilled by bootstrap)
    long RowVersion = 0);         // v10: optimistic-concurrency token (see User)

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
// v9 (B2): Note carries the reason captured by the deadline-change popup (null for other fields).
public sealed record BacklogAuditEntry(
    int Id, int BacklogId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt,
    string? Note = null);

// Allowed task statuses. Backlog status is derived from its tasks at runtime.
public static class TaskStatus
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Todo", "In-process", "Done", "Pending" };
}

public sealed record TaskItem(
    int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive,
    string Status = "Todo",
    string? Type = null,            // v9: task-level type (mirrors Backlog.Type)
    int? AssigneeUserId = null,     // v9: task-level PCT (mirrors Backlog.AssigneeUserId)
    long RowVersion = 0);           // v10: optimistic-concurrency token (see User)

// One audit-history row for a Task field change (v9); mirrors BacklogAuditEntry.
public sealed record TaskAuditEntry(
    int Id, int TaskId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt);

// TimeLogs is keyed by the natural (user_id, task_id, work_date) triple rather than by Id, but it is
// versioned like the rest: RowVersion is the token a timesheet cell hands back to UpsertCheckedAsync.
public sealed record TimeLog(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, DateTimeOffset CreatedAt,
    long RowVersion = 0);         // v10: optimistic-concurrency token (see User)

public sealed record TaskTemplate(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTask(int Id, string TaskName, int OrderIndex, bool IsActive);

// --- P8 Task List entities (schema v7) ---

// User-defined tag (TAG-01): free-text label with an icon glyph/emoji and a hex color. Hard-deletable.
public sealed record Tag(int Id, string Text, string Icon, string Color, DateTimeOffset CreatedAt, long RowVersion = 0);

// External (PCA) contact (TL-11). Soft-deletable via IsActive, mirroring User.
public sealed record PcaContact(int Id, string Name, bool IsActive, long RowVersion = 0);

// A manually-marked non-working day (HOL-01). Date is the natural key.
public sealed record Holiday(DateOnly Date, string? Description);
