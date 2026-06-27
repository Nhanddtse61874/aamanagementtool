---
must_haves:
  observable_truths:
    - "DR-01: opening a v4 DB runs migration v5 creating StandupEntries + StandupIssues (+ indexes) and sets user_version=5; idempotent; M1 tables untouched."
    - "DR-02/03: a standup entry round-trips all fields; ad-hoc codes persist with request_id=null; deadline nullable; queryable by (user,day) and by day."
    - "DR-04: an entry has 0..n issues (issue_text required, solution nullable=pending, status open/pending/resolved); deleting an entry cascades its issues."
    - "DR-05: entry status restricted to Todo/In-process/Done/Pending; invalid rejected."
    - "DR-06: CanEditDay true only for today+yesterday; entry add/edit/delete no-op on locked days and for non-owners; issues exempt."
    - "DR-07: Input tab lists my Yesterday/Today for a date, picks existing request+task or ad-hoc code, saves owned entries; read-only on locked days."
    - "DR-08: Board tab shows one card per active user for the date (dynamic count; empty sections allowed)."
    - "DR-09: weekly markdown archive {yyyyMMdd}_daily.md (week Monday) under Documents/TimesheetApp/StandupArchives; startup backfills any completed week with data but no file; empty week => no file; re-export idempotent."
    - "DR-10: Daily Report nav enabled hosting Input+Board sub-tabs; standup edits broadcast DataKind.Standup so the board refreshes live."
  required_artifacts:
    - "src/TimesheetApp/Models/StandupModels.cs — StandupEntry, StandupIssue, StandupStatus, StandupIssueStatus, StandupSection, UserStandup, StandupEntryView, StandupEntryDraft."
    - "src/TimesheetApp/Data/DatabaseInitializer.cs — SchemaVersion 4->5; CreateTables adds the two tables; Migrations[] step v5; nothing else changed."
    - "src/TimesheetApp/Data/Repositories/IStandupRepository.cs + StandupRepository.cs — Dapper CRUD + range query (Raw classes, TEXT dates, one connection/method)."
    - "src/TimesheetApp/Services/IStandupService.cs + StandupService.cs — grouping, validation, CanEditDay, picker passthrough, issue ops."
    - "src/TimesheetApp/Services/IStandupArchiveService.cs + StandupArchiveService.cs — FileNameFor, ExportWeekAsync, BackfillMissingWeeksAsync (IAppConfig dir + IClock + repo)."
    - "src/TimesheetApp/ViewModels/DailyReportViewModel.cs — date, my/board collections, add/edit/delete + issue commands, archive backfill, messenger refresh."
    - "src/TimesheetApp/Views/Tabs/DailyInputTab.xaml(.cs) + DailyBoardTab.xaml(.cs) — input table + board cards."
    - "src/TimesheetApp/Services/DataChangedMessage.cs — add DataKind.Standup."
    - "Tests: StandupRepositoryTests, StandupServiceTests, StandupArchiveServiceTests, DailyReportViewModelTests."
  required_wiring:
    - "App.xaml.cs DI: IStandupRepository, IStandupService, IStandupArchiveService singletons; DailyReportViewModel transient; startup calls IStandupArchiveService.BackfillMissingWeeksAsync()."
    - "MainViewModel: DailyReportViewModel child + OnActiveViewChanged 'dailyreport' case + ActivateTab hook."
    - "MainWindow.xaml: enable Daily Report nav (ConverterParameter=dailyreport) + content DockPanel hosting Input/Board sub-tabs."
    - "TestDb: extend schema via real DatabaseInitializer (auto-gets v5); add standup seed helpers as needed."
  key_links:
    - "DailyReportViewModel.LoadInputAsync -> IStandupService.GetMyStandupAsync(date) -> Yesterday/Today collections (DR-07)."
    - "DailyReportViewModel.LoadBoardAsync -> IStandupService.GetTeamStandupAsync(date) -> per-user cards (DR-08)."
    - "Add/Update/Delete commands -> IStandupService gated by CanEditDay+owner -> StandupRepository -> messenger DataKind.Standup (DR-06, DR-10)."
    - "App.OnStartup -> IStandupArchiveService.BackfillMissingWeeksAsync -> ExportWeekAsync per missing completed week -> {yyyyMMdd}_daily.md (DR-09)."
  req_coverage: [DR-01, DR-02, DR-03, DR-04, DR-05, DR-06, DR-07, DR-08, DR-09, DR-10]
---

# P7 — Daily Report (Standup) Implementation Plan

> **Mode Selection Gate (STEP 3):** **Mode A (solo, autonomous)** — user explicitly requested
> autonomous implementation; single stack (.NET/WPF), one cohesive feature, additive migration,
> no cross-team output contracts. Scored low on domain-count/cross-team/output-format → Mode A.
> Research (STEP 4): **skipped with justification** — brownfield, the feature reuses established
> in-repo patterns (Dapper repo + Raw classes, migration array, CommunityToolkit VM, sidebar nav,
> sub-tab host) already mapped in the design spec §6; no new external dependency. (Recorded as a
> deviation: `workflow.research:false` for P7.)

**Goal:** Add a Daily Standup feature — per-user Yesterday/Today entries (existing or ad-hoc
requests, deadline, status, issues+solutions), a team board for the day, and a weekly markdown
archive — on top of the existing M1 data/service/shell patterns. Source of truth:
`docs/superpowers/specs/2026-06-25-daily-report-standup-design.md`.

**Tech Stack:** .NET 8 (`net8.0-windows`, WPF), CommunityToolkit.Mvvm, Dapper + SQLite, xUnit + Moq.

## Waves (zero intra-wave file overlap)

| Wave | Tasks | Files |
|---|---|---|
| **W1** | T1 models, T2 migration v5 | `Models/StandupModels.cs`, `Data/DatabaseInitializer.cs` |
| **W2** | T3 repository + tests | `Data/Repositories/IStandupRepository.cs`+`StandupRepository.cs`, `Tests/Data/StandupRepositoryTests.cs` |
| **W3** | T4 service + tests, T5 archive service + tests | `Services/IStandupService.cs`+`StandupService.cs`, `Services/IStandupArchiveService.cs`+`StandupArchiveService.cs`, tests |
| **W4** | T6 VM + DI + DataChangedMessage + tests | `ViewModels/DailyReportViewModel.cs`, `App.xaml.cs`, `Services/DataChangedMessage.cs`, `Tests/ViewModels/DailyReportViewModelTests.cs` |
| **W5** | T7 views + nav wiring (manual-verify) | `Views/Tabs/DailyInputTab.xaml(.cs)`, `Views/Tabs/DailyBoardTab.xaml(.cs)`, `MainWindow.xaml`, `ViewModels/MainViewModel.cs` |

## Task summary

- **T1 — Models** (`StandupModels.cs`): entity records + status/section constant classes + read/draft DTOs (design §2).
- **T2 — Migration v5** (`DatabaseInitializer.cs`): bump `SchemaVersion` 4→5; add `StandupEntries`+`StandupIssues` to `CreateTables` (`IF NOT EXISTS` + indexes); append one `Migrations[]` step (no-op alter — tables are created idempotently in CreateTables, so the step just gates the version bump). Verify M1 untouched.
- **T3 — Repository** (TDD): Dapper CRUD with `Raw` classes + TEXT date parsing at the boundary; `GetEntriesAsync(user,day)`, `GetEntriesForDayAsync(day)`, entry insert/update/delete (cascade issues), issue CRUD, `GetIssuesForEntriesAsync`, `GetEntriesForRangeAsync`. Integration tests on `TestDb`.
- **T4 — Service** (TDD): `GetMyStandupAsync`/`GetTeamStandupAsync` grouping by section + issue attach + `Editable` flag; `CanEditDay` (today/yesterday); validation (section/status/issue-status/non-empty/ad-hoc resolution); owner gating on entry ops via `ICurrentUserService`; issue ops ungated; picker passthrough to request/task repos. Mock-based tests.
- **T5 — Archive service** (TDD): `FileNameFor` (Monday stamp `{yyyyMMdd}_daily.md`), `ExportWeekAsync` (Mon–Fri markdown grouped date→user→section+issues; overwrite), `BackfillMissingWeeksAsync` (scan completed weeks with data lacking a file, generate). Tests on a temp dir with a stub repo/clock.
- **T6 — ViewModel + wiring** (TDD where logic): `DailyReportViewModel` (CommunityToolkit) — `SelectedDate`, `Yesterday`/`Today`/`Board` collections, add/edit/delete + issue + archive commands, `DataChangedMessage` subscribe/broadcast; add `DataKind.Standup`; register DI + transient VM; App startup calls `BackfillMissingWeeksAsync`. VM tests with mocked service.
- **T7 — Views + nav** (manual-verify): `DailyInputTab` (date picker, Yesterday/Today editable tables, request combo existing-or-adhoc, task picker, issue add, read-only when locked) + `DailyBoardTab` (per-user cards). Enable nav in `MainWindow.xaml` (`ConverterParameter=dailyreport`) + content DockPanel + inner TabControl; `MainViewModel` child VM + `OnActiveViewChanged` case. Build + manual UAT.

## Commit strategy
`commit_atomic:true` — one commit per task (W1 may be one commit for T1+T2 as they form the data foundation). Conventional messages `feat(daily): …`. Branch `feature/daily-report-2026-06-25`.

## Self-review (coverage)
DR-01→T2; DR-02/03→T1+T3; DR-04→T1+T3; DR-05→T1+T4; DR-06→T4; DR-07→T6+T7; DR-08→T6+T7; DR-09→T5+T6; DR-10→T6+T7. Every DR maps to ≥1 task with a test or manual-verify gate. UI (T7) is manual-verify (XAML) per project convention; all logic (T2–T6) is automated-tested.
