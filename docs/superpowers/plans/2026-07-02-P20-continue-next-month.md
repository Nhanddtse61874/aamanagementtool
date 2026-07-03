# P20 — Task List "Continue on next month" — SPEC + PLAN

**Date:** 2026-07-02 · **Mode:** A · **Branch:** `feature/tasklist-continue-2026-07-02` (from `main`).
**Goal:** A per-card **"↻ Continue → next month"** action in the Task List that **copies** a backlog into M+1 with `Type="Continue"` — the original in M is untouched. (Not a move; a copy-forward of unfinished work.)

## Locked decisions (brainstorm 2026-07-02)
- **Trigger:** button on each backlog card; visible only when a concrete month is selected (not "All months").
- **Copy scope:** the backlog **+ its not-Done tasks** (Status ≠ "Done"), each with its type/assignee + tags. Backlog tags copied too.
- **Progress:** the copy **keeps** the original's `ProgressPercent`.
- **Type on copy:** `"Continue"` (existing `BacklogType`). `period_month = M+1`. Original unchanged.
- **Duplicate:** if M+1 (same team) already has a backlog with the same `backlog_code` → **block + inline message**, no-op.
- **Audit:** write a `BacklogAudit` `field="continued"` row on the copy (`new_value` = source period) for traceability.
- **Next month:** M+1 relative to the selected month (rollover 12→1).

## Facts (from code)
- `Backlog` (Entities.cs): `PeriodMonth` (yyyy-MM), `Type`, `ProgressPercent`, etc. `BacklogType.All` includes **"Continue"**. `backlog_code` is **not unique** → same code across months is legal.
- `BacklogRepository`: `GetByIdAsync`, `SearchAsync(term, teamIds)`, `InsertAsync(Backlog)`, `GetTagIdsAsync(id)`, `SetTagsAsync(id, ids, uid, uname)`. Audit is written inside `UpdateAsync` (field diffs) — so add a small `WriteContinuedAuditAsync`.
- `TaskRepository`: `GetActiveByBacklogAsync(id)` (active tasks), `InsertAsync(TaskItem)` (name/status/order/active only — type/assignee via `UpdateExtendedAsync`; tags via `SetTaskTagsAsync`), `GetTagIdsAsync(taskId)`.
- `TaskItem` ctor: `(Id, BacklogId, TaskName, OrderIndex, IsActive, Status, Type?, AssigneeUserId?)`.

## must_haves (goal-backward)
- OT-1 A "Continue → next month" button shows on each card when a concrete month is selected.
- OT-2 Clicking it creates a NEW backlog for M+1: cloned fields, `Type="Continue"`, `period_month=M+1`, **progress kept**, backlog tags copied; original in M unchanged.
- OT-3 Its **not-Done tasks** are copied (with type/assignee + task tags); Done tasks are not.
- OT-4 If M+1 already has that `backlog_code` (same team) → blocked with a message, nothing created.
- OT-5 Board refreshes; a `continued` audit row is written on the copy.

---

## Wave 1 — Service + repo audit + tests  `[sonnet]`
- **W1-T1** `[sonnet]`
  · **read_first:** `Data/Repositories/BacklogRepository.cs` + `IBacklogRepository.cs`; `Data/Repositories/TaskRepository.cs` + `ITaskRepository.cs`; `Models/Entities.cs` (Backlog/TaskItem/BacklogType); `App.xaml.cs` ConfigureServices; `Tests/Services/StandupServiceTests.cs` + `Tests/Data/TestDb.cs` (harness).
  · **action:** Add `IBacklogRepository.WriteContinuedAuditAsync(int backlogId, string? fromPeriod, int? uid, string? uname)` → inserts a `BacklogAudit` row `field='continued', old_value=null, new_value=fromPeriod, note="continued from {fromPeriod}"`. New `Services/IBacklogContinuationService.cs` + `BacklogContinuationService.cs`: `Task<int> ContinueAsync(int backlogId, string targetPeriod)`:
    1. `src = GetByIdAsync(backlogId)`; null → 0. `teamIds = src.TeamId is {} t ? new[]{t} : null`.
    2. Guard: `candidates = SearchAsync(null, teamIds)`; if any `b.BacklogCode==src.BacklogCode && b.PeriodMonth==targetPeriod` → return 0.
    3. Clone: `copy = src with { Id=0, PeriodMonth=targetPeriod, Type="Continue", CreatedAt=_clock.UtcNow }`; `newId = InsertAsync(copy)`.
    4. Backlog tags: `ids = GetTagIdsAsync(src.Id)`; if any → `SetTagsAsync(newId, ids, uid, uname)`.
    5. Not-Done tasks: `foreach t in GetActiveByBacklogAsync(src.Id) where t.Status != "Done"`: `ntid = _tasks.InsertAsync(new TaskItem(0, newId, t.TaskName, t.OrderIndex, true, t.Status))`; if `t.Type!=null || t.AssigneeUserId!=null` → `UpdateExtendedAsync(ntid, t.Type, t.AssigneeUserId, uid, uname)`; `ttags = GetTagIdsAsync(t.Id)`; if any → `SetTaskTagsAsync(ntid, ttags, uid, uname)`.
    6. `WriteContinuedAuditAsync(newId, src.PeriodMonth, uid, uname)`; return `newId`. (uid/uname from `ICurrentUserService.Current`.)
    Register in DI. **AVOID:** copying Done tasks; mutating the source; `GetByCodeAsync` for the guard (code not unique → would throw).
  · **verify (auto <60s):** `dotnet test src/TimesheetApp.sln --filter "FullyQualifiedName~Continuation"` — real-DB tests: clone fields + Type=Continue + period=M+1 + progress kept; original still in M; not-Done tasks copied (Done not); backlog+task tags copied; duplicate → returns 0 + nothing created; `continued` audit row present.
  · **done:** service + tests green; build clean.

## Wave 2 — VM command + card button  `[sonnet]`  *(depends on W1)*
- **W2-T1** `[sonnet]`
  · **read_first:** `ViewModels/TaskListViewModel.cs` (SelectedYear/Month, AllMonths, ExportStatus, LoadAsync, `_messenger`, ctor/DI, `ICurrentTeamService`); `Views/Tabs/TaskListTab.xaml` (card template + `VmProxy` + tag strip); `App.xaml.cs` (TaskListViewModel ctor injection).
  · **action:** VM: inject `IBacklogContinuationService? _continuation` (optional → tests). `public bool CanContinue => SelectedMonth != AllMonths;` (raise on month change). `[RelayCommand] async Task ContinueToNextMonthAsync(TaskListRowVm? row)`: compute targetPeriod (SelectedMonth==12 → next year/Jan else same year/+1) `$"{y:0000}-{m:00}"`; `var n = await _continuation.ContinueAsync(row.BacklogId, targetPeriod);` `ExportStatus = n<=0 ? $"'{row.BacklogCode}' đã có ở {targetPeriod}." : $"Continued '{row.BacklogCode}' → {targetPeriod}.";` if n>0 → reload + broadcast `DataKind.Backlogs`. XAML: a `MiniGhostButton` "↻ Continue" in the card tag strip (next to ✎ Tags), `Command="{Binding Data.ContinueToNextMonthCommand, Source={StaticResource VmProxy}}" CommandParameter="{Binding}"`, `Visibility` bound to `Data.CanContinue` via `VmProxy` + `BoolToVisibleConverter`.
  · **verify (auto):** `dotnet build` + `dotnet test src/TimesheetApp.sln` — full suite green (render test still renders the card with the new button).
  · **done:** button on card; VM command wired; build clean; suite green.

## Risks
1. Guard uses `SearchAsync(null, teamIds)` + exact code+period filter (not `GetByCodeAsync`, which throws on non-unique). 
2. `TaskItem.InsertAsync` doesn't set type/assignee → follow with `UpdateExtendedAsync`.
3. `CanContinue`/button hidden in "All months".
4. Build gotcha: kill running app before build. Both waves `[sonnet]` (`claude-sonnet-5`), inline.
