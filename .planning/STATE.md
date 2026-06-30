# STATE — TimesheetApp (resume doc)

**Last updated:** 2026-06-29 — branch `feature/task-list-2026-06-27` holds **FIVE stacked features**:
**M3 Task List [P8]** + **M5 Local Backup [P9]** + **M4 Multi-Team [P10]** + **M6 Export Restructure [P11]** + **M7 Retention [P12]** —
schema **v8**, **500 tests green**, build clean, all QA-passed + goal-backward VERIFIED. **IN LIVE UAT** (user running the app);
several runtime-crash fixes already landed (see "UAT crash fixes" below). Prior: M2 Daily Report merged into `main`.
**Next: finish UAT of all tabs → merge the whole branch to `main`** (NOT pushed/merged yet).

## How to resume (READ FIRST)
Open a session in `E:\Learning\AAM 2nd\aamanagementtool`, say *"đọc .planning/STATE.md để tiếp tục"*.
- Branch `feature/task-list-2026-06-27` (NOT merged, NOT pushed). HEAD = `93d5dd1`. `dotnet test src/TimesheetApp.sln` → expect **500 green**.
- Run the app: `dotnet run --project src/TimesheetApp` (or the built exe `src/TimesheetApp/bin/Debug/net8.0-windows/TimesheetApp.exe`). First launch already migrated the real DB to v8.
- **Real DB has demo data** seeded this session: 2 teams (Architect Improvement #1, Plus Team #2), 8 members (An/Binh/Chi/Dung/Em[50-50]/Phuc/Giang/Huy), 10 tasks across 5 backlogs (ARCS-2001/ARMS-2002/OTHER-2005/PLUS-2003/PLUS-2004, period_month 2026-06, varied schedule chips). Current user **Nhan** is in BOTH teams. DB backups exist: `…/Documents/TimesheetApp/timesheet.db.pre-v8-*.bak` and `.pre-seed-*.bak`.
- Config: model_profile `quality`; autonomous run, PAUSE at plan for schema/destructive phases.
- **What's left:** user confirms remaining tabs (Backlog/Reports/Daily/Settings/Holiday/Backup/Export/Retention) work with data → then merge branch to `main` (user must OK push/merge).

## UAT crash fixes (2026-06-28/29) — all committed, 500 green
Live UAT surfaced runtime crashes that unit/XAML-compile tests can't catch (the team filter defaulted to 0 teams, so every team-filtered grid was EMPTY and **latent render bugs were masked until rows appeared**):
1. `217f507` — 5 UAT issues: **add-member crash** (`<Run Text="{Binding TeamName}"/>` binds TwoWay-by-default on a read-only prop → `Mode=OneWay`); team switcher shown for single-team (`Count>=1`); Task List toggles → `ToolbarGhostToggle`; **holiday blocks Log Work entry** (TimeLogService rejects holiday dates + cells read-only); Task List "All months" option.
2. `04790b5`→`62fe66c` — **"Teams (0)" empty grids**: TeamFilters built in VM ctor before `ICurrentTeamService` resolved. Final fix = **lazy-seed** `TeamFilterViewModel.CheckedTeamIds` on first read (NOT raising `ActiveTeamChanged` during `InitializeAsync` — that runs inside the startup WPF layout pass → re-enters Measure → **stack overflow**).
3. `89fce98` — tag/chip icon TextBlocks use `FontFamily="Segoe UI Emoji"` (innocent re the SO, kept for emoji icons).
4. `93d5dd1` — **Task List render crash** (the real "click→crash"): row-expand `<ToggleButton Style="MiniGhostButton">` (MiniGhostButton is `TargetType=Button`) → added `MiniGhostToggle`; `<ProgressBar Value="{Binding ProgressPercent}">` (RangeBase.Value is TwoWay-by-default) on read-only prop → `Mode=OneWay`; hardened template `<Run Text>` with `Mode=OneWay`.
**LEARNING — recurring WPF bug class:** TwoWay-by-default DPs (`Run.Text`, `RangeBase.Value`, `ToggleButton.IsChecked`, `Selector.SelectedItem`) bound to **read-only** VM props throw at render; and **Button-typed Styles on a ToggleButton** throw "TargetType does not match". All views grepped + fixed for both patterns. A throwaway **STA render harness** (scratchpad `…/seeder/Program.cs`) reproduces a populated tab and was the tool that caught these — recreate it to diagnose any further render crash.

## M3 Task List [P8] — what was built (2026-06-27, this session)
Per-month backlog tracking overview. Schema **v7** (additive v6→v7): Backlogs +deadline_internal/external,
+rough/official_estimate_hours, +progress_percent, +note, +pca_contact_id (no inline FK; assignee_user_id reused
as PCT). New tables Tags/BacklogTags/PcaContacts/Holidays. New symbols: `IWorkingDayCalculator` (pure, holiday-aware),
`IScheduleStateService` (warning/late, never throws), `ITaskListArchiveService` (monthly md, mirrors standup),
`ITagRepository`/`IPcaContactRepository`/`IHolidayRepository`, `TaskListViewModel`/`TaskListTab`, `HexToBrushConverter`,
`ScheduleState` enum + `TaskListRow`/`GanttBar`/`GanttModel`. `ITimeLogRepository.GetLoggedHoursByBacklogAsync`
(all-time SUM, NO is_active — XC-06). Sidebar restructured: Backlog/Task List/Reports now TOP-LEVEL (string-keyed
`ActiveView`; sub-tab TabControl removed). New `DataKind`s Tags/PcaContacts/Holidays. Docs: spec
`docs/superpowers/specs/2026-06-27-task-list-design.md`, plan `docs/superpowers/plans/2026-06-27-P8-task-list.md`,
research `.planning/research/P8-*.md`, SUMMARY/UAT/VERIFICATION in `.planning/`. Commits d6deb8d→d2abd4e (9).
**Config changed this session:** model_profile `quality`, defaults {haiku,sonnet,opus} (→ effective sonnet/opus, no haiku).

## What this is
WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite+Dapper / ClosedXML / CommunityToolkit.Mvvm).
Brand shown in-app = **"Worklog"** (DB/internal names unchanged). App project: `src/TimesheetApp`; tests `src/TimesheetApp.Tests`.
GitHub: **Nhanddtse61874/aamanagementtool** (private). Local perms in `.claude/settings.local.json` (gitignored).

## Commands
- Build: `dotnet build src/TimesheetApp.sln`   ·  Test: `dotnet test src/TimesheetApp.sln`
- Run: `dotnet run --project src/TimesheetApp` (or `bin/Debug/net8.0-windows/TimesheetApp.exe`)
- DB: `%USERPROFILE%\Documents\TimesheetApp\timesheet.db` (path in `%APPDATA%\TimesheetApp\appsettings.json`).
- **Seed clean sample data**: scratchpad seeder at `…\3375b5db-…\scratchpad\seeder\` —
  `dotnet run --project Seeder.csproj` (delete the db first to reset); `-- check` prints DB contents,
  `-- repro` exercises the add-issue path. Seeds 4 users (Nhan mapped to the Windows account so the app
  auto-selects it), 4 backlogs (ARCS-1001/PLUS-2002/ARMS-3003/OTHER-404) + tasks + a week of timesheet +
  standup with an unresolved (amber) and a resolved (green) issue.

## Schema — user_version **8** (v7=P8 Task List, v8=P10 Multi-Team; on feature branch; `main` still v6)
v8 (P10): new tables Teams/UserTeams; nullable team_id on Backlogs + StandupEntries (no inline FK). Data migration is a
POST-INIT bootstrap (TeamBootstrapService) — not in the init tx — assigning existing data to "Architect Improvement".
v7 (P8): Backlogs +deadline_internal/external +rough/official_estimate_hours +progress_percent +note +pca_contact_id;
new tables Tags/BacklogTags/PcaContacts/Holidays. Both additive, const-bumped, gated on user_version.
P9 (backup) = file-level, no schema change.
## Schema — user_version **6** (history)
v2 ticket lifecycle cols + RequestAudit; v3 project normalization; v4 `assignee_user_id`; v5 Daily Report
(StandupEntries/StandupIssues); **v6 = Request→Backlog rename** (tables `Requests`→`Backlogs`,
`RequestAudit`→`BacklogAudit`; cols `request_code`→`backlog_code`, ticket `status`→`type`,
`request_id`→`backlog_id` on Backlogs/Tasks/BacklogAudit/StandupEntries; Tasks gains `status` default 'Todo').
DDL still creates the legacy `Requests`/`RequestAudit` (gated on `user_version < 6`) so historical migrations
apply, then v6 renames them. App has a global `DispatcherUnhandledException` handler (UAT-friendly error dialog).

## Done this session (2026-06-26/27) — all on `main` now
1. **Backlog refactor (`Request`→`Backlog`) COMPLETED** — a prior session left it half-done (app compiled, test
   project broken). Finished the test migration + fixed a real `TimeLogRepository` SQL bug (still queried
   `Requests` → would crash Reports live) + guarded the stray-`Requests`-table-on-relaunch. Symbols:
   `Backlog`/`BacklogCode`/`IBacklogRepository`/`BacklogRepository`/`BacklogsViewModel`/`BacklogEditorViewModel`/
   `WeekBacklogGroup`/`BacklogAuditEntry`/`MonthlyBacklogTaskTotal`/`BacklogNode`; ticket `Status`→`Type`
   (`BacklogType`); new per-task `TaskItem.Status` (+`TaskStatus.All` Todo/In-process/Done/Pending).
2. **DEFAULT backlog never gets a month** — `MoveMonthAsync` guards it (UI already hides Move); it holds the
   recurring default tasks and shows in EVERY month (Entry month-filter exempts null period_month).
3. **Daily Report issue UX redesign** — issue card: ⚠ amber "warning" until a solution is saved, then flips to
   green "✓ resolved". No-solution shows "No solution yet" + "Add solution" button (no empty input). Applied to
   BOTH Input tab and Team Board (`StandupIssue.HasSolution`). Input tab relaid out to a single stacked column
   (mirrors Board). Standup VM actions wrapped in try/catch → inline StatusMessage.
4. **Daily Report Add-entry** — ONE pinned "Add entry" button (was 2); section (Yesterday/Today) picked inside
   the dialog via radios (`StandupDraftVm.Section` settable, two-way via StringMatchConverter).
5. **Smart fill** — contains search (`SearchAsync` LIKE, not exact `GetByCodeAsync`; multi-match prefixes the
   code); two-column dialog (form left, preview right full-height, scrolls); Preview button pinned; Total hours
   greyed for Full 8h; themed to app dialog convention.
6. **Reports drill-down** — every level shows rolled-up hours (Project/Backlog/Task) and the Date leaf shows
   weekday + hours logged that day.
7. **Drag & drop (both big features)**:
   - **Daily Report**: ⠿ grip drag-reorders entries within/across Yesterday/Today; drop on the pinned
     "🗑 Drop an entry here to delete" zone deletes. `StandupService.ReorderEntryAsync` (owner+lock gated).
   - **Timesheet Entry**: ⠿ grip drag-reorders task rows within a backlog; drop-on-trash soft-deletes the task
     (time logs preserved). `ITaskRepository.SetOrderAsync`; `TimesheetViewModel.ReorderTaskAsync/DeleteTaskAsync`
     (no-op in read-only team view).

## Open / not yet verified
- **Drag & drop is mouse interaction — NOT auto-tested.** UAT needed: grips draggable, drop reorders, trash
  deletes, highlight on drag-over. Handlers live in `DailyInputTab.xaml.cs` / `TimesheetTab.xaml.cs`.
- The merge took the **feature** side for `RequestsTab.xaml` / `ReportsTab.xaml` / `SmartInputPreviewDialog.xaml`
  conflicts (origin/main's design-tweak commits `960b570`/`1792a75` were superseded by the feature branch's own
  design work). If a Users/tables/buttons tweak looks missing, recover it from commit `960b570`.

## Decisions locked (don't re-litigate)
- Brand "Worklog" display-only; DB path `timesheet.db`. Requests/Reports are sub-tabs of Timesheet; Users/Settings
  under sidebar ADMIN. Entry "whole team" view = read-only aggregate. "Move ▶" advances ticket to next month (audited).
- Projects = fixed enum ARCS/PlusArcs/ARMS/Other. Smart-fill total = grand total split across checked tasks × days.
- DESIGN SOURCE OF TRUTH = `E:\Learning\AAM\Design Old\Designfromclaude\Timesheet Tool.dc.html` (teal #0F766E,
  page #E6EAEF, surface #fff, 'Segoe UI' 13px). Read it before UI changes.

## Working style (this user)
- Iterative UAT: focused change → run the app → user tests → next. Mirror the user's language (VN↔EN).
- When a feature "doesn't work", get DB/runtime evidence first (several issues were UX traps / not-wired, not logic).
- Commit + push each accepted change. Surgical changes; don't "improve" working code unasked.
