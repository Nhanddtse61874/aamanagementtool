namespace TimesheetApp.Models;

// --- Read / projection models. Shapes are VERBATIM from architecture spec §2. ---
// Only the smart-input types are defined here for P2 Task 1; later P2/P3 tasks extend
// this file with TimeLogReportRow, WeekGrid, WeekRow, etc.

// Flat report/export join row — INNER JOIN by id with NO is_active filter (XC-06),
// so soft-deleted task/user names still resolve. Shape VERBATIM from architecture spec §2.
public sealed record TimeLogReportRow(
    int UserId, string UserName,
    string BacklogCode, string Project,
    int TaskId, string TaskName,
    DateOnly WorkDate, decimal Hours,
    // v8 (P10 Multi-Team): the owning team, projected trailing so existing positional ctors keep
    // compiling (R6). Null when the backlog has no team (pre-bootstrap).
    int? TeamId = null, string? TeamName = null);

// Smart-input preview cell (pure math output; also the apply unit). (SI-01/05)
public readonly record struct CellAssignment(DateOnly Date, decimal Hours);

// Smart-input result. Ok=false => no-op with a message (SI-03).
public readonly record struct SmartInputResult(
    bool Ok, IReadOnlyList<CellAssignment> Cells, string? Error);

// Smart-fill plan for one task: the cells to write. A whole smart-fill is a list of these
// (one per checked task) validated + applied together against the shared 8h/day cap.
public sealed record SmartFillTask(int TaskId, IReadOnlyList<CellAssignment> Cells);

// Shaped week for the Timesheet grid (one row per active task, 5 day slots). Shapes VERBATIM
// from architecture spec §2. (TS-01/02/05)
public sealed record WeekGrid(DateOnly Monday, IReadOnlyList<WeekRow> Rows);

// One day slot: the hours, AND the optimistic-concurrency token for that cell (M8.4).
//
// The version is per (task, date) — per CELL, not per row — because that is exactly what TimeLogs is
// keyed by (user_id, task_id, work_date), and therefore exactly what UpsertCheckedAsync versions. A
// row-level version would be a lie: the five day slots on one WeekRow are five independent TimeLogs rows,
// each with its own row_version, each editable without touching the others.
//
// This type exists because the week read USED to project `decimal?` and throw the row_version away. That
// left the web client holding hours and no version, so `PUT /api/timesheet/cell` had nothing to send as
// expectedVersion — and null is not a safe stand-in: null deliberately asserts "I believe this cell is
// EMPTY" (see ITimeLogRepository.UpsertCheckedAsync's five-case table), so every edit of a pre-existing
// cell would 409, for every user, forever.
//
//   Hours == null  =>  the cell is empty  =>  RowVersion is null: there is no row, so there is nothing to
//                      version — and null is precisely the expectedVersion that asserts "still empty".
//   Hours != null  =>  a row exists and RowVersion is ITS row_version — the caller's next expectedVersion.
//
// The ONE legitimate exception is hours-without-a-version, and it is not an oversight: the read-only team
// aggregate (TimeLogService.GetWeekGroupedAllUsersAsync) SUMS hours across users, so no single row backs
// the cell and no single version can exist. See the comment there before trying to "fix" it.
public readonly record struct WeekCell(decimal? Hours, long? RowVersion);

// EXACTLY ONE CONSTRUCTOR — deliberately, and do not add a second.
//
// This type is the wire contract for GET /api/timesheet/week (the endpoint serialises it straight onto the
// response; there is no week DTO), so System.Text.Json must be able to pick a SINGULAR parameterized
// constructor to deserialize it. An hours-only convenience overload — the obvious way to spare the older
// positional test fixtures — makes that ambiguous and STJ throws NotSupportedException on every week read.
// It is also, independently, a constructor that builds a WeekRow with no versions: the exact silent
// version-drop this milestone exists to eliminate. Tests that only care about hours use the `WeekRows.Row`
// builder in the test project instead — terseness belongs there, not in the contract.
public sealed record WeekRow(
    int TaskId, string BacklogCode, string TaskName, int OrderIndex,
    WeekCell Mon, WeekCell Tue, WeekCell Wed, WeekCell Thu, WeekCell Fri);

// Grouped-by-backlog shape for the Timesheet tab: EVERY backlog item (incl. DEFAULT and empty ones)
// becomes one collapsible group; Tasks may be empty so an empty backlog still renders + is loggable.
public sealed record WeekBacklogGroup(
    int BacklogId, string BacklogCode, string Project, IReadOnlyList<WeekRow> Tasks,
    string? PeriodMonth = null, string? Type = null,    // v2: month a ticket belongs to + its type
    string? AssigneeName = null);                        // v4: who the ticket is assigned to

// Save / validation result. Ok=false => no write, with a user-facing message. Shape VERBATIM
// from architecture spec §2. (XC-02/03/04/05)
public readonly record struct SaveResult(bool Ok, string? Error);

// Export selection (user/month/project). Shape VERBATIM from architecture spec §2. (EXP-01..04)
// v8 (P10 Multi-Team): TeamIds appended trailing (null = all teams = legacy behavior, keeps existing
// positional ctors compiling). When set, the export is scoped to the checked teams (no cross-team leak).
public readonly record struct ExportFilter(
    int? UserId, int Year, int Month, string? Project, IReadOnlyList<int>? TeamIds = null);

// Current-user resolution outcome (XC-07). Shapes VERBATIM from architecture spec §2.
public enum CurrentUserOutcome { Resolved, NeedsSelection }
public readonly record struct CurrentUserResult(CurrentUserOutcome Outcome, User? User);

// ---- P5 Reports read-models (projections built from TimeLogReportRow) ----

// RPT-01: one row per weekday in the selected week.
public sealed record WeeklyDayTotal(DateOnly Date, decimal TotalHours);

// RPT-01 detail: one row per (date, backlog, task) in the selected week — shows which ticket/task
// the hours went to, not just the day total.
public sealed record WeeklyDetailRow(
    DateOnly Date, string BacklogCode, string Project, string TaskName, decimal TotalHours);

// RPT-02: one row per (backlog, task) in the selected month.
public sealed record MonthlyBacklogTaskTotal(
    string BacklogCode, string Project, string TaskName, decimal TotalHours);

// RPT-03: drill-down tree. v8 (P10) adds a Team top node above Project so a multi-team report groups
// by team first (Team -> Project -> Backlog -> Task -> Date). When a single team is selected the tree
// still has exactly one TeamNode root. Null team name (pre-bootstrap rows) renders as "(no team)".
public sealed record TeamNode(string TeamName, decimal TotalHours, IReadOnlyList<ProjectNode> Projects);
public sealed record ProjectNode(string Project, decimal TotalHours, IReadOnlyList<BacklogNode> Backlogs);
public sealed record BacklogNode(string BacklogCode, string Project, decimal TotalHours, IReadOnlyList<TaskNode> Tasks);
public sealed record TaskNode(int TaskId, string TaskName, decimal TotalHours, IReadOnlyList<DateEntry> Dates);
public sealed record DateEntry(DateOnly Date, decimal TotalHours);

// RPT-04: one flagged active user (banner shows the configured N, not the actual gap).
public sealed record MissingLogWarning(string UserName);

// ---- P8 Task List read-models (spec §2.4) ----

// Schedule chip state for a backlog (TL-07/08). Late takes precedence over Warning.
public enum ScheduleState { Normal, Warning, Late }

// One Task List grid row per non-DEFAULT backlog in the selected month. Logged hours are all-time;
// EstimateHours is official ?? rough; Tasks are the backlog's tasks (for the expand panel).
public sealed record TaskListRow(
    int BacklogId, string BacklogCode, string Project, string? Type,
    string? PctAssigneeName, string? PcaContactName,
    DateOnly? DeadlineInternal, DateOnly? DeadlineExternal, DateOnly? StartDate, DateOnly? EndDate,
    int? ProgressPercent, decimal LoggedHours, decimal? EstimateHours,
    ScheduleState ScheduleState, IReadOnlyList<Tag> Tags, IReadOnlyList<TaskItem> Tasks);

// One Gantt bar: start->internal-deadline over the working-day axis. Indices are positions on
// GanttModel.Axis; HasStart=false => faint placeholder row (no start_date).
public sealed record GanttBar(int BacklogId, string BacklogCode,
    DateOnly? Start, DateOnly? End, int StartDayIndex, int SpanWorkingDays,
    int? ExternalMarkerIndex, bool HasStart, ScheduleState ScheduleState);

// The Gantt chart model: a working-day axis (weekends/holidays excluded) + one bar per backlog.
public sealed record GanttModel(IReadOnlyList<DateOnly> Axis, IReadOnlyList<GanttBar> Bars);

// M9 (P1c): the WHOLE Task List screen in ONE value — the grid rows and the Gantt built from those
// same rows, at one instant.
//
// It is one record rather than two calls on purpose. The grid's schedule chips and the Gantt's bar
// colours are the SAME ScheduleState; fetched separately, a write landing between the two calls would
// let the chart and the grid disagree on screen with no way for the client to notice. Rows and Bars
// here are guaranteed to have been computed from a single snapshot.
public sealed record TaskListScreen(IReadOnlyList<TaskListRow> Rows, GanttModel Gantt);
