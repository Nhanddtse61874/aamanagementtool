# STATE — TimesheetApp (resume doc)

**Last updated:** 2026-06-23
**How to resume:** open a session in `E:\Learning\AgentArchitectureManagement` and say *"đọc .planning/STATE.md để tiếp tục"*.

## What this is
WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite+Dapper / ClosedXML / CommunityToolkit.Mvvm).
App project: `src/TimesheetApp`. Tests: `src/TimesheetApp.Tests`. Branch: `main`.

## Current status
- **181 tests green**, `dotnet build` clean, app launches and is usable.
- Full M1 (P1–P6 + shell + QA) was built autonomously (Mode B), then a long **UAT-driven UI rework** (this session). All planning artifacts in `.planning/` + `docs/superpowers/`.

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

## Remaining UX backlog (user feedback — NOT yet done)
- **Auto-save cells** (remove the Save-button friction; persist on cell commit + surface validation/revert).
- **Week DatePicker** to jump to any week (currently Prev/Next only).
- **Smart Input usability**: replace raw "Task Id" + text dates with a **task dropdown + DatePickers**.
- **First-run zero-config**: auto-use Windows username (currently prefilled inline create).
- **Visual redesign**: user said *"tôi sẽ thiết kế và gửi cho bạn"* — apply their design to `Views/Theme/Theme.xaml` + tabs when received.
- XC-09 warning → surface to a UI banner (currently Trace). XC-10 `.bak` files **accumulate unbounded** in the DB folder (many already) — add a keep-last-N prune.

## Current DB data (for context)
- User: `Nhan` (windows_username=`Admin`).
- Requests: DEFAULT, SHOKAI_QA_REQ-8739, SHOKAI_QA_REQ-44444 (tasks "d","sdfs"), SHOKAI_QA_REQ-1234.
- Template: **"Default Task"** = Design / Investigate / Implement / Automation Test / IT.

## Working style notes (this user)
- Iterative UAT: build a focused fix → **run the app** → user tests → next. They want to "kiểm từng bước".
- Mirror the user's chat language (Vietnamese ↔ English).
- When a feature "doesn't work", get **DB/runtime evidence first** (systematic-debugging) — several issues were "built but not wired into the UI" or UX traps, not logic bugs.
- All recent commits on `main` (~30+ during build + ~10 during UAT). Latest: `4896a58`.
