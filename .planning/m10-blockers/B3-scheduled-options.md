# B3 — Four scheduled jobs whose only caller is `App.xaml.cs`

**Options for a human to decide. Nothing here is decided.**

Every claim below carries `file:line` and a `[VERIFIED]` / `[ASSUMED]` tag. Verified means I read that line in
this pass. No build was run, no test was run, no SQLite file was opened.

---

## Short answer to "is this genuinely straightforward?"

**Mostly yes, and the prompt's framing of the hard part is wrong in a way that makes it easier, not harder.**

The task brief asks for guards "so a job cannot run while a request is mid-write". That guard is **not needed
for database correctness**, because none of the four jobs writes to the database:

- Zero `INSERT` / `UPDATE` / `DELETE FROM` / `ExecuteAsync` / `ExecuteScalarAsync` in any of
  `BackupService.cs`, `ExportHubService.cs`, `StandupArchiveService.cs`, `TaskListArchiveService.cs`,
  `ExportService.cs`. [VERIFIED — grep over all five files returned no matches]
- The repository methods they call are `SELECT`-only: `StandupRepository.cs:62-63`,
  `TimeLogRepository.cs:159-160`, `BacklogRepository.cs:329-330`, `BacklogRepository.cs:358-359`.
  [VERIFIED — 4 of the ~12 repository methods in the four job graphs, sampled not exhaustive]
- The one thing that touches the live `.db` is `SqliteOnlineBackup.Copy`, which reads *through* a connection
  and is documented in-repo as consistent against a live WAL database while another connection holds an open
  write transaction. `SqliteOnlineBackup.cs:17-19`. The API really is on WAL — `HostBootTests.cs:66-74`
  asserts `PRAGMA journal_mode = wal` **from the API's own container**, not a hand-built factory. [VERIFIED —
  this closes the prior agent's open question #1]

So the port is: one `BackgroundService`, one `SemaphoreSlim` so a tick cannot overlap itself, and a decision
about how often to fire. **The real difficulty is not concurrency. It is frequency**, for the reason in the
next section.

---

## The one thing that can destroy data, and it is live today

`BackupService.BackupToFolderAsync` calls `Prune(folder, keep)` on **every single invocation**
(`BackupService.cs:54`), and `Prune` deletes every `timesheet_*.db` beyond the newest `keep`
(`BackupService.cs:130-144`). `keep` is `IAppConfig.BackupKeepCount`, default **30**
(`JsonAppConfig.cs:31,61`). [VERIFIED]

`ExportHubService` calls `BackupToFolderAsync(Path.Combine(root, "db"), keep)` **unconditionally, once per
configured export root, on every run** — outside the `if (backfillOnly && File.Exists(path)) continue;` guards
that protect the markdown files, and not skipped in either mode (`ExportHubService.cs:145`). [VERIFIED]

Consequences a human needs to weigh:

1. **A naive hourly scheduler destroys your backup depth in a day.** Thirty runs → thirty same-day snapshots →
   every older snapshot pruned. The `keep` window is a *count*, not a *time span*. Nothing in the code
   prevents this.
2. **This is already reachable without M10.** `POST /api/ops/export/run` → `ExportNowAsync()` → `RunAsync` →
   the same ungated line 145 (`SettingsEndpoints.cs:1041-1049`). An admin clicking "Export now" thirty times
   today evicts every older `.db` snapshot from `{root}/db`. That is a pre-existing defect, not one M10
   introduces — but it bounds any scheduler design, and someone should be told it exists regardless of which
   option is chosen. [VERIFIED]

**Design consequence:** any scheduled trigger for job 2 needs a once-per-period guard of its own. The
`File.Exists` guards inside `ExportRootAsync` protect only the markdown, never the DB copy.

---

## What each job actually is

| # | Job | Invoked at | Calls into Core | Idempotency guard today | Writes to disk |
|---|---|---|---|---|---|
| 1 | Auto-backup (BK-03) | `App.xaml.cs:63`, unconditional, `try/catch` swallowed to a Trace warning | `IBackupService.AutoBackupIfDueAsync` (`BackupService.cs:59-69`) | `AutoBackupEnabled` flag, non-blank `BackupFolderPath`, then `ListBackups().Any(b => b.Timestamp.Date == today)` — a **filesystem read**, parsed out of the filename stamp, not a DB flag, and not atomic with the write (TOCTOU) | `{BackupFolderPath}/timesheet_{yyyyMMddHHmmssfff}.db` + prunes to `keep` |
| 2 | Export-hub backfill | `App.xaml.cs:83`, **only when `ExportRoot1Path` or `ExportRoot2Path` is set** (`:80-82`) | `IExportHubService.BackfillAsync` → `RunAsync(backfillOnly: true)` (`ExportHubService.cs:46`) | Per-output-file `File.Exists` at `:112`, `:124`, `:136`. **The DB copy at `:145` has no guard at all.** | Per root, per team: `{root}/{team}/tasklist/*.md`, `/timesheet/*.md`, `/daily/*.md` (12 months + 12 weeks back, `:14-15`), plus `{root}/db/timesheet_*.db` |
| 3 | Weekly standup archive backfill (DR-09) | `App.xaml.cs:100`, **only in the `else` branch — i.e. only when NO export root is set** | `IStandupArchiveService.BackfillMissingWeeksAsync` (`StandupArchiveService.cs:69-83`) | `File.Exists(path)` per week (`:81`) | `{ArchivePath}` or `{dbDir}/StandupArchives/{yyyyMMdd}_daily.md` (`:85-92`) |
| 4 | Monthly task-list archive backfill (TL-09) | `App.xaml.cs:104`, same `else` branch | `ITaskListArchiveService.BackfillMissingMonthsAsync` (`TaskListArchiveService.cs:118-150`) | `File.Exists(path)` per month (`:148`) | `{ArchivePath}` or `{dbDir}/TaskListArchives/{yyyyMM}_tasklist.md` (`:218-225`) |

All four [VERIFIED] against the cited lines.

Additional facts that change the shape of the decision:

- **Jobs 3 and 4 are mutually exclusive with job 2 today.** `App.xaml.cs:80-82` vs `:97-106`. The in-code
  rationale (`:76-78`) is explicit: "When an export root is configured the structured per-team hub supersedes
  the legacy flat archives (EX-06)". [VERIFIED] If the server deployment configures an export root — which is
  the whole SharePoint story — then **on the desktop, jobs 3 and 4 never ran either**, and porting them would
  *add* behavior rather than preserve it.
- **Jobs 3 and 4 are whole-organisation.** `BackfillMissingWeeksAsync` → `ExportWeekAsync` →
  `BuildWeekMarkdownAsync(null, …)` (`StandupArchiveService.cs:59`); `BackfillMissingMonthsAsync` →
  `ExportMonthAsync` → `BuildMonthMarkdownAsync(null, …)` (`TaskListArchiveService.cs:54`). `null` teamIds =
  every team, one flat file. [VERIFIED] On the desktop that file landed on the individual user's own disk. On
  the API it lands on the shared host. **That is a new cross-team aggregation-on-disk that the desktop
  deployment did not have** — worth a conscious yes, not a side effect of a port.
- **All four output paths come from one process-wide `IAppConfig` singleton** (`Program.cs:49`) reading
  `%APPDATA%\TimesheetApp\appsettings.json`, loaded **once in the constructor and cached in fields** —
  `JsonAppConfig.cs:53-67,69-78`. Changing config on disk does not reach a running process. One backup folder,
  one pair of export roots, for the whole deployment. [VERIFIED]
- **All four are already `AddSingleton` in the API and have entirely singleton dependency graphs**
  (`Program.cs:104,107,108,112`). A `BackgroundService` can inject them directly with no scope juggling and no
  captive-dependency risk under `ValidateScopes`/`ValidateOnBuild` (`Program.cs:22-26`). [VERIFIED]
- **No `IHostedService` / `BackgroundService` / `AddHostedService` exists anywhere in `src/`.** [VERIFIED —
  grep] This is a new pattern for the codebase, but a first-party one on `net8.0` with no new package.
- **None of the four services takes any lock.** [VERIFIED — prior agent's grep, consistent with my reads]
- **The existing fire-and-forget precedent is `SettingsEndpoints.cs:1027-1031`** (retention off the request
  path, `Task.Run` + `try/catch` + `ILogger`), whose own comment says it is that shape only because "There is
  no DI-registered background queue available here (Program.cs is frozen for Wave 2)". [VERIFIED] Program.cs
  is not frozen now.

### One trap that will bite whoever implements this

`ApiFactory` is a `WebApplicationFactory<Program>` running the **real** `Program.cs` (`ApiFactory.cs:31`), so
**any `AddHostedService` in `Program.cs` starts in all ~212 ApiTests hosts.** With `ApiFactory`'s fresh temp
config, jobs 1 and 2 no-op (`AutoBackupEnabled` false, both export roots `""` —
`JsonAppConfig.cs:60,62-63`), but **jobs 3 and 4 would actually execute**, full-table-scanning each test
database and writing markdown into the test temp root. Best case wasted work; worst case a flake source in the
concurrency/409 tests. `ApiFactory.cs:112` already calls `UseEnvironment("Testing")`, which is a ready-made
gate. [VERIFIED for the mechanism and the config defaults; [ASSUMED] that it would actually cause a flake —
not demonstrated]

---

## Option 1 — One `BackgroundService`, all four jobs, persisted once-per-period markers

**What gets built**

- New `src/TimesheetApp.Api/Infrastructure/ScheduledJobsService.cs` — a `BackgroundService` injecting
  `IBackupService`, `IExportHubService`, `IStandupArchiveService`, `ITaskListArchiveService`, `IAppConfig`,
  `IClock`, `ISettingsRepository`, `ILogger<T>`.
  - `PeriodicTimer`, ~1 hour, first tick delayed ~60s so the backfill never competes with boot.
  - One `SemaphoreSlim(1,1)` held across the whole tick → a tick cannot overlap itself; the four jobs run
    sequentially inside it. This is the *only* lock needed (see "Short answer" above).
  - Each job wrapped in its own `try/catch` + `logger.LogError`, mirroring `App.xaml.cs:63-64,83-84,100-105`
    but **logged rather than swallowed to a Trace warning**.
  - Per-job once-per-period marker in `ISettingsRepository` (the existing DB key/value store, already routed
    at `GET|PUT /api/settings/{key}` — `AdminEndpoints.cs`), e.g. `scheduler.lastRun.autoBackup` = ISO date.
    **Written only after the job returns successfully.**
  - Preserve `App.xaml.cs`'s root-configured / else branching verbatim, so behavior is unchanged.
- One line in `Program.cs`: `builder.Services.AddHostedService<ScheduledJobsService>();`, gated on
  `!builder.Environment.IsEnvironment("Testing")` (see the ApiFactory trap).
- Tests in `src/TimesheetApp.ApiTests/` for the marker guard, plus a `TimesheetApp.Tests` test that a second
  run inside the same period is a no-op.

**Rough cost.** One new file, ~150–200 lines with the comment density this codebase uses. One `Program.cs`
line. 3–6 new tests. No Core change, no Angular change, no regeneration. Roughly a day.

**What could go wrong, especially to production data**

- **The marker lies.** If the marker is written before/independently of success, a backup that failed reads as
  "done today" and the safety net is silently gone — the exact failure mode nobody notices until they need the
  backup. Mitigation: write on success only, *and* keep `AutoBackupIfDueAsync`'s own `ListBackups()` check as
  the primary guard, because that one fails toward taking an extra backup (unreachable folder → empty list →
  "no backup today" → tries again), which is the safe direction. The DB marker then only bounds *frequency*.
- **Prune eats the backup depth** if the period is set shorter than a day for job 1 or job 2. See the section
  above. Job 2 in particular has no guard of its own at `ExportHubService.cs:145`.
- **Jobs 3/4 write all-teams markdown to the shared API host** where the desktop wrote it to one user's own
  disk. New exposure surface; see above.
- **A long backfill on a slow/unreachable network share** holds the semaphore. Harmless (nothing else waits on
  it) but it means a tick can be skipped. `File.WriteAllTextAsync` to a share that dies mid-write can leave a
  truncated `.md`; the next backfill's `File.Exists` check sees the truncated file and **will not repair it**.
  [VERIFIED that the guard is `File.Exists` only — `ExportHubService.cs:112,124,136`,
  `StandupArchiveService.cs:81`, `TaskListArchiveService.cs:148`; [ASSUMED] that a torn write is achievable in
  practice — not tested]
- **Backup under contention.** `SqliteOnlineBackup.Open` sets only `DataSource`/`Mode`/`Pooling` — **no
  `busy_timeout`, no `DefaultTimeout`** (`SqliteOnlineBackup.cs:92-102`), unlike the Server profile's
  `busy_timeout=1000` + `DefaultTimeout=5` (`SqliteConnectionFactory.cs:60-66`). Whether
  `BackupDatabase` can throw `SQLITE_BUSY` against an actively-written source is **something I could not
  determine** — the in-repo doc comment says WAL readers do not block on the writer (`SqliteOnlineBackup.cs:17-19`)
  and I have no evidence against it, but the absence of a busy timeout on that connection is a real gap and I
  will not assert safety I did not verify. If it can throw, a nightly backup that always collides with traffic
  fails every night, and with `try/catch` swallowing it, silently. This is the strongest argument for logging
  loudly and for a follow-up "was there a backup today?" surface.

**Undo cost.** Delete one file, delete one `Program.cs` line. Markdown and `.db` artifacts already written
stay on disk (they are the intended output, harmless). The `Settings` marker rows become inert. **Cheap.**

---

## Option 2 — Run them once at startup in the existing `Program.cs` scope

**What gets built**

`Program.cs` already has a one-time startup block, `using (var scope = app.Services.CreateScope())` at
`:174-209`, which runs `InitializeAsync` + `EnsureBootstrappedAsync` + `AdminBootstrap`. Add the four calls
there, in `App.xaml.cs`'s order and branching. Two sub-shapes:

- **2a, awaited inline** — simplest, but the backfill (12 months × 12 weeks × N teams × up to 2 roots of
  markdown over a network share, `ExportHubService.cs:14-15,96-142`) runs **before the server accepts a single
  request**. On a slow share that is minutes of unavailability at every restart.
- **2b, `_ = Task.Run(…)` after `Build()`** — follows the existing precedent at `SettingsEndpoints.cs:1027`.
  No readiness delay; the work overlaps early traffic, which is fine given the jobs are read-only.

**Rough cost.** ~15–25 lines in one existing file. No new file, no new pattern. **The cheapest of the three.**

**What could go wrong**

- **"Once per day" silently becomes "once per restart", and that is strictly worse than the desktop for
  job 1.** A desktop is started daily, so `AutoBackupIfDueAsync` fired daily. A server stays up for weeks:
  boot on day 1, then **no backup on days 2–60**. The BK-03 safety net looks ported and is not. This is, in my
  reading, the decisive fact against Option 2.
- **The mirror failure:** a server restarted five times in a day runs the export backfill five times → five
  ungated DB copies into `{root}/db` (`ExportHubService.cs:145`) → five entries chewed out of the 30-deep
  prune window (`BackupService.cs:130-144`). Job 1's own `ListBackups()` check absorbs this for the backup
  folder; job 2 has nothing.
- Fixing both requires a persisted last-run marker — at which point you have built most of Option 1 minus the
  timer, and you still do not get a daily backup on a long-lived server.

**Undo cost.** Delete the lines. **Cheapest of the three to undo.**

---

## Option 3 — No in-app scheduler: expose the missing triggers as ops routes, schedule on the host

**What gets built**

- `POST /api/ops/export/backfill` → `IExportHubService.BackfillAsync()` and `POST /api/ops/archive/backfill`
  → `BackfillMissingWeeksAsync()` + `BackfillMissingMonthsAsync()`. These mirror the existing four
  `/api/ops/*` routes (`SettingsEndpoints.cs:1000-1060`) — same `AuthSetup.AdminPolicy` + DB-fresh
  `ctx.IsAdmin` double gate, `202 Accepted` for the long ones like retention at `:1022-1039`. Put them in a
  new `AdminOpsEndpoints.cs` rather than the closed `SettingsEndpoints.cs`, exactly as `AdminEndpoints.cs`
  explains its own existence (`AdminEndpoints.cs:10-14`).
- `POST /api/ops/backup/run` **already exists** (`SettingsEndpoints.cs:1051-1059`) — no work.
- A Windows Scheduled Task on the API host calling them nightly, plus a runbook in `docs/`.

**Rough cost.** ~2 routes + 2 tests + a runbook. Low code. **The cost lands in operations, not the repo.**

**What could go wrong**

- **A non-interactive caller needs a credential.** Every `/api/ops/*` route requires an authenticated admin
  cookie (`AuthSetup.AdminPolicy` + `ctx.IsAdmin`). A scheduled task therefore needs either a stored service
  account password or a new API-key surface. **That is a new authentication surface, and it is the single
  biggest risk in this option** — larger than anything in Options 1 or 2. Note the related open finding that
  the admin claim is frozen into a 30-day cookie (`AdminEndpoints.cs:23-31`).
- **The schedule leaves the repository.** Nobody reading the code can see that a nightly backup is expected.
  It can be disabled, deleted, or lost in a host migration with no signal. A missing backup is invisible until
  it is needed.
- Same prune hazard if the task is scheduled more often than daily — with *no* code-side guard at all beyond
  job 1's own `ListBackups()` check.
- Overlap between a scheduled call and an admin clicking the button becomes genuinely possible (no in-process
  semaphore spans two requests). For the read-only jobs that is benign; for two concurrent
  `BackupToFolderAsync` calls into the same folder, two `Prune` passes run concurrently — **I could not
  determine** whether that is harmful. Destination filenames are millisecond-stamped so a same-file collision
  needs a same-millisecond race, and `Copy` deletes the destination first (`SqliteOnlineBackup.cs:41,85-90`),
  so the failure mode would be one run deleting a file another is mid-write. Not demonstrated, not disproven.

**Undo cost.** Deleting a Windows task is trivial, and the routes stay useful even if a hosted service is
added later — Option 3 is a **strict subset** of the work in Option 1 in that sense. **The most reversible of
the three.**

---

## What I would pick, and why — for a human to accept or reject

**Option 1, scoped to jobs 1 and 2 only, with jobs 3 and 4 decided separately by whether the deployment
configures an export root.**

The reasoning:

1. **The four jobs are not equally urgent, and treating them as one bundle hides that.** Jobs 2, 3 and 4 write
   markdown derived entirely from the database. If they miss a period, running the backfill later regenerates
   it — that is literally what `backfillOnly` + the `File.Exists` guards are for. **Job 1 is different: a
   backup you did not take on the day the data was correct is not recoverable later.** The only job whose
   absence loses something is the backup. Ordering by urgency: job 1 ≫ job 2 > jobs 3/4.
2. **Option 2 does not actually port job 1.** On a long-lived server "on startup" means "once, in
   September". That is not the BK-03 behavior; it just looks like it in a diff.
3. **Option 3's credential problem is a bigger new risk than the whole of Option 1.** Introducing a stored
   admin credential or an API key to make a backup happen is worse, on the axis that matters, than 180 lines
   of first-party `BackgroundService`.
4. **Jobs 3 and 4 may need no port at all.** `App.xaml.cs:76-82,97-106` says the export hub supersedes them
   when a root is configured. If the server configures an export root, the desktop never ran jobs 3/4 either
   and job 2 already writes per-team `tasklist/` and `daily/` markdown. Porting them anyway would newly
   materialise an all-teams flat archive on the shared host — an addition, not a preservation.

**But the following are trade-offs I am deliberately not resolving, because they are operational and
data-safety calls that need an owner:**

- **Where the once-per-day marker lives.** Filesystem (`ListBackups()`, as today) fails toward taking an
  *extra* backup — safe direction, but TOCTOU and dependent on the folder being reachable. A DB settings key
  is durable and survives a folder change, but if it is ever written on a path other than confirmed success it
  produces a **silently missing backup**. My inclination is both — filesystem as the correctness guard, DB key
  only to bound frequency — but that is a judgement about which failure you would rather have.
- **Whether jobs 3/4 should run on a server at all**, given they write whole-organisation data to shared host
  storage. Only someone who knows whether the teams are meant to see each other's records can answer that.
- **`ExportHubService.cs:145`'s ungated DB copy is a live defect on `POST /api/ops/export/run` today**,
  independent of M10. Fixing it (a guard, or moving the copy behind the same `backfillOnly` check as the
  markdown) is one line, but it is a behavior change to a shipped route and belongs to whoever owns that
  route, not to this port.
- **`RetentionEnabled` becomes a dead flag** regardless of which option is chosen — `App.xaml.cs:91` is its
  sole consumer and `RetentionService` reads only `RetentionMonths`. Retention is *not* one of the four B3
  jobs and is deliberately out of scope here, but any scheduler design will invite the question "why isn't
  retention in it?", and the answer (it is destructive, default-off, and manual-only may be safer) should be
  recorded rather than rediscovered.

---

## Open, not closed

- Whether `SqliteConnection.BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source,
  given `SqliteOnlineBackup.Open` sets no `busy_timeout` and no `DefaultTimeout`
  (`SqliteOnlineBackup.cs:92-102`). The in-repo doc comment claims safety; I did not verify it and did not run
  anything.
- Whether two concurrent `BackupToFolderAsync` calls into the same folder (scheduled job + admin button) can
  have one run's `Prune`/`Copy` delete a file another run is mid-write.
- Whether `File.WriteAllTextAsync` to a network/SharePoint root can leave a truncated `.md` that the
  `File.Exists` guards then permanently treat as complete. The guard is `File.Exists` only — that much is
  verified; the torn-write scenario is not.
- Whether adding `AddHostedService` would actually destabilise the ApiTests suite, or merely waste work in it.
  The mechanism is verified (`ApiFactory.cs:31,112`); the flake is inferred.

*Also noted in passing, not part of B3:* `IDefaultTaskSyncService.SyncAsync()` runs at `App.xaml.cs:71` on
every desktop startup but is **not** in the API's startup block (`Program.cs:174-209`) — on the web it fires
only from `SettingsEndpoints.cs:249,673,689,709` after a DefaultTasks write. That is a fifth startup-parity
difference. It has real API callers, so it is not a B3 blocker, but nobody appears to have decided it
deliberately. [VERIFIED — grep]
