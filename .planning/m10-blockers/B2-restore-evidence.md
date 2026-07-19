# B2-restore — Evidence (gathering only, no proposal)

Scope: establish what `RestoreAsync` does today, what safety copies exist, what "open connections"
means concretely for the API host, and whether a backup LIST route exists. No solution design in this
file — that is explicitly out of scope for this pass.

---

## 1. What `RestoreAsync` actually does

`BackupService.RestoreAsync(string backupPath)` — `src/TimesheetApp.Core/Services/BackupService.cs:97-126` [VERIFIED]

Step by step, exactly as written:

1. **Validate** (synchronous, before any I/O): `backupPath` must be non-empty and `File.Exists` —
   else throws `FileNotFoundException` (`BackupService.cs:99-100`).
2. **Self-restore guard**: if `Path.GetFullPath(backupPath)` case-insensitively equals
   `Path.GetFullPath(_config.DbPath)`, throws `InvalidOperationException` before touching anything
   (`BackupService.cs:106-108`). Rationale in the code comment: the live DB would otherwise be both the
   safety-copy source and the restore destination, destroying it.
3. Everything past this point runs inside `Task.Run(...)` (off the caller's thread) — `BackupService.cs:113-125`:
   a. `SqliteOnlineBackup.ClearPools()` → `Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()`
      (`SqliteOnlineBackup.cs:83`). Doc comment: *"Release every pooled connection handle. With pooling
      on, a closed `SqliteConnection` keeps the file open; the .db cannot be replaced until the pool
      lets go of it."*
   b. **Pre-restore safety copy** of the CURRENT live `.db` (only if it exists) to
      `{dbPath}.pre-restore_{yyyyMMddHHmmssfff}.bak`, via `SqliteOnlineBackup.Copy` — i.e. the SQLite
      online-backup API (`sqlite3_backup_*`), not `File.Copy` (`BackupService.cs:118-122`).
   c. **Overwrite**: `SqliteOnlineBackup.Copy(backupPath, dbPath)` — copies the CHOSEN backup file onto
      the live `.db` path, again via the online-backup API (`BackupService.cs:124`).

`SqliteOnlineBackup.Copy(source, dest)` (`src/TimesheetApp.Core/Data/SqliteOnlineBackup.cs:39-53`) itself:
1. `DeleteWithSidecars(dest)` — `File.Delete(dest)`, `File.Delete(dest + "-wal")`, `File.Delete(dest + "-shm")`
   (`SqliteOnlineBackup.cs:85-90`). Comment: deleting the destination's sidecars is "the load-bearing
   half" of the operation — a stale `-wal` left over from the REPLACED database would otherwise be
   replayed by SQLite over the newly restored file on next open.
2. Opens `source` (ReadWrite) and `dest` (ReadWriteCreate) as fresh, **`Pooling = false`** one-shot
   connections (`SqliteOnlineBackup.cs:92-105` — `Open()` helper).
3. `source.BackupDatabase(dest)` — the native SQLite backup API call.
4. `PRAGMA journal_mode=DELETE;` on `dest`, converting the artifact back to a single self-contained
   file (not WAL-marked), so it doesn't need `-wal`/`-shm` siblings to be reopened later.

**Doc-comment on `Copy`, load-bearing precondition (`SqliteOnlineBackup.cs:33-38`)**: *"Callers must
ensure no handle is still open on the destination (see `ClearPools`) — otherwise this throws rather
than silently corrupting it."* I could not find, anywhere in the test suite, a test that actually
exercises this claim under the API's Server connection profile (see §3 and §5 below) — it is asserted
in a comment, not demonstrated against a pooled/WAL connection held open at call time.

---

## 2. The safety copy — two distinct mechanisms, do not conflate

There are **two separate backup/safety-copy mechanisms** in Core, both real, both online-backup-based,
serving different purposes:

| | `DbBackupHelper` (XC-10) | `BackupService.RestoreAsync`'s pre-restore copy (BK-05) |
|---|---|---|
| File | `src/TimesheetApp.Core/Services/DbBackupHelper.cs` | `src/TimesheetApp.Core/Services/BackupService.cs:118-122` |
| Trigger | Runs automatically before every bulk write (Smart Fill apply, DefaultTask sync, retention, team bootstrap) | Runs only inside `RestoreAsync`, immediately before the destructive overwrite |
| Destination | `{dbPath}.{stamp}.bak`, next to the live `.db` (`DbBackupHelper.cs:38`) | `{dbPath}.pre-restore_{stamp}.bak`, next to the live `.db` (`BackupService.cs:122`) |
| Retention | Keeps newest 10, best-effort prune (`DbBackupHelper.cs:20,48-67`) | **Not pruned at all** — every restore leaves one more `.pre-restore_*.bak` forever; no code path deletes these |
| **API host registration** | **The REAL `DbBackupHelper`** — `src/TimesheetApp.Api/Program.cs:103`: `AddSingleton<IDbBackupHelper, DbBackupHelper>()`. **Not** the `NoOpDbBackupHelper` class that exists in Core (`src/TimesheetApp.Core/Services/NoOpDbBackupHelper.cs`) and whose own doc-comment claims *"the `IDbBackupHelper` the API host registers. Does nothing."* — that claim is FALSE against the actual `Program.cs` registration as of this read. Already flagged once before, independently, in `.planning/m10-audit/B1-refute.md:46` [VERIFIED there too]. | N/A — one-shot, invoked only from within `RestoreAsync` itself |

Net: every Smart Fill apply / DefaultTask sync on the live API host today already triggers a full
online-backup snapshot of the entire database via `SqliteOnlineBackup.Copy`, using the SAME code path
`RestoreAsync` uses for both its safety copy and its final overwrite. That path is therefore exercised
under real concurrent server load routinely — but only in the "copy a live db to a NEW file" shape, never
in the "delete + replace the file every pooled connection points at" shape that `RestoreAsync` alone
does (see §3).

Separately: `BackupService.ListBackups()`/`BackupNowAsync()`/`AutoBackupIfDueAsync()` write to
`{BackupFolderPath}/timesheet_{stamp}.db`, pruned to `BackupKeepCount` (default 30) — this is the
user-facing "Backup now" folder, a THIRD location, distinct from both `.bak` mechanisms above.
On the config file this environment currently resolves against (`%APPDATA%\TimesheetApp\appsettings.json`,
read directly, values only — not opened as a database) [VERIFIED]:
```
"BackupFolderPath": null,
"AutoBackupEnabled": false,
"BackupKeepCount": 30,
```
`BackupFolderPath` is unset. Per `BackupService.BackupToFolderAsync` (`BackupService.cs:40-57`),
`string.IsNullOrWhiteSpace(folder)` short-circuits to a no-op returning `null` — so on this
configuration, `POST /api/ops/backup/run` currently produces **no backup file at all** (silently:
`SettingsOpsResult(null)`, `200 OK`). This is a fact about the currently-resolved config, not
necessarily the production one — I did not locate any other `appsettings.json` in the repo for the API
host (see §4), so this is the only config file identified.

---

## 3. What "open connections" means for the API host specifically

**Connection policy** — `src/TimesheetApp.Core/Data/SqliteConnectionFactory.cs:28-88`, instantiated for
the API with `SqliteProfile.Server` explicitly (`src/TimesheetApp.Api/Program.cs:75-76`):

```
builder.Pooling = true;
builder.DefaultTimeout = 5;
pragmaSql = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000; PRAGMA synchronous=NORMAL;"
```
(`SqliteConnectionFactory.cs:58-66`) [VERIFIED]

Contrast with `SqliteProfile.Desktop` (WPF): `Pooling = false`, `journal_mode=DELETE`
(`SqliteConnectionFactory.cs:68-72`).

**Connection lifetime pattern**: every repository (`BacklogRepository`, `DefaultTaskRepository`,
`HolidayRepository`, `PcaContactRepository`, etc. — confirmed by grep across
`src/TimesheetApp.Core/Data/Repositories/`) opens a connection per method call and disposes it at the
end of that call: `using var c = _factory.Create();` [VERIFIED, pattern repeated at every call site
grepped]. No repository holds a connection open across requests or across multiple operations.

**What "open connections" concretely means here, given `Pooling = true`**: `Microsoft.Data.Sqlite`'s ADO.NET
pool keeps the underlying native `sqlite3` file handle alive after `Dispose()` returns it to the pool,
rather than closing it, so it can be handed back out to the next `Create()` call without re-opening the
file. This is *process-wide* idle-handle retention between requests — not one request holding a
connection open indefinitely. `Program.cs:67-70`'s own comment states the reason `Server` profile is used
explicitly rather than relying on the constructor default: *"a plain `AddSingleton<IConnectionFactory,
SqliteConnectionFactory>()` takes [the Desktop] default SILENTLY, and then: (1) journal_mode=DELETE, so
readers block writers and the API serialises every request."*

`SqliteOnlineBackup.ClearPools()` calls the static, **process-global**
`SqliteConnection.ClearAllPools()` (`SqliteOnlineBackup.cs:83`). I could not verify from source in this
repo (Microsoft.Data.Sqlite is a NuGet dependency, not vendored) exactly what `ClearAllPools()` does to
a connection that is **currently checked out** (i.e., a request mid-flight, inside its own
`using var c = _factory.Create(); ... ` block, at the exact moment `RestoreAsync` runs) versus one that
is merely idle in the pool. The two plausible behaviors — (a) only idle pooled connections are closed,
in-flight ones are unaffected until their own `Dispose()`, or (b) all handles including in-flight ones
are forcibly closed — lead to materially different failure modes for a concurrent reader/writer, and I
am not certifying either. **[ASSUMED — flagged, not verified]**: general ADO.NET provider pooling
convention is (a); I found nothing in this codebase that proves it for `Microsoft.Data.Sqlite`
specifically.

Independent of that question, **`DeleteWithSidecars` calls `File.Delete` directly on the live `.db`
path** (`SqliteOnlineBackup.cs:87-89`). On Windows, whether this throws (file still locked by another
process/handle) or silently succeeds while another handle is still validly open on the old file depends
on the share-mode flags the SQLite Win32 VFS used when it opened that handle — I could not determine
this from the repo (the native `e_sqlite3` library is a NuGet-vendored binary, not source in this repo,
and I did not run anything to observe it empirically, per the read-only constraint). The class doc
comment's own claim — *"otherwise this throws rather than silently corrupting it"* — is asserted, not
demonstrated by any test I found (see §5).

**Journal mode**: `WAL` (`SqliteConnectionFactory.cs:65`). Per `SqliteOnlineBackup.cs:17-19`'s own
doc comment, committed pages under WAL sit in the `-wal` sidecar until checkpoint — which is exactly why
the whole class exists (a raw `File.Copy` of `.db` alone would miss them). This applies symmetrically to
`RestoreAsync`'s own two `Copy` calls (the pre-restore safety copy of the live db, and the final
overwrite reading the chosen backup file) — both go through the online-backup API precisely because the
live source may itself be in WAL with uncheckpointed pages.

**Design-time acknowledgment that restore-while-serving is unsound**: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md:351` [VERIFIED], written when WAL was adopted for the server profile:
> *"Restore must be offline. With pooling on, `File.Copy(backup, dbPath, overwrite:true)` throws
> `IOException` on the pooled handles, or replays a stale `-wal` onto the new file. Restore-while-serving
> cannot be done by file copy at all."*
This predates and motivates the current `SqliteOnlineBackup`-based implementation, but the document's
own conclusion is that restore is fundamentally an offline operation under this profile — it does not
claim the online-backup-API version (what exists today) makes restore-while-serving safe; it only
explains why the naive `File.Copy` version was unsafe.

---

## 4. Hosting/deployment shape (bears on what "the API host" means operationally)

- **Single process, single port.** `deploy-local.bat:1-9` [VERIFIED]: *"single-process local run... the
  UI AND /api are served from ONE process, ONE port, ONE origin"*, started via `dotnet run -c Release
  --urls http://localhost:5080` (`deploy-local.bat:38`).
- No `web.config`, no `appsettings.Production.json`, no IIS hosting-model configuration found anywhere
  under `src/TimesheetApp.Api/` (searched by filename glob) [VERIFIED — absence]. The only
  `launchSettings.json` present is the dev-run profile at `src/TimesheetApp.Api/Properties/launchSettings.json`.
- No graceful-shutdown, drain, or maintenance-mode mechanism found: grepped `src/TimesheetApp.Api` for
  `IHostApplicationLifetime`, `ApplicationStopping`, `ShutdownTimeout`, `graceful`, `maintenance`,
  `drain` — **no matches** [VERIFIED — absence]. `/health` (`Program.cs:237-240`) is a static
  `Results.Ok(new { status = "ok" })` with no dependency on DB state, connection count, or any readiness
  signal — it cannot be used today to detect "no active requests" or "no open connections."
- No admin/maintenance flag store beyond `ISettingsRepository`'s generic key/value scalars
  (`AdminEndpoints.cs:10-45`), which is DB-backed itself (reading it requires an open connection) and is
  not wired to gate request admission anywhere.
- `RetentionService`'s comment (`Program.cs:1015-1021`, referenced via `SettingsEndpoints.cs`) already
  establishes precedent that a single admin action can serialize/block every other writer app-wide (one
  `BEGIN IMMEDIATE` across six DELETEs) — the code's own mitigation for that is to run it as a
  fire-and-forget background `Task.Run`, returning `202 Accepted` immediately, specifically so it does
  not block the request pipeline. No equivalent pattern exists for restore.

---

## 5. Whether a backup LIST route exists

**No.** Grepped `src/TimesheetApp.Api` for `Backup`/`backup`/`Restore`/`restore`
(case-sensitive; case-insensitive scan of `SettingsEndpoints.cs` specifically also checked) — the
complete set of hits:

- `Program.cs:103-104` — DI registrations (`IDbBackupHelper`, `IBackupService`), not routes.
- `SettingsEndpoints.cs:40,43-44` — doc comments (one names the 4 ops routes; one explains why restore
  is excluded).
- `SettingsEndpoints.cs:1051-1059` — the ONE and ONLY backup-related route:
  ```
  api.MapPost("/api/ops/backup/run", async (IClientContext ctx, IBackupService backup) => {
      if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
      return Results.Ok(new SettingsOpsResult(await backup.BackupNowAsync()));
  })
  .RequireAuthorization(AuthSetup.AdminPolicy)
  ```
  This calls `BackupNowAsync()` only. It does not call `ListBackups()` and does not call `RestoreAsync()`.

`IBackupService.ListBackups()` (`BackupService.cs:71-87`, interface at `IBackupService.cs:25-26`)
survives in Core and is fully implemented (parses `timesheet_*.db` filenames in `BackupFolderPath`,
returns newest-first with size/timestamp) — but **has zero callers in `src/TimesheetApp.Api` or
`src/timesheet-web`** [VERIFIED — grep found no match for `ListBackups` under either directory]. The web
app's Settings page (`settings.component.html:352-361`) shows only a "Back up now" button and a
free-text result string (`settings.component.ts:544-551`); there is no list, table, or any rendering of
existing backups anywhere in the Angular tree.

`RestoreAsync` likewise has **zero callers** anywhere outside `src/TimesheetApp/` (WPF) and the test
suite — confirmed independently by `.planning/m10-audit/A8.md` row "Restore" and `.planning/m10-audit/B8.md`
row "BK-05: Restore..." (both already [VERIFIED] in the earlier audit pass; re-confirmed here by my own
grep of `src/TimesheetApp.Api` and `src/timesheet-web/src/app`, same zero-match result).

---

## 6. Test coverage of `RestoreAsync` — what is and is not exercised

`src/TimesheetApp.Tests/Services/BackupServiceTests.cs:187-229` — three `RestoreAsync` tests:
- `Restore_writes_pre_restore_safety_copy_then_swaps_db_contents` — happy path, single-threaded, no
  concurrent connections.
- `Restore_throws_when_backup_unreadable` — the `FileNotFoundException` guard.
- `Restore_rejects_restoring_live_db_onto_itself` — the self-restore guard.

`src/TimesheetApp.Tests/Services/WalBackupSafetyTests.cs:56-114` — two tests that use `RestoreAsync`
(`Backup_during_an_open_write_transaction_in_WAL_survives_restore_intact`,
`Restore_removes_the_stale_wal_sidecar_of_the_replaced_db`) — these DO hold a WAL connection open with
an uncommitted transaction *while backing up*, proving the backup step is safe under those conditions.
The subsequent `RestoreAsync` call in that same test, however, runs only after the WAL connection block
has already been disposed (`using (var live = TinyDb.OpenWal(...)) { ... }` closes before
`RestoreAsync` is called at line 87) — so **no test in the repo calls `RestoreAsync` while any
connection is concurrently open on the destination `.db`**, `Pooling = true` or otherwise. `TinyDb`
(the test helper) is not shown to construct connections via `SqliteProfile.Server`; every test in both
files uses `BackupService` constructed directly with a mocked `IAppConfig`/`IClock`, never going through
`SqliteConnectionFactory` at all for the destination side. **The exact scenario the API host would
present at restore time — a live `Pooling=true`/WAL connection pool with idle or in-flight handles on
the destination file — is not exercised by any automated test I found.**

---

## 7. Summary of what is established vs. not

**Established [VERIFIED]:**
- `RestoreAsync`'s full algorithm (validate → self-restore guard → `ClearAllPools()` → pre-restore
  safety copy → overwrite via online-backup API → sidecar cleanup + `journal_mode=DELETE` on the result).
- The pre-restore safety copy is real, online-backup-based, unpruned, at `{db}.pre-restore_{stamp}.bak`.
- The API host's connection profile: `Pooling=true`, `journal_mode=WAL`, `busy_timeout=1000`,
  `synchronous=NORMAL`, `DefaultTimeout=5`; connections are opened/disposed per repository call, never
  held across requests.
- `IDbBackupHelper` on the API host is the REAL implementation, not the no-op the class doc-comment
  claims — every bulk write already exercises `SqliteOnlineBackup.Copy` under live concurrent load
  today, though never in the delete-and-replace-the-live-file shape.
- No LIST route, no RESTORE route, no maintenance/drain mechanism, no graceful-shutdown hook anywhere in
  the API host. `ListBackups()` and `RestoreAsync()` both have zero callers outside WPF/tests.
- Deployment is single-process, single-port, `dotnet run`-style; no IIS/web-farm artifacts found.

**Not established — flagged, not guessed:**
- What `SqliteConnection.ClearAllPools()` does to a connection that is checked out (in-flight) versus
  merely idle, specifically for `Microsoft.Data.Sqlite` — no source in this repo, not verified.
- Whether `File.Delete` on the live `.db` throws or succeeds when another process handle is genuinely
  still open on it, under this SQLite build's Win32 VFS share-mode flags — asserted by a code comment,
  not demonstrated by any test.
- Whether the production deployment (as opposed to `deploy-local.bat`'s single-process description)
  ever runs more than one API process/instance against the same `.db` — no evidence found either way;
  only the local dev config and the deploy script were available to inspect.
