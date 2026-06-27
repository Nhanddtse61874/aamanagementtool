# P8 — Task List (M3) — Phase Summary

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-27 · **Mode:** B
**Status:** Implemented + verified + QA-passed. **Awaiting user UAT** (`.planning/P8-Task-List-UAT.md`) before merge.
**Tests:** 228 baseline → **314 green** (86 new) · build clean (0 warnings/errors) · schema **v6 → v7**.

## What shipped (15 REQs)
A per-month **Task List** overview of backlogs with backlog-level tracking, plus the supporting infrastructure:
- **Tracking fields** (TL-03): internal (PCT) + external (PCA) deadlines, rough + official estimates (hours), manual progress %, note, PCT assignee (User), PCA contact (from a managed list). All audited.
- **Task List screen** (TL-04/05/06): month-scoped grid (DEFAULT excluded) — code/project/type/assignees/deadlines/progress/logged-hours/estimate/tag-chips, expandable to tasks.
- **Auto schedule chips** (TL-07/08): amber "warning" when ≤2 working days from the internal deadline AND behind (done% < elapsed%, standard formula), red "late" when past the internal deadline; Done suppresses both.
- **Tags** (TAG-01/02): user-created tags (icon+color+text) in Settings, many-to-many on backlogs; chips = system-first (late before warning) then custom by id.
- **Holidays** (HOL-01/02): a Settings calendar to mark/unmark holidays, excluded from every working-day computation via one shared `IWorkingDayCalculator` (smart-input, schedule math, Gantt axis).
- **Gantt** (TL-10): native WPF Canvas timeline (start→internal deadline) over a working-day axis, colored by schedule state, external-deadline marker, no-start placeholder; Grid↔Gantt toggle + collapsible chart.
- **Monthly markdown export** (TL-09): auto-backfill of completed months on startup + manual button; includes a "Moved to next month" section derived from `BacklogAudit`.
- **Sidebar restructure** (TL-02): Backlog, Task List, Reports promoted to top-level (siblings of Log Work + Daily Report); the old Timesheet sub-tab group removed.
- **Schema v7** (TL-01): additive migration — 7 Backlog columns + Tags/BacklogTags/PcaContacts/Holidays tables.

## Architecture notes
- New repos (singletons, `IConnectionFactory` per-method seam): `ITagRepository`, `IPcaContactRepository` (soft-delete), `IHolidayRepository`; `IBacklogRepository` gained tag-link methods + v7 columns; `ITimeLogRepository.GetLoggedHoursByBacklogAsync` (all-time SUM, **no `is_active` filter** — XC-06).
- New pure services (testable, never throw): `IWorkingDayCalculator`, `IScheduleStateService` (divide-by-zero removed via cross-multiply). New `ITaskListArchiveService` mirrors `StandupArchiveService`.
- `SmartInputService` gained holiday-aware overloads while preserving its parameterless ctor (SI-01/02 tests unchanged).
- `TaskListViewModel` (transient) + `TaskListTab`; Settings/backlog-editor extended; `HexToBrushConverter` (safe on bad hex).

## Commits (W1→W8 + QA fix)
`d6deb8d` data · `78e99e9` services · `7cfc8f3` settings UI · `8381a84` backlog editor · `a32d66c` task list grid · `1520131` Gantt · `cade805` sidebar · `1eb3126` DI/startup · `d2abd4e` QA polish.

## QA / verification
- **Plan Checker** (STEP 6): APPROVE-WITH-FIXES → both blocking fixes (migration index off-by-one; SmartInput ctor) applied before execution.
- **Goal-Backward** (STEP 8b, `.planning/P8-Task-List-VERIFICATION.md`): VERIFIED — 9/9 truths, 9/9 artifacts, 6/6 key_links, 15/15 REQs.
- **QA Gate** (STEP 9): APPROVE-WITH-SUGGESTIONS, 0 Critical/Important; 2 live-sync suggestions applied (`d2abd4e`), the rest are accepted minor polish.

## UAT-pending (visual/interaction — STEP 8a)
Gantt rendering/colors/markers, holiday-calendar clicks, tag-chip hex rendering, sidebar nav no-regression, backlog editor field round-trip in the form, Grid↔Gantt toggle/collapse, holiday effect on smart-fill. See UAT doc.

## Deferred / accepted limitations
- Monthly archive snapshots current data at generation time (re-export to refresh) — mirrors standup archive.
- Multi-step period moves (M→N→O) only carry the latest hop into M's export (Q5 accepted).
- `ErrorMessage` on bad estimate/progress input persists until save (minor UX).
- RPT-04's `LastNWorkingDays` left weekend-only (not migrated to the new calculator) — out of scope.
