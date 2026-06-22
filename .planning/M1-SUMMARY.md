# M1 SUMMARY — WPF Desktop Timesheet Tool v1

**Date:** 2026-06-22
**Mode:** B (team spine), Subagent-Driven autonomous execution
**Status:** Build complete, QA-passed (APPROVE-WITH-CONDITIONS → both conditions cleared). Awaiting user UAT.

## What was built
Internal desktop timesheet tool (.NET 8 / WPF MVVM / SQLite + Dapper / ClosedXML) for a 2–5 person team, shared `.db` over OneDrive/Teams, Markdown + Excel export. App = `src/TimesheetApp`.

## Outcome
- **45 REQs** across 6 phases — all implemented.
- **162 unit tests** passing; `dotnet build` clean (0 warnings/errors).
- WPF app **launches** (smoke-verified); 5 tabs wired (Timesheet · Requests · Users · Reports · Settings).
- **34 atomic commits** on `main`.

## Phases (commits)
- **P1 Data+Schema** (26 tests): solution, models, JsonAppConfig, SqliteConnectionFactory (journal=DELETE/FK/Pooling-off), SqliteMaintenance (conflict-copy + journal-verify), DatabaseInitializer (schema+seed+migrations).
- **P2 Services** (→78): SmartInputService (integer-tenths), CurrentUserService, DbBackupHelper, repositories (TimeLog/User/Task/Request/Settings/DefaultTask), TimeLogService (8h validation + atomic apply), DefaultTaskSyncService.
- **P3 Timesheet UI** (→104): TimesheetRowVm, SmartInputPanelVm, TimesheetViewModel + full DI container, TimesheetTab + SmartInputPreviewDialog.
- **P4 Requests+Users UI** (→126): ITaskTemplateRepository (canonical), RequestEditorViewModel, RequestsViewModel, UsersViewModel, tab XAML.
- **P5 Reports** (→137): report read-models, ReportAggregator, ReportsViewModel, ReportsTab (TreeView drill-down + banner).
- **P6 Settings+Export** (→152): ExportService (Markdown+Excel), SettingsViewModel, SettingsTab.
- **Shell** (→157): MainWindow + MainViewModel + SelectUserDialog + startup wiring.
- **Closeout fixes** (→162): first-run DB-dir auto-create (smoke-caught); QA C1 (startup DefaultTask sync) + I1 (XC-09 journal-verify wired to bulk paths).

## Key cross-phase reconciliations (controller-resolved)
1. TaskTemplate store unified to canonical `ITaskTemplateRepository` (was split P4 read vs P6 ITaskRepository).
2. `IAppConfig.DbPath` is get-only + `SetDbPath()` method (P6 plan patched).
3. Settings N-days key unified to `ReportsViewModel.NDaysKey="chua_log_n_days"` (Settings-write == Reports-read).
4. Test project TFM `net8.0-windows` (net8.0 can't reference the WPF app).

## Verification gaps closed by integration testing (not unit tests)
- **Smoke launch** caught: first-run `SQLite Error 14` (missing parent dir) → fixed.
- **QA review** caught: DefaultTasks never materialized at startup (C1) + XC-09 dead code (I1) → both fixed + regression-tested.

## Known suggestions deferred to UAT/backlog (non-blocking)
- S1: XC-10 backup filename collision possible within same millisecond (low probability).
- S2: XC-10 `.bak` files accumulate unbounded in the synced folder (no retention prune).
- S3: Timesheet row labels don't show request_code (GetWeekAsync RequestCode empty) — cosmetic; TS-02 rows still correct.
- XC-09 warning currently routes to `Trace` (swap sink for a UI banner later if desired).
- Advisory single-editor lock: deferred by design (conflict-copy detection covers the dominant failure mode).

## For UAT (manual-verify — XAML/runtime, not unit-testable)
Run `src/TimesheetApp` and verify: tabs load; Timesheet weekly grid inline-edit + 8h red block + footer totals + smart-input preview; Requests create-with-template/edit/soft-delete-task (no delete-Request); Users add/soft-delete; Reports weekly/monthly/drill-down + "chưa log" banner; Settings DB-path Browse/N-days/templates/default-tasks; Excel + Markdown export.
