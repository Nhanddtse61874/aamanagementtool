# P10 Multi-Team — PITFALL RESEARCH (happypowerprocess STEP 4, Mode B)

**Date:** 2026-06-27 · **Branch:** `feature/task-list-2026-06-27` · **Schema:** v7 → target **v8**
**Stack:** WPF .NET 8 / SQLite+Dapper / OneDrive-synced single-file DB, short open→work→close connections, `journal_mode=DELETE`, FK ON every connection.
**Scope of this doc:** concrete risks of retrofitting a `team_id` dimension into an app built single-team. Every claim tagged `[VERIFIED]` (read the code), `[CITED]` (from REQUIREMENTS/STATE/UPCOMING), or `[ASSUMED]` (inferred).

Source REQs: TM-01..10, XC-01/06/09/10, DATA-05 (`.planning/REQUIREMENTS.md`). Decisions: `.planning/UPCOMING-FEATURES.md` P10. Schema history + OneDrive/journal rules: `.planning/STATE.md`.

---

## TOP-6 RISKS (ranked by severity)

| # | Risk | Severity | Where |
|---|---|---|---|
| **R1** | **Cross-team data leak via `GetExportRowsAsync` + whole-team grid + reports** — no user/team filter today; exports & whole-team views show ALL teams once data is multi-team. | **Critical** (privacy + correctness) | `TimeLogRepository.cs:67-78` |
| **R2** | **`EnsureDefaultBacklogIdAsync` keys on `GetByCodeAsync("DEFAULT")`** — returns the *first* DEFAULT regardless of team. With DEFAULT-per-team, sync writes all default tasks under one team's DEFAULT and Annual Leave/Meeting attribute to the wrong team / double-count. | **Critical** (data mis-assignment) | `DefaultTaskSyncService.cs:34-39,45-50` |
| **R3** | **v8 backfill correctness + idempotency** — assigning ALL existing data to "Architect Improvement" inside the init transaction; re-run safety; new `team_id` columns must be nullable (not NOT NULL) or every old-client/legacy insert path breaks. | **Critical** (corruption / partial-failure) | `DatabaseInitializer.cs:179-258` |
| **R4** | **RPT-04 "chưa log" banner cannot be team-scoped** — it intersects `GetUserIdsWithLogsInRangeAsync` (no team) with `Users.GetActiveAsync` (no team). The plan adds `team_id` only to Backlogs+StandupEntries, **not Users** → schema gap; banner lists every team's users on every team. | **High** (correctness, schema gap) | `TimeLogRepository.cs:86-87` + `UserRepository.cs:18` |
| **R5** | **Active-team staleness on shared OneDrive DB** — active team persisted app-local (`appsettings.json`, no validation), but team rows live in the shared DB and can be renamed/deactivated/deleted, or the user removed from membership, on another machine. Dereferencing a stale `CurrentTeam` → empty views or crash. | **High** (correctness / crash) | `JsonAppConfig.cs` (no validation) + `CurrentUserService.cs` pattern |
| **R6** | **TestDb fixture + single-DEFAULT invariant break the whole suite** — `TestDb.SeedRequestAsync` inserts Backlogs with no `team_id`; `DatabaseInitializerTests` asserts exactly one DEFAULT and "Seven tables"; `SchemaV7UpgradeTests` pins target version. ~316 tests, integration tests cascade off TestDb. | **High** (regression blast radius) | `TestDb.cs:61-69`, `DatabaseInitializerTests.cs:55,17,35` |

---

## 1. Migration v7→v8 + data backfill (HIGHEST)

**Current shape** `[VERIFIED]` (`DatabaseInitializer.cs`):
- One transaction wraps `CreateTables → RunMigrations → EnsureDefaultBacklog → SeedDefaultTasksIfEmpty` (`:23-35`).
- `Migrations[]` is an index-gated array; step N runs when `user_version < N+1`; const `SchemaVersion=7` (`:14,247-257`). v8 = append step at index 7→8 region, bump const to 8.
- All prior steps are additive `ALTER TABLE ADD COLUMN` / `CREATE TABLE` — never `NOT NULL` without DEFAULT (`:191-244`).
- `EnsureDefaultBacklog` (`:260-271`) and `SeedDefaultTasksIfEmpty` (`:273-288`) are idempotent (INSERT…WHERE NOT EXISTS / COUNT==0 gate) and run **inside** the same tx, **after** migrations.

**Pitfalls:**
1.1 **NOT NULL `team_id` would break every legacy/old-client insert.** `[VERIFIED]` `TM-01` says "no inline FK"; it must also be **nullable** (or `DEFAULT NULL`). `Tasks`, `StandupEntries.InsertEntryAsync` (`StandupRepository.cs:65`), `BacklogRepository.InsertAsync`, and the test `TestDb.SeedRequestAsync` (`TestDb.cs:65`) all insert without team_id today. A NOT NULL column with no default fails those inserts. **Mitigation:** `ADD COLUMN team_id INTEGER` (nullable), backfill in the same step, never NOT NULL. `[CITED]` mirrors the v4 `assignee_user_id` precedent (`DatabaseInitializer.cs:214-215`).

1.2 **Backfill ordering vs `EnsureDefaultBacklog`.** `[VERIFIED]` `EnsureDefaultBacklog` runs after migrations and creates the single legacy DEFAULT if missing. If the v8 step creates team "Architect Improvement" and stamps existing backlogs (incl. the existing DEFAULT) with its id, that step **must run before** `EnsureDefaultBacklog`, or `EnsureDefaultBacklog` (which has no team awareness) will see a DEFAULT already exists and skip — leaving the team's DEFAULT correct, BUT a *fresh* DB path (TM-09) needs `EnsureDefaultBacklog` to create the DEFAULT **for the new team**, which it can't (it has no team_id param). **Mitigation:** make DEFAULT-backlog creation team-aware (param `teamId`), call it once per team; the v8 migration creates the team THEN assigns the existing DEFAULT's team_id; first-run (TM-09) creates team then DEFAULT-for-team. Do not leave `EnsureDefaultBacklog` team-blind. `[ASSUMED]`

1.3 **Idempotency of "create team Architect Improvement".** `[VERIFIED]` migration steps run exactly once (index-gated), so a literal `INSERT Teams` runs once — fine. BUT if a partial failure leaves `user_version` un-bumped (tx rollback), re-run re-executes the whole step set from `current` — the INSERT must be `WHERE NOT EXISTS (name='Architect Improvement')` to be safe even though gating normally prevents re-entry. **Mitigation:** guard the team INSERT idempotently like `EnsureDefaultBacklog` does (`:265-268`). `[ASSUMED]`

1.4 **Partial failure / single big transaction.** `[VERIFIED]` everything is one tx → atomic: a throw mid-backfill rolls back cleanly and `user_version` stays 7 (good). The real risk is **OneDrive-mid-write corruption**, not SQL atomicity: the bulk UPDATE of every backlog/standup row is exactly the kind of "bulk write" XC-10 wants a backup before. The init path does **not** call `DbBackupHelper` (only `DefaultTaskSyncService.SyncAsync` does, `:43`). **Mitigation:** take a one-shot `File.Copy` backup of the `.db` immediately before running the v8 step (extend XC-10 to the migration), and verify `-journal` is gone after (XC-09, `SqliteMaintenance.IsJournalGone`). `[VERIFIED]` `DefaultTaskSyncService.cs:72`.

1.5 **Backward-compat: old client opening a v8 DB.** `[CITED]` DATA-05/TM-01 require additive-only so an old client still works. ADD COLUMN is additive and old read queries (`SELECT explicit columns`) won't see `team_id` — safe. The danger: an **old client writing** a new Backlog/StandupEntry leaves `team_id` NULL → that row becomes "teamless" and invisible to team-scoped queries on a new client. **Mitigation:** team-scoped queries should treat `team_id IS NULL` as belonging to the migrated default team (a `COALESCE(team_id, @defaultTeam)` or an "orphan adoption" sweep on startup). `[ASSUMED]`

1.6 **`GetByCodeAsync("DEFAULT")` collides across teams post-migration.** `[VERIFIED]` `BacklogRepository.GetByCodeAsync` filters only `WHERE backlog_code=@code`; with DEFAULT-per-team there are N rows named DEFAULT — `QuerySingleOrDefault` will throw or pick arbitrarily. This is the engine of R2. **Mitigation:** add `team_id` to that query (and to `EnsureDefaultBacklogIdAsync`).

> **Most dangerous in this section:** R3 (NOT NULL + ordering) — a wrong column constraint or wrong step order corrupts the upgrade for every existing user, and the existing user IS the company (single shared DB on OneDrive). **Must not regress:** `SchemaV7UpgradeTests` (v6→v7 path still valid), `DatabaseInitializerTests` idempotency (3× init), the legacy-`Requests`-not-recreated guard (`DatabaseInitializer.cs:154-176`).

---

## 2. Cross-team data leakage (every unfiltered query)

After `team_id` exists, **any query missing a team filter shows or mis-assigns across teams.** `Tasks`/`TimeLogs` carry no team_id — they inherit via `Tasks.backlog_id → Backlogs.team_id`, so those queries must JOIN through Backlogs to filter. `[VERIFIED]`

Enumerated by screen/feature (file:line):

| Screen / feature | Query | Filters today | Leak if team_id added but filter missing |
|---|---|---|---|
| **Exports (Excel/MD)** | `GetExportRowsAsync` `TimeLogRepository.cs:67-78` | date + optional project only; **no user, no team** | **R1 — most dangerous.** Already JOINs `Backlogs r`; add `AND (@team IS NULL OR r.team_id=@team)`. Feeds `ExportService.LoadAsync`. |
| **Whole-team timesheet grid** | same `GetExportRowsAsync` via `TimeLogService.GetWeekGroupedAllUsersAsync` | — | shows all teams' logs in the "whole team" aggregate. |
| **Per-user Reports (weekly/monthly/tree)** | `GetReportRowsAsync` `TimeLogRepository.cs:48-58` | user + date | already JOINs `Backlogs r`; add `AND r.team_id=@team`. |
| **Timesheet grid rows** | `GetActiveForTimesheetAsync` `TaskRepository.cs:33-38` | `t.is_active=1` | already JOINs `Backlogs b`; add `AND b.team_id=@team`. Otherwise grid shows every team's tasks. |
| **Backlog list / picker** | `SearchAsync(null)` `BacklogRepository.cs:24` | none | returns ALL backlogs; root of grid/picker/smart-fill leaks. Add `WHERE team_id=@team`. |
| **Smart-fill task list** | `SearchAsync(term)` `BacklogRepository.cs:30` → `GetActiveByBacklogAsync` | code/project LIKE | smart-fill backlog search spans teams; tasks pulled per matched backlog. Fix at SearchAsync. |
| **Standup board (team)** | `GetEntriesForDayAsync` `StandupRepository.cs:31-37` | date only | board shows all teams' standup cards. Add `AND team_id=@team`. |
| **Standup weekly archive** | `GetEntriesForRangeAsync` `StandupRepository.cs:42-49` | range only | archive mixes teams. |
| **Backlog hours roll-up** | `GetLoggedHoursByBacklogAsync` `TimeLogRepository.cs:110` (the GROUP BY) | none (all-time SUM) | rolls up across all teams' backlogs; must scope via JOIN Backlogs. |
| **"chưa log" banner** | `GetUserIdsWithLogsInRangeAsync` `TimeLogRepository.cs:86` + `Users.GetActiveAsync` `UserRepository.cs:18` | date / is_active | **R4** — see §below; needs Users team concept. |

**Insight `[VERIFIED]`:** the three highest-traffic leaks (`GetExportRowsAsync`, `GetReportRowsAsync`, `GetActiveForTimesheetAsync`) already JOIN `Backlogs`, so each is a one-clause fix — but each is independently easy to forget. **Most dangerous = R1 (`GetExportRowsAsync`)** because it has *no user filter at all today* and feeds exports leaving the app (privacy boundary), and it backs three surfaces at once.

**Mitigation:** centralize the team predicate — pass `teamIds` (the checked teams for view queries, the single active team for write/grid queries) into every query above; add a repository-level unit test per query asserting team-A data is absent from a team-B fetch.

---

## 3. Active-team context staleness

`[VERIFIED]` Active team will follow the `IAppConfig`/`CurrentUserService` pattern: persisted app-local in `%APPDATA%\TimesheetApp\appsettings.json` via `JsonAppConfig` (which performs **no validation** — stores any int verbatim), resolved/cached by a `CurrentTeamService` mirroring `CurrentUserService` (`Current` cached for process lifetime, no invalidation).

The DB is shared on OneDrive; the active-team pointer is per-machine. Divergence is inevitable:
- Team deactivated/renamed/deleted on machine B → machine A's persisted `ActiveTeamId` is stale.
- User removed from the team (UserTeams) on machine B → it's still their active team on machine A.
- `ActiveTeamId` points at a now-inactive/deleted team → team-scoped queries run with that id and return empty (silent wrong state) or, if code dereferences `CurrentTeam.Name`, NPE.

**Defensive rules (mitigation) `[ASSUMED]`:**
- `CurrentTeamService.ResolveAsync` must **validate the persisted id against the DB** (row exists, `is_active=1`, current user is a member via UserTeams) — mirror `CurrentUserService.ResolveAsync` doing a DB lookup, not trusting the cache.
- On invalid → fall back: pick the user's first active team; if none, return a `NeedsSelection`-style outcome (mirror `CurrentUserOutcome`, `ReadModels.cs:48-49`) and prompt, never crash.
- Re-resolve on `DataKind.Teams` broadcast (add this enum value, `DataChangedMessage.cs:4-16`) so a deactivation elsewhere refreshes the switcher.
- The DI `Func<int>` team-id seam should return `Current?.Id ?? 0` (mirror the user seam `App.xaml.cs:96-100`) — but team-scoped queries must treat **teamId 0 as "no team" → empty/guarded**, not as "match all".

---

## 4. DEFAULT backlog per team

`[VERIFIED]` Today there is exactly one DEFAULT, created by `EnsureDefaultBacklog` (`DatabaseInitializer.cs:260`) and reconciled by `DefaultTaskSyncService.SyncAsync` (runs once at startup, `App.xaml.cs:137`).

**Pitfalls:**
4.1 **`EnsureDefaultBacklogIdAsync` is team-blind (R2).** `[VERIFIED]` `DefaultTaskSyncService.cs:34-39` resolves DEFAULT via `GetByCodeAsync("DEFAULT")` and `GetActiveByBacklogAsync(defReqId)` — single backlog. Post-retrofit it will (a) `GetByCodeAsync` throws/picks-arbitrary on N DEFAULTs, (b) seed all default tasks under one team only. **Mitigation:** loop sync per active team; `EnsureDefaultBacklogId(teamId)` scoped by `(backlog_code='DEFAULT' AND team_id=@team)`.
4.2 **Duplicate DEFAULT creation.** team-scoped `EnsureDefaultBacklog` must keep the `WHERE NOT EXISTS` guard but scoped per team, else creating/switching teams spawns duplicate DEFAULTs. `[VERIFIED]` pattern at `:265-268`.
4.3 **Annual Leave / Meeting double-counting in reports.** `[CITED]` TM-04 wants per-team Annual Leave so reports separate by team. Because each team now has its own "Annual Leave" task id, the report grouping (`ReportAggregator`, in-memory by backlog/task name) is fine **only if** the source rows are team-filtered (R1/R4). If a multi-team checkbox view aggregates two teams, "Annual Leave" appears twice (once per team) — desired per TM-08 ("per-team Annual Leave separable") but the UI must label the team or users will read it as a duplicate bug. `[ASSUMED]`
4.4 **`GetActiveForTimesheetAsync` scoping.** `[VERIFIED]` `TaskRepository.cs:33-38` returns DEFAULT tasks for ALL teams once multiplied; must scope to the active team's DEFAULT (see §2).

**Must not regress:** `DefaultTaskSyncServiceTests` (rename = soft-delete old + insert new, TimeLogs preserved), the single-DEFAULT idempotency.

---

## 5. Multi-team checkbox view + the M3 month filter

`[VERIFIED]` Task List is month-scoped (`period_month` filter; DEFAULT exempt because its `period_month` is null — `STATE.md` "Entry month-filter exempts null period_month"). Gantt is native WPF Canvas, one bar per backlog (`STATE.md` P8). Reports/Board already exist.

**Pitfalls:**
5.1 **Two orthogonal filters compound.** `[CITED]` TM-07 adds a team multi-select to Backlog/TaskList/Reports/Board; TaskList already has a month selector (TL-04). The combined predicate is `period_month=@m AND team_id IN @teams`. Forgetting the team half re-introduces R1-class leaks; forgetting the month half breaks M3. **Mitigation:** thread both into one query; default team selection = active team only (TM-07 `[CITED]`), default month = current.
5.2 **Gantt axis / chips across teams.** `[ASSUMED]` aggregating multiple teams into one Canvas means more bars and per-bar team identification (TM-07 "indicate team per row/card where ambiguous"). Schedule chips (warning/late, `IScheduleStateService`) are per-backlog and team-agnostic — safe, but the working-day calc (`IWorkingDayCalculator`, holiday-aware) stays global (Holidays are global per UPCOMING-FEATURES) — no team interaction there. Good.
5.3 **Performance of aggregating many teams.** `[ASSUMED]` `IN @teams` is fine for a handful of teams (scope is a 2–5 person company, `REQUIREMENTS.md` intro). `GetLoggedHoursByBacklogAsync` is an all-time GROUP BY with no team filter today — across many teams it scans everything; scope it to the checked teams to keep it cheap. Low risk at this data scale but cheap to do right.

---

## 6. Regression surface (~316 tests, will grow toward ~328)

`[VERIFIED]` No existing test references `team_id`/`TeamId` (clean retrofit). Dominant breakage vectors:

**Will break / must update:**
- **`TestDb.cs:61-69`** — `SeedRequestAsync` inserts Backlogs with no team_id; **single biggest blast radius** (every integration test in Repo/TimeLog/TaskList/Standup/Sync uses it). If team_id is NOT NULL this fails everywhere — another reason to keep it nullable + backfill. `[VERIFIED]`
- **`DatabaseInitializerTests.cs:55-56`** — asserts `COUNT(DEFAULT)==1`; breaks with DEFAULT-per-team. `:17,35` "Seven tables" list breaks when `Teams`/`UserTeams` added. `[VERIFIED]`
- **`SchemaV7UpgradeTests.cs`** — pins target `user_version==7`; the v6→v7 assertions stay valid, but a sibling v7→v8 test is needed and the family invariant moves. `[VERIFIED]`
- **`DefaultTaskSyncServiceTests.cs`** — `EnsureDefaultBacklogIdAsync()` gains a team param; all "under DEFAULT" tests adapt. `[VERIFIED]`
- **`StandupRepositoryTests` / `StandupServiceTests` / `StandupArchiveServiceTests` / `DailyReportViewModelTests`** — StandupEntry record + insert columns gain team_id. `[VERIFIED]`
- **`TimeLogServiceTests` (grouping / GetActiveForTimesheet), `RepositoryCrudTests`, `Models/EntitiesTests` (Backlog positional record gains TeamId), `TaskListRepositoryTests`.** `[VERIFIED]`

**Must stay green, untouched (pure logic, zero team coupling — confirmed by grep):**
- `WorkingDayCalculatorTests` (holiday/working-day math — HOL-02, smart-fill, schedule).
- `SmartInputServiceTests` + `SmartInputPanelVmTests` (even-distribution math SI-01..04).
- `ReportAggregatorTests` + `ExportServiceTests` (in-memory projection) — green **unless** `TimeLogReportRow` gains a *positional* team field (then every `Row(...)` call site updates). **Recommendation:** add team as an optional/last positional or a separate field to avoid churning these.
- `HexToBrushConverterTests`, `JsonAppConfigTests`, `ScheduleStateServiceTests`.

`[CITED]` TM-10 ("no regression to team-agnostic features"): smart-fill, validation, Daily Report, Task List chips/Gantt, backup must all keep passing.

---

## 7. First-run setup (TM-09) — fresh DB with zero teams

`[VERIFIED]` Startup is defensive: `EnsureDefaultBacklog`/`SeedDefaultTasksIfEmpty` make init zero-config; `MainViewModel.ResolveCurrentUserAsync` (`:162-191`) auto-creates a user on an empty DB. **There is no equivalent "ensure an active team exists" step anywhere.** `[VERIFIED]`

**NPE/crash surfaces before setup completes:**
- Any team-scoped query running with `ActiveTeamId` unset → teamId 0 → must return empty, not crash, and not "match all".
- A `CurrentTeamName` binding in the shell must null-guard like `CurrentUserName` does (`MainViewModel.cs:83-84`).
- `DefaultTaskSyncService.SyncAsync` runs at startup (`App.xaml.cs:137`, **not** in try/catch) — if it now iterates teams and there are zero teams, it must no-op, not throw (an unhandled throw surfaces via the dispatcher dialog but blocks setup).
- Archive backfills (standup/tasklist) and reports iterate data — with zero teams they read empty sets (safe) but must not assume an active team.

**Mitigation `[ASSUMED]`:** add an `EnsureTeamExists`/first-run step (mirror `EnsureDefaultBacklog`) in the initializer or `MainViewModel.InitializeAsync`: create a default team, set it active, create its DEFAULT + seed default tasks, apply validated default settings (N-days=3, backup off, retention default — TM-09 `[CITED]`). Make every team-scoped consumer tolerate "no active team yet."

---

## 8. Testing strategy

**Unit tests (deterministic, automatable — write these):**
- **Migration v7→v8 + backfill** — new `SchemaV8UpgradeTests` (mirror `SchemaV7UpgradeTests`): seed a v7 DB with backlogs/standup/users, run init, assert `user_version==8`, team "Architect Improvement" exists once, every non-DEFAULT backlog + every StandupEntry carries that team_id, all users in UserTeams, the existing DEFAULT becomes that team's DEFAULT, columns nullable, idempotent on re-run, REQ-OLD data preserved.
- **Team-filtered queries** — per query in §2, a 2-team fixture asserting team-A fetch excludes team-B rows (export, report, timesheet grid, backlog search, standup day/range, logged-hours roll-up).
- **CurrentTeamService** — resolve from valid persisted id; stale id (deleted/inactive/non-member) → fallback/NeedsSelection, never throw; SetActiveTeam persists + re-caches + broadcasts.
- **DEFAULT-per-team** — `EnsureDefaultBacklogId(teamId)` idempotent per team; `DefaultTaskSync` seeds each team's DEFAULT independently; rename/hide semantics preserved per team.
- **First-run** — fresh DB ends with exactly one active team + working DEFAULT + seeded tasks + validated settings.

**UAT-only (mouse/visual — cannot auto-test, mirrors existing drag&drop UAT note in STATE.md):**
- Sidebar team switcher changes the working set live.
- Multi-team checkbox view: checking 2 teams aggregates rows/cards; team labels disambiguate; selection persists per screen for the session.
- Grid ↔ Gantt toggle keeps month + team selection.
- Stale-team behavior across two machines (deactivate on B, observe graceful fallback on A) — manual.

---

## Appendix — key files

- `src/TimesheetApp/Data/DatabaseInitializer.cs` (migration array, EnsureDefaultBacklog, SeedDefaultTasksIfEmpty)
- `src/TimesheetApp/Data/SqliteConnectionFactory.cs` (FK ON, journal_mode=DELETE, Pooling=False)
- `src/TimesheetApp/Data/Repositories/{TimeLog,Task,Backlog,Standup,User}Repository.cs` (query surface)
- `src/TimesheetApp/Services/DefaultTaskSyncService.cs` (DEFAULT reconciliation)
- `src/TimesheetApp/Services/CurrentUserService.cs` + `Models/ReadModels.cs:48-49` (context-service pattern + outcome enum)
- `src/TimesheetApp/Config/JsonAppConfig.cs` (app-local persistence, no validation)
- `src/TimesheetApp/Services/DataChangedMessage.cs` (DataKind broadcast; add `Teams`)
- `src/TimesheetApp/App.xaml.cs` (startup order) + `ViewModels/MainViewModel.cs` (shell, first-run user auto-create)
- `src/TimesheetApp.Tests/Data/TestDb.cs` (fixture — team_id seed), `DatabaseInitializerTests.cs`, `SchemaV7UpgradeTests.cs`
