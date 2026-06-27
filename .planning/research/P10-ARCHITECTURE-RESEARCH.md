# P10 "Multi-Team" — Architecture Research (Mode B, STEP 4)

**Phase:** P10 / M4 — Multi-Team (team scoping, membership, active team, multi-team view)
**Stack:** WPF .NET 8, MVVM (CommunityToolkit.Mvvm), SQLite + Dapper, repository pattern, bare `ServiceCollection` DI.
**Branch:** `feature/task-list-2026-06-27` (M3 Task List + P9 backup already merged — schema currently **v7**).
**Date:** 2026-06-27
**Source REQs:** TM-01..10 (`.planning/REQUIREMENTS.md`), locked decisions in `.planning/UPCOMING-FEATURES.md` (P10).
**Tagging:** every claim is `[VERIFIED]` (confirmed in code, file:line), `[CITED]` (from REQ/decision), or `[ASSUMED]` (inferred).
**Run note:** config is Mode B, `parallelization:false`, `commit_atomic:true`, `quality` profile `[VERIFIED config.json]`. **Execution PAUSES for user plan approval** (schema-wide / high-risk) `[CITED UPCOMING-FEATURES P10]`.

> This is the highest-value deliverable for P10: the **complete list of queries/methods that gain a team dimension**, the **safe data migration**, and the **wave decomposition with zero same-wave file overlap**. It does not decide product behavior — only how P10 retrofits onto the existing architecture without regressing M1/M2/M3.

---

## 0. Baseline facts (confirmed against the live branch)

- **Schema is at v7** — `const long SchemaVersion = 7` `[VERIFIED DatabaseInitializer.cs:14]`. P10 target is **v8** `[CITED TM-01]`.
- **Migration shape:** `DatabaseInitializer.InitializeAsync()` runs, in ONE transaction: `CreateTables` (idempotent `CREATE TABLE IF NOT EXISTS`) → `RunMigrations` (forward-only `migrations[]` array, step N runs when `user_version < N+1`) → `EnsureDefaultBacklog` → `SeedDefaultTasksIfEmpty` → commit `[VERIFIED DatabaseInitializer.cs:23-35,184-251]`. The `migrations[]` array currently has 8 entries (index 0..7); v7 ALTERs Backlogs at index 7 `[VERIFIED DatabaseInitializer.cs:237-244]`.
- **DEFAULT-backlog seeding** is a single hidden backlog `backlog_code='DEFAULT', project='DEFAULT'`, created idempotently AFTER migrations `[VERIFIED DatabaseInitializer.cs:260-271]`. `DefaultTasks` seeded only-when-empty `[VERIFIED DatabaseInitializer.cs:273-288]`.
- **DefaultTaskSyncService** materializes active `DefaultTasks` rows as `Tasks` under the single DEFAULT backlog (insert missing, soft-delete orphaned), backs up first (XC-10), checks lingering journal (XC-09). It resolves the DEFAULT backlog via `IBacklogRepository.GetByCodeAsync("DEFAULT")` `[VERIFIED DefaultTaskSyncService.cs:34-76]`. **Runs at startup** from `App.OnStartup` after init commits `[VERIFIED App.xaml.cs:137]`.
- **Startup order** (`App.OnStartup`): build SP → `DatabaseInitializer.InitializeAsync()` → standup archive backfill → tasklist archive backfill → auto-backup → `DefaultTaskSyncService.SyncAsync()` → resolve `MainViewModel` → `mainVm.InitializeAsync(...)` (current-user + conflict-scan + tab loads) → show window `[VERIFIED App.xaml.cs:114-152]`. **This is the TM-09 first-run + TM-04 per-team-sync hook point.**
- **Connection seam:** every repo takes `IConnectionFactory`, `using var c = _factory.Create()` per method `[VERIFIED BacklogRepository.cs:15-21]`. New repos MUST follow this (XC-01 OneDrive policy).
- **Entities** are positional records in `Models/Entities.cs`; read/projection models in `Models/ReadModels.cs` `[VERIFIED Entities.cs]`. `Backlog` already grows via nullable-defaulted ctor params (v2/v4/v7) — append-friendly `[VERIFIED Entities.cs:10-19]`.
- **No inline FK precedent:** `assignee_user_id` (v4) and `pca_contact_id` (v7) are plain INTEGER columns, **no `FOREIGN KEY` constraint**, so deactivating a referenced row never blocks (OneDrive precedent) `[VERIFIED DatabaseInitializer.cs:214,244; CITED TM-01 "no inline FK"]`. `team_id` follows this.
- **Cross-tab sync:** `DataChangedMessage(DataKind Kind)` over `WeakReferenceMessenger.Default`; consumers register with a static lambda + recipient arg `[VERIFIED DataChangedMessage.cs:1-23]`. `DataKind` enum: `Backlogs, Tasks, Users, Logs, Templates, DefaultTasks, Standup, Tags, PcaContacts, Holidays` `[VERIFIED DataChangedMessage.cs:4-16]` — P10 adds `Teams`.
- **Sidebar nav is string-keyed**: RadioButtons bind `MainViewModel.ActiveView` (string) via `StringMatch` (IsChecked) + `StringMatchToVisibility` (panel visibility). One sibling `DockPanel` per `ActiveView`; no TabControl-of-pages. Current-user chip is `DockPanel.Dock="Bottom"` in the sidebar `[VERIFIED MainWindow.xaml:38-57,60-92,144-251]`. **The TL-02 sidebar restructure already shipped** (Backlog/TaskList/Reports are top-level) `[VERIFIED MainWindow.xaml:65-91]`.
- **App-local config** (`%APPDATA%\TimesheetApp\appsettings.json`) is a JSON `Model` record with getters + `Set*` save methods on `IAppConfig`/`JsonAppConfig` — this is the **ActiveTeamId persistence template** (mirrors `DbPath`/`BackupFolderPath`) `[VERIFIED JsonAppConfig.cs:11-91; IAppConfig.cs:5-23]`.
- **Current-user infra:** `ICurrentUserService.Current` (a `User?`), resolved from `Environment.UserName → Users.windows_username`; DI exposes `Func<int>` = `currentUser.Current?.Id ?? 0` so VMs stay live after login `[VERIFIED CurrentUserService.cs:23-43; App.xaml.cs:96-100]`. **This is the `ICurrentTeamService` template.**

---

## 1. Migration v7 → v8 (DatabaseInitializer)

### 1a. Const bump + append step

```csharp
private const long SchemaVersion = 8;   // was 7  [VERIFIED DatabaseInitializer.cs:14]
```
Append **one** entry to the `migrations[]` array (becomes index 8, runs when `user_version < 9`, i.e. exactly once on a v7 DB) `[VERIFIED migration-step pattern DatabaseInitializer.cs:184-251]`:

```csharp
// v8 -> P10 Multi-Team. team_id on Backlogs + StandupEntries (no inline FK, mirroring
// assignee_user_id / pca_contact_id). Teams + UserTeams tables are created idempotently in
// CreateTables. The DATA MIGRATION (create "Architect Improvement", repoint existing rows) is
// NOT here — it runs as a post-init bootstrap (see 1d) so it can use repos + own transactions.
static (c, t) => c.Execute(
    @"ALTER TABLE Backlogs       ADD COLUMN team_id INTEGER;
      ALTER TABLE StandupEntries ADD COLUMN team_id INTEGER;", transaction: t),
```
`[CITED TM-01]`. ADD COLUMN is not idempotent → gated by `user_version`, runs once `[VERIFIED pattern at DatabaseInitializer.cs:191-195]`.

### 1b. Teams + UserTeams DDL (in `CreateTables`, idempotent)

Append to the `ddl` string in `CreateTables` (after the Holidays block) `[VERIFIED CreateTables structure DatabaseInitializer.cs:39-147]`:
```sql
CREATE TABLE IF NOT EXISTS Teams (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,
    is_active  INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS UserTeams (
    user_id INTEGER NOT NULL,
    team_id INTEGER NOT NULL,
    PRIMARY KEY (user_id, team_id)
);
```
`[CITED TM-01]`. No inline FK on UserTeams (matches `BacklogTags` which is also FK-less) `[VERIFIED DatabaseInitializer.cs:131-135]`. Idempotent CREATE-IF-NOT-EXISTS means re-running init never duplicates `[VERIFIED pattern]`.

### 1c. team_id ALTERs — covered by 1a (Backlogs + StandupEntries get a nullable `team_id INTEGER`).

`Tasks`/`TimeLogs` do **NOT** get `team_id` — they inherit team via `Tasks.backlog_id → Backlogs.team_id` `[CITED UPCOMING-FEATURES "backlog-scoped (Task/TimeLog/Standup inherit via backlog; Standup also gets team_id)"]`. StandupEntries gets its own `team_id` because a standup row may carry an ad-hoc code with `backlog_id = null` (DR-03), so it cannot rely on a backlog join `[VERIFIED StandupRepository.cs:65-68 backlog_id nullable; CITED]`.

### 1d. DATA MIGRATION — "Architect Improvement" (post-init bootstrap, NOT inside the init transaction)

**Decision: do the data migration as a post-init bootstrap service, not inside `RunMigrations`.** Reasons `[VERIFIED + ASSUMED]`:
- The init transaction uses raw `IDbConnection`/`IDbTransaction`; the data migration wants the same repo helpers (date/REAL boundary mapping) that `DefaultTaskSyncService` already uses, and it must run **before** `DefaultTaskSyncService.SyncAsync()` so per-team DEFAULT backlogs exist `[VERIFIED App.xaml.cs:137 ordering]`.
- `DefaultTaskSyncService` already demonstrates the "bulk write outside the initializer transaction, own connection" pattern `[VERIFIED DefaultTaskSyncService.cs:41-76; App.xaml.cs:132-137 comment "must not run inside the initializer's open transaction"]`.

**New `ITeamBootstrapService.EnsureBootstrappedAsync()`** (runs in `App.OnStartup` right after `InitializeAsync()`, before the archive backfills + DefaultTaskSync — see §6). Logic, idempotent:

1. If `Teams` has ≥1 row → **already bootstrapped, return** (covers both first-run-done and migrated DBs). `[ASSUMED guard mirrors SeedDefaultTasksIfEmpty only-when-empty gate VERIFIED DatabaseInitializer.cs:277-278]`
2. Else, decide **migration vs first-run** by whether business data exists:
   - **Migration path** (existing v7 data: any `Backlogs` other than DEFAULT, or any `StandupEntries`, or any `Users`): create team **"Architect Improvement"** (name fixed per user) `[CITED TM-02]`; capture its id `T`.
     - `UPDATE Backlogs SET team_id = T WHERE team_id IS NULL` (assigns every existing backlog — including the existing single DEFAULT — to `T`) `[CITED TM-02 "the existing single DEFAULT backlog becomes that team's DEFAULT"]`.
     - `UPDATE StandupEntries SET team_id = T WHERE team_id IS NULL` `[CITED TM-02]`.
     - `INSERT INTO UserTeams(user_id, team_id) SELECT id, T FROM Users` (every existing user becomes a member) `[CITED TM-02]`.
     - Set `T` as the **active team** in app-local config (see §3) `[CITED TM-02 "the team is the active team"]`.
   - **First-run path** (fresh DB, no business data): create a default team (default name e.g. "My Team", user-renamable), set active, then let DefaultTaskSync seed its DEFAULT `[CITED TM-09]`. (Note: the existing fresh-DB user auto-create lives in `MainViewModel.ResolveCurrentUserAsync` AFTER bootstrap; that new user must also be added to the active team — see §3/§6 ordering note.)
3. Either way: ensure the active team has a **DEFAULT backlog** (its `backlog_code='DEFAULT'`, `team_id=T`). On the migration path the existing DEFAULT is repointed in step 2; on first-run it is created here or by the per-team DefaultTaskSync (see §4).

**Safety:** wrap each UPDATE/INSERT group in its own transaction via the connection factory (mirrors `BacklogRepository.SetTagsAsync` `using var tx` `[VERIFIED BacklogRepository.cs:182-192]`). The "Teams empty?" guard makes the whole thing idempotent — re-running on a bootstrapped DB is a no-op. XC-10 backup before this bulk write (call `IDbBackupHelper.BackupAsync()` first, as DefaultTaskSync does `[VERIFIED DefaultTaskSyncService.cs:43]`).

> **Migration tension to flag for spec:** the existing single DEFAULT backlog becomes the *Architect Improvement* team's DEFAULT. After that, every OTHER team needs its OWN DEFAULT backlog (TM-04). Today `EnsureDefaultBacklog` and `DefaultTaskSyncService` assume **exactly one** DEFAULT (`GetByCodeAsync("DEFAULT")` returns a single row) `[VERIFIED DatabaseInitializer.cs:265-268; DefaultTaskSyncService.cs:36]`. P10 must make "DEFAULT" **unique per team**, not globally — see §4.

---

## 2. Every repository / query that needs a team dimension or filter

> Convention proposed: read/list methods that today take no team arg gain an optional `IReadOnlyList<int>? teamIds = null` (null = no filter = all teams, preserves current behavior and existing tests); write/create methods that establish team ownership take a required `int teamId`. Single-active-team callers pass `[activeTeamId]`; multi-team checkbox screens pass the checked set. `[ASSUMED API convention; minimizes regression per TM-10]`

### 2a. `ITeamRepository` (NEW) — TM-03, mirrors `IUserRepository` soft-delete
```csharp
public interface ITeamRepository
{
    Task<IReadOnlyList<Team>> GetAllAsync();                 // Settings list (incl. inactive)
    Task<IReadOnlyList<Team>> GetActiveAsync();              // switcher + filter source
    Task<Team?> GetByIdAsync(int id);
    Task<Team?> GetByNameAsync(string name);                 // bootstrap idempotency ("Architect Improvement")
    Task<int> InsertAsync(Team team);                        // returns new id
    Task UpdateNameAsync(int id, string name);              // rename
    Task SetActiveAsync(int id, bool isActive);             // deactivate (soft, like Users)
    // membership (UserTeams)
    Task<IReadOnlyList<int>> GetTeamIdsForUserAsync(int userId);   // the user's teams (switcher/filter)
    Task<IReadOnlyList<int>> GetUserIdsForTeamAsync(int teamId);   // Settings membership editor
    Task SetMembersAsync(int teamId, IReadOnlyList<int> userIds);  // replace-all in one tx (like SetTagsAsync)
    Task AddMemberAsync(int userId, int teamId);                   // first-run user join
}
```
`[CITED TM-03 CRUD+membership; VERIFIED soft-delete mirrors IUserRepository.cs:8-18, replace-all mirrors BacklogRepository.SetTagsAsync VERIFIED:179-193]`. Register singleton `[VERIFIED App.xaml.cs:61-67 pattern]`.

### 2b. `IBacklogRepository` — team filter + team ownership `[VERIFIED file BacklogRepository.cs]`
- `SearchAsync(string? term)` → **`SearchAsync(string? term, IReadOnlyList<int>? teamIds = null)`**. Add `AND (@noTeam OR team_id IN @teamIds)` to both branches. This is the single most-called read — `BacklogsViewModel.RefreshAsync`, `TaskListViewModel.LoadAsync`, `TimeLogService.BuildGroupsAsync`, `DailyReportViewModel` picker, `TaskListArchiveService`, `StandupService.SearchBacklogsAsync` all funnel through it `[VERIFIED BacklogRepository.cs:19-36; callers below]`.
- `InsertAsync(Backlog backlog)` → grows to write `team_id` (the `Backlog` record gains `TeamId`; add `team_id` to the `Cols` const + INSERT/UPDATE column lists + `BacklogRaw` + `MapBacklog`) `[VERIFIED Cols at BacklogRepository.cs:12-13; INSERT:59-81; MapBacklog:231-242; BacklogRaw:249-268]`.
- `GetByCodeAsync("DEFAULT")` → **must become team-scoped**: `GetDefaultForTeamAsync(int teamId)` (find `backlog_code='DEFAULT' AND team_id=@t`) — see §4. The existing global `GetByCodeAsync` stays for non-DEFAULT lookups `[VERIFIED BacklogRepository.cs:46-53]`.
- `GetTagIdsForAllAsync()` — no team arg needed (keyed by backlog_id; callers already filter backlogs by team first) `[VERIFIED BacklogRepository.cs:195-203]`.

### 2c. `ITimeLogRepository` — team-aware roll-up + report/export `[VERIFIED file TimeLogRepository.cs]`
- `GetReportRowsAsync(userId, from, to)` → add optional `IReadOnlyList<int>? teamIds = null`. The query already joins `Backlogs r` `[VERIFIED TimeLogRepository.cs:53-56]`, so add `AND (@noTeam OR r.team_id IN @teamIds)`. Also **project `r.team_id` (+ team name via a join to Teams)** into `TimeLogReportRow` for TM-08 grouping `[CITED TM-08 "identify team in output"]`.
- `GetExportRowsAsync(from, to, projectFilter)` → add `IReadOnlyList<int>? teamIds = null` + same team predicate + project team `[VERIFIED TimeLogRepository.cs:63-81]`. Used by Reports team view, ExportService, and `TimeLogService.GetWeekGroupedAllUsersAsync` (team aggregate) `[VERIFIED callers]`.
- `GetLoggedHoursByBacklogAsync()` → **no team arg needed** (returns a dict keyed by backlog_id; the Task List/archive callers already restrict which backlogs they render by team) `[VERIFIED TimeLogRepository.cs:105-116; consumed at TaskListViewModel.cs:99,117]`. Leaving it global avoids a join and is correct because lookups are by id.
- `GetUserIdsWithLogsInRangeAsync(from, to)` → for RPT-04 "chưa log" banner; **flag for spec** whether the banner is per-active-team or global. If team-scoped, add a `team_id IN` predicate via the Tasks→Backlogs join (currently it doesn't join Backlogs) `[VERIFIED TimeLogRepository.cs:83-90; ASSUMED banner scope — recommend active-team-scoped to match TM-06 working scope]`.
- `GetByUserAndRangeAsync` / `UpsertAsync` / `DeleteAsync` / `UpsertBatchAsync` — **no team arg**: a TimeLog points at a `task_id`, and the task's backlog already carries the team. Team scoping happens by which **tasks/rows are shown** in the grid (which `GetActiveForTimesheetAsync` returns), not on the log write `[VERIFIED TimeLogRepository.cs:16-103; CITED backlog-scoped inheritance]`.

### 2d. `ITaskRepository` — the **critical** working-scope change (TM-06) `[VERIFIED file TaskRepository.cs]`
- `GetActiveForTimesheetAsync()` → **`GetActiveForTimesheetAsync(int teamId)`** (or `IReadOnlyList<int> teamIds`). Today it returns active tasks across **all** backlogs incl. the single DEFAULT `[VERIFIED TaskRepository.cs:27-40]`. It must filter `WHERE t.is_active=1 AND b.team_id = @teamId` so the Log Work grid shows only the **active team's** tasks (incl. that team's DEFAULT) `[CITED TM-06 "Timesheet grid shows only the active team's tasks (incl. its DEFAULT)"]`. **This is the single highest-impact method change in P10.** Callers: `TimeLogService.GetWeekAsync` + `TimeLogService.BuildGroupsAsync` `[VERIFIED TimeLogService.cs:149,198]`.
- `GetActiveByBacklogAsync(backlogId)` — no team arg (backlog already determines team) `[VERIFIED TaskRepository.cs:15-25]`.

### 2e. `IStandupRepository` — team scope on entries (TM-06/07) `[VERIFIED file StandupRepository.cs]`
- Add `team_id` to `EntryCols` + INSERT/UPDATE + `EntryRaw` + `MapEntry` (the `StandupEntry` record gains `TeamId`) `[VERIFIED EntryCols:11-12; InsertEntryAsync:61-84; EntryRaw:186-200; MapEntry:176-179]`.
- `GetEntriesAsync(userId, workDate)` (Input tab — own standup) → add optional `int? teamId = null`; when set, `AND team_id=@t`. The Input tab is **active-team only** (TM-06) so it passes the active team `[CITED TM-06; VERIFIED StandupRepository.cs:20-29]`.
- `GetEntriesForDayAsync(workDate)` (Board) → **`GetEntriesForDayAsync(workDate, IReadOnlyList<int>? teamIds = null)`** — the Board is a multi-team checkbox view (TM-07) `[CITED TM-07; VERIFIED StandupRepository.cs:31-40]`.
- `GetEntriesForRangeAsync(from, to)` (weekly archive) → add `IReadOnlyList<int>? teamIds = null` for team-aware archives (TM-08) `[VERIFIED StandupRepository.cs:42-51]`.

### 2f. `IStandupService` (orchestration) — TM-06 ownership on create `[VERIFIED file StandupService.cs]`
- `AddEntryAsync(workDate, draft)` → the new `StandupEntry` must carry the **active team's id**. The service needs `ICurrentTeamService` injected; `entry = new StandupEntry(... teamId: _currentTeam.ActiveTeamId ...)` `[VERIFIED StandupService.cs:69-82; CITED TM-06 "a new standup entry carries the active team"]`.
- `GetMyStandupAsync(workDate)` → pass active team to `GetEntriesAsync` `[VERIFIED StandupService.cs:35-44]`.
- `GetTeamStandupAsync(workDate)` → **becomes the multi-team board feed**: accept `IReadOnlyList<int> teamIds`, pass to `GetEntriesForDayAsync`. Also the "active users" loop should be the **members of the checked teams** (via `ITeamRepository.GetUserIdsForTeamAsync`), not all active users `[VERIFIED StandupService.cs:46-62; CITED TM-07 board shows checked teams' members]`.
- `SearchBacklogsAsync(term)` → pass team filter for the Input picker (active team) `[VERIFIED StandupService.cs:64]`.

### 2g. `ExportService` (EXP-01..04) — TM-08 `[VERIFIED file ExportService.cs]`
- `ExportMarkdownAsync(filter)` / `ExportExcelAsync(filter)` → `ExportFilter` gains a team dimension (`IReadOnlyList<int>? TeamIds`); `LoadAsync` passes it to `GetExportRowsAsync` `[VERIFIED ExportService.cs:108-116]`. Add a **team grouping** to the markdown (a `## {team}` level above `## {user}`, or a team column in Excel) `[CITED TM-08 "exports include a team grouping/column"; ASSUMED markdown header level — flag for spec]`.

### 2h. `ReportAggregator` / `TimeLogService` `[VERIFIED TimeLogService.cs]`
- `TimeLogService.GetWeekAsync` + `BuildGroupsAsync` + `GetWeekGroupedAsync` + `GetWeekGroupedAllUsersAsync` → all call `_tasks.GetActiveForTimesheetAsync()` and `_requests.SearchAsync(null)`; both must pass the **active team** so the Log Work grid is active-team-scoped (TM-06) `[VERIFIED TimeLogService.cs:149,197-198]`. Inject `ICurrentTeamService` (or pass `teamId` from the VM). `GetWeekGroupedAllUsersAsync` uses `GetExportRowsAsync` → pass active team `[VERIFIED TimeLogService.cs:184]`.
- `GetUsersMissingLogsAsync(n)` (RPT-04) → if banner is team-scoped, pass active team to `GetUserIdsWithLogsInRangeAsync` + restrict `active` users to team members `[VERIFIED TimeLogService.cs:223-230; ASSUMED — flag for spec]`.
- `ReportAggregator` (the projection partial) is pure roll-up over rows already filtered by the repo — **no team arg needed**; if TM-08 wants a team grouping level in the drill-down tree, add a `Team` node above `Project` (the rows now carry team) `[CITED RPT-03 drill-down; ASSUMED extra tree level — flag for spec]`.

### 2i. `TaskListArchiveService` (TL-09) — TM-08 `[VERIFIED file TaskListArchiveService.cs]`
- `ExportMonthAsync` / `BackfillMissingMonthsAsync` call `_backlogs.SearchAsync(null)` `[VERIFIED TaskListArchiveService.cs:53,103]`. For team-aware archives, either pass team filter or group rows by team in the markdown table (add a Team column to the `| Code | Project | ... |` header) `[CITED TM-08; ASSUMED column vs per-team file — flag for spec]`.

### 2j. ViewModel queries summary (which VM passes what)
| VM | Method touched | Team value passed |
|---|---|---|
| `TimesheetViewModel` (Log Work) | via `ITimeLogService.GetWeekGrouped*` | **active team** (TM-06) `[VERIFIED TimesheetViewModel.cs:223-225]` |
| `BacklogsViewModel` (list) | `_backlogs.SearchAsync(null)` | **multi-team checkbox** (TM-07) `[VERIFIED BacklogsViewModel.cs:76]` |
| `BacklogsViewModel` (create) | `_backlogs.InsertAsync(...)` | **active team** on the new backlog (TM-06) `[VERIFIED BacklogsViewModel.cs:172-177]` |
| `TaskListViewModel` | `_backlogs.SearchAsync(null)` + `GetLoggedHoursByBacklogAsync` | **multi-team checkbox** (TM-07); filter the `allBacklogs` foreach by checked teams `[VERIFIED TaskListViewModel.cs:98-113]` |
| `ReportsViewModel` | `GetExportRowsAsync` / `GetReportRowsAsync` | **multi-team checkbox** (TM-07/08) `[VERIFIED ReportsViewModel.cs:150-158]` |
| `DailyReportViewModel` (Input) | `GetMyStandupAsync` | **active team** (TM-06) `[VERIFIED DailyReportViewModel.cs:61]` |
| `DailyReportViewModel` (Board) | `GetTeamStandupAsync` | **multi-team checkbox** (TM-07) `[VERIFIED DailyReportViewModel.cs:66]` |

---

## 3. Current-team context — `ICurrentTeamService`

Mirror `ICurrentUserService` exactly (it's the proven seam) `[VERIFIED CurrentUserService.cs]`.

```csharp
public interface ICurrentTeamService
{
    int ActiveTeamId { get; }                       // 0 until resolved
    Team? ActiveTeam { get; }
    IReadOnlyList<Team> AvailableTeams { get; }      // the current user's active teams (switcher source)
    event EventHandler? ActiveTeamChanged;           // VMs reload working scope on change
    Task InitializeAsync();                          // load AvailableTeams for the current user; resolve active
    Task SetActiveTeamAsync(int teamId);             // persist + raise ActiveTeamChanged
}
```

- **Resolution / available teams:** `AvailableTeams = ITeamRepository.GetTeamIdsForUserAsync(currentUser.Id)` → `GetActiveAsync` intersect (the user's *active* memberships) `[CITED TM-05 "lists the current user's active teams"; VERIFIED membership via UserTeams §2a]`.
- **Active-team resolution order** (in `InitializeAsync`): persisted id from app-local config **if it is still one of `AvailableTeams`**, else the first available team (which, post-migration, is "Architect Improvement") `[CITED TM-05 "defaults to the migrated/first team"]`.
- **Persistence:** app-local, per machine/user, in `appsettings.json` — add `ActiveTeamId` to `JsonAppConfig.Model` + `IAppConfig.ActiveTeamId`/`SetActiveTeamId(int)`, exactly mirroring `BackupKeepCount` `[CITED TM-05 "persists app-locally"; VERIFIED JsonAppConfig.cs:15-16,41,74-78; IAppConfig.cs:21-22]`. **Why app-local not the shared DB:** the active team is a per-user/per-machine UI preference, like DbPath/collapse-all — putting it in the shared OneDrive DB would make two users fight over one active team `[ASSUMED, consistent with DATA-07 locality split]`.
- **Broadcast:** on `SetActiveTeamAsync`, persist then raise `ActiveTeamChanged` **and** send `DataChangedMessage(DataKind.Teams)`. VMs whose working scope is the active team (TimesheetViewModel, Backlogs create form, DailyReport Input) subscribe and reload `[ASSUMED dual signal; VERIFIED messenger pattern DataChangedMessage.cs; CITED TM-05 "changing it reloads team-scoped views"]`. Using both lets the sidebar switcher VM hold a strong ref via the event while leaf VMs use the weak messenger.
- **Composition with `ICurrentUserService`:** `ICurrentTeamService.InitializeAsync` must run **after** the current user is resolved (it needs `currentUser.Id` for `GetTeamIdsForUserAsync`). The current user is resolved inside `MainViewModel.InitializeAsync` `[VERIFIED MainViewModel.cs:130-191]`, so current-team init is best invoked there (right after `ResolveCurrentUserAsync`) — see §6 ordering. Like the DI `Func<int>` for user id, expose a DI **`Func<int>` for active team id** so leaf services (StandupService, TimeLogService) can take a captured-live team id without re-resolving `[ASSUMED, mirrors App.xaml.cs:96-100]`.
- **DI:** `sc.AddSingleton<ICurrentTeamService, CurrentTeamService>();` + the `Func<int>` provider `[VERIFIED registration site App.xaml.cs:71]`.

---

## 4. DEFAULT backlog per team + DefaultTasks sync per team (TM-04)

Today there is **exactly one** DEFAULT backlog and one sync pass over it `[VERIFIED DatabaseInitializer.cs:260-271; DefaultTaskSyncService.cs:34-76]`. P10 makes DEFAULT **per team**.

### 4a. Schema/seed change
- `EnsureDefaultBacklog` in `DatabaseInitializer` currently creates a single team-less DEFAULT `[VERIFIED DatabaseInitializer.cs:260-271]`. After v8, "DEFAULT" is no longer globally unique — it is unique **per `team_id`**. **Recommendation:** remove per-team DEFAULT creation from the initializer (it has no team context at init time) and move it into the bootstrap (§1d) + `DefaultTaskSyncService` (which gains team awareness). Keep `EnsureDefaultBacklog` only as the legacy single-DEFAULT for the migration repoint, OR drop it once the bootstrap handles repointing `[ASSUMED — flag for spec; the cleanest is: initializer no longer seeds DEFAULT, bootstrap+sync own it per team]`.

### 4b. `IDefaultTaskSyncService` — per team
```csharp
Task<int> EnsureDefaultBacklogIdAsync(int teamId);   // find/create backlog_code='DEFAULT' for THIS team
Task SyncAsync();                                      // loops over ALL active teams, syncing each team's DEFAULT
```
`[VERIFIED current signatures IDefaultTaskSyncService.cs:5-9]`. Inside `SyncAsync`: `foreach team in ITeamRepository.GetActiveAsync()` → `EnsureDefaultBacklogIdAsync(team.Id)` → reconcile active `DefaultTasks` into Tasks under **that** team's DEFAULT (the existing insert-missing / soft-delete-orphan logic, now per team) `[CITED TM-04 "DefaultTaskSync seeds/updates default tasks under each team's DEFAULT"; VERIFIED reconcile logic DefaultTaskSyncService.cs:45-69]`. `DefaultTasks` themselves stay **global** (shared list); they materialize once per team `[CITED UPCOMING-FEATURES "DefaultTasks = global … DefaultTaskSync materializes per team"]`.
- `EnsureDefaultBacklogIdAsync(teamId)` uses a new `IBacklogRepository.GetDefaultForTeamAsync(teamId)` (find `backlog_code='DEFAULT' AND team_id=@t`); if absent, `InsertAsync(new Backlog(0,"DEFAULT","DEFAULT",now, TeamId: teamId))` `[VERIFIED current GetByCodeAsync usage DefaultTaskSyncService.cs:36-38]`.
- **Creating a new team** (TM-03) must trigger creation of its DEFAULT + a sync pass for it (idempotent) `[CITED TM-04 "Creating a team creates its DEFAULT backlog (idempotent)"]`. The Settings team-create command calls `EnsureDefaultBacklogIdAsync(newTeamId)` then `SyncAsync()` (or a per-team sync) `[ASSUMED wiring]`.

### 4c. Impact on `GetActiveForTimesheetAsync` (already covered §2d)
Annual Leave/Meeting hours log under the **active team's** DEFAULT because the grid only shows that team's DEFAULT tasks `[CITED TM-04 "Annual Leave/Meeting hours log under the active team's DEFAULT and attribute to that team in reports"; VERIFIED grid source TimeLogService.cs:149,198]`. Reports attribute correctly because each DEFAULT backlog now carries `team_id` (§2c projects it).

> **Edge to flag:** a TimeLog already attached to the old global DEFAULT's tasks (pre-migration) is now attributed to Architect Improvement (its backlog was repointed in §1d). Correct per TM-02.

---

## 5. UI wiring

### 5a. Active-team switcher (sidebar) — TM-05
The sidebar has the **current-user chip** docked Bottom `[VERIFIED MainWindow.xaml:38-57]`. Add the team switcher **directly above it** (or just below the WORKSPACE nav) as a `DockPanel.Dock="Bottom"` `ComboBox` styled like the chip. Bind `ItemsSource` to `MainViewModel`-exposed `AvailableTeams` and `SelectedItem` to a `MainViewModel.ActiveTeam` property whose setter calls `ICurrentTeamService.SetActiveTeamAsync` `[ASSUMED placement near chip; CITED TM-05 "sidebar switcher"; VERIFIED chip location]`. The nav itself is string-keyed and untouched — the switcher is a separate control, not a nav item `[VERIFIED MainWindow.xaml:60-92]`. On change, `ActiveTeamChanged`/`DataKind.Teams` reloads the working-scope views (§3).
- `MainViewModel` gains `ICurrentTeamService` injection + `ObservableCollection<Team> AvailableTeams` + `[ObservableProperty] Team? ActiveTeam` `[VERIFIED MainViewModel ctor shape:29-70]`.

### 5b. Multi-team checkbox filter component (reused on 4 screens) — TM-07
A reusable multi-select-of-teams. Two viable shapes `[ASSUMED]`:
- A small **shared sub-VM** `TeamFilterViewModel` holding `ObservableCollection<TeamCheckVm>` (each `Team` + `bool IsChecked`), seeded from `ICurrentTeamService.AvailableTeams`, default = **active team only** (TM-07), with `CheckedTeamIds` exposed + a `SelectionChanged` event. Each of `BacklogsViewModel`, `TaskListViewModel`, `ReportsViewModel`, `DailyReportViewModel` owns one and reloads on change.
- A shared `UserControl` (`Views/Controls/TeamFilter.xaml`) bound to that sub-VM, dropped into each tab's toolbar.
`[CITED TM-07 "multi-select checkbox … default selection = the active team; selection persists per screen for the session"]`. Per-screen session persistence = the sub-VM instance lives as long as its parent VM (transient VMs are recreated per shell, fine) `[ASSUMED]`.
- **Screens & insertion points:** Backlog toolbar (next to the existing project/type/assignee filters `[VERIFIED BacklogsViewModel.cs:52-61]`), Task List toolbar (next to the month combos `[VERIFIED TaskListViewModel.cs:70-73]`), Reports toolbar (next to the target/project filters `[VERIFIED ReportsViewModel.cs:69-80]`), Daily Board (next to the date toolbar `[VERIFIED MainWindow.xaml:207-217]`).

### 5c. New `DataKind.Teams`
Add to the enum `[VERIFIED DataChangedMessage.cs:4-16]`:
```csharp
Teams,  // P10: teams / membership / active-team changed (affects switcher, filters, working scope)
```
Producers: team CRUD in Settings, `SetActiveTeamAsync`. Consumers: `MainViewModel` (rebuild switcher), the 4 filter sub-VMs (rebuild checkbox list), TimesheetViewModel (working scope) `[CITED TM-03 "broadcasts a DataKind.Teams change"; TM-05]`.

### 5d. Settings "Teams" section (CRUD + membership) — TM-03
Mirror the **Users** + **PCA contacts** patterns in `SettingsViewModel` (soft-delete CRUD) and the **template/tag editor overlay** for membership editing `[VERIFIED SettingsViewModel.cs:25-50,67-79; PcaContact row pattern:70]`. Add to `SettingsViewModel`:
- `ITeamRepository` + `IUserRepository` deps; `ObservableCollection<TeamRowVm> Teams` (editable name like `PcaContactRowVm`), `NewTeamName`, `[RelayCommand]` Add/Rename/Deactivate.
- Membership: a `TeamMembershipEditorViewModel` overlay (checkbox list of users, replace-all save via `SetMembersAsync`) — mirrors the tag multi-select in `BacklogEditorViewModel` `[VERIFIED tag multi-select usage BacklogsViewModel.cs:139-156; CITED TM-03 "assign/unassign users (many-to-many)"]`.
- Each command broadcasts `DataChangedMessage(DataKind.Teams)` and (on create) triggers per-team DEFAULT + sync (§4b) `[CITED TM-03/TM-04]`.
- **View:** append a `Teams` section to `SettingsTab.xaml` mirroring the PCA/Tags sections + an overlay `Border` (NullToCollapsed) for the membership editor `[VERIFIED Settings overlay pattern referenced in P8 research §4c; VERIFIED SettingsTab.xaml exists]`.

---

## 6. DI + startup wiring

### 6a. DI registrations (`App.xaml.cs`)
```csharp
// repos (singletons, next to line 67)
sc.AddSingleton<ITeamRepository, TeamRepository>();
// services (singletons, next to line 71)
sc.AddSingleton<ICurrentTeamService, CurrentTeamService>();
sc.AddSingleton<ITeamBootstrapService, TeamBootstrapService>();
// active-team-id provider (mirrors the Func<int> user-id provider at lines 96-100)
sc.AddSingleton<Func<int>>(...);   // NOTE: collides with the existing Func<int> for user id — see below
```
`[VERIFIED registration sites App.xaml.cs:60-100]`.
> **DI collision to flag:** there is already `sc.AddSingleton<Func<int>>` for the **current-user id** `[VERIFIED App.xaml.cs:96-100]`. Registering a second `Func<int>` for **active-team id** overwrites it (last-wins) and breaks user-id resolution. **Fix:** do NOT use a bare `Func<int>` for team; instead inject `ICurrentTeamService` directly into the services that need the team id (TimeLogService, StandupService, DefaultTaskSyncService), or introduce a named delegate type (`delegate int ActiveTeamIdProvider();`). **Recommend injecting `ICurrentTeamService`** — simpler, no delegate-type collision `[VERIFIED collision risk; ASSUMED resolution]`.

### 6b. Startup ordering (`App.OnStartup`) — TM-09 first-run hook
Insert **after** `InitializeAsync()` (line 115) and **before** the archive backfills + DefaultTaskSync `[VERIFIED App.xaml.cs:114-137]`:
```
1. DatabaseInitializer.InitializeAsync()           // v8 schema + team_id columns + Teams/UserTeams tables  [VERIFIED:115]
2. TeamBootstrapService.EnsureBootstrappedAsync()  // NEW: create "Architect Improvement"/first-run team, repoint rows, set active  (§1d)
3. standup archive backfill                         // [VERIFIED:119] (now team-aware if §2e shipped)
4. tasklist archive backfill                        // [VERIFIED:124]
5. auto-backup                                       // [VERIFIED:129]
6. DefaultTaskSyncService.SyncAsync()               // [VERIFIED:137] now loops per active team (§4b) — needs teams to exist (step 2 first)
7. MainViewModel.InitializeAsync(...)               // [VERIFIED:143] resolves current user; then call ICurrentTeamService.InitializeAsync()
```
- **Critical:** bootstrap (step 2) MUST precede DefaultTaskSync (step 6) because per-team sync iterates `ITeamRepository.GetActiveAsync()` `[CITED §4b; VERIFIED ordering App.xaml.cs:137]`.
- **Current-team init** must follow current-user resolution. Cleanest: call `ICurrentTeamService.InitializeAsync()` inside `MainViewModel.InitializeAsync` right after `ResolveCurrentUserAsync` `[VERIFIED MainViewModel.cs:130-135]`.
- **First-run user↔team join:** the fresh-DB auto-create of a user happens in `ResolveCurrentUserAsync` `[VERIFIED MainViewModel.cs:173-183]`, which is AFTER bootstrap created the first team. So after auto-creating the user, also `ITeamRepository.AddMemberAsync(newUserId, activeTeamId)` so the new user is a member of the first-run team `[CITED TM-09; VERIFIED MainViewModel.cs:179-181]`. **Flag this ordering dependency for the plan.**
- **TM-09 validated default settings:** the bootstrap/first-run also ensures sane setting defaults (N-days=3, auto-backup off, retention=30). These defaults already exist (`ReportsViewModel.DefaultNDays=3` `[VERIFIED ReportsViewModel.cs:14]`, `JsonAppConfig.DefaultBackupKeepCount=30` `[VERIFIED JsonAppConfig.cs:19]`, auto-backup defaults false `[VERIFIED JsonAppConfig.cs:40]`) — TM-09 is mostly **already satisfied**; the new work is just the team creation + ensuring no null/invalid state `[CITED TM-09; VERIFIED existing defaults]`.

---

## 7. Wave decomposition (zero same-wave file overlap)

`commit_atomic:true`, `parallelization:false` `[VERIFIED config.json:3-6]` → sequential dispatch, commit per task. Same-wave plans must touch **disjoint files** (STEP 6 rule). **Execution PAUSES for user plan approval before W1.**

**File-overlap hotspots (force serialization):** `DatabaseInitializer.cs` (W1 only), `Models/Entities.cs` + `ReadModels.cs` (W1 appends; later waves read), `App.xaml.cs` (DI + startup — batch in the last wave), `BacklogRepository.cs`/`TimeLogRepository.cs`/`TaskRepository.cs`/`StandupRepository.cs` (W1 owns the repo team-dimension edits), `TimeLogService.cs` + `StandupService.cs` (W2/W3), `SettingsViewModel.cs`+`SettingsTab.xaml` (Settings wave owns), `MainWindow.xaml`+`MainViewModel.cs` (switcher wave owns), each view-screen VM (one wave each).

| Wave | Sub-phase | Owned files | REQs | Dep |
|---|---|---|---|---|
| **W1** | **Data layer + schema**: v8 migration (const bump + step + Teams/UserTeams DDL + team_id ALTERs), `Team` entity + `TeamId` on `Backlog`/`StandupEntry` records, `ReadModels` (team name on report row), `ITeamRepository`+impl, team params on `IBacklogRepository`/`ITimeLogRepository`/`ITaskRepository`/`IStandupRepository`+impls | `DatabaseInitializer.cs`, `Entities.cs`, `ReadModels.cs`, `TeamRepository.cs`+`I…`, `BacklogRepository.cs`+`I…`, `TimeLogRepository.cs`+`I…`, `TaskRepository.cs`+`I…`, `StandupRepository.cs`+`I…` | TM-01, TM-02(schema), TM-06(query), TM-07(query), TM-08(query) | — |
| **W2** | **Current-team service + data migration bootstrap + config**: `ICurrentTeamService`+impl, `ITeamBootstrapService`+impl (Architect Improvement migration + first-run), `IAppConfig.ActiveTeamId` | `CurrentTeamService.cs`+`I…`, `TeamBootstrapService.cs`+`I…`, `IAppConfig.cs`, `JsonAppConfig.cs` | TM-02(data), TM-05(persist), TM-09(bootstrap) | W1 |
| **W3** | **DEFAULT-per-team + per-team sync**: `IDefaultTaskSyncService` per-team loop, `GetDefaultForTeamAsync`, working-scope wiring in `TimeLogService` | `DefaultTaskSyncService.cs`+`I…`, `TimeLogService.cs`, (+ `IBacklogRepository.GetDefaultForTeamAsync` if not in W1) | TM-04, TM-06(working scope) | W1, W2 |
| **W4** | **Standup team scope**: active-team on create, team-filtered board feed | `StandupService.cs`+`I…` | TM-06, TM-07(standup) | W1, W2 |
| **W5** | **Settings Teams UI**: team CRUD + membership editor + DataKind.Teams broadcast | `SettingsViewModel.cs`, `SettingsTab.xaml`(+`.cs`), `TeamMembershipEditorViewModel.cs`, `DataChangedMessage.cs` (add `Teams`) | TM-03 | W1, W2 |
| **W6** | **Active-team switcher + working scope**: sidebar switcher + `MainViewModel` active-team props + reload-on-change | `MainWindow.xaml`, `MainViewModel.cs` | TM-05, TM-06 | W2, W5 |
| **W7** | **Multi-team checkbox filter on the 4 screens**: shared `TeamFilterViewModel`/control wired into Backlog, Task List, Reports, Daily Board | `TeamFilterViewModel.cs`, `Views/Controls/TeamFilter.xaml`, `BacklogsViewModel.cs`, `TaskListViewModel.cs`, `ReportsViewModel.cs`, `DailyReportViewModel.cs`, the 4 tab XAMLs | TM-07 | W1, W2, W6 |
| **W8** | **Reports/export team-aware**: team grouping/column + filter | `ExportService.cs`, `TaskListArchiveService.cs`, `StandupArchiveService.cs`, `ReportsViewModel.cs`(if not done W7) | TM-08 | W1, W3 |
| **W9** | **DI + startup wiring + first-run join**: register repos/services, bootstrap+current-team init order, Func<int> collision fix, first-run user→team join | `App.xaml.cs`, `MainViewModel.cs`(first-run join — overlaps W6) | TM-09, TM-10(wire-up) | all |

**Notes for STEP 6:**
- **W6 and W9 both touch `MainViewModel.cs`** (switcher props vs first-run join) and **W7 and W8 both touch `ReportsViewModel.cs`** → those pairs MUST be different waves (already are) or the overlapping edits merged into one wave. Flag explicitly: **`MainViewModel.cs` is touched by W6 and W9; `ReportsViewModel.cs` by W7 and W8; `App.xaml.cs` only by W9.** `[CITED STEP 6 zero-overlap rule]`
- **W1 is large** (one migration + 5 repos). If granularity demands, split into W1a (migration + entities + ITeamRepository) and W1b (team params on the 4 existing repos) — but those share no files with each other except none, so they can be sequential sub-waves `[ASSUMED]`.
- `DatabaseInitializer.cs`, `App.xaml.cs` are single-owner files (W1, W9) — never share a wave `[VERIFIED single DI/init root]`.
- Because `parallelization:false`, all waves run sequentially regardless; the disjoint-file rule mainly guards the commit-per-task bisectability `[VERIFIED config.json:3]`.

---

## 8. Open items for STEP 5 (spec), not blocking architecture
1. **RPT-04 "chưa log" banner scope** — active-team vs global (§2c/§2h). Recommend active-team to match TM-06.
2. **Export/markdown team representation** — header level (`## {team}`) vs column vs per-team file, for ExportService + TaskListArchive + StandupArchive (§2g/§2i). Recommend a team grouping level above user.
3. **Drill-down tree extra Team level** in `ReportAggregator.BuildProjectTree` for RPT-03 under multi-team (§2h).
4. **DEFAULT seeding ownership** — drop `EnsureDefaultBacklog` from the initializer in favor of bootstrap + per-team sync (§4a).
5. **First-run default team name** — fixed vs user-prompted at first launch (TM-09 says renamable).
6. **`Func<int>` team-id provider vs `ICurrentTeamService` injection** — recommend direct injection to avoid the DI `Func<int>` collision (§6a). This is the one concrete **bug risk** if mishandled.
