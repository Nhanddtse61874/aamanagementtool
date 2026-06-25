# Daily Report (Daily Standup) — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorm) — 2 implementation details pending confirmation (see §10)
**Stack:** .NET 8 / WPF MVVM / SQLite + Dapper (TimesheetApp)
**Replaces:** the `SOON` placeholder nav item "Daily Report" in `MainWindow.xaml`

## 1. Purpose & Scope

A **Daily Standup** surface for the team (team **size is not fixed** — render all active
users dynamically). Each member fills in **their own** standup for a given day ("what I
did **yesterday**" + "what I'm doing **today**"), per request/task, with a deadline, a
status, and optional **issues/solutions**. Anyone can open the **board** that shows every
member's standup for the selected day together, to run the daily meeting.

This is **not** an hours-aggregation report (Reports already covers weekly/monthly hours).
Daily Report is a **narrative standup tool** with its own persisted data, **stored per
day**. Each weekday's data is persisted independently; a **weekly markdown archive** is
generated for backup/log (default trigger: **Friday**).

Two surfaces (sub-tabs under the Daily Report nav, mirroring how Timesheet hosts
Entry/Requests/Reports sub-tabs):
- **Input** tab — the signed-in member enters/edits only their own standup.
- **Board** tab — read view of the whole team for the selected day (the meeting view).

### In scope (v1)
- Per-user self-entry of Yesterday/Today rows for a selected date.
- Request column = pick an existing request **or** type an ad-hoc code (request may not
  exist in DB yet — no reverse-link/backfill; standup data is consumed separately).
- When an existing request is picked, its **tasks are pickable** to save typing.
- Deadline + Status are **typed/edited manually**, applied at the **task-row** level.
- Multiple **Issues** per row; each issue has a **status (open / pending / resolved)** and
  an optional Solution; **anyone** can edit an issue/solution/status (collaborative).
- **Edit-lock window** on a member's own entries (see §10.1).
- **Board** view showing all active users for the selected day.
- **Weekly markdown archive** of standup data (see §6), default on Friday.

### Out of scope (v1)
- No auto-carry of Yesterday from the previous day's Today (tasks change during the day).
- No reverse-link of ad-hoc request codes to Requests created later.
- No export beyond the weekly markdown archive.
- No cross-user editing of another member's standup rows (issues are the exception).

## 2. Data Model — Migration v5

Two new tables, added to `DatabaseInitializer` gated on `user_version` (current = 4 → 5).

```sql
CREATE TABLE StandupEntries (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id      INTEGER NOT NULL,           -- owner (the member who wrote it)
  work_date    TEXT    NOT NULL,           -- 'yyyy-MM-dd'  (per-day storage)
  section      TEXT    NOT NULL,           -- 'yesterday' | 'today'
  request_id   INTEGER NULL,               -- set when chosen from existing Requests
  request_code TEXT    NOT NULL,           -- code (existing OR ad-hoc free text)
  task_text    TEXT    NOT NULL,           -- task name (from request task OR free text)
  description  TEXT    NOT NULL DEFAULT '',-- "what did you do / are you doing"
  deadline     TEXT    NULL,               -- 'yyyy-MM-dd', optional, task-level
  status       TEXT    NOT NULL,           -- Todo | In-process | Done | Pending
  order_index  INTEGER NOT NULL DEFAULT 0,
  created_at   TEXT    NOT NULL            -- used by the edit-lock window
);
CREATE INDEX ix_standup_user_date ON StandupEntries(user_id, work_date);
CREATE INDEX ix_standup_date      ON StandupEntries(work_date);

CREATE TABLE StandupIssues (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  entry_id      INTEGER NOT NULL REFERENCES StandupEntries(id) ON DELETE CASCADE,
  issue_text    TEXT    NOT NULL,
  solution_text TEXT    NULL,              -- empty/null = pending discussion
  status        TEXT    NOT NULL DEFAULT 'open', -- open | pending | resolved
  order_index   INTEGER NOT NULL DEFAULT 0,
  created_at    TEXT    NOT NULL
);
CREATE INDEX ix_standup_issue_entry ON StandupIssues(entry_id);
```

**Decision:** Issues attach to the **standup entry** (date + task + user already on the
entry), not to `(date, task_id)` — required because tasks may be ad-hoc (no `task_id`),
and it keeps each member's issues under their own row. A row with zero StandupIssues has
no issue block ("only saved when created").

### Entities / read models (Models)
```csharp
public sealed record StandupEntry(
    int Id, int UserId, DateOnly WorkDate, string Section,
    int? RequestId, string RequestCode, string TaskText, string Description,
    DateOnly? Deadline, string Status, int OrderIndex, DateTimeOffset CreatedAt);

public sealed record StandupIssue(
    int Id, int EntryId, string IssueText, string? SolutionText, string Status,
    int OrderIndex, DateTimeOffset CreatedAt);

public static class StandupStatus
{   public static readonly IReadOnlyList<string> All = new[]{ "Todo","In-process","Done","Pending" }; }
public static class StandupIssueStatus
{   public static readonly IReadOnlyList<string> All = new[]{ "open","pending","resolved" }; }
public static class StandupSection { public const string Yesterday = "yesterday", Today = "today"; }
```

## 3. Data Access — `IStandupRepository`

```csharp
public interface IStandupRepository
{
    Task<IReadOnlyList<StandupEntry>> GetEntriesAsync(int userId, DateOnly workDate);
    Task<IReadOnlyList<StandupEntry>> GetEntriesForDayAsync(DateOnly workDate);   // board
    Task<int>  InsertEntryAsync(StandupEntry entry);
    Task UpdateEntryAsync(StandupEntry entry);
    Task DeleteEntryAsync(int entryId);                  // cascades issues

    Task<IReadOnlyList<StandupIssue>> GetIssuesForEntriesAsync(IReadOnlyList<int> entryIds);
    Task<int>  InsertIssueAsync(StandupIssue issue);
    Task UpdateIssueAsync(StandupIssue issue);
    Task DeleteIssueAsync(int issueId);

    // Archive: all entries+issues for a date range (used by the weekly markdown export).
    Task<IReadOnlyList<StandupEntry>> GetEntriesForRangeAsync(DateOnly from, DateOnly to);
}
```

## 4. Service — `IStandupService`

Thin orchestration over the repo + current user + requests (for the request/task picker).

```csharp
public interface IStandupService
{
    Task<UserStandup> GetMyStandupAsync(DateOnly workDate);              // Input tab
    Task<IReadOnlyList<UserStandup>> GetTeamStandupAsync(DateOnly workDate); // Board tab

    Task<IReadOnlyList<Request>> SearchRequestsAsync(string? term);      // picker
    Task<IReadOnlyList<TaskItem>> GetTasksForRequestAsync(int requestId);// picker

    bool CanEditDay(DateOnly workDate);   // true only when workDate == today or yesterday

    Task<int> AddEntryAsync(StandupEntryDraft draft);
    Task UpdateEntryAsync(int entryId, StandupEntryDraft draft);
    Task DeleteEntryAsync(int entryId);

    // Issues — editable by anyone (not owner-gated).
    Task<int> AddIssueAsync(int entryId, string issueText, string? solutionText, string status);
    Task UpdateIssueAsync(int issueId, string issueText, string? solutionText, string status);
    Task DeleteIssueAsync(int issueId);
}

public sealed record UserStandup(
    int UserId, string UserName,
    IReadOnlyList<StandupEntryView> Yesterday, IReadOnlyList<StandupEntryView> Today);
public sealed record StandupEntryView(StandupEntry Entry, IReadOnlyList<StandupIssue> Issues, bool Editable);
public sealed record StandupEntryDraft(
    string Section, int? RequestId, string RequestCode, string TaskText,
    string Description, DateOnly? Deadline, string Status);
```

**Validation:** `Section` ∈ {yesterday, today}; `Status` ∈ `StandupStatus.All`; issue
`Status` ∈ `StandupIssueStatus.All`; `RequestCode`/`TaskText` non-empty; `RequestId` must
exist if provided (else ad-hoc → null). Entry add/edit/delete gated by `CanEditDay` +
owner = current user. **Issues are exempt** from owner/lock gating (collaborative).

## 5. UI Flow

### 5.1 Input tab (own standup)
- Date picker (default today) + two editable sections (Yesterday, Today). Each is a small
  table: `Code · Task · Description · Deadline · Status` with a `[+ Issue]` affordance per row.
- **Add row**: a **Request combo** filtering existing codes as you type, also accepting a
  brand-new code (ad-hoc). Existing request → Task becomes a dropdown of that request's
  tasks (pick to save typing); Deadline/Status still typed/edited manually. Ad-hoc → all typed.
- **Issues**: `[+ Issue]` adds `issue_text` + optional `solution_text` + a status
  (open/pending/resolved). Multiple per row. Editable by anyone.
- If `CanEditDay` is false for the selected day, the sections render **read-only** (locked).

### 5.2 Board tab (team meeting view)
- Date picker; renders **one card per active user** (dynamic count) with their
  Yesterday/Today tables and issue blocks — matching the mockup. Read-only.

```
[📅 2026-06-25 ▾]

╔══ 👤 Nguyen Van A ════════════════════════════════════════════════╗
║ ▸ Yesterday (2026-06-24)                                           ║
║  Code   │ Task  │ What did you do yesterday │ Deadline │ Status    ║
║  REQ-01 │ task  │ …                         │ 06-30    │ Done      ║
║ ▾ Today (2026-06-25)                                               ║
║  REQ-01 │ task  │ …                         │ 06-30    │ In-process║
║   ⚠ task2 — Issues                                                 ║
║     • [resolved] issue: …   solution: …                            ║
║     • [open]     issue: Nội dung issue   solution: (pending)       ║
╚════════════════════════════════════════════════════════════════════╝
(… one card per active user, scroll vertically …)
```

## 6. Weekly Markdown Archive

Standup data is persisted per day in SQLite (the source of truth). For backup/log, a
**markdown snapshot per week** is written to disk — **one file per week**, accumulating
continuously (this week → one file, next week → a different file).

- **Service:** `IStandupArchiveService`
  - `string FileNameFor(DateOnly anyDayInWeek)` → `"{yyyyMMdd}_daily.md"` where the stamp
    is the **Monday** of that week (deterministic week identity).
  - `Task<string> ExportWeekAsync(DateOnly anyDayInWeek)` → builds a markdown doc covering
    Mon–Fri of that week (grouped by date → user → Yesterday/Today + issues) and writes the
    file. Overwrites if it already exists (idempotent).
  - `Task BackfillMissingWeeksAsync()` → the startup hook (below).
- **Location:** `…/Documents/TimesheetApp/StandupArchives/{yyyyMMdd}_daily.md` (next to the
  DB; `{yyyyMMdd}` = week's Monday).
- **Trigger:** **on every app startup**, scan completed weeks (any week strictly before the
  current week that has standup data) and, for each whose archive file is **missing**,
  generate it. This guarantees the previous week is always backed up the next time the app
  is opened, and no completed week is skipped even after several days offline. (No reliance
  on a background scheduler.) A manual **"Archive this week"** button on the Board tab is
  optional/low-priority for re-exporting the in-progress week.

## 7. Wiring (follows existing patterns)

| Concern | File | Change |
|---|---|---|
| Nav | `MainWindow.xaml` | Enable "Daily Report" nav (`ConverterParameter=dailyreport`), remove `SOON` wrapper, add content DockPanel + an inner TabControl (Input / Board) like the Timesheet sub-tabs |
| Shell | `ViewModels/MainViewModel.cs` | Add `DailyReportViewModel DailyReport`; add `"dailyreport"` case to `OnActiveViewChanged` |
| VM | `ViewModels/DailyReportViewModel.cs` (new) | Date, Input/Board state, `UserStandup` collections, add/edit/delete + issue commands, archive command; subscribe to `DataChangedMessage` |
| Views | `Views/Tabs/DailyInputTab.xaml`, `Views/Tabs/DailyBoardTab.xaml` (new) | Input table + Board cards per mockup; reuse Theme styles |
| Service | `Services/StandupService.cs` + `IStandupService.cs`, `StandupArchiveService.cs` + `IStandupArchiveService.cs` (new) | Orchestration, edit-lock, validation, weekly export |
| Repo | `Data/Repositories/StandupRepository.cs` + `IStandupRepository.cs` (new) | Dapper CRUD + range query |
| Migration | `Data/DatabaseInitializer.cs` | v4 → v5: StandupEntries + StandupIssues + indexes |
| DI | `App.xaml.cs` | Register repos + services (singletons) + `DailyReportViewModel` (transient) |
| Live sync | `Services/DataChangedMessage.cs` | Add `DataKind.Standup` so edits refresh the board |

## 8. Testing (Xunit + Moq, existing conventions)
- `StandupRepositoryTests` — CRUD round-trip on in-memory SQLite (`TestDb`); cascade delete
  of issues; range query; migration v5 creates tables.
- `StandupServiceTests` — validation, ad-hoc vs existing request, owner gating + `CanEditDay`
  lock, issue collaborative edits, grouping by section, team grouping.
- `StandupArchiveServiceTests` — markdown content for a week, idempotent overwrite, empty week.
- `DailyReportViewModelTests` — Input vs Board load, commands mutate collections, date reload,
  locked day renders read-only, messenger refresh.

## 9. Edge Cases
- **Ad-hoc request**: persists with `request_code` text only; never joins Requests.
- **Issue with no solution**: `solution_text` null/empty → "pending"; row still saves.
- **No standup for a user on a day**: board card shows empty Yesterday/Today.
- **Locked day**: Input renders read-only; commands no-op with a status message.
- **Empty past day**: still fillable per the chosen lock rule (§10.1).
- **Deadline omitted**: allowed (nullable), shown blank.

## 10. Edit-lock rule (confirmed)

A member may add/edit/delete **their own** entries only when the selected `work_date` is
**today or yesterday** (≤1 calendar day in the past), regardless of when it was entered.
Any older day is **locked (read-only)** — no backfill. `created_at` is not used for the
lock. Issues remain editable by anyone at any time (collaborative; exempt from the lock).

## 11. Confirmed Decisions
1. No auto-carry of Yesterday. ✅
2. Two tabs: Input (own) + Board (team). ✅
3. Team size dynamic (not fixed at 4). ✅
4. Issues editable by anyone; issue status open/pending/resolved. ✅
5. No reverse-link of ad-hoc codes. ✅
6. Deadline + Status typed manually, at task-row level. ✅
7. Existing tasks pickable in the input picker. ✅
8. Per-day persistence + weekly markdown archive. ✅
9. Status set = Todo / In-process / Done / Pending. ✅
10. Edit-lock = today + yesterday only; older days locked, no backfill. ✅
11. Archive = one file per week `{yyyyMMdd}_daily.md` (week's Monday); auto-backfill any
    missing completed week on every app startup. ✅
