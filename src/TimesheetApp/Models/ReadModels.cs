namespace TimesheetApp.Models;

// --- Read / projection models. Shapes are VERBATIM from architecture spec §2. ---
// Only the smart-input types are defined here for P2 Task 1; later P2/P3 tasks extend
// this file with TimeLogReportRow, WeekGrid, WeekRow, etc.

// Flat report/export join row — INNER JOIN by id with NO is_active filter (XC-06),
// so soft-deleted task/user names still resolve. Shape VERBATIM from architecture spec §2.
public sealed record TimeLogReportRow(
    int UserId, string UserName,
    string RequestCode, string Project,
    int TaskId, string TaskName,
    DateOnly WorkDate, decimal Hours);

// Smart-input preview cell (pure math output; also the apply unit). (SI-01/05)
public readonly record struct CellAssignment(DateOnly Date, decimal Hours);

// Smart-input result. Ok=false => no-op with a message (SI-03).
public readonly record struct SmartInputResult(
    bool Ok, IReadOnlyList<CellAssignment> Cells, string? Error);

// Shaped week for the Timesheet grid (one row per active task, 5 day slots). Shapes VERBATIM
// from architecture spec §2. (TS-01/02/05)
public sealed record WeekGrid(DateOnly Monday, IReadOnlyList<WeekRow> Rows);
public sealed record WeekRow(
    int TaskId, string RequestCode, string TaskName, int OrderIndex,
    decimal? Mon, decimal? Tue, decimal? Wed, decimal? Thu, decimal? Fri);  // null = empty = 0h

// Save / validation result. Ok=false => no write, with a user-facing message. Shape VERBATIM
// from architecture spec §2. (XC-02/03/04/05)
public readonly record struct SaveResult(bool Ok, string? Error);

// Current-user resolution outcome (XC-07). Shapes VERBATIM from architecture spec §2.
public enum CurrentUserOutcome { Resolved, NeedsSelection }
public readonly record struct CurrentUserResult(CurrentUserOutcome Outcome, User? User);
