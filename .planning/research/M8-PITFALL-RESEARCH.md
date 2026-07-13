# M8 Backend Foundation — PITFALL RESEARCH

> ## ⛔ CORRECTION — 2026-07-12, after execution
>
> **The claim in B3 that Dapper fails *silently* on the renamed column is WRONG, and it was tagged `[VERIFIED]`.**
>
> B3 states: *"rename the column but not the DTO and Dapper **silently returns null** (broken login, no exception)."* That claim propagated into spec §7.7, into the M8.2 plan, and into three task prompts — and it was the **sole justification** for scheduling the `UserRepository` fix into a *later* wave than the migration that renames the column.
>
> Running it says otherwise:
>
> ```
> SqliteException: no such column: windows_username     x24
> table Users has no column named windows_username        x4
> -> 28 tests red
> ```
>
> Silent-null happens only when a `SELECT` **does not name the column**. All six statements in `UserRepository` name it explicitly (`SELECT id, name, windows_username, is_active FROM Users …`), so SQLite throws. Loudly.
>
> **Consequence:** a schema rename and the SQL that reads it are **atomically coupled** — any commit holding one without the other leaves a red tree, and on a parallel wave hands every sibling agent a broken baseline. Wave 1 was amended to own both.
>
> **Treat the rest of this document accordingly.** A `[VERIFIED]` tag means the author believed they had checked it; it does not mean they executed it. Claims here about *how a failure presents* — silent vs. loud, at build time vs. at runtime — are the ones most likely to be wrong, and they are exactly the ones that change how work is sequenced. Measure before you rely on one.

**Date:** 2026-07-12
**Agent:** Pitfall Research (STEP 4, Mode B)
**Spec under review:** `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md`
**Supersedes:** the 2026-06-21 pitfall report (WPF / SQLite-over-OneDrive, milestone M1 — see git history)
**Stance:** adversarial. This document hunts for what breaks at runtime, not for what the spec got right.

**Tag convention:** `[VERIFIED]` = I read the code / extracted it from the binary / read the library source. `[CITED]` = official doc, URL given. `[ASSUMED]` = inference, flagged as such.

**Environment facts established up front (all `[VERIFIED]`):**

| Fact | How verified |
|---|---|
| Bundled SQLite = **3.41.2** | grep of `e_sqlite3.dll` in `~/.nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.6/runtimes/win-x64/native/` |
| Dependency chain | `Microsoft.Data.Sqlite 8.0.10` → `SQLitePCLRaw.bundle_e_sqlite3 2.1.6` (`obj/project.assets.json`) |
| Test project targets `net8.0-windows`, `ProjectReference`s the WPF project | `src/TimesheetApp.Tests/TimesheetApp.Tests.csproj` |
| Test census | 511 `[Fact]`/`[Theory]` attributes (548 = expanded `[InlineData]` cases) |
| `Users` has **no index, no view, no trigger** on `windows_username` | `src/TimesheetApp/Data/DatabaseInitializer.cs` — only 3 indexes exist, all on `Standup*` |
| Exactly **one** file under `Services/`+`Data/`+`Models/`+`Config/` has a real `using System.Windows` | `ThemeService.cs:1`. The other 5 grep hits are comments. **Spec §1's premise holds.** |

---

## Summary — ranked

| # | Pitfall | Level |
|---|---|---|
| B1 | `File.Copy` backup/restore is **incompatible with WAL + Pooling** — and §8.3 ships it as an admin endpoint | **BLOCKER** |
| B2 | "548 tests move to Core and stay green" is **arithmetically impossible** as written | **BLOCKER** |
| B3 | The `windows_username` rename breaks `TestDb` — the fixture **every** DB test depends on | **BLOCKER** |
| B4 | v10 `RENAME COLUMN` vs. the `CreateTables` DDL + full migration replay on fresh DBs | **BLOCKER** |
| B5 | Smart Fill's carve-out makes `row_version` **lie** → reintroduces the lost update D4 exists to kill | **BLOCKER** |
| H1 | `FallbackPolicy` **blocks the Angular SPA's own static files** → login page unreachable | HIGH |
| H2 | On .NET 8, cookie auth returns **302 → /Account/Login, not 401**. §7.1's contract is wrong out of the box | HIGH |
| H3 | SQLite on a **network share** — WAL is *unsupported* there. On-prem makes it likely. Spec never pins DB location | HIGH |
| H4 | IIS app-pool identity needs write access to the **directory**, not just the `.db` file | HIGH |
| H5 | OneDrive scaffolding is **not inert** on the server — it runs, and under WAL it corrupts | HIGH |
| H6 | `SqliteConnection.ClearAllPools()` in test teardown is **process-global** + xunit runs collections in parallel | HIGH |
| H7 | 3 existing tests **hard-assert the current connection policy**; the ctor change breaks 6 | HIGH |
| M1 | `internal` helpers straddle the Core/WPF split → **hard compile break** on day one | MEDIUM |
| M2 | Complete `UPDATE` inventory — the spec's single template covers only **half** the sites | MEDIUM |
| M3 | `SQLITE_BUSY` under WAL: where `busy_timeout` saves you, and where it does not (Retention) | MEDIUM |
| M4 | Bootstrap password: the race is real only under web-garden/overlapped-recycle; secret-in-git is real always | MEDIUM |
| L1 | §6.1 lists **8** tables, not 7 | LOW |
| L2 | `Cache=Shared` + WAL is explicitly discouraged — don't add it "for concurrency" | LOW |
| L3 | `synchronous=NORMAL` on WAL can lose last commits on power loss — record as a decision | LOW |
| L4 | §1's "one WPF file in `Services/`" — **verified true**, the premise holds | LOW (good news) |
| L5 | §11's `LastNWorkingDays` fix needs **no ctor change** — cheaper than it looks | LOW (good news) |
| L6 | `DAYS LOGGED` has **zero test coverage** — nothing was ever guarding it | LOW |

---

# BLOCKERS

## B1 — `File.Copy` backup/restore is incompatible with WAL + Pooling

**The spec turns on two settings (§5.2: `Pooling=true`, `journal_mode=WAL`) that destroy a documented, load-bearing invariant — and never notices.**

`[VERIFIED]` `src/TimesheetApp/Services/BackupService.cs:9`, verbatim:

> `/// File-level File.Copy is safe at idle — the app uses short connections + journal_mode=DELETE.`

That sentence **is** the safety argument for the entire backup subsystem. §5.2 deletes both of its premises.

**Affected call sites — every one a `File.Copy` of the live `.db`** `[VERIFIED]`:

| Site | What it does | When it runs |
|---|---|---|
| `BackupService.cs:47` | `File.Copy(dbPath, backupPath, overwrite:true)` | "Backup now" / auto-once-per-day |
| `BackupService.cs:103` | safety copy `{db}.pre-restore_{stamp}.bak` | before restore |
| `BackupService.cs:105` | **`File.Copy(backupPath, dbPath, overwrite:true)`** | `POST /api/ops/backup/restore` (§8.3) |
| `DbBackupHelper.cs:36` | `File.Copy(dbPath, backupPath, overwrite:false)` | **before every bulk write** — Smart Fill apply, DefaultTaskSync, TeamBootstrap, Retention |
| `PruneArchiver.cs:164` | `File.Copy(dbPath, snapPath, overwrite:false)` | the pre-prune snapshot **Retention gates its DELETEs on** |

**Why WAL breaks it** `[CITED]` — https://www.sqlite.org/wal.html:

> "The WAL file is part of the persistent state of the database and should be kept with the database if the database is copied or moved. If a database file is separated from its WAL file, then transactions that were previously committed to the database might be lost, or the database file might become corrupted."

Copying only `timesheet.db` on a WAL database yields a backup **missing every un-checkpointed commit** — silently. `PruneArchiver` then reports "snapshot OK" (it only checks `Exists && Length > 0`, `:166`) and `RetentionService` proceeds to **permanently delete the originals**. That is data loss behind a green light.

**Why Pooling breaks restore** `[CITED]` — https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings (the `Pooling` keyword, added in 6.0, **default `True`**), plus the documented caveat: *"The database file may still be locked after you close a connection… clear the pool to release the lock"* (`SqliteConnection.ClearAllPools()`).

`File.Copy(backupPath, dbPath, overwrite: true)` against a file held open by pooled connections → `IOException` on Windows. And in the case where it *doesn't* throw, the stale `-wal`/`-shm` sidecars belonging to the **old** database are still on disk and get replayed onto the **new** file → corruption.

**Restore-while-serving cannot be done by `File.Copy` at all.** There is no ordering of `ClearAllPools()` + copy that is safe against a concurrent request opening a connection.

### Fix required before coding
1. Replace file-copy backup with SQLite's online backup: `SqliteConnection.BackupDatabase(dest)`, or `VACUUM INTO 'path'` (needs SQLite ≥ 3.27 — bundled is **3.41.2** `[VERIFIED]`, so both are available).
2. Restore must be an **offline** operation: drain/stop the app pool → `ClearAllPools()` → delete `-wal` + `-shm` → copy → restart. Or stage the backup to a temp path and swap it in at next startup. Either way it is **not** a plain HTTP request handler.
3. Add a backup/restore row to §13's porting-surface table. It is absent, which is how this got missed.

---

## B2 — "548 existing tests move to Core and stay 100% green" is arithmetically impossible

§9 and the §5.3 acceptance gate both assert this. It cannot happen.

`[VERIFIED]` `src/TimesheetApp.Tests/TimesheetApp.Tests.csproj` targets **`net8.0-windows`** and `ProjectReference`s the WPF project. A large fraction of its tests exercise WPF ViewModels and real XAML.

`[VERIFIED]` census of `[Fact]`/`[Theory]` attributes:

| Group | Attrs | Can move to a `net8.0` Core test project? |
|---|---|---|
| `Views/` — 7 files, all `[Collection("WpfSta")]`, STA threads, real XAML load | **10** | ❌ WPF-bound |
| `ViewModels/` — 14 files (`SettingsViewModelTests` 41, `TimesheetViewModelTests` 20, `TaskListViewModelTests` 18, `RequestEditorViewModelTests` 18, `RequestsViewModelTests` 15, `MainViewModelTests` 14, `ReportsViewModelTests` 11, `GanttModelTests` 10, `DailyReportViewModelTests` 10, `SmartInputPanelVmTests` 9, `TimesheetRowVmTests` 7, `TeamFilterViewModelTests` 6, `UsersViewModelTests` 4, `CrossTabSyncTests` 2) | **~185** | ❌ WPF-bound |
| `DependencyInjectionTests` — resolves the **`App.xaml.cs`** container | **4** | ❌ WPF-bound |
| `Services/` + `Data/` + `Models/` + `Config/` + `SmokeTests` | ~312 | ✅ |

→ **≈200 of 511 attributes (~40%) cannot move to Core.**

**Worse: the spec's own §11/§12 delete tests that are part of the 548.**
- §11 "Delete `BuildPlan`" → `ViewModels/SmartInputPanelVmTests.cs` (**9 attrs**) loses its subject.
- §12 drop `SelectUserDialog` → `Views/SelectUserDialogLoadTests.cs` (**1 attr**) loses its subject.
- §11 moving `DAYS LOGGED` into `ReportAggregator` relocates its (nonexistent — see **L6**) coverage.
- **B3** breaks a further set outright.

### Fix required before coding
Restate the gate honestly. Suggested shape:

> Split into `TimesheetApp.Core.Tests` (net8.0) + `TimesheetApp.Tests` (net8.0-windows, WPF VMs/Views). Post-slice count = 548 − *D* + *N*, where *D* = tests whose subject was deliberately deleted (**enumerate them**) and *N* = new concurrency/auth/API tests. **Invariant: no surviving test may have its assertion weakened.**

Leaving the gate at "548/548" guarantees it is red on day one — and the path of least resistance from a red gate is to delete tests until it's green. That is the failure mode to design against.

---

## B3 — The `windows_username` rename breaks `TestDb`, the fixture every DB test depends on

**First, the good news — the SQL itself is safe.** Answering the brief directly:

- `ALTER TABLE … RENAME COLUMN` requires SQLite **3.25.0+**. Bundled: **3.41.2** `[VERIFIED]`. ✅
- `[CITED]` https://www.sqlite.org/lang_altertable.html: *"The column name is changed both within the table definition itself and also within all indexes, triggers, and views that reference the column."* It does **not** fail on an indexed column — it rewrites the index. The **only** failure mode is: *"If the column name change would result in a semantic ambiguity in a trigger or view, then the RENAME COLUMN fails with an error and no changes are applied."*
- `[VERIFIED]` `DatabaseInitializer.cs` declares **no views and no triggers**, and **no index on `windows_username`** — the only three indexes are `ix_standup_user_date`, `ix_standup_date`, `ix_standup_issue_entry`.

→ **The rename is not blocked at the SQL layer.** Clean answer: no.

**The blocker is in C#.**

| Site | What breaks | Loud or silent? |
|---|---|---|
| `[VERIFIED]` `TestDb.cs:54` — `INSERT INTO Users(name, windows_username, is_active)` | `TestDb` is the **shared fixture for every repository + service integration test**. After the rename: `SqliteException: no such column: windows_username` → **the entire DB-backed test suite dies at once.** | LOUD |
| `[VERIFIED]` `DatabaseInitializerTests.cs:144` — `Assert.Contains("windows_username", pragma_table_info('Users'))` | Asserts the old name by design. | LOUD |
| `[VERIFIED]` `UserRepository.cs:88` — `private sealed class UserRaw { public string? windows_username { get; set; } }` and `:81` `MapUser(r) => new(..., r.windows_username, ...)` | Dapper binds **by column name**. Rename the column but not the DTO property → the property stays `null` → **`GetByUsernameAsync` returns a `User` whose username is silently null.** This is the dangerous one — no exception, just a broken login. | **SILENT** |
| `[VERIFIED]` `Models/Entities.cs:6` — `record User(int, string, string? WindowsUsername, bool)` | Public record shape. Consumers: `EntitiesTests:24-25`, `UsersViewModelTests:40`, `CurrentUserServiceTests` (×4), `MainViewModelTests:104,121`, plus two hand-rolled fakes implementing `SetWindowsUsernameAsync` (`BacklogContinuationServiceTests:38`, `StandupServiceTests:26`). | LOUD |

**Spec correction:** §6.2 says *"`windows_username` appears in **5** SQL statements in `UserRepository`"*. `[VERIFIED]` it is **6** — `UserRepository.cs` lines 18, 26, 34, 42, 51-52, 61. And the blast-radius paragraph omits `TestDb.cs` entirely, which is the single highest-impact site.

---

## B4 — v10 `RENAME COLUMN` vs. the `CreateTables` DDL, and full migration replay on fresh DBs

`[VERIFIED]` `DatabaseInitializer.InitializeAsync()`:

```
CreateTables(conn, tx);     // CREATE TABLE IF NOT EXISTS Users (... windows_username TEXT ...)
RunMigrations(conn, tx);    // for (var step = current; step < migrations.Length; step++) → replays ALL steps on a fresh DB
```

On a **fresh** database `user_version = 0`, so **every** migration step replays — which is exactly why `CreateTables` still creates the legacy `Requests` table (gated on `version < 6`, `:190-212`): so the v6 rename has something to act on.

A v10 step doing `ALTER TABLE Users RENAME COLUMN windows_username TO username` therefore **works today** on both fresh and existing DBs, because `CreateTables` hands it a `Users.windows_username` to rename.

**The trap:** this permanently requires the DDL at `DatabaseInitializer.cs:43` to keep saying `windows_username` — forever. The moment someone "tidies up" and changes that line to `username`:
- **existing** installs: fine (table exists, `IF NOT EXISTS` no-ops)
- **fresh** installs: `ALTER TABLE Users RENAME COLUMN windows_username` → **`no such column`** → startup fails

A bug that reproduces **only on new machines**. And §6.2's own argument ("leaving `Windows` in the name is how stale vocabulary calcifies") is precisely the pressure that will provoke that edit.

### Fix
Either (a) gate the `Users` DDL on `user_version < 10` the way `Requests` is gated, or (b) make the v10 step defensive — check `pragma_table_info('Users')` for `username` before renaming. Add a test that runs `InitializeAsync()` on a **fresh** DB *and* on a seeded **v9** DB, twice each.

### Rollback state (asked directly)
`[VERIFIED]` **Good news — and the spec should state it rather than rely on it by accident.** All of `InitializeAsync` runs inside **one** transaction (`using var tx = conn.BeginTransaction()`), SQLite DDL is transactional, and `PRAGMA user_version` is a journaled write to the DB header. A mid-migration failure therefore **rolls back everything, including the version bump**. The DB is never left half-migrated.

⚠️ **But**: `PRAGMA journal_mode=WAL` **cannot be set inside an open transaction.** It must be issued on connection open in `SqliteConnectionFactory.Create()` — which is where the current code puts `journal_mode=DELETE` (`:45`), so the pattern is already right. If anyone relocates the pragma into the initializer, it will **silently no-op** and you will be on DELETE journal in production, wondering why WAL "didn't help".

---

## B5 — Smart Fill's carve-out makes `row_version` lie

§6.1.1 declares Smart Fill *"a deliberate carve-out… its writes are not version-checked per cell."* It does **not** say whether they still **bump** the version. If they don't, the scheme has a hole big enough to drive D4 through:

1. User A opens the week grid. Cell `(taskX, Tue)` is at `row_version = 3`.
2. User B runs Smart Fill; it overwrites that cell's hours to `8.0`. No bump → still `row_version = 3`.
3. User A edits the cell, sending `expectedVersion = 3`. **It matches.** The update succeeds.
4. **Smart Fill's write is silently overwritten.** No 409. No warning.

That is precisely the lost update D4 exists to eliminate, reintroduced through the exemption.

`[VERIFIED]` the single upsert statement is shared by both paths — `TimeLogRepository.cs:129-132`:
```sql
INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at)
VALUES(@UserId, @TaskId, @WorkDate, @Hours, @CreatedAt)
ON CONFLICT(user_id, task_id, work_date) DO UPDATE SET hours = excluded.hours;
```
…called by `UpsertAsync` (single cell) **and** `UpsertBatchAsync` (`:103-114`, Smart Fill's atomic batch).

### Fix
State the rule explicitly: **"not version-*checked*" ≠ "not version-*bumped*". Every write to a versioned table bumps `row_version`, without exception.** Only the `WHERE row_version = @expected` predicate is optional.

Related, and also missing from the spec: §6.1.1's row *"`expectedVersion = null` + row **present** → 409"* must be implemented as **one statement**, not `SELECT` → `if (exists) 409` → `INSERT`. The latter is a TOCTOU race that the concurrency feature would then lose to. SQLite supports the single-statement form:

```sql
-- expectedVersion = N (update path)
INSERT INTO TimeLogs(...) VALUES(...)
ON CONFLICT(user_id, task_id, work_date) DO UPDATE
   SET hours = excluded.hours, row_version = row_version + 1
 WHERE TimeLogs.row_version = @expected;   -- 0 rows affected → 409

-- expectedVersion = null (insert path)
INSERT INTO TimeLogs(...) VALUES(...)
ON CONFLICT(user_id, task_id, work_date) DO NOTHING;   -- 0 rows affected → 409
```
Both report `changes() == 0` on conflict, which Dapper's `ExecuteAsync` returns directly. (Inside `DO UPDATE SET`, bare `row_version` is the **existing** row's value; `excluded.` is the would-be-inserted row.)

---

# HIGH

## H1 — `FallbackPolicy = DefaultPolicy` blocks the Angular SPA's own static files

`[CITED]` https://learn.microsoft.com/en-us/aspnet/core/security/authorization/secure-data — verbatim:

> "The fallback authorization policy applies to all requests that don't explicitly specify an authorization policy. For requests served by endpoint routing, the policy applies to any endpoint that doesn't specify an authorization attribute. **For requests served by other middleware after the authorization middleware, such as static files, the policy applies to all requests.**"

So with §8.1's `o.FallbackPolicy = o.DefaultPolicy;`:

| Surface | Blocked? | Consequence |
|---|---|---|
| `index.html`, `main.js`, `styles.css` (Angular) | ❌ **401** if `UseStaticFiles()` runs **after** `UseAuthorization()` | **The user can never reach the login page.** Bootstrap deadlock. |
| `MapFallbackToFile("index.html")` (SPA deep links) | ❌ **401** | It **is** an endpoint → fallback policy applies |
| `POST /api/auth/login` | ❌ **401** | Spec never says `[AllowAnonymous]` |
| `MapHealthChecks("/health")` | ❌ **401** | It is an endpoint |
| `/hubs/data` incl. `/negotiate` | ✅ blocked — **and that is correct** | No action needed |

### Fix
- `app.UseStaticFiles()` **before** `app.UseAuthorization()` (the default template order — but the SPA fallback is *not*).
- `[AllowAnonymous]` on the login endpoint.
- `.AllowAnonymous()` on `MapFallbackToFile(...)` and on health checks.

## H2 — On .NET 8, cookie auth returns **302**, not **401**. §7.1's error contract is wrong out of the box

`[CITED]` https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz — the docs flag this as a **behaviour change in ASP.NET Core 10**:

> "Starting with ASP.NET Core **10**, known API endpoints no longer redirect to login pages when using cookie authentication. Instead, they return 401/403 status codes."

The spec targets **ASP.NET Core 8**. On 8, `CookieAuthenticationOptions.LoginPath` defaults to `/Account/Login`, and an unauthenticated API call gets a **302** the browser transparently follows → Angular's HTTP interceptor sees a 200-with-HTML or a 404, **never the 401** that §7.1's table promises.

This lands as the *same symptom* as §10's cookie-transport trap ("auth just doesn't work, no error") for a completely different reason — so it will be misdiagnosed as one of those. §10 needs a third item.

### Fix
```csharp
o.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
```

**Also confirmed — no action needed:** `[CITED]` same doc — *"In a browser-based app, cookie authentication allows existing user credentials to automatically flow to SignalR connections. When the browser client is used, no extra configuration is needed."* Combined with §10's same-origin proxy (`proxy.conf.json`), `SameSite=Lax` is **not** a problem for the WebSocket upgrade — SameSite governs only *cross-site* requests, and the proxy makes the hub same-origin. **The spec is right here.** It would break only if Angular were served from a different origin in production, which §10 already rules out.

## H3 — SQLite on a network share: WAL is *unsupported* there

`[CITED]` https://www.sqlite.org/wal.html — verbatim:

> "All processes using a database must be on the same host computer; **WAL does not work over a network filesystem.** This is because WAL requires all processes to share a small amount of memory and processes on separate host machines obviously cannot share memory with each other."

The spec **never states where `timesheet.db` lives on the server.** D1 says "on-prem, internal network, IIS"; the inventory's §D1 says "DB path → connection string" and stops. On-prem shops routinely park application data on a file server, a mapped drive, or a DFS namespace — and this team's **entire current architecture is built around the DB living on a shared folder** (OneDrive). The path of least resistance is to put it on a share again.

If it lands on SMB, WAL is not merely slow: locking over SMB is unreliable and the failure mode is **corruption**, not an exception.

### Fix — make it an enforced constraint, not a hope
1. Write into the spec: **"the `.db` MUST be on a local disk attached to the IIS host."**
2. **Fail startup** if `DbPath` resolves to a UNC path or a network drive. The codebase already has the detection logic — `SharePointDestinationValidator` checks `DriveInfo.DriveType == DriveType.Network` and UNC prefixes `[VERIFIED]`. Reuse it, inverted.

## H4 — IIS app-pool identity needs write access to the **directory**, not just the `.db`

`[CITED]` https://www.sqlite.org/wal.html:

> "The opening process must have write privileges for '-shm' wal-index shared memory file associated with the database, if that file exists, **or else write access on the directory containing the database file** if the '-shm' file does not exist."

WAL creates `timesheet.db-wal` and `timesheet.db-shm` **as siblings** of the DB. The standard deployment reflex — grant `IIS AppPool\TimesheetApp` *Modify* on `timesheet.db` — is **not enough**. SQLite cannot create the sidecars → `SQLITE_CANTOPEN` / "unable to open database file", surfacing **on the first write**, after the app has already started cleanly. Maximally confusing.

Not mentioned anywhere in the spec. Deploy-checklist item: grant *Modify* on the **containing folder**.

## H5 — The OneDrive scaffolding is not inert on the server. It runs.

§5.2 says the XC-08/09/10 scaffolding *"stays in Core while WPF still runs, and is deleted in M8.10."* That reads as "harmless until then". It isn't — because it is **constructor-injected into the shared services the API will reuse verbatim**.

`[VERIFIED]` `TimeLogService.cs:21,24,27-36` — the ctor takes `IDbBackupHelper _backup` and `IJournalWarningSink _journalWarnings`. Same for `DefaultTaskSyncService`, `TeamBootstrapService`, `RetentionService`, `PruneArchiver`.

What each actually does on the API host:

| Scaffolding | Behaviour on the server |
|---|---|
| **XC-10** `DbBackupHelper` | `File.Copy` of the **entire DB** before *every* Smart Fill apply (`DbBackupHelper.cs:36`). Under WAL the copy is torn (**B1**). And it is a whole-file copy **on the request path**, for 10–50 users. |
| **XC-09** journal check | Looks for `{db}-journal`. **In WAL mode that file never exists** → the check passes forever. A safety net reporting "all clear" because it is looking for the wrong file. Silent. |
| **XC-08** conflict-copy scan | `RetentionService` step (a) **aborts the whole run** if `FindConflictCopies` > 0. On a server it finds none — so Retention's guard chain is one guard shorter than it reads. |

### Fix
§5.2 must state what the **API profile injects**: a no-op `IJournalWarningSink`, and either a no-op or an **online-backup-based** `IDbBackupHelper`. "Stays in Core" ≠ "is harmless".

## H6 — `ClearAllPools()` in test teardown is process-global; xunit parallelises collections

`[VERIFIED]` **three** teardowns call the process-wide `SqliteConnection.ClearAllPools()`:
- `TestDb.cs:114` (the shared fixture)
- `DatabaseInitializerTests.cs:150`
- `SqliteConnectionFactoryTests.cs:101`

`[VERIFIED]` xunit parallelises test **collections** by default; the only opt-out in this repo is `Views/WpfStaCollection.cs:13` → `[CollectionDefinition("WpfSta", DisableParallelization = true)]`. Everything else runs concurrently.

Today `Pooling=false` makes `ClearAllPools()` a no-op, so it is harmless. **The moment Core's factory defaults to pooling** — or a Core test opts into the API profile — one test class's teardown starts evicting a *concurrently running* test class's pooled connections. Result: flaky, non-deterministic failures that look like SQLite bugs and will burn days.

### Fix
Keep the **WPF/DELETE profile as the factory default** so nothing changes for existing tests; give API-profile tests their own fixture inside a `DisableParallelization = true` collection.

## H7 — Three existing tests hard-assert the current connection policy

`[VERIFIED]` `src/TimesheetApp.Tests/Data/SqliteConnectionFactoryTests.cs`:

| Test | Assertion |
|---|---|
| `Journal_Mode_Is_Delete_Not_Wal` (`:42`) | `Assert.Equal("delete", mode)` |
| `No_Wal_Or_Shm_Sidecar_Is_Created_By_A_Write` (`:52`) | `-wal` / `-shm` must **never** exist |
| `Pooling_Is_Off_So_Dispose_Releases_The_File_Handle` (`:69`) | `File.Move(_dbPath, …)` succeeds after `Dispose` |

§5.2 changes `SqliteConnectionFactory` to "take options" — **all 6 tests in that file break on the constructor signature alone** (`new SqliteConnectionFactory(cfg)`), and the 3 above break *semantically* if the default profile flips to WAL/pooled.

This is the acceptance gate's tripwire, and it is also the argument for H6's fix: **keep the WPF profile as the default**, pass options explicitly from the API. Then these three keep testing something real (the WPF profile still exists and is still correct) and only the ctor overload needs touching.

---

# MEDIUM

## M1 — `internal` helpers straddle the Core/WPF split → hard compile break

`[VERIFIED]` `TimesheetApp.csproj:13` — `<InternalsVisibleTo Include="TimesheetApp.Tests" />`.

`[VERIFIED]` `Services/DateHelpers.cs:3` and `Services/FormatHelpers.cs:5` are **`internal static class`** — and are consumed from **both sides of the proposed split**:

| Consumer | Lands in |
|---|---|
| `ExportHubService:126,171`, `PruneArchiver:134,175`, `StandupArchiveService:32,39,58,71,76`, `TaskListArchiveService:202-203`, `TimeLogService:158,182,193` | **Core** |
| `TimesheetViewModel.cs:83,99`, `ReportsViewModel.cs:56` | **WPF** |

Move them into `TimesheetApp.Core` and the WPF ViewModels **stop compiling** — `internal` is assembly-scoped. Same class of break: `CurrentTeamService.OnTeamsChangedAsync` (`:85`, internal, called by `CurrentTeamServiceTests:146,169`) and `ExportService.EscapePipe` (`:135`, internal).

**Fix:** `TimesheetApp.Core.csproj` needs `<InternalsVisibleTo Include="TimesheetApp" />` **and** `<InternalsVisibleTo Include="TimesheetApp.Tests" />` (plus `TimesheetApp.Core.Tests` if split). Or promote the two helper classes to `public`. Cheap — but it is a hard stop on hour one of M8.1, and it is nowhere in the spec.

## M2 — The complete `UPDATE` inventory (answering the brief directly)

First, a count correction: §6.1's table lists **8** tables getting `row_version`, not 7 — `Backlogs, Tasks, StandupIssues, Users, Teams, Tags, PcaContacts, TimeLogs`.

`[VERIFIED]` — exhaustive grep of `Data/Repositories/` **plus** `Services/` (the spec's "14 repositories" framing misses the latter):

| # | Site | Statement | Table | Action |
|---|---|---|---|---|
| 1 | `BacklogRepository.cs:112` | `UPDATE Backlogs SET backlog_code, project, …` | Backlogs | ✅ **check + bump** |
| 2 | `TaskRepository.cs:98` | `UPDATE Tasks SET task_name, order_index, status` | Tasks | ✅ check + bump |
| 3 | `TaskRepository.cs:106` | `UPDATE Tasks SET is_active` (soft-delete) | Tasks | ✅ **bump only** |
| 4 | `TaskRepository.cs:114` | `UPDATE Tasks SET order_index` — **called once per row during a drag-reorder** | Tasks | ✅ **bump only** — version-checking this will 409-storm |
| 5 | `TaskRepository.cs:132` | `UPDATE Tasks SET type, assignee_user_id` | Tasks | ✅ check + bump |
| 6 | `TaskRepository.cs:171` | `UPDATE Tasks SET status` | Tasks | ✅ check + bump |
| 7 | `UserRepository.cs:61` | `UPDATE Users SET windows_username` | Users | ✅ bump only (**+ renamed, B3**) |
| 8 | `UserRepository.cs:69` | `UPDATE Users SET is_active` | Users | ✅ bump only |
| 9 | `UserRepository.cs:77` | `UPDATE Users SET name` | Users | ✅ bump only |
| 10 | `TeamRepository.cs:62` | `UPDATE Teams SET name` | Teams | ✅ bump only |
| 11 | `TeamRepository.cs:69` | `UPDATE Teams SET is_active` — **the admin `PATCH /teams/{id}/deactivate`** | Teams | ✅ bump only |
| 12 | `TagRepository.cs:37` | `UPDATE Tags SET text, icon, color` | Tags | ✅ bump only |
| 13 | `PcaContactRepository.cs:52` | `UPDATE PcaContacts SET name` | PcaContacts | ✅ bump only |
| 14 | `PcaContactRepository.cs:59` | `UPDATE PcaContacts SET is_active` | PcaContacts | ✅ bump only |
| 15 | `StandupRepository.cs:160` | `UPDATE StandupIssues SET …` | StandupIssues | ✅ check + bump (DR-04 collaborative) |
| 16 | `TimeLogRepository.cs:132` | `ON CONFLICT(…) DO UPDATE SET hours` — **shared by single-cell save AND Smart Fill batch** | TimeLogs | ✅ **see B5** |
| 17 | **`TeamBootstrapService.cs:94`** | `UPDATE Backlogs SET team_id WHERE team_id IS NULL` | Backlogs | ⚠️ **versioned table, bulk write, outside `Data/Repositories/`** — the spec's inventory misses it entirely |
| 18 | `StandupRepository.cs:99` | `UPDATE StandupEntries SET …` | StandupEntries | ❌ excluded by §6.1 (owner-gated) |
| 19 | `HolidayRepository.cs:52` | `ON CONFLICT(holiday_date) DO UPDATE` | Holidays | ❌ excluded |
| 20 | `DefaultTaskRepository.cs:39` | `UPDATE DefaultTasks SET is_active` | DefaultTasks | ❌ not versioned |
| 21 | `TeamBootstrapService.cs:96` | `UPDATE StandupEntries SET team_id …` | StandupEntries | — not versioned |
| 22 | `DatabaseInitializer.cs:236-246` | v3 migration `UPDATE Requests SET project=…` | — | migration-time, N/A |

**The finding that matters:** §6.1 gives **one** template —

```sql
UPDATE … SET …, row_version = row_version + 1 WHERE id = @id AND row_version = @expected;
```

— but **10 of the 16 versioned sites have no client-supplied expected version** (soft-deletes, reorders, admin edits). Applying the template blindly to `SetOrderAsync` (#4), invoked once per row during a drag, produces spurious 409s on a pure UI reorder.

**The spec needs two templates, not one: "check-and-bump" and "bump-only" — and it must say which sites get which.**

## M3 — `SQLITE_BUSY` under WAL: where `busy_timeout` saves you, and where it does not

**What WAL actually buys** `[CITED]` https://www.sqlite.org/wal.html: *"readers do not block writers and a writer does not block readers… however, since there is only one WAL file, there can only be one writer at a time."* So `SQLITE_BUSY` still happens — **writer vs. writer**.

**The nasty variant** `[CITED]` https://www.sqlite.org/rescode.html#busy_snapshot: `SQLITE_BUSY_SNAPSHOT` fires when *"a database connection tries to promote a read transaction into a write transaction but finds that another database connection has already written to the database and thus invalidated prior reads."* The remedy is to **restart the transaction** — **`busy_timeout` cannot fix it**, because the snapshot is stale, not merely contended. This is the classic WAL footgun.

**`[VERIFIED]` — this codebase is already immune, and it is worth knowing why so nobody breaks it.** From the `Microsoft.Data.Sqlite` source (release/8.0):

`SqliteTransaction.cs`:
```csharp
connection.ExecuteNonQuery(
    IsolationLevel == IsolationLevel.Serializable && !deferred
        ? "BEGIN IMMEDIATE;"
        : "BEGIN;");
```
`SqliteConnection.cs`:
```csharp
public new virtual SqliteTransaction BeginTransaction(IsolationLevel isolationLevel)
    => BeginTransaction(isolationLevel, deferred: isolationLevel == IsolationLevel.ReadUncommitted);
```

`IDbConnection.BeginTransaction()` → `IsolationLevel.Unspecified` → promoted to `Serializable`, `deferred = false` → **`BEGIN IMMEDIATE`**. Every `using var tx = conn.BeginTransaction()` in this codebase takes the write lock **up front** → plain `SQLITE_BUSY` → **which `busy_timeout` does retry.** ✅

Note also that §6.1's optimistic-concurrency mechanism is a **single atomic `UPDATE`** with no read-then-write, so it cannot hit `BUSY_SNAPSHOT` either. The design is sound — for the right reason, which the spec doesn't state.

⚠️ **Rule to add to the spec: never use the `BeginTransaction(deferred: true)` overload.** MS's own remark `[CITED]` https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.begintransaction: *"commands inside a deferred transaction can fail if they cause the transaction to be upgraded from a read transaction to a write transaction but the database is locked. The application will need to retry the entire transaction when this happens."*

**Where `busy_timeout = 5000` is not enough:** `[VERIFIED]` `RetentionService` holds **one `BEGIN IMMEDIATE` transaction across six bulk DELETEs** (`RetentionService.cs:221-260`). While it runs, **every other writer in the app blocks**; anything exceeding 5 s → `SQLITE_BUSY` → HTTP 500 across every screen. §8.3 exposes this as `POST /api/ops/retention/run` — one admin click that can take the app down for the duration on a large DB.

**Decide, don't discover:** run Retention as a drained maintenance operation (or a hosted background service outside request scope), or raise `busy_timeout` for it and accept the stall. Either is fine. Silently shipping it as a normal HTTP handler is not.

## M4 — Bootstrap admin password (§8.2)

**Race** `[ASSUMED — depends on IIS config not yet chosen]`: with one app pool and `maxProcesses = 1` (the default) there is exactly one process, so the startup write is not racy. But IIS **web gardens** (`maxProcesses > 1`) and **overlapped recycle** (`disallowOverlappingRotation = false`, the default) both run **two processes through startup concurrently**. The bootstrap is safe **iff written as a single atomic statement**:

```sql
UPDATE Users SET password_hash = @hash WHERE id = @adminId AND password_hash IS NULL;
```
A `SELECT … if (null) … UPDATE` sequence is a genuine TOCTOU. **Mandate the single-statement form and say why**, so it survives a later refactor.

**Secret in git** — the real problem, and unconditional. §8.2 explicitly offers `appsettings.Production.json` as a delivery vehicle: that is a plaintext admin password in the repository. Ranked options for an internal on-prem box:

1. **Environment variable on the app pool only** — `Bootstrap__AdminPassword`. Add `appsettings.Production.json` to `.gitignore` regardless.
2. **Better — remove the persistent secret entirely.** §8.2's own constraint ("never start in a state where nobody can log in") is satisfiable *without* any config secret: on startup, if no user has a password, **generate a random one, write the hash, and emit the plaintext once to the console/log**, then force rotation on first login. This is the `dotnet dev-certs` / Jenkins `initialAdminPassword` pattern. It cannot be committed, cannot be left behind, and cannot be reused.

**Note on §8.1's `PasswordHasher<User>`:** it works standalone as claimed, but it lives in `Microsoft.AspNetCore.Identity`, so a non-web Core project needs the `Microsoft.Extensions.Identity.Core` package. Also — the `TUser` type parameter is **ignored** by the implementation. Harmless; just don't spend time wondering what it's for.

---

# LOW

**L1 — §6.1 lists 8 tables, not 7.** `Backlogs, Tasks, StandupIssues, Users, Teams, Tags, PcaContacts, TimeLogs`. Trivial, except that plans get generated from these counts.

**L2 — Don't add `Cache=Shared`.** `[CITED]` https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings: *"Mixing shared-cache mode and write-ahead logging is discouraged. For optimal performance, remove `Cache=Shared` when the database is configured to use write-ahead logging."* Not used today — this is a pre-emptive fence, because "shared cache" is exactly what someone reaches for when tuning concurrency.

**L3 — `synchronous=NORMAL` (§5.2) is the right call for WAL, but the cost is worth recording**: on WAL, `NORMAL` means a power loss can lose the **last few commits** (it cannot corrupt the DB). For a timesheet app that is a fine trade — record it as a decision in §4 rather than leaving it as an unexplained pragma.

**L4 — §1's central premise is TRUE.** `[VERIFIED]` `grep "using System.Windows"` across `Services/` (50 files), `Data/` (29), `Models/` (3), `Config/` → **exactly one hit: `ThemeService.cs:1`.** (The other five grep hits are *comments* that say "no System.Windows.*".) I checked this specifically because if it were false the whole slice would collapse. It isn't. The extraction is as tractable as the spec claims.

**L5 — §11's `LastNWorkingDays` fix is cheaper than it looks.** `[VERIFIED]` `TimeLogService` **already injects `IHolidayRepository`** (`TimeLogService.cs:25,31`). Delegating `LastNWorkingDays` (`:270-280` — currently `private static`, weekend-only) to `IWorkingDayCalculator` therefore needs **no constructor change** → no cascade through the 26 `TimeLogServiceTests`. And the three `GetUsersMissingLogs_*` tests (`:271-319`) seed no holidays, so they stay green on their existing assertions.

**L6 — `DAYS LOGGED` has zero test coverage.** `[VERIFIED]` `grep "DaysLoggedText|AvgPerDayText|WeekTotalText" src/TimesheetApp.Tests` → **no matches**. Moving the computation into `ReportAggregator` (§11) therefore breaks nothing — and equally, nothing was ever guarding it, which is how a stat that can only ever read `N / N` survived to production. **Write the test first.**

---

## Sources

- [SQLite — ALTER TABLE](https://www.sqlite.org/lang_altertable.html)
- [SQLite — Write-Ahead Logging](https://www.sqlite.org/wal.html)
- [SQLite — Result and Error Codes (SQLITE_BUSY_SNAPSHOT)](https://www.sqlite.org/rescode.html#busy_snapshot)
- [Microsoft.Data.Sqlite — Connection strings (Pooling, Cache)](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)
- [Microsoft.Data.Sqlite — SqliteConnection.BeginTransaction](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.begintransaction)
- [Microsoft.Data.Sqlite — SqliteTransaction source (release/8.0)](https://github.com/dotnet/efcore/blob/release/8.0/src/Microsoft.Data.Sqlite.Core/SqliteTransaction.cs)
- [Microsoft.Data.Sqlite — SqliteConnection.ClearAllPools](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnection.clearallpools)
- [ASP.NET Core — Require authenticated users / FallbackPolicy](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/secure-data)
- [ASP.NET Core — SignalR authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz)
- [ASP.NET Core — Configure to work with proxy servers and load balancers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer)
