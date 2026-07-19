# B2-restore — Backup restore has no web path

**Status:** options presented, NOT decided. A human decides.
**Scope:** M10 deletes `src/TimesheetApp/`. `IBackupService.RestoreAsync` and `ListBackups` lose their only
callers and become dead code in a surviving Core project. This document establishes what restore actually
does today, what "open connections" means for the API host specifically, and lays out three restore paths
with honest costs.

---

## Part 1 — Verified ground truth

### 1.1 What `RestoreAsync` actually does

`src/TimesheetApp.Core/Services/BackupService.cs:97-126` [VERIFIED — read]

1. Throws `FileNotFoundException` if the backup path is missing (`:99-100`).
2. Throws `InvalidOperationException` if the backup path resolves to the live DB itself (`:106-108`) —
   case-insensitive full-path compare.
3. On a background thread (`:113`):
   - `SqliteOnlineBackup.ClearPools()` (`:116`)
   - safety copy: `SqliteOnlineBackup.Copy(dbPath, "{dbPath}.pre-restore_{stamp}.bak")` (`:121-122`)
   - the restore itself: `SqliteOnlineBackup.Copy(backupPath, dbPath)` (`:124`)

Both copies go through the SQLite **online-backup API**, never `File.Copy`.

### 1.2 What `SqliteOnlineBackup.Copy` does to the destination

`src/TimesheetApp.Core/Data/SqliteOnlineBackup.cs:39-53` [VERIFIED — read]

```
DeleteWithSidecars(destDbPath);              // :41  -> File.Delete on .db, .db-wal, .db-shm  (:85-90)
using var source = Open(sourceDbPath, ReadWrite);
using var dest   = Open(destDbPath, ReadWriteCreate);
source.BackupDatabase(dest);                 // :46  native sqlite3_backup_*
Exec(dest, "PRAGMA journal_mode=DELETE;");   // :52  artifact is self-contained, one file
```

Two consequences that matter downstream:

- **The destination `.db` and its sidecars are DELETED before anything is written** (`:41`, `:85-90`).
  There is a window in which the live database path does not exist.
- **Backup artifacts are `journal_mode=DELETE`** (`:52`) — a single self-contained file with no `-wal`.
  This is what makes a manual file copy of an *artifact* viable in Option 1.

`Open()` uses `Pooling = false` (`:101`) for these maintenance connections.

The class's own remark (`:32-38`) states the precondition plainly: *"Callers must ensure no handle is still
open on the destination."*

### 1.3 The safety copy

`{dbPath}.pre-restore_{stamp}.bak`, written **before** the overwrite (`BackupService.cs:121-122`), stamp
format `yyyyMMddHHmmssfff` (`:24`). It is itself an online backup, and it is `journal_mode=DELETE`, so it
is a restorable database, not a raw file snapshot. Asserted at
`src/TimesheetApp.Tests/Services/BackupServiceTests.cs:199-201`. [VERIFIED]

Note the guard order: the self-restore rejection (`:106-108`) fires **before** any safety copy is taken, so
a rejected restore leaves no `.bak` behind (asserted at `BackupServiceTests.cs:228`). [VERIFIED]

### 1.4 What "open connections" means for the API host specifically

| Property | Value | Evidence |
|---|---|---|
| Profile | `SqliteProfile.Server` — explicit, not the constructor default | `src/TimesheetApp.Api/Program.cs:75-76` |
| Pooling | `true` | `src/TimesheetApp.Core/Data/SqliteConnectionFactory.cs:60` |
| Journal mode | `WAL` | `SqliteConnectionFactory.cs:65` |
| busy_timeout | 1000 ms | `SqliteConnectionFactory.cs:66` |
| synchronous | NORMAL | `SqliteConnectionFactory.cs:66` |
| DefaultTimeout | 5 s | `SqliteConnectionFactory.cs:64` |
| Open mode | **`ReadWriteCreate`** | `SqliteConnectionFactory.cs:53` |

All [VERIFIED — read].

**Repositories hold nothing open across requests.** Every repository method is `using var c = _factory.Create()`.
Under `Pooling=true` that means `Dispose()` returns the handle to the ADO.NET pool rather than closing the
file — the pool retains idle native handles between requests. This is stated in the codebase's own words at
`SqliteOnlineBackup.cs:81-83` and corroborated empirically at
`src/TimesheetApp.Tests/Data/SqliteConnectionFactoryServerProfileTests.cs:42-46`. [VERIFIED]

**SignalR hubs do NOT hold a SQLite connection for the connection lifetime.** — *this resolves an open
question from the prior pass.* `DataHub` touches the database only inside `OnConnectedAsync`, via
`_teams.GetTeamIdsForUserAsync(uid)` (`src/TimesheetApp.Api/Hubs/DataHub.cs:39`), which is an ordinary
per-call repository method. The hub holds no connection between calls. [VERIFIED — read DataHub.cs in full]

*But* the hub is not harmless during a restore: a dropped hub connection **reconnects**, and
`OnConnectedAsync` runs again and hits the database — the class doc says exactly this at `DataHub.cs:20-23`.
So SignalR clients are an automatic, self-retrying source of new DB opens during any maintenance window.

**There is no drain, maintenance mode, or graceful-shutdown hook anywhere.** `grep` over
`src/TimesheetApp.Api` for `IHostApplicationLifetime|ApplicationStopping|maintenance|drain|Environment.Exit`
returns **no matches in the API project** (only unrelated hits in Core comments and Angular specs).
`/health` is a static `Results.Ok` unrelated to connection or request state
(`src/TimesheetApp.Api/Program.cs:237-240`). [VERIFIED]

**Deployment is single-process, single-port.** `deploy-local.bat:37` ends in
`dotnet run -c Release --urls http://localhost:5080`; `start-web.bat` starts one API window. No IIS,
service, or web-farm artifact found in the repo. [VERIFIED for the *scripts in the repo*.]
[COULD NOT DETERMINE] whether the real production deployment matches these scripts — no production
topology evidence exists in this repo. If two API processes ever point at one `.db`, every option below
changes.

### 1.5 Is there a LIST route? A RESTORE route?

**No, to both.** `/api/ops/*` is exactly four routes, all `AdminPolicy`
(`src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:1000-1060`):

- `POST /api/ops/retention/preview` (`:1002`)
- `POST /api/ops/retention/run` (`:1022`)
- `POST /api/ops/export/run` (`:1041`)
- `POST /api/ops/backup/run` (`:1051`) — calls `BackupNowAsync()` only

The omission is deliberate and documented: *"`IBackupService.RestoreAsync` is NOT exposed in M8.3: it
overwrites the live .db in place while the API holds open connections, which corrupts live readers."*
(`SettingsEndpoints.cs:43-44`). [VERIFIED]

`ListBackups()` and `RestoreAsync()` have **zero callers** in `src/TimesheetApp.Api` and
`src/timesheet-web`. Their only production callers are
`src/TimesheetApp/ViewModels/SettingsViewModel.cs:287` and `:298` — the WPF app M10 deletes. [VERIFIED — grep]

### 1.6 The precedent that governs where restore belongs

The web Settings screen deliberately has **no host-configuration inputs at all** — no DB path, no export
roots, **no backup folder**. The reasoning is written out at
`src/timesheet-web/src/app/pages/settings/settings.component.ts:45-60` and enforced by a test at
`settings.component.spec.ts:127`:

> *"These are HOST configuration. They live in `appsettings.json`, where exactly one person can change them
> and a restart makes it so. Shipping them as web inputs would let any admin repoint the production
> database from a browser tab."*

The line the project already drew: **operations** (`/api/ops/*` actions) belong on the web;
**host configuration** belongs in `appsettings.json` + restart. Restore sits awkwardly across that line —
it is an action, but it is an action on host state that requires a restart. [VERIFIED]

### 1.7 Two adjacent facts that may outrank this blocker

**(a) No backups are being produced today.** On the config this environment resolves against,
`C:\Users\Admin\AppData\Roaming\TimesheetApp\appsettings.json:4` has `"BackupFolderPath": null`.
`BackupService.cs:43-44` returns `null` when the folder is blank, so `POST /api/ops/backup/run` currently
**no-ops and returns 200 OK** with a null value. The Angular client detects this and shows
*"Backup did not run — no file was written."* (`settings.component.ts:549-555`). [VERIFIED]

**(b) M10 deletes scheduled backup entirely.** `AutoBackupIfDueAsync()` (BK-03, once-per-day-on-startup)
has exactly one production caller: `src/TimesheetApp/App.xaml.cs:63`. The API host never calls it —
`grep` confirms no occurrence in `src/TimesheetApp.Api`. After M10 there is no scheduled backup at all,
only the manual admin button. [VERIFIED]

> **Restore is worthless without backups.** Whichever option below is chosen, someone must (1) set
> `BackupFolderPath` in the host's `appsettings.json` and (2) decide what replaces the daily auto-backup
> (Windows Task Scheduler calling the endpoint? a hosted service? nothing?). I would argue this pair is a
> higher-priority decision than the restore path itself, but it is a separate decision and I am not
> folding it into the options below.

### 1.8 What is genuinely NOT known

- **`SqliteConnection.ClearAllPools()` semantics for a checked-out connection.** Whether it forcibly closes
  a connection an in-flight request currently holds, or only reclaims idle pooled handles, could not be
  determined — `Microsoft.Data.Sqlite` is a NuGet dependency, not vendored source. [COULD NOT DETERMINE]
  Confirming the prior pass: **no test exercises `RestoreAsync` against a live Server-profile connection.**
  `WalBackupSafetyTests.cs:87` calls `RestoreAsync` *after* the `using` block closed at `:81`, and
  `TinyDb.Open` sets `Pooling = false` (`src/TimesheetApp.Tests/Services/TinyDb.cs:86`). The restore path
  is tested **only in the quiesced regime.** [VERIFIED by reading both files]

- **Whether `File.Delete` on the live `.db` throws or silently succeeds while another handle is open.**
  Asserted by the code comment at `SqliteOnlineBackup.cs:36-37` ("otherwise this throws rather than
  silently corrupting it"). Never demonstrated by a test. [COULD NOT DETERMINE]

- **Production topology** (see 1.4). [COULD NOT DETERMINE]

### 1.9 The failure mode that drives the whole design

[ASSUMED — reasoned from verified code, not demonstrated by any test. This is the single most important
inference in this document and it deserves a skeptical read.]

`ClearAllPools()` is a **one-shot drain, not a gate.** Even granting the most favourable answer to 1.8
(it perfectly closes every handle at instant T), nothing prevents a request arriving at T+1ms from calling
`_factory.Create()`. That opens the live path with **`Mode = ReadWriteCreate`**
(`SqliteConnectionFactory.cs:53`) and immediately issues `PRAGMA journal_mode=WAL` (`:65`).

If that lands inside the window opened by `DeleteWithSidecars` (`SqliteOnlineBackup.cs:41`), SQLite
**creates a brand-new empty database** at the production path and marks it WAL. From there:

- the restore's own `dest` connection and the request's connection are racing on one path;
- the request's repository queries hit a schema-less file → `SqliteException: no such table`
  (`DatabaseInitializer` only runs at startup, `Program.cs:176`);
- the restored file can end up WAL-marked with a foreign `-wal` beside it — the precise corruption
  `WalBackupSafetyTests.cs:97-114` exists to prevent.

**This is not fixed by calling `ClearAllPools` harder.** It is fixed only by guaranteeing no code path can
open the database for the duration. That guarantee is what separates the three options below.

---

## Part 2 — The options

### Option A — Documented manual file-swap runbook, zero code

**What gets built**

One document, `docs/runbooks/restore-database.md`, with an exact numbered procedure:

1. Stop the API: close the `Worklog API` console window (or `Stop-Process` on the `dotnet`/`TimesheetApp.Api`
   process). Confirm it is gone.
2. Confirm nothing holds the file (Resource Monitor, or `handle.exe` if available).
3. Copy the **current** live set aside — `timesheet.db`, `timesheet.db-wal`, `timesheet.db-shm` — into a
   dated folder. This is the manual equivalent of the `.pre-restore_*.bak` and it is the only undo.
4. Delete `timesheet.db`, `timesheet.db-wal`, `timesheet.db-shm` from the live path.
   **Deleting the two sidecars is the load-bearing step** — a stale `-wal` from the replaced database is
   replayed by SQLite over the restored file on the next open (`SqliteOnlineBackup.cs:33-37`;
   `WalBackupSafetyTests.cs:94-114`).
5. Copy the chosen `timesheet_{stamp}.db` to the live path and rename it to `timesheet.db`. A plain file
   copy is correct **here specifically** because backup artifacts are `journal_mode=DELETE`, one
   self-contained file (`SqliteOnlineBackup.cs:52`).
6. Start the API (`deploy-local.bat`). Confirm the startup banner prints the expected DB path
   (`Program.cs:57-60`).
7. Verify: log in, open Log Work for a known week, confirm expected rows.

Optionally also decide the fate of the now-callerless `RestoreAsync`/`ListBackups` in Core (keep as the
tested reference implementation, or delete). That is a separate call, noted not resolved.

**Rough cost.** One markdown file. Zero code, zero test, zero regen, no Angular, no OpenAPI. Under an hour.

**What could go wrong — especially to production data**

The risk is entirely human, and both failure modes are silent:
- **Skips step 1** (stop the API). A copy over a pooled handle either throws, or — per 1.8, unknown —
  succeeds against a file SQLite still believes it owns.
- **Skips step 4's sidecars.** The restore *appears* to work and is then silently reverted or corrupted on
  the next open. This is the exact bug `WalBackupSafetyTests.cs:97` was written to catch in code; a runbook
  has no test.
- **Skips step 3.** Then the restore is unrecoverable. No `.pre-restore` file exists because no code ran.

**What undoing it costs.** The document: delete it, zero cost. A *badly executed* restore: recoverable only
if step 3 was performed. That is the trade — the safety net is human-enforced, whereas in Options B and C
the safety copy is code (`BackupService.cs:121-122`) and cannot be forgotten.

---

### Option B — Offline restore mode in the existing API host (CLI)

**What gets built**

A command branch at the top of `src/TimesheetApp.Api/Program.cs`, before `builder.Build()` (`:169`) and
before Kestrel ever listens — ideally right after config resolution at `:34-36`, so it reuses the exact
same `IAppConfig`/`DbPath` the server would have opened:

```
dotnet run --project src/TimesheetApp.Api -- --list-backups
dotnet run --project src/TimesheetApp.Api -- --restore "D:\backups\timesheet_20260718203000123.db"
```

Files touched:
- `src/TimesheetApp.Api/Program.cs` — one guard block (~10-15 lines) that dispatches and `return`s without
  reaching `app.Run()` (`:284`). `args` is already in scope (`WebApplication.CreateBuilder(args)`, `:10`).
- `src/TimesheetApp.Api/Infrastructure/RestoreCommand.cs` — **new.** Constructs `BackupService` directly
  (it needs only `IAppConfig` + `IClock`, per `BackupService.cs:29-33`), runs `ListBackups()` or
  `RestoreAsync()`, prints the resulting `.pre-restore_*.bak` path, returns an exit code.
- `src/TimesheetApp.ApiTests/` — tests for the arg parsing and the exclusivity guard.
- `docs/runbooks/restore-database.md` — the operator-facing procedure (stop the server, run the command,
  start the server, verify).

**The exclusivity guard is the whole design, not a detail.** "No requests in *this* process" is guaranteed
by construction — Kestrel never starts. It does **not** guarantee no *other* process holds the database.
Two candidate guards:

- *Port bind probe* — try to bind the configured port; if it binds, nothing is serving. This is a **proxy,
  not proof**: a second instance on a different port defeats it. [ASSUMED correlation.]
- *SQLite `BEGIN EXCLUSIVE` probe* — open the live `.db` with `Pooling=false` and attempt an exclusive
  transaction. If it fails with `SQLITE_BUSY`, someone else is in there. This asks the **authority** rather
  than a proxy, and I would recommend it over the port probe. It still cannot detect a process that holds
  the file open without an active transaction, so it is a strong signal, not a proof either.

**Rough cost.** One new file (~60-80 lines), ~15 lines in `Program.cs`, 2-3 tests, one doc. No new route,
so **no OpenAPI regen and no Angular work**. Core untouched. Roughly half a day to a day.

**What could go wrong — especially to production data**

- The command still overwrites the live `.db`. If the guard is wrong or bypassed (`--force`, or an operator
  running it against a live second instance), the corruption risk is identical to a naive admin route.
- The `Program.cs` guard block sits at the top of the host's entry point. A parsing bug that swallows normal
  startup args would stop the server booting — loud and immediately obvious, but it is a change to the one
  file every deployment runs.
- The pre-restore safety copy (`BackupService.cs:121-122`) only runs when `dbPath` exists and is readable
  (`:121`). A restore against a missing/unreadable live DB proceeds with **no safety copy**. Worth an
  explicit operator warning.

**What undoing it costs.** Delete the new file and the `Program.cs` block; nothing else references them, no
contract changes, no client regen. Genuinely cheap to reverse. An *executed* restore is undone by restoring
the printed `.pre-restore_*.bak` — which is the same command, so the undo path is the path.

**Why this is the least-extrapolated option:** it runs `RestoreAsync` in the *only regime its tests cover* —
quiesced, no concurrent connections (`WalBackupSafetyTests.cs:87`, `TinyDb.cs:86`). It does not extend the
code into the untested regime described in 1.8/1.9.

---

### Option C — Admin routes behind a maintenance-mode gate

**What gets built**

1. `IMaintenanceState` singleton + a middleware registered **outermost**, before `ExceptionMapper`
   (`Program.cs:215`), returning 503 for every `/api/*` and `/hubs/*` request while engaged.
2. `POST /api/ops/maintenance/enter` and `/exit` (AdminPolicy), in `SettingsEndpoints.MapOpsEndpoints`
   (`SettingsEndpoints.cs:1000`).
3. `GET /api/ops/backup/list` → `IBackupService.ListBackups()` mapped to a new `BackupInfo` contract in
   `src/TimesheetApp.Api/Contracts/`.
4. `POST /api/ops/backup/restore` — **409 unless maintenance is engaged**, then quiesce (wait for the
   in-flight request count to reach zero), then `RestoreAsync`.
5. Angular: backup list + restore confirm dialog + maintenance banner in
   `src/timesheet-web/src/app/pages/settings/`, plus `worklog.service.ts` methods
   (alongside `runBackup` at `:1195`).
6. **OpenAPI regen** → new generated functions under `src/timesheet-web/src/app/api/fn/ops/`.

**Rough cost.** The largest by a wide margin. New middleware + state service + in-flight accounting,
2 contracts, 4 routes, Angular component + service + generated-client regen, ApiTests + Angular specs.
The only option requiring an OpenAPI regen and front-end work. Several days.

**What could go wrong — especially to production data.** This is the option that *looks* safest and is the
hardest to actually make safe. To be correct it must guarantee **zero** open SQLite handles at the instant
`DeleteWithSidecars` runs (`SqliteOnlineBackup.cs:41`). That requires all of:

- 503-gate every route **and** the hub — and note SignalR clients **auto-reconnect and re-query on reconnect**
  (`DataHub.cs:20-23,39`), so they are a self-retrying source of DB opens throughout the window;
- wait for in-flight requests to complete;
- `ClearAllPools()`;
- **and that nothing else in the process re-opens the DB.** This last one is not satisfiable by request
  accounting alone: the codebase already contains fire-and-forget background work that is invisible to a
  request counter — the retention run at `SettingsEndpoints.cs:1027` (`_ = Task.Run(...)`, holding
  `BEGIN IMMEDIATE` across six bulk deletes) and every `DbBackupHelper` call on bulk writes
  (`Program.cs:103`).

Miss any one and the result is 1.9: an auto-created empty database at the production path, or a half-written
file — **triggered by a browser click, on production data, in the regime the code has no test for.**

There is also an unresolved after-state: the WPF flow told the user to restart
(`SettingsViewModel.cs:299`). A web restore has no equivalent, so every DI singleton continues against a
swapped file. Doing this properly means forcing a process exit after restore — which reintroduces "who
restarts the server", i.e. Option A's operator, minus the control.

**What undoing it costs.** Expensive. Unwind middleware, state service, 4 routes, 2 contracts, Angular UI,
and regenerate the client. And once admins have a restore button, removing it is a capability regression
that needs explaining — the code is reversible, the expectation is not.

---

## Part 3 — Which I would pick, and the trade-off I am not resolving

**I would pick Option B, with Option A's runbook written as part of it — not instead of it.** Four reasons:

1. **It is the only option that obtains quiescence by construction rather than by enforcement.** Options A
   and C both have to *achieve* "nobody has the database open" — A through operator discipline, C through
   middleware that must be exhaustive against background work it does not know about. B *starts* quiesced:
   Kestrel never listens.
2. **It does not extend the code into an untested regime.** `RestoreAsync` is tested only with no concurrent
   connections and `Pooling=false` (1.8). B runs it exactly there. C runs it somewhere no test has been.
3. **It matches a ruling this project already made.** `settings.component.ts:45-60` establishes that
   host-level state changes belong at the host with a restart, not in a browser tab. Restore is the most
   host-level operation in the system.
4. **Cost and reversibility.** No contract change, no client regen, deletable in one commit.

**The trade-off I am NOT resolving, because it is not mine to resolve:**

Option B requires physical or RDP access to the host, someone comfortable at a command line, and taking the
app down. If the person who will actually need a restore at 9am on a Monday is not that person, then B is
safe code that never gets used and the real-world outcome is *"we never restore."*

Option C is the only option a non-technical admin can execute unaided — and it buys that reachability by
placing a destructive operation one click from production data, in the regime the code is least tested in.

That is an **operational-capability vs data-safety trade**, not a technical one. It turns on who actually
operates this system and how fast a restore must be possible. I do not have that information and will not
assume it.

**A middle path exists if the answer is "the operator is non-technical":** ship Option B now (cheap, safe,
unblocks M10), and add **only** `GET /api/ops/backup/list` as a read-only route so the web UI can *show*
which backups exist and tell the operator the exact command to run. Listing is non-destructive — it reads a
directory (`BackupService.cs:71-87`) and never touches the database. That closes the visibility half of the
gap at near-zero risk, and leaves the destructive half at the host. It does require an OpenAPI regen, so it
is not free.

**Regardless of which option is chosen, §1.7 still needs a decision:** `BackupFolderPath` is `null`, so no
backups are being written today, and M10 removes the only caller of the daily auto-backup
(`App.xaml.cs:63`). A restore path with nothing to restore from is ceremony.
