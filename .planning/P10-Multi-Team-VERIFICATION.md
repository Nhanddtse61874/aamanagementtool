# P10 — Multi-Team · Goal-Backward Verification (STEP 8b, Mode B)

**Date:** 2026-06-28
**Branch:** `feature/task-list-2026-06-27` (all 9 P10 waves committed)
**Verifier inputs:** plan `docs/superpowers/plans/2026-06-27-P10-multi-team.md` (must_haves), spec `docs/superpowers/specs/2026-06-27-multi-team-design.md`, `.planning/REQUIREMENTS.md` (TM-01..10).
**Method:** cross-referenced each `must_have` (observable_truths / required_artifacts / key_links) and each TM REQ against the real code under `src/TimesheetApp` (symbols + line numbers cited). Test suite executed.

**Build:** `dotnet build src/TimesheetApp.sln` → 0 errors, 0 warnings.
**Tests:** `dotnet test src/TimesheetApp.sln` → **423 / 423 passing** (0 failed, 0 skipped). Baseline was 328 → +95 new tests.

---

## 1. must_haves coverage

### 1a. observable_truths

| # | Truth | Status | Evidence |
|---|---|---|---|
| 1 | v7→v8 upgrade (Teams+UserTeams + team_id) + post-init bootstrap assigns ALL legacy backlogs/standup/users to "Architect Improvement" (idempotent, backup-first, no data loss) | **MET** | `DatabaseInitializer.cs` SchemaVersion=8 (L14); v8 migration step adds `team_id` to Backlogs + StandupEntries (L265-267); Teams/UserTeams DDL (L151-162). `TeamBootstrapService.MigrateExistingDbAsync` (L49-69): backup first (L51), `UPDATE Backlogs/StandupEntries SET team_id` WHERE NULL (L58-61), `INSERT OR IGNORE UserTeams SELECT id FROM Users` (L63-65). Idempotent guard `GetAllAsync().Count>0 → return` (L40). Tests: `SchemaV8UpgradeTests`, `TeamBootstrapServiceTests.Migration_assigns_all_rows_…` (asserts 0 NULL team_id, both users joined, backup Times.Once). |
| 2 | Fresh DB auto-creates active "My Team", seeds its per-team DEFAULT + tasks, immediately usable | **PARTIAL** | `TeamBootstrapService.FirstRunAsync` (L73-77) creates "My Team" + sets active. Per-team DEFAULT + tasks seeded by `DefaultTaskSyncService.SyncAsync` at startup (`App.xaml.cs:77`). **Gap:** the initializer STILL seeds a single global team-less DEFAULT (`EnsureDefaultBacklog`, `DatabaseInitializer.cs:283-294`, team_id NULL) which FirstRun does **not** repoint → fresh DB ends with a leftover orphan NULL-team DEFAULT plus the per-team DEFAULT (duplicate/dead row). No leak (NULL team_id excluded from all team-scoped grids/reports). See GAP-1. |
| 3 | Team = new entity; Project enum unchanged; user belongs to ≥1 teams (50-50 = two memberships) | **MET** | `Team` record `Entities.cs:9`; `BacklogProjects` enum unchanged `Entities.cs:33-37`; UserTeams many-to-many (`UserTeams` PK(user_id,team_id) `DatabaseInitializer.cs:158-162`; `TeamRepository.SetMembersAsync/AddMemberAsync`). |
| 4 | One active team scopes editing/creation; Log Work shows only active team's tasks incl DEFAULT | **MET** | `TimeLogService.GetWeekAsync` → `_tasks.GetActiveForTimesheetAsync(_currentTeam.ActiveTeamId)` (L154); `BuildGroupsAsync` scopes backlogs + tasks to active team (L204-205). `TaskRepository.GetActiveForTimesheetAsync` joins Backlogs, `b.team_id = @teamId` incl DEFAULT (same team_id). |
| 5 | Backlog/TaskList/Reports/DailyBoard each have a multi-team checkbox (default=active team) that aggregates; switching active team resets each filter | **MET** | Shared `TeamFilterViewModel` (default = active team only, L76; reset on `ActiveTeamChanged` → `Reload()`, L84-90; `CheckedTeamIds`). Consumed by `BacklogsViewModel` (SearchAsync teamIds), `TaskListViewModel`, `ReportsViewModel`, `DailyReportViewModel` (`GetTeamStandupAsync(CheckedTeamIds)`). UAT-pending: visual checkbox UX. |
| 6 | Each team has own DEFAULT; Annual Leave/Meeting attribute to active team; DefaultTaskSync materializes global DefaultTasks under every team | **MET** | `DefaultTaskSyncService.SyncAsync` loops `_teams.GetActiveAsync()` → `EnsureDefaultBacklogIdAsync(teamId)` via `GetDefaultForTeamAsync` (per-team, never global) → materializes global DefaultTasks per team (L46-90). `BacklogRepository.GetDefaultForTeamAsync` = `WHERE code='DEFAULT' AND team_id=@t` (L63-70). |
| 7 | Settings can create/rename/deactivate teams + assign users (M:N) | **MET** | `SettingsViewModel.AddTeamAsync/RenameTeamAsync/DeactivateTeamAsync` (L370-405); membership overlay `TeamMembershipEditorViewModel` + `SetMembersAsync` replace-all (L408-427). `SettingsTab.xaml` Teams list (L222-277) + membership overlay (L546-590). UAT-pending: visuals. |
| 8 | Reports/exports team-aware (grouping/column; RPT-04 banner scoped to active-team members) | **MET** | Markdown `## {team}` above `### {user}` (`ExportService.cs:42-52`); Excel Team column (L86,L102); TaskList archive Team column; standup archive groups by team; report tree adds `TeamNode` root (`ReportAggregator.cs:49-80`). RPT-04: `TimeLogService.GetUsersMissingLogsAsync` scoped to `_teams.GetUserIdsForTeamAsync(activeTeamId)` (L234-246). |
| 9 | No exports/grids leak another team's data; all prior ~328 tests stay green | **MET** | Team filter enforced in SQL (`team_id IN @teamIds`) not post-load, in `BacklogRepository.SearchAsync`, `TimeLogRepository.GetReportRowsAsync/GetExportRowsAsync` (R1), `StandupRepository.GetEntriesForDay/Range`. `noTeam = teamIds is null` short-circuits to all-teams; empty list / teamId 0 → matches nothing (R6). Leak-guard tests in `TeamScopedQueryTests`. All 423 tests green (328 prior + new). |

### 1b. required_artifacts

| Artifact | Status | Evidence |
|---|---|---|
| DatabaseInitializer v8 + Teams/UserTeams DDL; Team entity + TeamId on Backlog/StandupEntry; team name trailing on TimeLogReportRow | **MET** | `DatabaseInitializer.cs:14,151-162,265-267`; `Entities.cs:9` (Team), `Backlog.TeamId` L23, `StandupEntry.TeamId` (Standup entity); `ReadModels.cs:9-16` TimeLogReportRow `int? TeamId, string? TeamName` trailing (R6). |
| ITeamRepository (CRUD + UserTeams) + team params on Backlog/TimeLog/Task/Standup repos; GetDefaultForTeamAsync | **MET** | `ITeamRepository.cs` / `TeamRepository.cs` full CRUD + soft-delete + membership. Repo team params verified in all four repos. `GetDefaultForTeamAsync` `BacklogRepository.cs:63`. |
| ICurrentTeamService + JsonAppConfig.ActiveTeamId; ITeamBootstrapService | **MET** | `ICurrentTeamService.cs` / `CurrentTeamService.cs`; `IAppConfig.ActiveTeamId` (L28) + `JsonAppConfig` (L52,L84-88, backward-compat default 0 L44); `ITeamBootstrapService.cs` / `TeamBootstrapService.cs`. |
| DefaultTaskSyncService per-team loop; TimeLogService active-team working scope | **MET** | `DefaultTaskSyncService.cs:46-90` per-team loop; `TimeLogService.cs:154,190,204-205,234-246`. |
| StandupService team scope; SettingsViewModel Teams section + membership overlay + DataKind.Teams | **MET** | `StandupService` AddEntry stamps team (L103-107), GetTeamStandup(teamIds) members of checked teams; `SettingsViewModel` Teams section; `DataChangedMessage.cs:16` `Teams`. |
| Sidebar switcher (MainWindow/MainViewModel); TeamFilterViewModel + TeamFilter control on 4 screens | **MET** | `MainWindow.xaml:59-69` ComboBox switcher above user chip, gated `ShowTeamSwitcher`; `MainViewModel` `AvailableTeams`/`ActiveTeam`/`ShowTeamSwitcher` (>1) + setter → `SetActiveTeamAsync`. `TeamFilterViewModel` + `Views/Controls/TeamFilter.xaml` wired into 4 tab XAMLs. UAT-pending: visuals. |
| ExportService/TaskListArchive/StandupArchive/Reports team-aware | **MET** | All four verified (see truth #8). |
| App.xaml.cs DI + startup order + first-run user→team join | **MET** | `App.xaml.cs` registers ITeamRepository/ICurrentTeamService/ITeamBootstrapService singletons (L150-152); startup order InitializeAsync→EnsureBootstrapped→archive backfills→auto-backup→DefaultTaskSync→MainViewModel.InitializeAsync (L47-83); first-run join `MainViewModel.InitializeActiveTeamAsync` → `AddMemberAsync` then `ICurrentTeamService.InitializeAsync` (L219-234). |

### 1c. key_links

| Key link | Status | Evidence |
|---|---|---|
| team_id NULLABLE; bootstrap after InitializeAsync, BEFORE DefaultTaskSync; XC-10 backup before backfill; idempotent "Teams empty?" guard (R3) | **MET** | team_id columns nullable (no NOT NULL `DatabaseInitializer.cs:266-267`). Startup order `App.xaml.cs:55` (bootstrap) then `:77` (sync). `TeamBootstrapService` backup L51, guard L40. |
| GetExportRowsAsync/GetReportRowsAsync gain team filter — no cross-team leak (R1) | **MET** | `TimeLogRepository` both methods `AND (@noTeam OR r.team_id IN @teamIds)`, SQL-enforced. Tests `GetExportRows_filters_by_team…`, `GetReportRows_filters_by_team`. |
| DEFAULT resolved per team via GetDefaultForTeamAsync, never global GetByCodeAsync (R2) | **MET** | `DefaultTaskSyncService.EnsureDefaultBacklogIdAsync` uses `GetDefaultForTeamAsync(teamId)` (L40). Test `GetDefaultForTeam_resolves_only_that_teams_default`. |
| Inject ICurrentTeamService directly (NO second Func<int>) | **MET** | `App.xaml.cs:147-152` explicit comment; only the user-id `Func<int>` registered (L162-166). DI test asserts the provider is not clobbered. |
| active team validated against user's active memberships on resolve; stale → first available (R5) | **MET** | `CurrentTeamService.InitializeAsync` (L33-46): AvailableTeams = memberships ∩ active; persisted-if-available else first else 0. `SetActiveTeamAsync` rejects non-member (L50-52). Test `Falls_back_to_first_available_when_persisted_id_is_stale`. |
| teamId 0 = empty (not match-all); read APIs optional teamIds=null preserves existing tests (R6) | **MET** | All read repos: `noTeam = teamIds is null`; empty list / teamId 0 → no rows. Tests `…_null_teamIds_returns_all_teams`, `…_empty_teamIds_returns_nothing`, `GetActiveForTimesheet_teamId_zero_returns_nothing`. |

---

## 2. TM REQ coverage (TM-01..10 → implementing artifact)

| REQ | Status | Implementing artifact / evidence |
|---|---|---|
| **TM-01** Schema v8: Teams + UserTeams + team_id additive migration | **MET** | `DatabaseInitializer.cs` (v8 step L265-267, DDL L151-162, version pin L276-280). Test `SchemaV8UpgradeTests` (adds tables/cols, user_version=8, idempotent, preserves data). |
| **TM-02** Data migration → "Architect Improvement" (backlogs, standup, users; legacy DEFAULT becomes its DEFAULT) | **MET** | `TeamBootstrapService.MigrateExistingDbAsync` (L49-69) repoints NULL team_id incl the seeded global DEFAULT (test asserts 0 NULL, `…sets_active`). Active team set (L68). |
| **TM-03** Team CRUD + membership in Settings | **MET** | `SettingsViewModel` (L370-427), `TeamMembershipEditorViewModel`, `TeamRepository` (CRUD + soft-delete + SetMembers replace-all). Broadcasts `DataKind.Teams`. UAT-pending: Settings UI visuals. |
| **TM-04** DEFAULT backlog per team + DefaultTasks sync per team | **MET** | `DefaultTaskSyncService.cs:37-90` (per-team loop, `GetDefaultForTeamAsync`); team-create triggers `EnsureDefaultBacklogIdAsync` + `SyncAsync` (`SettingsViewModel.cs:378-379`). |
| **TM-05** Active team + sidebar switcher (persisted) | **MET** | `ICurrentTeamService` + `MainViewModel`/`MainWindow.xaml` switcher; persisted `JsonAppConfig.ActiveTeamId`. UAT-pending: sidebar visuals. |
| **TM-06** Working scope = active team (grid, backlog create, standup create) | **MET** | Grid: `TimeLogService` (L154,L204-205). Backlog create: `BacklogsViewModel.SaveNewAsync` stamps `TeamId = ActiveTeamId` (`RequestsViewModel.cs:197-203`), edit preserves (L229). Standup create: `StandupService.AddEntryAsync` (L103-107). |
| **TM-07** Multi-team display filter (checkbox) on 4 view screens | **MET** | `TeamFilterViewModel` + `TeamFilter.xaml` on Backlog/TaskList/Reports/DailyBoard; default=active, session-persist, team chip/column when >1 (Reports uses `TeamNode` tree root variant). UAT-pending: checkbox UX. |
| **TM-08** Team-aware reports & exports | **MET** | `ExportService` (MD `## team`, Excel Team col), `TaskListArchiveService` (Team col), `StandupArchiveService` (team groups), `ReportAggregator` (`TeamNode` root, single-team collapses). |
| **TM-09** First-run setup + validated defaults | **MET** (defaults) / **PARTIAL** (DEFAULT dup) | `TeamBootstrapService.FirstRunAsync` creates one active "My Team"; validated defaults already in `JsonAppConfig` (N=3 via SET-02, auto-backup off L42, retention 30 L43). Per-team DEFAULT + tasks via startup `SyncAsync`. **Caveat:** leftover orphan global DEFAULT on fresh DB (GAP-1) — does not break usability but violates the plan's "drop initializer single-DEFAULT seeding" intent. |
| **TM-10** No regression to team-agnostic features | **MET** | Full suite 423/423 green (328 prior preserved via `teamIds=null` legacy behavior + ExportFilter trailing optional + Moq call-site upkeep). |

---

## 3. GAPS (ranked)

**GAP-1 (Low / cosmetic-data, non-blocking) — Fresh DB leaves an orphan team-less DEFAULT backlog.**
The plan (W1) and spec §4 say to "drop the initializer's single-DEFAULT seeding" and let bootstrap + per-team `DefaultTaskSync` own DEFAULT. The initializer still runs `EnsureDefaultBacklog` (`DatabaseInitializer.cs:283-294`, team_id NULL) and `SeedDefaultTasksIfEmpty` (L296-311). On the **existing-DB** path this is harmless (bootstrap repoints the NULL DEFAULT before sync — verified by `TeamBootstrapServiceTests` L80). On the **fresh-DB** path, `FirstRunAsync` does NOT repoint it, then `SyncAsync` creates a *second* DEFAULT (team-scoped) via `GetDefaultForTeamAsync` not finding the NULL one → DB ends with two DEFAULT backlogs (one orphan team_id=NULL).
- Impact: no data leak (NULL team_id is excluded from every team-scoped grid/report/export), no test failure, no crash. Purely a dead/duplicate hidden row.
- Fix options: (a) drop `EnsureDefaultBacklog`/`SeedDefaultTasksIfEmpty` from the initializer per the original plan and retarget `DatabaseInitializerTests.InitializeAsync_Is_Idempotent_No_Duplicate_Default_Request` (the FIX-3 note about the `COUNT(DEFAULT)==1` assertion was NOT actioned — it still asserts 1 at `DatabaseInitializerTests.cs:57`); or (b) have `FirstRunAsync` repoint the seeded NULL DEFAULT to the new team (one-line UPDATE, mirrors the migrate path) so sync reuses it. Option (b) is the smaller surgical change.

**GAP-2 (Informational, not a defect) — TM-09 "no null/invalid setting state" relies on existing defaults, not a bootstrap validation pass.** The spec says bootstrap "just ensures no null/invalid state." `TeamBootstrapService` does not actively re-validate settings; correctness is delegated to `JsonAppConfig`'s defaulted fields (N-days handled in SET-02 code, auto-backup=false, retention=30). Acceptable per the spec's own "already defaulted in code" note; flagged only for traceability.

**No Critical or Important gaps. No cross-team leak found. No regression (all 423 tests green).**

---

## 4. UAT-pending items (visual-only, deferred to STEP 8a by design — spec §11)
- Sidebar active-team switcher UX (TM-05).
- Multi-team checkbox dropdown UX on the 4 screens (TM-07).
- Settings Teams section + membership editor overlay visuals (TM-03).
- Team chips/columns rendering; Daily board membership cards; first-run experience on a brand-new DB.

---

## 5. VERDICT

**VERIFIED-WITH-GAPS.**

All 9 observable truths, 9 required artifacts, 6 key links, and all 10 TM REQs are implemented and evidenced in code; build is clean and the full suite (423/423) is green with no cross-team leak and no regression. The only deviation is **GAP-1** (Low): the initializer's legacy single-DEFAULT seeding was not removed as the plan specified, leaving a harmless orphan team-less DEFAULT backlog on a fresh DB (no leak, no functional impact). This does not block ship but should be closed (a one-line repoint in `FirstRunAsync`, or removal of initializer DEFAULT seeding + retargeting the `COUNT(DEFAULT)==1` test per the un-actioned FIX-3 note) to fully satisfy spec §4 / observable-truth #2.
