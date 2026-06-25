# STATE — TimesheetApp (resume doc)

**Last updated:** 2026-06-26 (Daily Report / Standup feature — P7 / M2, on a feature branch awaiting UAT)

## Daily Report (Standup) — BUILT, awaiting UAT (2026-06-26, branch `feature/daily-report-2026-06-25`)
New feature replacing the "Daily Report" SOON nav placeholder. **Schema v5** adds `StandupEntries`
(per user · work_date · section yesterday/today · nullable request_id for **ad-hoc** codes · task ·
description · nullable deadline · status) + `StandupIssues` (multiple per entry; solution nullable =
pending; status open/pending/resolved; cascade-delete). Each member self-fills their own standup;
**edit-lock = today + yesterday only** (older locked, no backfill); **issues are collaborative** (anyone,
any day). **Team board** shows one card per active user for the day. **Weekly markdown archive**
(`Documents/TimesheetApp/StandupArchives/{yyyyMMdd}_daily.md`, one file per week, stamp = week Monday)
is auto-backfilled for any completed week on every app startup. Status set = Todo/In-process/Done/Pending.
- Layers: `Models/StandupModels.cs`; `Data/DatabaseInitializer.cs` (v4→v5); `Data/Repositories/StandupRepository.cs`;
  `Services/StandupService.cs` (edit-lock+owner gate, validation, picker) + `Services/StandupArchiveService.cs`;
  `ViewModels/DailyReportViewModel.cs` (+ row/issue/draft sub-VMs); `Views/Tabs/DailyInputTab.xaml` +
  `DailyBoardTab.xaml`; nav in `MainWindow.xaml` + `MainViewModel` child; DI + `DataKind.Standup` in `App.xaml.cs`.
- **225 tests green** (+32: 7 repo, 17 service/archive, 8 VM); app launches clean. REQ-IDs **DR-01..10**.
- Decisions (don't re-litigate): no auto-carry yesterday; no reverse-link of ad-hoc codes; deadline/status
  manual at task-row level; team size dynamic (not fixed at 4); per-day persistence + weekly md archive.
- Follow-ups (backlog): inline edit of an existing entry's fields (today = delete+re-add); per-row reorder.

## (M1) Last updated: 2026-06-24 (auto-save + backlog batch: prune/banner, zero-config, persist collapse, jump-to-week)
**How to resume:** open a session in `E:\Learning\AgentArchitectureManagement` and say *"đọc .planning/STATE.md để tiếp tục"*.

## What this is
WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite+Dapper / ClosedXML / CommunityToolkit.Mvvm).
Brand shown in-app = **"Worklog"** (DB/internal names unchanged). App project: `src/TimesheetApp`.
Tests: `src/TimesheetApp.Tests`. Branch: `main`. GitHub: **Nhanddtse61874/aamanagementtool** (private).

## Current status
- **193 tests green**, `dotnet build` clean, app runs. Latest commit on `main`: **`587fc61`** (all work pushed).
- Schema is at **user_version 4** (v2 = ticket lifecycle cols + RequestAudit; v3 = project normalization;
  v4 = `requests.assignee_user_id`).
- UI is fully **English**. Local perms in `.claude/settings.local.json` (gitignored).
- Built autonomously, then many rounds of **UAT-driven UI rework**. Planning artifacts in `.planning/` + `docs/superpowers/`.

## Commands
- Build: `dotnet build src/TimesheetApp.sln`
- Test: `dotnet test src/TimesheetApp.sln`
- Run: `dotnet run --project src/TimesheetApp` (or run the built exe in `bin/Debug/net8.0-windows/TimesheetApp.exe`)
- DB lives at `%USERPROFILE%\Documents\TimesheetApp\timesheet.db` (path stored in `%APPDATA%\TimesheetApp\appsettings.json`).

## UAT fixes completed THIS session (newest last)
1. Interim **Modern Light theme** (`Views/Theme/Theme.xaml`, accent #2563EB) — to be superseded by the user's own visual design (they will send one).
2. First-run: **SelectUserDialog can create a user inline** + `ShutdownMode=OnExplicitShutdown` during startup (cancel no longer kills app).
3. **DB parent-dir auto-create** on first run (was crashing with SQLite Error 14).
4. QA gate fixes: **C1** startup `DefaultTaskSync.SyncAsync` runs at App.OnStartup; **I1** XC-09 journal check wired to bulk write paths.
5. **Live cross-tab sync** via `WeakReferenceMessenger` (`Services/DataChangedMessage.cs`): producers Send on save, consumers reload (Requests→Timesheet, Settings→Timesheet, Users→Reports, Timesheet→Reports). + tab-activation reload (`MainViewModel.ActivateTabAsync` + MainWindow SelectionChanged).
6. **Smart Input** wired into Timesheet (a "⚡ Smart fill" button opens `SmartInputPreviewDialog`).
7. **Grouped/expandable Timesheet by Request** (Expander ▼/▶): every request shows even if empty; inline "➕ Add task" per group; footer totals across all groups. (`RequestGroupVm`, `TimeLogService.GetWeekGroupedAsync`, `WeekRequestGroup`.)
8. **Template editor dialog** (`TemplateEditorViewModel`): name once + multi-task list, labeled; Edit/Delete; `ITaskTemplateRepository.DeleteByTemplateNameAsync`. All Settings inputs labeled.
9. **Reports**: user-NAME dropdown + **"Cả team (tất cả)"** option (`ReportTarget`; team uses `GetExportRowsAsync`, single user uses `GetReportRowsAsync`).
10. **Auto-apply template on dropdown selection** (picking a template now adds its tasks immediately; guard prevents duplicate). Root cause: user selected template but never clicked the separate Apply button → request saved empty (DB-verified).

## Visual redesign — DONE (2026-06-23, "Worklog" design)
User sent an HTML/React mockup (`E:\Learning\AAM\Design Old\Designfromclaude\`). Applied a full
**sidebar shell** redesign (decisions: brand→"Worklog" display-only, DB path unchanged; dropped
invented columns Role/Email/Status/Last-used; kept derivable Tasks-count/avatar/badge/stat-cards):
- **Shell** (`MainWindow.xaml` rewrite): left sidebar (Worklog logo, WORKSPACE/ADMIN sections,
  user chip w/ avatar+green dot), feature header per view. Nav = RadioButtons bound to
  `MainViewModel.ActiveView` via `StringMatchConverter` (two-way) + `StringMatchToVisibilityConverter`
  for content swap. `OnActiveViewChanged` reuses `ActivateTabAsync(0/2/4)`.
- **Requests + Reports are now SUB-TABS of Timesheet** (Entry/Requests/Reports TabControl in MainWindow,
  `OnSubTabChanged` maps sub-tab idx→ActivateTabAsync 0/1/3). Users + Settings are sidebar ADMIN items.
- **Planned placeholders**: Daily Report + Task List shown as disabled nav items w/ "SOON" pill.
- Per-tab polish: Timesheet **Week-total chip** (`TimesheetViewModel.WeekTotal`) + **group-total chip**
  (`RequestGroupVm.GroupTotal/RefreshTotal`); Requests **Tasks-count column** (`RequestsViewModel`
  now uses `RequestListItem` record w/ TaskCount); Users **avatar (`InitialConverter`) + Active/Inactive
  badge**; Reports **4 stat cards** (`WeekTotalText/AvgPerDayText/DaysLoggedText` computed in LoadWeekly).
- New theme styles in `Theme.xaml` (NavItem, SidebarSection, SoonPill, StatCard, MiniGhostButton, badge brushes).
- **181 tests still green, build clean.** Verified by screenshotting all 5 destinations (app launches as "Worklog").
- Old-UI reference shots preserved in `E:\Learning\AAM\Design Old\01_tab_*.png` (current design = the new one).

## Ticket lifecycle v2 — DONE (2026-06-23, schema v2)
Requests gained **start_date / end_date / period_month ("yyyy-MM") / status** + a **RequestAudit**
change-history table (migration v2, gated on user_version). Status set = Continue / Implement /
Investigate / IT / Estimate (`RequestStatus.All`).
- **Phase A** (`b5a6e05`): Request entity + repo CRUD for the new cols; `UpdateAsync` diffs the four
  audited fields and writes RequestAudit rows with the current user (`GetAuditAsync` reads history).
  Requests editor got Tháng(period)/Status/Start/End inputs + a "Lịch sử thay đổi" list; grid shows
  Month/Status columns. New tickets default to the current month.
- **Phase B** (`08618eb`): Entry **user filter** ("Cả team" read-only aggregate via
  `TimeLogService.GetWeekGroupedAllUsersAsync` + per-user editable), **month filter** (only tickets of
  the picked month; null/DEFAULT always shown), and per-group **"Move ▶"** (bumps period_month to next
  month, audited). `TimesheetViewModel` gained optional `IUserRepository/IRequestRepository/ICurrentUserService`.
- 182 tests (added a RequestRepository audit round-trip integration test); migration verified on the real DB.
- Repo is on GitHub: **Nhanddtse61874/aamanagementtool** (private). Local perms in `.claude/settings.local.json` (gitignored).

## UI polish + feature rework — DONE (2026-06-24, commits up to `f27d895`)
After the Worklog redesign + ticket lifecycle, a long UAT polish pass (newest last):
- **Reports**: Weekly grid now breaks down by **date · ticket · task · hours** (`WeeklyDetailRow` +
  `ReportAggregator.WeeklyDetailRows`). Entering Reports **auto-loads** the default view (whole team,
  current month + week) via `ActivateTabAsync(3)` so stat cards / grids / drill-down show immediately.
- **Request editor**: month is **required** via **Month + Year combos** (not a DatePicker);
  **Project is a fixed enum dropdown** = ARCS / PlusArcs / ARMS / Other (`RequestProjects.All`).
  Creating a request **requires ≥1 task** (editor stays open with a red message otherwise).
- **Migration v3**: normalizes legacy free-text `Request.project` onto the enum (DEFAULT kept).
- **English UI**: all user-facing strings translated (labels, tooltips, dialogs, banners, dropdowns).
- **Entry day grid = Excel-like**: bordered/centred day cells (GridLinesVisibility=All), header strip +
  footer reserve the scrollbar gutter (always-visible) and the indent was removed, so columns line up at
  ANY window size. Compact, balanced toolbar: segmented prev/week/next + Save + Smart fill + **Collapse all
  ⇄ Expand all** toggle (`TimesheetViewModel.ToggleCollapseAllCommand`), Week-total chip anchored right.
  Entry **month filter** is also Month+Year combos (`FilterMonthNumber/FilterYear`).
- **Smart fill REDESIGNED** (`SmartInputPanelVm` rewritten, new dialog): enter **request code → Find →
  tasks as checkboxes → check tasks → From/To DatePickers + total hours (or Full 8h) → Preview → Confirm**.
  Total (Split evenly) + the 8h/day cap are distributed across **all checked tasks × working days**.
  New `ITimeLogService.ValidateSmartFillAsync/ApplySmartFillAsync` validate the combined per-day totals
  and apply atomically (`SmartFillTask` record). Date range defaults to the on-screen week.
- **Buttons**: smaller default (Padding 16,8→10,4); secondary actions → ghost, destructive → danger,
  Settings input-row buttons height-matched to inputs (32px).

## Auto-save cells — DONE (2026-06-24, commit `4a700e3`)
Entry cells persist on commit (LostFocus); the **Save button is gone**, replaced by a status indicator
(`SaveStatus`/`SaveStatusIsError`: "Saving…" / green "✓ Saved" / red warning). `TimesheetRowVm.DayChanged`
now carries `(row, DayColumn)` so `OnRowDayChanged` auto-saves ONLY the changed cell via `AutoSaveCellAsync`.
A red cell (`TimesheetRowVm.HasErrorFor(col)`) is never written; the service still rejects a day >8h
(status warns, value reverts on reload). Team (read-only) view skips auto-save. `SaveCellAsync` now returns
`SaveResult`. Note: `SaveCommand`/`CanSave`/`AnyDayOverEight` are KEPT (internal bulk-save, no button) so
`Save_DisabledWhenAnyColumnExceedsEight` still guards the >8h invariant. 3 new VM auto-save tests.

## Assignee + Entry redesign — DONE (2026-06-25, commits `f3f378d`, `587fc61`)
- **Assignee per Request (migration v4)**: `requests.assignee_user_id` (nullable, not an FK). RequestRepo
  persists+reads it and **audits changes by NAME** (gated on id change). Edit-request dialog got an
  **Assignee dropdown** (`RequestEditorViewModel.Unassigned` sentinel id 0 → null; `Users`/`SelectedAssignee`/
  `AssigneeUserId`). Requests list shows an **Assignee column**; Entry section headers show a 👤 badge
  (`WeekRequestGroup.AssigneeName`, resolved in `TimeLogService.BuildGroupsAsync` via `_users.GetAllAsync`).
  `RequestsViewModel`/`TimeLogService` guard null user lists (mocks return null). Tasks stay shared
  (decision: no per-task owner) — assignee is ticket-level only.
- **Edit-request dialog restyled** to the Worklog theme: rounded shadowed card, New/Edit title, hierarchical
  buttons (Save primary, Cancel/Add ghost, ▲▼✕ icon), sticky footer w/ error. Dropped the redundant
  "Apply Template" button (selecting a template auto-appends). `FieldLabel` style added.
- **Entry grid redesigned to a single continuous table** (user mockup): each request = a full-width clickable
  **section band** (caret + CODE · project + badges, group total right-aligned under TOTAL); task rows beneath
  with editable Mon–Fri cells aligned to shared 7-col star grid (2*,*,*,*,*,*,*); **"Move to next month"
  moved into the expanded add-task row** (right) so the header never clips. Header uppercased (TASK/MON../TOTAL),
  `Header()` now uppercases the day-of-week; DAY TOTALS footer shows the week grand total; Prev/Next relabeled.
  Replaces the old per-group Expander+DataGrid. Auto-save (LostFocus) unchanged on the new TextBox cells.

## Backlog batch — DONE (2026-06-24, commits `5b441de`..`086efae`)
Autonomous pass over the remaining UX backlog (newest last):
- **XC-10 .bak prune + XC-09 banner** (`5b441de`): `DbBackupHelper` deletes stale `{db}.{stamp}.bak`
  siblings after each backup, keeping only the newest `KeepBackups` (10) — best-effort, never fails a
  backup. `UiJournalWarningSink` wraps the trace sink (still traced) + raises an event; `App` marshals
  it onto the UI thread → `MainViewModel.JournalWarning` shown as a dismissible **red banner** next to
  the XC-08 conflict banner. Sink + VM stay System.Windows-free.
- **Zero-config first run** (`62c6e8d`): a fresh DB (zero users) **auto-creates** a user from the
  Windows account + maps it — no dialog. When users exist but the account is unmapped, SelectUserDialog
  still shows and now **prefills** the create-name box with `Environment.UserName`.
- **Persist Collapse-all** (`bcc9d40`): the Entry collapse/expand-all toggle is saved to settings
  (`entry.collapseAll`) and restored on load. `TimesheetViewModel` gained optional `ISettingsRepository`.
  Also **fixed the long-standing flaky** `ActivateTab_reloads_timesheet_rows` (isolated messenger).
- **Jump-to-week** (`086efae`): a `DatePicker` (`TimesheetViewModel.JumpDate`) on the Entry toolbar
  snaps the grid to any date's Monday + reloads; Prev/Next keep it in sync (`OnCurrentWeekChanged`).
- **191 tests green** (full suite stable across repeated runs); app launches clean.

## Remaining UX backlog (NOT yet done)
- Per-tab button/spacing review if any still feel large; remember per-GROUP expand state across restarts
  (only the global collapse-all flag is persisted today).

## Decisions locked (don't re-litigate)
- Brand "Worklog" is **display-only**; DB path stays `timesheet.db`.
- Requests/Reports are **sub-tabs of Timesheet**; Users/Settings under sidebar ADMIN; Daily Report/Task List are SOON placeholders.
- Entry "Cả team / Whole team (read-only)" view is **read-only aggregate**; a specific user is editable.
- "Move ▶" = advance ticket to the **next** month (audited). Arbitrary month change is via the Requests editor.
- Smart fill total = **grand total split across checked tasks × days** (not per task).
- Projects are the fixed enum; legacy values were normalized by migration v3.

## Working style notes (this user)
- Iterative UAT: build a focused fix → **run the app** → user tests → next ("kiểm từng bước"). Verify with screenshots.
- Mirror the user's chat language (Vietnamese ↔ English). Commit + push each accepted change to `main`.
- When a feature "doesn't work", get **DB/runtime evidence first** — several issues were UX traps / not-wired, not logic bugs.
- Note: `MainViewModelTests.ActivateTab_reloads_timesheet_rows` was historically **flaky** (process-wide
  default WeakReferenceMessenger cross-talk) — **fixed in `bcc9d40`** by giving its VM an isolated messenger.
