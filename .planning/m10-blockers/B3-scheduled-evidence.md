# B3 — Scheduled Jobs Evidence

Scope: the four scheduled/background jobs whose only production caller today is
`src/TimesheetApp/App.xaml.cs` (the WPF app). Evidence only — no solution design in this file.

Confirmed by `grep` across `src/` for each method name: outside `App.xaml.cs`, every other hit is a test
file (`TimesheetApp.Tests/**`) calling the method directly on a fresh instance — never anywhere in
`TimesheetApp.Api` production code. [VERIFIED]

---

## 1. Where each job is invoked in `App.xaml.cs`

All four run inline in `OnStartup`, in this order, all `try/catch`-wrapped as best-effort (a failure
logs a `Trace.TraceWarning` and startup continues) — **except** job 2's containing `if` block also gates
job 3/4 (see below). [VERIFIED: `src/TimesheetApp/App.xaml.cs:59-106`]

| # | Job | Call site | Guard before the call |
|---|-----|-----------|------------------------|
| 1 | Auto-backup (BK-03) | `App.xaml.cs:63` — `IBackupService.AutoBackupIfDueAsync()` | None — always attempted on every startup, right after team bootstrap. Internal to the method (see §3). |
| 2 | Export-hub 12-month/12-week backfill | `App.xaml.cs:83` — `IExportHubService.BackfillAsync()` | Only runs `if (!string.IsNullOrWhiteSpace(config.ExportRoot1Path) \|\| !string.IsNullOrWhiteSpace(config.ExportRoot2Path))` (`App.xaml.cs:80-82`) — i.e. only when at least one structured export root is configured. |
| 3 | Weekly standup-archive backfill | `App.xaml.cs:100` — `IStandupArchiveService.BackfillMissingWeeksAsync()` | Runs only in the **`else`** branch of the same `if` — i.e. **only when NO export root is configured** (`App.xaml.cs:97-106`). When an export root IS set, this legacy flat backfill is skipped entirely; `ExportHubService.RunAsync` writes the equivalent per-team `daily/*.md` files instead (see §2). |
| 4 | Monthly task-list archive backfill | `App.xaml.cs:104` — `ITaskListArchiveService.BackfillMissingMonthsAsync()` | Same `else` branch as job 3 — mutually exclusive with the export-hub backfill, same reasoning. |

Retention (`IRetentionService.EnsureRetentionAsync`, `App.xaml.cs:93`) also runs from `OnStartup` in this
same block but is **not** one of the four jobs in scope for B3 — noted only for completeness since it sits
in the same `if (ExportRoot...)` block, nested one level deeper, gated additionally on `config.RetentionEnabled`.

---

## 2. What each calls into Core

All four services already live in `TimesheetApp.Core` (`src/TimesheetApp.Core/Services/*.cs`) — none of
the logic is in the WPF project. [VERIFIED]

- **`BackupService.AutoBackupIfDueAsync()`** (`src/TimesheetApp.Core/Services/BackupService.cs:59-69`)
  → if due, calls `BackupNowAsync()` → `BackupToFolderAsync(_config.BackupFolderPath, _config.BackupKeepCount)`
  (`BackupService.cs:40-57`) → `SqliteOnlineBackup.Copy(dbPath, backupPath)` (online backup, safe against a
  live/WAL-mode DB — see §5) → `Prune(folder, keep)`.

- **`ExportHubService.BackfillAsync()`** (`src/TimesheetApp.Core/Services/ExportHubService.cs:46`)
  → `RunAsync(backfillOnly: true)` → for each configured root × each team (`GetAllAsync()` incl. inactive):
  calls `ITaskListArchiveService.BuildMonthMarkdownAsync`, `IExportService.ExportMarkdownAsync`,
  `IStandupArchiveService.BuildWeekMarkdownAsync` (content-only builders, not the disk-writing
  `ExportMonthAsync`/`ExportWeekAsync`) for the last 12 completed months / 12 completed weeks
  (`BackfillMonths = 12`, `BackfillWeeks = 12`, `ExportHubService.cs:14-15,150-165,169-184`), writes each via
  `File.WriteAllTextAsync`, and **always** calls `IBackupService.BackupToFolderAsync(Path.Combine(root, "db"), _config.BackupKeepCount)` once per root (`ExportHubService.cs:145`) — ungated, see §3.

- **`StandupArchiveService.BackfillMissingWeeksAsync()`** (`src/TimesheetApp.Core/Services/StandupArchiveService.cs:69-83`)
  → reads all standup entries strictly before the current week via `IStandupRepository.GetEntriesForRangeAsync`,
  groups by week Monday, and for each week whose file does not yet exist on disk calls `ExportWeekAsync(monday)`
  (which itself calls `BuildWeekMarkdownAsync(null, monday)` then `File.WriteAllTextAsync`).

- **`TaskListArchiveService.BackfillMissingMonthsAsync()`** (`src/TimesheetApp.Core/Services/TaskListArchiveService.cs:118-150`)
  → reads all backlogs (`IBacklogRepository.SearchAsync`) plus their `period_month` audit trail
  (`GetAuditForBacklogsAsync`) to discover every month that ever had data, and for each such month strictly
  before the current month whose file does not yet exist calls `ExportMonthAsync(y, m)`.

---

## 3. Idempotency / once-per-period guard — per job

No guard is DB-backed; all four rely on reading the filesystem at call time. None of the four services
takes any lock (`SemaphoreSlim`/`Mutex`/`lock`) — confirmed by grep across
`src/TimesheetApp.Core/Services/*.cs`: the only hits for `SemaphoreSlim|lock (|Mutex|Interlocked` are in
`StandupService`/`IStandupService`, unrelated to these four. [VERIFIED]

| Job | Guard mechanism | Scope | Gap |
|---|---|---|---|
| 1. Auto-backup | `ListBackups().Any(b => b.Timestamp.Date == today)` (`BackupService.cs:65`) — lists `timesheet_*.db` files in `_config.BackupFolderPath` and checks whether one is dated today (local date). | Per `BackupFolderPath` (one folder = one guard scope; server-wide, since `IAppConfig` is one process-wide instance — see §6). | **TOCTOU race**: the check-then-write is not atomic. Two calls in-flight at once (e.g. a restart landing at the same moment as an admin's manual "Backup now" click) can both read "no backup today" before either writes, producing two backups in the same run. Confirmed no lock exists anywhere in `BackupService`. |
| 2. Export-hub backfill | **Per-file** `File.Exists(path)` before building each markdown file (`ExportHubService.cs:112,124,136`). | Per file, per root. | The **whole-DB copy is NOT gated at all** — `BackupToFolderAsync(Path.Combine(root, "db"), ...)` (`ExportHubService.cs:145`) runs unconditionally every time `BackfillAsync()`/`ExportNowAsync()` is called, once per configured root. Confirmed by test `ExportHubServiceTests.cs:243` invoking `BackfillAsync()` directly. If this job is later run on a recurring schedule (rather than once per app launch) each firing appends a fresh `{root}/db/timesheet_{stamp}.db`, bounded only by `Prune()`'s `keep` count — there is no "already copied today" check the way job 1 has. |
| 3. Weekly standup backfill | `File.Exists(path)` per week (`StandupArchiveService.cs:81`), where `path` is under `ArchiveDir()` = `_config.ArchivePath` if set, else `{dbDir}/StandupArchives`. | Per file. | Same TOCTOU shape as job 1 if two runs overlap on the same week — no lock. |
| 4. Monthly task-list backfill | `File.Exists(path)` per month (`TaskListArchiveService.cs:148`), `ArchiveDir()` same fallback pattern as job 3 (`.../TaskListArchives`). | Per file. | Same TOCTOU shape as job 3. |

Note the **mutual exclusivity** in `App.xaml.cs` today (§1): jobs 3/4 currently only run when NO export
root is configured; when a root IS configured, `ExportHubService`'s per-team builders write the
functionally-equivalent files instead, under a different path layout (`{root}/{team}/daily|tasklist/*.md`
vs. the flat `{dbDir}/StandupArchives`/`TaskListArchives`). A background-service design that runs all four
independently must decide whether to preserve this either/or (both paths kept, one file set silently going
stale) or collapse it — flagged for the next stage, not decided here.

---

## 4. What each writes to disk

| Job | Path pattern | Path source |
|---|---|---|
| 1. Auto-backup | `{BackupFolderPath}/timesheet_{yyyyMMddHHmmssfff}.db` | `IAppConfig.BackupFolderPath` (empty = no-op, `BackupService.cs:62`) |
| 2. Export-hub backfill | `{root}/{team-segment}/tasklist/{yyyyMM}_tasklist.md`, `.../timesheet/{yyyyMM}_timesheet.md`, `.../daily/{yyyyMMdd}_daily.md`, plus `{root}/db/timesheet_{stamp}.db` | `IAppConfig.ExportRoot1Path` / `ExportRoot2Path` (both empty = whole job no-ops, `ExportHubService.cs:50-53`) |
| 3. Weekly standup backfill | `{ArchivePath or dbDir}/StandupArchives/{yyyyMMdd}_daily.md` | `IAppConfig.ArchivePath`, falling back to `Path.GetDirectoryName(IAppConfig.DbPath)` (`StandupArchiveService.cs:85-92`) |
| 4. Monthly task-list backfill | `{ArchivePath or dbDir}/TaskListArchives/{yyyyMM}_tasklist.md` | Same fallback pattern (`TaskListArchiveService.cs:218-225`) |

All four paths ultimately derive from `IAppConfig`, which is `%APPDATA%\TimesheetApp\appsettings.json`
by default (`JsonAppConfig.cs:6,48-49`) — a single JSON file, loaded once into one process-wide instance.
[VERIFIED]

---

## 5. Concurrency: can a job run while a request is mid-write?

- `SqliteConnectionFactory` has an explicit `SqliteProfile.Server` used only by the API
  (`Program.cs:75-76`, comment explains why `Desktop` — the constructor default — would be wrong here):
  `journal_mode=WAL`, `busy_timeout=1000`, `DefaultTimeout=5` (`SqliteConnectionFactory.cs:58-67`).
  `HostBootTests` is cited as asserting `PRAGMA journal_mode = wal` from this container
  (`Program.cs:73` comment). [VERIFIED via comment + code, test file itself not opened]
- The backup path (`SqliteOnlineBackup.Copy`, job 1 and the whole-DB copy inside job 2) uses SQLite's
  **online backup API** (`SqliteConnection.BackupDatabase`), which the type's own doc block states is safe
  to run against a live database mid-write under WAL: "WAL readers do not block on the writer"
  (`SqliteOnlineBackup.cs:6-24`). This connection is opened separately from the pooled `IConnectionFactory`
  connections (`Pooling=false`, its own one-shot `SqliteConnection`, `SqliteOnlineBackup.cs:92-105`), so it
  does not contend with the API's connection pool for a handle. On this evidence, the **backup side of job
  1/2 is already safe to run concurrently with request-time writes** by construction of SQLite's WAL +
  online-backup API — no additional locking is evidently needed for that specific operation.
- The markdown-writing jobs (2 tasklist/timesheet/daily builders, 3, 4) only **read** through the normal
  repositories (which go through the pooled WAL connection) and then write markdown files — not the
  database — so they carry the same "no special locking needed for DB access" property; WAL readers do not
  block behind an in-flight writer.
- What is **not** covered by any of the above: two invocations of the *same* job (or of job 1 and job 2's
  DB-copy step) overlapping each other. No mutual-exclusion primitive exists in any of these four services
  today (§3) — the only precedent for locking in this codebase is `RetentionService`, which is explicitly
  called out elsewhere (`SettingsEndpoints.cs` class doc, `AdminEndpoints`-adjacent comment at
  `SettingsEndpoints.cs:1015-1021`) as holding "one `BEGIN IMMEDIATE` across six bulk DELETEs, blocking
  every writer app-wide" — and even that is worked around today only by firing it off the request path via
  `Task.Run`, not by a scheduler-level guard. Retention is out of B3's scope but is the only existing
  precedent in this codebase for "a job that must not overlap a request."

---

## 6. Cross-user / server-process considerations already flagged in the codebase

`IAppConfig` is registered as a single process-wide **instance** (`builder.Services.AddSingleton(appConfig)`,
`Program.cs:49`), not a per-request type — i.e. one shared config for the whole server process, matching its
"app-local" design intent (`IAppConfig.cs:24-31` comment: "IAppConfig is per-PROCESS, and on a desktop one
process serves one user... In an API one process serves EVERYONE"). For a scheduled background job (which
has no per-user identity anyway) this is consistent with how the four jobs already read config today — but
it is the same singleton the class-doc in `SettingsEndpoints.cs:46-48` warns never to *write* from a
request ("on a server every one of them is cross-user state"). Relevant to B3 only in that a hosted service
reading `BackupFolderPath`/`ExportRoot1Path`/`ExportRoot2Path`/`ArchivePath`/`AutoBackupEnabled` inherits
this same server-wide, single-config-file scope — there is exactly one backup folder / export roots for
the whole deployment, not one per user or team.

---

## 7. Current API-side wiring (what already exists vs. what's missing)

All four services are **already registered as `AddSingleton` in `TimesheetApp.Api/Program.cs`**
(`IBackupService` line 104, `IStandupArchiveService` line 107, `ITaskListArchiveService` line 108,
`IExportHubService` line 112) — they compile and resolve in the API's DI graph today. [VERIFIED] They are
consumed by **on-demand, admin-triggered** endpoints only, and none of those on-demand routes calls the
same method App.xaml.cs calls:

| Job | On-demand API route today | Method it calls | Same as the App.xaml.cs scheduled call? |
|---|---|---|---|
| 1. Auto-backup | `POST /api/ops/backup/run` (`SettingsEndpoints.cs:1051-1059`) | `IBackupService.BackupNowAsync()` | **No** — `BackupNowAsync` always backs up; `AutoBackupIfDueAsync` (the scheduled one) additionally checks `AutoBackupEnabled` + the once-per-day guard. Neither the flag nor the guard is exercised by this route. |
| 2. Export-hub backfill | `POST /api/ops/export/run` (`SettingsEndpoints.cs:1041-1049`) | `IExportHubService.ExportNowAsync()` | **No** — `ExportNowAsync` covers only the current+previous month/week (`backfillOnly: false`); the 12-month/12-week `BackfillAsync()` has no route at all. |
| 3. Weekly standup backfill | `POST /api/standup/archive?date=` (`AdminEndpoints.cs:133-154`) | `IStandupArchiveService.ExportWeekAsync(date)` | **No** — writes exactly one named week; `BackfillMissingWeeksAsync()` (discover-and-fill every missing completed week) **has no API route at all**. Confirmed explicitly in the codebase's own doc comment: *"Program.cs never calls `BackfillMissingWeeksAsync` (only the WPF `App.xaml.cs` does), so on a web-only deployment nothing ever wrote it"* (`AdminEndpoints.cs:16-21`, repeated at `AdminEndpointsTests.cs:299-301`). |
| 4. Monthly task-list backfill | `GET /api/tasklist/export?year=&month=` (`TaskListEndpoints.cs:99-129`) | `ITaskListArchiveService.BuildMonthMarkdownAsync(...)` — **content-only**, streamed to the browser as `text/markdown`, never written to disk | **No** — this route doesn't even call the disk-writing `ExportMonthAsync`, let alone `BackfillMissingMonthsAsync()`. The monthly task-list archive-to-disk job has **zero API surface today**, on-demand or scheduled. |

So today, on a web-only deployment (no `TimesheetApp` WPF process running against the same DB): job 1 only
ever runs when an admin manually clicks "Backup now" (and then unconditionally, bypassing the
enabled-flag/once-daily guard); job 2's 12-month backfill never runs at all (only the 2-period "export now"
does, and only on manual click); jobs 3 and 4 never write their disk archives at all — confirmed for job 3
by the codebase's own comment, and independently confirmed for job 4 by tracing every route that touches
`ITaskListArchiveService`.

---

## 8. Framework/hosting precedent

No `BackgroundService` or `IHostedService` implementation exists anywhere in `src/` today (grep for both
across the whole `src/` tree returns no matches) — a hosted-service-based port of these four jobs would be
a new pattern for this codebase, not a variation on an existing one. `TimesheetApp.Api` targets `net8.0`
(seen in `obj/Debug/net8.0/...` generated files) and is a standard `WebApplication`/generic-host-based app
(`Program.cs:10` `WebApplication.CreateBuilder`), so `AddHostedService<T>` is available without adding any
package.

---

## 9. Straightforwardness assessment (as requested, evidence-level only)

The wiring gap is narrow and mechanical: all four services are Core-clean (no WPF/System.Windows
dependency — confirmed by their being already `AddSingleton`-registered and exercised inside
`TimesheetApp.Api` today for the on-demand routes above), already resolve inside the API's DI container,
and each job's own idempotency is self-contained (file-existence checks, or — for job 1 — a
filesystem-date check) rather than relying on any WPF-only state. The only things genuinely absent are:
(a) a process that calls `BackfillAsync()` / `BackfillMissingWeeksAsync()` / `BackfillMissingMonthsAsync()`
/ `AutoBackupIfDueAsync()` on some cadence instead of once per WPF launch, and (b) a decision on
serialization — none of the four takes a lock today, and job 2's DB-copy step has no once-per-period guard
at all (§3). Those two points are the real content of the next design stage; they are not evidence of
hidden complexity in the jobs themselves.
