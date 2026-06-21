# STATE — TimesheetApp

**Last updated:** 2026-06-21

## Current Position
- **Phase:** Step 7 — Execute (Subagent-Driven, autonomous all 6 phases)
- **Status:** in_progress — building P1 Wave 1
- **Approved Mode:** Mode B (user override from suggested Mode A, 2026-06-21)
- **Execution mode:** Subagent-Driven (fresh subagent per task, controller reviews between tasks), **autonomous all 6 phases** (user approved 2026-06-21 — user does final UAT when product complete). Commit atomic per task.

## Autonomous-run deviation (this project, 2026-06-21)
User instruction: build the full WPF Timesheet Tool autonomously, Subagent-Driven, P1→P6, user checks result at the end. Skip per-task confirmation + per-phase UAT gates; controller owns review between tasks + cross-plan consistency. Hard stops: 3-attempt unresolvable test failure, genuine ambiguity not derivable from spec/plans, or scope beyond the 45 REQs. Do NOT fabricate REQ-IDs.

## Next Action
P1-P6 COMPLETE (152 tests green, build OK). Dispatch FINAL WIRING task: MainWindow + MainViewModel (host 5 tabs, current-user resolution + SelectUserDialog XC-07, conflict-copy banner XC-08, show window on startup). Then UAT/QA (Steps 8-9) + ship (Step 11).
**MainWindow + MainViewModel do NOT exist yet** (P3 T3 deferred first-window show) — must be created in a final wiring task to host the 5 tabs (Timesheet/Requests/Users/Reports/Settings), current-user resolution + conflict-copy banner, and actually run the app. Track this as a required closeout task.

## Carry-over notes for UI phases
- `IClock` has `UtcNow` + `Today` (Services/IClock.cs); `SystemClock` impl exists.
- ReadModels.cs holds: CellAssignment, SmartInputResult, CurrentUserResult/Outcome, TimeLogReportRow, WeekGrid, WeekRow, SaveResult.
- **GetWeekAsync `WeekRow.RequestCode` is empty** (ITaskRepository.GetActiveForTimesheetAsync returns TaskItem w/o request_code) — P3 must join request_code in the VM or extend the query for grouping.
- ValidateDayTotals/ApplySmartInput assume ≤1 cell per (taskId,date) — P3 must not pass duplicate same-day cells per task.
- Interfaces NOT to recreate: IUserRepository, IRequestRepository, ITaskRepository, ITimeLogRepository, ISettingsRepository, IDefaultTaskRepository, ICurrentUserService, ISmartInputService, ITimeLogService, IDefaultTaskSyncService, IDbBackupHelper, IClock, IAppConfig, IConnectionFactory, IDatabaseInitializer.

## Execution Notes (standing corrections — apply to ALL tasks)
- **Test project TFM:** `TimesheetApp.Tests` targets **`net8.0-windows` + `<UseWPF>false</UseWPF>`** (NOT bare `net8.0` — net8.0 cannot reference the net8.0-windows app on SDK 8.0.205). Plans that say `net8.0` for tests are superseded by this.
- **Every test file** must include `using Xunit;` (plan test templates omit it; xUnit 2.9.2 has no implicit usings).
- `.gitignore` exists (bin/obj ignored).

## Progress (Step 7)
- **P1 COMPLETE** (6 tasks, 26 tests green, controller-verified). Commits: 2add21a, 4244ef9, 98657ea, 5a598eb, af895a1, a4a159f.
  - T1 solution/projects/NuGet · T2 models · T3 JsonAppConfig (`IAppConfig.DbPath`+`SetDbPath`) · T4 SqliteConnectionFactory (journal=DELETE/FK/Pooling off) · T5 SqliteMaintenance (XC-08/09) · T6 DatabaseInitializer (schema+seed+migrations).
  - Cross-phase fix: P6 plan patched to use `IAppConfig.SetDbPath()` (P1 used method, not property setter).
- **P2 COMPLETE** (7 tasks, 78 tests green, controller-verified). Commits: 84905f3, 0a54ada, 0375308, 9d958a0, 2e49b73, dffd5db, 64408a5.
  - T1 SmartInputService (integer-tenths) · T2 CurrentUserService · T3 DbBackupHelper · T4 TimeLogRepository (+TestDb.cs) · T5 User/Task/Request/Settings repos · T6 TimeLogService (8h validation, atomic apply) · T7 DefaultTaskSyncService (+IDefaultTaskRepository).
  - P2 filled several P1 carry-over gaps (ITimeLogService, WeekGrid/SaveResult, IClock, ReadModels types) — all from architecture spec verbatim.
- **P3 COMPLETE** (5 tasks, 104 tests green, build OK). Commits: 44f9110, 8341dd1, 978535f, 6cc685f, eb2e417.
  - T1 TimesheetRowVm · T2 SmartInputPanelVm · T3 TimesheetViewModel + **full DI container in App.xaml.cs** (all repos+services+VM, InitializeAsync at startup) · T4 TimesheetTab.xaml · T5 SmartInputPreviewDialog.xaml.
  - DI does NOT yet register IExportService (P6) or VMs for other tabs — each later phase adds its own.
- **P4 COMPLETE** (5 tasks, 126 tests green, build OK). Commits: 730e0e5, 2e75938, bab9bf1, 8b79636, c268585.
  - T1 ITaskTemplateRepository (canonical CRUD + DI) · T2 RequestEditorViewModel/EditableTaskRowVm · T4 UsersViewModel · T3 RequestsViewModel (search→explicit RefreshCommand) · T5 Requests/UsersTab.xaml (+RequestsVM/UsersVM in DI).
  - DI now registers: all repos+services (P3), ITaskTemplateRepository, RequestsViewModel, UsersViewModel, TimesheetViewModel.
- **P5 COMPLETE** (4 tasks, 137 tests green, build OK). Commits: 6203d0f, de977be, 95f4825, b934444.
  - T1 report read-models · T2 ReportAggregator (pure, distinct-by-TaskId, no is_active filter) · T3 ReportsViewModel + DI (IReportAggregator + ReportsViewModel) · T4 ReportsTab.xaml + DateOnlyConverter (local resource).
  - Cross-phase fix: settings N-days key reconciled to `ReportsViewModel.NDaysKey="chua_log_n_days"` (P6 plan patched to write same key).
- **P6 COMPLETE** (3 tasks, 152 tests green, build OK). Commits: ae99e13, 5847146, 4cea478.
  - T1 ExportService (Markdown+Excel, EXP-01..04, DI) · T2 SettingsViewModel (SET-01..04, uses NDaysKey/SetDbPath/ITaskTemplateRepository) · T3 SettingsTab.xaml (Browse via OpenFileDialog code-behind).
  - T2 was committed on a stray feature branch by the subagent; controller merged it back to main (ff).
- **ALL 6 BUILD PHASES DONE. 152 tests, build clean.** Remaining: MainWindow+MainViewModel closeout (app not yet runnable — no window shown), then UAT/QA/ship.

## Resolved Items
- **TaskTemplate repository home (RESOLVED 2026-06-21):** single canonical `ITaskTemplateRepository` (GetAll/Insert/Delete) delivered by P4 Task 1, consumed by P6 SettingsViewModel; template methods NOT on ITaskRepository. Architecture spec §3 + P4 + P6 patched (commit d91e8f6).
- **Minor — `IAppConfig.DbPath` member name:** confirm during P1 (IAppConfig defined in P1 Task 3) — canonical name `DbPath`.

## Plan Set (Step 6)
| Phase | Plan file | Tasks / Waves | REQ coverage |
|---|---|---|---|
| P1 Data+Schema | `2026-06-21-P1-data-schema.md` | 6 / 4 | DATA-01..07, XC-01, XC-08, XC-09 ✓ |
| P2 Services | `2026-06-21-P2-services.md` | 7 / 3 | XC-02..07, XC-10, SI-01..04 ✓ |
| P3 Timesheet UI | `2026-06-21-P3-timesheet-ui.md` | 5 / 3 | TS-01..07, SI-05, SI-06 ✓ |
| P4 Requests+Users UI | `2026-06-21-P4-requests-users-ui.md` | 5 / 4 | REQ-01..04, USR-01..03 ✓ |
| P5 Reports | `2026-06-21-P5-reports.md` | 4 / 4 | RPT-01..04 ✓ |
| P6 Settings+Export | `2026-06-21-P6-settings-export.md` | 3 / 2 | SET-01..04, EXP-01..04 ✓ |

All plans: goal-backward `must_haves`, checkbox TDD tasks with full code, per-task `<model>` (base; quality profile shift applied at dispatch), zero intra-wave file overlap. XAML tasks are manual-verify (no headless WPF tests); all testable logic lives in unit-tested ViewModels/services.

## Key Decisions Made
- Project = WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite + Dapper / ClosedXML). Folder `AgentArchitectureManagement` is container; app = `TimesheetApp`.
- User identity via Windows username (`Users.windows_username`), fallback `SelectUserDialog` once.
- `TimeLogs` = single FK `task_id`; DefaultTasks unified into `Tasks` under hidden `DEFAULT` request.
- Concurrency: single-writer / last-write-wins on shared SQLite (OneDrive); conflict at file level = accepted risk.
- Mode B chosen by user (override of suggested Mode A).
- Research resolved: journal_mode=DELETE (not WAL), CommunityToolkit.Mvvm, MS.Data.Sqlite 8.0.x, MS.DI. 8 policy/scope decisions logged in spec appendix.
- Spec (STEP 5): 45 REQs (43 + XC-09, XC-10; advisory edit-lock deferred). 0 traceability gaps.
- Plan (STEP 6): 6 phase plans (30 tasks total) written + each orchestrator-reviewed to APPROVE. Execution mode TBD by user.

## Artifacts
- Design spec: `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
- Architecture spec: `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md`
- REQUIREMENTS: `.planning/REQUIREMENTS.md` (45 REQ, P1–P6)
- Plans: `docs/superpowers/plans/` (P1–P6)
- Config: `.planning/config.json`
- Research: `.planning/research/` (STACK, FEATURE, ARCHITECTURE, PITFALL, RESEARCH-SYNTHESIS)
