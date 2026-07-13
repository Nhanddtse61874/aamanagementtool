namespace TimesheetApp.Models;

// --- Daily Report (Standup) models — P7 / schema v5. ---
// Design: docs/superpowers/specs/2026-06-25-daily-report-standup-design.md §2.
// Issues attach to a StandupEntry (which already carries date + task + user), NOT to a
// (date, task_id) pair — required because tasks may be ad-hoc (no task_id).

// One standup row: a member's Yesterday/Today line for a request/task on a given day (DR-02).
public sealed record StandupEntry(
    int Id, int UserId, DateOnly WorkDate, string Section,
    int? BacklogId, string BacklogCode, string TaskText, string Description,
    DateOnly? Deadline, string Status, int OrderIndex, DateTimeOffset CreatedAt,
    int? TeamId = null);   // v8: owning team (null = unassigned, backfilled by bootstrap)

// Zero-or-more per entry (DR-04). SolutionText null/empty = pending discussion.
// RowVersion: v10 (M8.2) optimistic-concurrency token -- StandupIssues is deliberately collaborative
// (anyone may edit, DR-04), so it is the one standup table that can be raced. It arrives from the
// SELECT; UpdateIssueCheckedAsync takes it back as an EXPLICIT expectedVersion argument and never
// reads it off the record. Default 0 is a fail-closed sentinel (see User in Entities.cs).
public sealed record StandupIssue(
    int Id, int EntryId, string IssueText, string? SolutionText, string Status,
    int OrderIndex, DateTimeOffset CreatedAt, long RowVersion = 0)
{
    // A solved issue (has a solution) renders as "resolved" (green ✓); otherwise as a warning (amber ⚠).
    public bool HasSolution => !string.IsNullOrWhiteSpace(SolutionText);
}

// Allowed standup entry statuses (DR-05). Order = display order.
public static class StandupStatus
{
    public static readonly IReadOnlyList<string> All =
        new[] { "Todo", "In-process", "Done", "Pending" };
}

// Allowed issue statuses (DR-04).
public static class StandupIssueStatus
{
    public static readonly IReadOnlyList<string> All =
        new[] { "open", "pending", "resolved" };
}

// The two day-sections a standup entry can belong to.
public static class StandupSection
{
    public const string Yesterday = "yesterday";
    public const string Today = "today";

    public static bool IsValid(string s) =>
        s is Yesterday or Today;
}

// ---- Read / draft DTOs (service layer) ----

// One member's standup for a day, grouped by section, issues attached (DR-07/DR-08).
public sealed record UserStandup(
    int UserId, string UserName,
    IReadOnlyList<StandupEntryView> Yesterday,
    IReadOnlyList<StandupEntryView> Today);

// An entry plus its issues plus whether the current viewer may edit it (edit-lock, DR-06).
public sealed record StandupEntryView(
    StandupEntry Entry, IReadOnlyList<StandupIssue> Issues, bool Editable);

// Input payload for add/update of an entry (DR-07). UserId + WorkDate are stamped by the service.
public sealed record StandupEntryDraft(
    string Section, int? BacklogId, string BacklogCode, string TaskText,
    string Description, DateOnly? Deadline, string Status);
