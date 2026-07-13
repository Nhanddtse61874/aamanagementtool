# M8 Backend Foundation — Design

**Date:** 2026-07-12 (rev. 2 — after STEP 4 research)
**Milestone:** M8 — Migrate WPF → Web
**Slice:** M8.1 (Core extraction) + M8.2 (API host, DB, concurrency) + M8.3 (Auth)
**Status:** Revised after research; awaiting approval
**Inputs:** `.planning/M8-FEATURE-INVENTORY.md` · `.planning/research/M8-{STACK,ARCHITECTURE,PITFALL}-RESEARCH.md` · `.planning/research/FEATURE-RESEARCH-concurrency.md`

> **Rev. 2 exists because research falsified five things rev. 1 asserted.** Each is called out inline as **[REV2]**. Two of them — the backup/WAL interaction and the missing fifth case in the concurrency table — would have destroyed data in production.

---

## 1. Context

TimesheetApp is a WPF (.NET 8, MVVM, Dapper + SQLite) internal tool being migrated to a web app (ASP.NET Core 8 + Angular 17). The WPF project is deleted at the end of M8.

Two facts drive the design, both verified against the code:

1. **The business layer is already portable.** Of 50 files in `Services/`, exactly **one** (`ThemeService.cs`) has a real `using System.Windows` — the other grep hits are comments *boasting* about being WPF-free. `Data/` (29) and `Models/` (3) are entirely clean. Every Core-bound file uses a file-scoped namespace, so moving a file between assemblies cannot change its namespace, and **no `using` in any `.cs` file changes.**

   **[REV3] That statement is about C#, and this project also compiles XAML — where it does not hold.** XAML resolves types by **assembly**, not by namespace import. `Views/Tabs/ReportsTab.xaml:4` declares `xmlns:m="clr-namespace:TimesheetApp.Models"` **with no `;assembly=`**, which means "look in *this* assembly", and it genuinely binds five types from it (`m:TeamNode`, `m:ProjectNode`, `m:BacklogNode`, `m:TaskNode`, `m:DateEntry` at lines 165–197). The moment `Models/` lands in Core, that build fails with `MC3050: Cannot find the type 'm:TeamNode'`. The fix is one line — `;assembly=TimesheetApp.Core` — but the claim above had to be corrected, because a plan written from it told its executor "if you are editing a file, you have gone wrong," which would have cornered them.

   Of the project's 14 `clr-namespace` declarations, **exactly one** points at a namespace that moves; the other 13 name `TimesheetApp.Views.*` / `TimesheetApp.ViewModels`, which stay in WPF. That ratio is why a spot-check misses it, and why the STEP 4 research missed it: `clr-namespace` and `xmlns` return **zero hits** across all four research reports, the feature inventory, and rev. 1–2 of this spec. **Nobody opened a `.xaml` file.**
2. **The data layer is architected around SQLite living on a shared folder.** `Pooling=false`, `journal_mode=DELETE`, plus conflict-copy detection (XC-08), journal-gone checks (XC-09) and backup-before-every-bulk-write (XC-10). Those are load-bearing *for that topology* and become wrong under a different one.

## 2. Deployment model — decided, and it changes everything downstream

The company has **no server, no always-on machine, and no cloud subscription** — only company-issued workstations.

**Chosen: one designated machine acts as the host.** It runs the API; everyone else reaches it over the LAN at `http://<host>:5000`. The database lives on **that machine's local disk**.

This is the decision the rest of the spec hangs on, because it restores the single premise that makes SQLite viable: **exactly one process writes the database.** N concurrent users are not N concurrent writers. Write contention is solved by architecture rather than by engine.

The two alternatives were considered and rejected:

| Rejected | Why |
|---|---|
| Every user runs their own API against a shared `.db` on the network | Destroys the single-writer premise. N processes on N hosts writing one SQLite file over SMB is the exact configuration [sqlite.org/faq](https://www.sqlite.org/faq.html) warns about: *"file locking of network files is very buggy and is not dependable."* WAL becomes impossible, SignalR becomes impossible, and auth stops being a boundary. |
| Keep the live `.db` on the network share | `WAL does not work over a network filesystem` ([sqlite.org/wal](https://www.sqlite.org/wal.html)) — the wal-index lives in shared memory, which cannot cross hosts. |

### 2.1 The risk this creates, and the mitigation that is therefore mandatory

**All company timesheet data now lives on one workstation's disk.** If that machine dies, is lost, or is reimaged, everything is gone.

This is not a footnote — it is the single largest operational risk in the design, and it is created *by* the deployment choice. The mitigation is a `must_have`, not an option:

```
host machine:  D:\Worklog\timesheet.db          ← live DB. Local disk, WAL, safe writes.
                        │
                        └── hourly ──►  \\share\worklog-backup\timesheet_<stamp>.db
                                        ↑ IT backs this up. Host dies → lose ≤ 1 hour.
```

`BackupService` already implements the machinery (auto-backup, retention count, user-chosen destination folder). The changes are: point the destination at the share, move the cadence from daily to hourly, and **replace `File.Copy` with an online backup** (§9).

### 2.2 Host runbook (M8.2 deliverable)

Each of these fails **silently** if skipped, and each will be misdiagnosed as an application bug:

| Step | Symptom if skipped |
|---|---|
| Disable sleep/hibernate (`powercfg`) | Machine sleeps → every client's requests hang. No error anywhere. |
| Open the port in Windows Firewall | The host can reach the app; **nobody else can.** Looks like the app is broken. |
| Run as a Windows Service, not a console app | Host logs out → API dies with the session. |
| Static IP or DHCP reservation | There is **no Active Directory**, so LAN name resolution is not guaranteed. A DHCP lease change silently moves the app. |
| Grant Modify on the **directory**, not just the file | WAL creates `-wal`/`-shm` *next to* the `.db`. File-only permission gives a clean startup and then **fails on the first write**. [H4] |

## 3. Goals

- `TimesheetApp.Core` (net8.0) exists; WPF and the API both consume it; **the 548 existing tests stay green**.
- A running ASP.NET Core 8 API over that Core, hosted on the designated machine.
- **Concurrent updates never silently overwrite each other.**
- Users log in with username + password and stay logged in across browser restarts.
- Three destructive operations are admin-only.
- The live database is never more than an hour from a copy on backed-up infrastructure.

## 4. Non-goals (this slice)

Angular UI beyond proving auth works (M8.4–M8.8) · deleting WPF (M8.10) · moving export/retention to server storage (M8.9) · password reset, email, MFA, lockout, self-registration (**YAGNI** for 10–50 trusted internal users).

## 5. Decisions

| # | Decision | Rationale |
|---|---|---|
| D1 | **One designated host machine**, LAN-only, HTTP | §2. No server exists; this is the cheapest way to recover the single-writer property. |
| D2 | 10–50 users, multiple teams | Given. |
| D3 | **SQLite on the host's local disk**, WAL + pooling — **interim** | Single writer process (§2). No managed DB is available. Because replacement is *anticipated, not hypothetical*, the SQLite-specific surface is enumerated in **§15** rather than left to be rediscovered. |
| D4 | **Optimistic concurrency (`row_version` + HTTP 409) + SignalR** | No table has a version column today, and Task List commits every inline edit as a bare `UPDATE` — concurrent editors silently overwrite each other **right now**. No database engine fixes this; it is an application-layer problem. |
| D5 | **Auth: username + password, cookie session, on the existing `Users` table** | No Active Directory, so Windows Auth is out. ASP.NET Core **Identity** rejected: it drags in EF Core (the app is Dapper-only) and creates a second user table. Research confirmed `PasswordHasher<T>` can be used **standalone** from `Microsoft.Extensions.Identity.Core` — 3 transitive packages, **no EF Core**, nothing auto-registered. |
| D6 | **Cookie, not JWT** | "Remember me" is one flag (`IsPersistent`) versus a refresh-token scheme; an `HttpOnly` cookie is not readable by XSS. Research bonus: **SignalR sends cookies to the hub automatically, zero config** — the real payoff of this choice. |
| D7 | **Authorization: a single `is_admin` boolean**, gating exactly 3 endpoints | The user does not want edit-level permissions, which is reasonable for a trusted team. But run-retention / restore-backup / deactivate-team are **destructive**, not merely privileged, and today *any* user can trigger them. |
| D8 | **HTTP, not HTTPS** — accepted risk | User decision. `SecurePolicy = SameAsRequest`. **The session cookie travels the LAN in plaintext**, so anyone who can capture packets can assume any identity — including the admin who can permanently delete three months of data. This is recorded as a knowingly accepted risk, not an oversight. Switching to HTTPS later is a one-line config change; the design does not foreclose it. |

## 6. Solution structure

```
src/TimesheetApp.sln
├── TimesheetApp.Core/        net8.0            ← NEW
│     Services/  48 files  (50 minus ThemeService + IThemeService)   [REV2: was "49"]
│     Data/      29 files
│     Models/     3 files
│     Config/     IAppConfig + JsonAppConfig     [REV2: impl moves too]
├── TimesheetApp.Api/         net8.0            ← NEW (ASP.NET Core 8)
├── TimesheetApp.ApiTests/    net8.0            ← NEW (integration tests)   [REV2]
├── TimesheetApp/             net8.0-windows    ← WPF → references Core
└── TimesheetApp.Tests/       net8.0-windows    ← references Core AND WPF   [REV2]
```

### 6.1 [REV2] The test project does **not** move to Core

Rev. 1 said `TimesheetApp.Tests → references Core`. **False, and it would have broken the plan in wave 1.** 23 of 61 test files need WPF: 13 ViewModel tests, 8 **STA render tests** (`WpfStaCollection.cs` exists because WPF permits exactly one `Application` per AppDomain), and `DependencyInjectionTests`, which calls `App.ConfigureServices`. Roughly **200 of 511 test attributes (~40%) cannot live in a `net8.0` assembly.**

The test project therefore stays `net8.0-windows` and references **both** Core and WPF. It is not retargeted, not split, and not touched during the extraction. A separate `TimesheetApp.ApiTests` (net8.0, Web SDK) is created for `WebApplicationFactory` integration tests.

### 6.2 [REV2] `IAppConfig` is frozen — and it leaks user state

Two independent findings, both verified:

**It cannot gain members.** Four test files (`BackupServiceTests`, `ExportHubServiceTests`, `PruneArchiverTests`, `RetentionServiceTests`) **hand-implement `IAppConfig`**. Adding *any* member breaks compilation and all 548 tests fail. This rules out hanging `SqliteOptions` off it.

**It carries per-user state, and on a server that is a cross-user data leak.** `IAppConfig.ActiveTeamId` is read and written *by Core services*: `CurrentTeamService:72,94` and `TeamBootstrapService:66,76` call `SetActiveTeamId`; `TimeLogService` (4 sites) and `StandupService` (7 sites) read `ActiveTeamId` to scope every query. The original authors documented the hazard themselves in `IAppConfig.cs:24-27`:

> *"the active team is an app-local, **per-machine/user** UI preference — never in the shared DB, else two users fight over one active team"*

That reasoning is correct **for WPF, where one process serves one user.** In an API, one process serves everyone: user A switches team → the singleton now says team X for **everybody** → user B's next request reads `ActiveTeamId = X` and is served team X's timesheets. This violates the R6 no-leak rule the whole codebase is built around.

**Fix:** remove `ActiveTeamId` / `SetActiveTeamId` from `IAppConfig`; add **`Users.active_team_id`** (schema v10, already being touched); make `ICurrentTeamService` **scoped per-request** in the API, resolving from the authenticated user. The root defect is that `IAppConfig` mixes per-**app** state (`DbPath`, `BackupFolderPath`, `ExportRoot*`) with per-**user** state. On a desktop those coincide; on a server they do not. WPF benefits too — two people sharing a machine stop overwriting each other's active team.

### 6.3 Connection policy is per-host

Core is shared by WPF (SQLite on a synced folder) and the API (SQLite on the host's local disk), and they need **opposite** settings.

| Setting | WPF profile (**default**) | API profile (explicit) |
|---|---|---|
| `Pooling` | `false` | `true` |
| `journal_mode` | `DELETE` | **`WAL`** |
| `busy_timeout` | — | **`1000`** |
| `DefaultTimeout` | — | **`5`** ← see §8.4 |
| `synchronous` | default | `NORMAL` |
| `foreign_keys` | `ON` | `ON` |

**[REV2] Mechanism: an optional constructor parameter on `SqliteConnectionFactory`, not `IOptions<T>` and not `IAppConfig`.** Verified by building a probe against MS.DI 8.0.1: an optional ctor param works on all three call paths — unregistered (WPF → desktop default), registered (API → WAL), and direct `new SqliteConnectionFactory(cfg)` (the test fixtures) — with **zero test changes**. `IOptions<T>` breaks the three direct-`new` sites. And `SqliteConnectionFactoryTests` hard-asserts `journal_mode=delete`, no `-wal`/`-shm`, pooling-off, so **the WPF profile must remain the default** [H7].

**[REV2] `PRAGMA journal_mode=WAL` cannot be set inside a transaction** [B4], so it stays in `Create()` alongside `foreign_keys`.

### 6.4 Extraction sequence — every step ends with 548 green

Baseline verified: **548/548 in 5 seconds.** At that speed there is no excuse for skipping a gate.

0. Baseline.
1. Create empty `Core` (net8.0). Wire all three project references and **both** `InternalsVisibleTo` — `TimesheetApp` *and* `TimesheetApp.Tests`. **Nothing moves yet.** (`DateHelpers`/`FormatHelpers` are `internal static` and used by both Core-bound services *and* WPF ViewModels; without this, the move is a hard `CS0122`. [M1])
2. `git mv Models/` (3 files).
3. `git mv Config/` (2 files — **including `JsonAppConfig`**: it is `System.IO` + `System.Text.Json` only, and is `new`-ed at 25 call sites across 9 test files, 8 of which are otherwise pure-Core. Leaving it in WPF would make Core's tests transitively depend on the WPF assembly for no benefit; the API simply doesn't register it).
4. `git mv Data/` (29 files).
5. `git mv Services/` minus `ThemeService.cs` + `IThemeService.cs` (48 files).
6. Hygiene: drop now-dead packages from the WPF csproj.
   **← M8.1 GATE: 548/548 green + WPF still launches.**
7. `SqliteOptions` (§6.3).
8. *(not M8.1)* the §13 bug fixes.

**Steps 2–5 are pure `git mv` + csproj edits — zero C# changes.** Do not mix the bug fixes in. A pure move that goes red is trivially diagnosable; a move plus three behaviour changes is not.

**[REV2] `CommunityToolkit.Mvvm` does not go to Core.** `DataChangedMessage.cs` has zero package imports (a bare enum + record). The only `IMessenger` consumer is `CurrentTeamService`, and `TimeLogService`/`StandupService` depend on the *interface* only. So: move `ICurrentTeamService` + `DataChangedMessage` to Core, **leave the messenger implementation in WPF**. Free, zero code change. Rev. 1 floated an `IDataChangeNotifier` abstraction — rejected: no Core service needs to *send* messages, so it is speculative generality.

### 6.5 [REV2] The M8.1 gate is "548 green", and that is only achievable because the bug fixes are excluded

Rev. 1 said the 548 tests move to Core and stay green while §13 *deletes code those tests cover* (`SmartInputPanelVm.BuildPlan` — 9 attributes; `SelectUserDialog` — 1). Those two claims cannot both hold. A gate that cannot be met is worse than no gate: **the cheapest way to turn a red gate green is to delete tests.**

So M8.1 is a pure move with an exactly-548 gate, and the bug fixes land afterwards as their own step, whose test-count change is stated up front and reviewed.

## 7. Schema v10

### 7.1 Optimistic concurrency

`row_version INTEGER NOT NULL DEFAULT 1`, on **8 tables** [REV2 — rev. 1 said 7 and listed 8]:

| Table | Reason |
|---|---|
| `Backlogs` | Task List lets **multiple people** edit one card inline (PCT, Type, PCA, both deadlines, progress, tags). |
| `Tasks` | Type / assignee / status edited inline. |
| `StandupIssues` | **Deliberately collaborative** — not owner-gated (DR-04). |
| **`TimeLogs`** | **[REV2 — reversed.]** Rev. 1 excluded it, arguing a cross-user collision was impossible because the natural key scopes rows to one user and nobody logs hours for anyone else. That rests on *current behaviour* (Log Work's team view is read-only), **not on an enforced invariant** — unlike `StandupEntries`, which really is owner-gated in `StandupService`. The user asked what happens if someone *can* edit another person's hours. Retrofitting after the endpoints and the Angular grid exist costs far more than one column in a migration we are already writing. |
| `Users`, `Teams`, `Tags`, `PcaContacts` | Admin-edited; low frequency, cheap to include. |
| `Holidays`, `Settings` | **No** — date/key-keyed; overwrite is the correct semantics. |
| `StandupEntries` | **No** — owner-gated *in code*, not merely absent from the UI. |

### 7.2 [REV2] Two update templates, not one

There are **22 `UPDATE` sites**, and **10 of the 16 on versioned tables have no client-supplied version** — soft-deletes, reorders, admin edits. Rev. 1's single template applied to `TaskRepository.SetOrderAsync` (`:114`), which is called **once per row during a drag**, would **409-storm on an ordinary reorder**.

| Template | Used by | SQL |
|---|---|---|
| **check-and-bump** | user edits carrying an `expectedVersion` | `SET …, row_version = row_version + 1 WHERE id = @id AND row_version = @expected` |
| **bump-only** | reorders, soft-deletes, system writes | `SET …, row_version = row_version + 1 WHERE id = @id` |

The rule is: **always bump; check selectively.** A write that bumps without checking is safe. A write that checks without bumping is the bug in §7.4.

Note also `TeamBootstrapService.cs:94`, which writes to `Backlogs` from **outside** `Data/Repositories/` — the "14 repositories" framing misses it.

### 7.3 [REV2] `TimeLogs`: the upsert has **five** cases, not four

Rev. 1's table had four. The fifth silently destroys data:

| Client sends | Row on server | Result |
|---|---|---|
| `expectedVersion = null` | absent | INSERT, `row_version = 1` |
| `expectedVersion = null` | **present** | **409** — someone filled this cell while you looked at it |
| `expectedVersion = N` | present, version `N` | UPDATE, `row_version = N + 1` |
| `expectedVersion = N` | present, version `≠ N` | **409** |
| **`expectedVersion = N`** | **absent (deleted)** | **409** ← **missing from rev. 1** |

The naive `INSERT … ON CONFLICT … DO UPDATE … WHERE row_version = @expected` gets the last case **wrong**: with no row there is no conflict, so the `WHERE` never runs, the `INSERT` succeeds, and the row is **resurrected at v=1 with HTTP 200**. Alice deletes a cell; Bob, holding v=3, types a number; **Alice's delete evaporates with no 409 and no trace.** That is precisely the bug class this slice exists to eliminate — the `WHERE` guards the UPDATE branch, not the INSERT branch.

The fix makes the **INSERT itself conditional**, and returns the new version in the same statement (verified 5/5 cases; 12 threads racing one empty cell → exactly 1 winner, 11 × 409, 0 exceptions):

```sql
INSERT INTO TimeLogs (user_id, task_id, work_date, hours, created_at, row_version)
SELECT @UserId, @TaskId, @WorkDate, @Hours, @CreatedAt, 1
 WHERE @Expected IS NULL
    OR EXISTS (SELECT 1 FROM TimeLogs
                WHERE user_id = @UserId AND task_id = @TaskId
                  AND work_date = @WorkDate AND row_version = @Expected)
    ON CONFLICT(user_id, task_id, work_date) DO UPDATE
   SET hours = excluded.hours, row_version = TimeLogs.row_version + 1
 WHERE TimeLogs.row_version = @Expected
RETURNING row_version;
```

`RETURNING` emits **zero rows** when the `DO UPDATE`'s `WHERE` turns it into a no-op — the SQLite docs are silent on this; it was measured. So one statement both detects the conflict and returns the new version: `QueryFirstOrDefaultAsync<long?>` → `null` means 409. No `changes()` call is needed — Dapper's `ExecuteAsync` **is** `changes()`.

**Clearing a cell** is a `DELETE` (empty ≠ zero, semantically). RFC 9110 §9.3.5 discourages DELETE-with-body, so clear is modelled as `PUT { hours: null, expectedVersion }` [G6].

### 7.4 [REV2] Smart Fill must **bump** even though it does not **check**

Rev. 1 carved Smart Fill out of version *checking* and never said what happens to the version. Both the pitfall and the concurrency agent independently found the hole:

```
A reads a cell at v3  →  Smart Fill overwrites it (no bump — still v3)
                      →  A saves with expected=3  →  MATCHES  →  silently overwrites Smart Fill
```

That is the lost update the feature exists to prevent, reintroduced by its own exception. **Smart Fill bumps `row_version` on every cell it writes.** Its protection remains the server-side re-validation that already runs inside the apply transaction (`ValidateSmartFillAsync`), which rejects the batch if the day would exceed 8h given whatever anyone else has since written.

### 7.5 [REV2] `BacklogRepository.UpdateAsync` needs a transaction before it can be version-checked

`:140-192` currently does read → `UPDATE` → N× audit `INSERT`, **with no transaction**. Bolt a version check onto that and a 409 still writes the audit rows — the history would describe a change that never happened. Wrap it first.

### 7.6 Why 409 and not `If-Match` / 412

RFC 9110 §13.1.1 names `If-Match` as *the* standard tool for the lost-update problem, so this deserves an explicit answer rather than silence. `GET /api/timelogs/week` returns ~35 cells, **each with its own version**, and HTTP permits exactly **one** `ETag` per response. The versions must therefore live in the body on the read path; once they do, moving them to a header on the write path yields a hybrid that is worse than either. And 412 is defined in terms of *request header fields* (§15.5.13) — with the version in the body, **409 is the correct code, not a compromise.**

### 7.7 Auth columns

```sql
ALTER TABLE Users RENAME COLUMN windows_username TO username;  -- values preserved
ALTER TABLE Users ADD COLUMN password_hash TEXT;
ALTER TABLE Users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Users ADD COLUMN active_team_id INTEGER NOT NULL DEFAULT 0;   -- §6.2
```

`windows_username` already holds bare usernames (`nhan`, `chi.le`, …), so the rename is **not a data migration** and nobody's history is orphaned — people log in with the name they already use.

**[REV2] Blast radius of the rename is larger than rev. 1 said, and one part of it fails silently.** It is **6** SQL statements, not 5. Beyond `UserRepository`:

| Site | Failure if missed |
|---|---|
| `TestDb.cs:54` seeds `INSERT INTO Users(name, windows_username, …)` | **Every DB-backed test dies at once.** |
| `DatabaseInitializerTests.cs:144` asserts the old column name | Fails loudly (fine). |
| `SchemaV7/V8/V9UpgradeTests.cs` | They assert "an old DB upgrades to the **latest** schema", with *latest* hard-coded as `9`. **Any** `SchemaVersion` bump forces them. |
| **`UserRepository.cs` — 6 raw SQL statements + the `UserRaw` DTO** | Throws: `SqliteException: no such column: windows_username`. **28 tests red.** |

**[REV4] The rename cannot be split across commits.** Rev. 3 claimed that renaming the column without the DTO would make **Dapper fail *silently*, returning `null`** — a claim carried up from `M8-PITFALL-RESEARCH.md:142` — and that belief was the entire reason the repository fix was scheduled into a *later* wave than the migration. **It is wrong**, and it was caught by running it rather than reasoning about it.

Silent-null happens only when a `SELECT` **does not name the column**. All six statements in `UserRepository` name it explicitly (`SELECT id, name, windows_username, is_active FROM Users …`), so SQLite raises `no such column` and takes 28 tests down with it. **The schema rename and the SQL that reads it are atomically coupled**: any commit that contains one without the other leaves a red tree — and on a parallel wave, hands every sibling agent a broken baseline.

The lesson generalises past this one column: a claim about *how a failure presents* is exactly the kind of thing that must be measured, not inherited. This one travelled from research → spec → plan → three separate task prompts without anyone executing it.

Rename the repository members too (`GetByUsernameAsync` / `SetUsernameAsync`). Leaving `Windows` in the name of a method that has nothing to do with Windows is how stale vocabulary calcifies — this codebase already carries that scar (files named `Requests*` containing classes named `Backlogs*`, three migrations after the rename).

**[REV2] The `CreateTables` DDL at `DatabaseInitializer.cs:43` must keep saying `windows_username` forever** [B4]. `RunMigrations` replays every step on a fresh database, so "tidying" the baseline DDL to the new name breaks **fresh installs only** — the worst kind of bug to find. Leave it, with a comment saying why. (Good news: the whole of `InitializeAsync` including the `user_version` bump is one transaction, so a mid-migration failure rolls back cleanly.)

## 8. API

### 8.1 [REV2] The error contract does not work out of the box

Rev. 1's table assumed ASP.NET returns 401 when unauthenticated. **On .NET 8 it does not.** Reading `CookieAuthenticationEvents.cs` on `release/8.0`: the *only* trigger for a 401 is the literal request header `X-Requested-With: XMLHttpRequest`. Everything else gets `Response.Redirect(...)` → **302 → `/Account/Login`** → which does not exist → **Angular sees 404, never 401**. Microsoft changed this default only in **.NET 10**.

The twist that will waste a day: **the SignalR JS client *does* send that header**, so the hub 401s correctly while the API 404s. The asymmetry sends you looking in the wrong place.

```csharp
o.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
```

| Condition | Status |
|---|---|
| Validation failure (`TimeLogService` rules) | `400` + message |
| Not authenticated | `401` (only after the override above) |
| Authenticated, not admin | `403` |
| Concurrency conflict | **`409`** + current server state **+ `deleted: bool`** |

**[REV2] The 409 body needs `deleted`** [G4]: `rowsAffected == 0` conflates "your version is stale" with "the row is gone", and the user-facing wording differs. Related limitation to design around: **`TimeLogs` has no `changed_by` column**, so the API *cannot name who* changed a cell. The "Someone else just changed this" dialog can name the person for `Backlogs` (via `BacklogAudit`) but not for timesheet cells.

### 8.2 [REV2] `FallbackPolicy` locks the user out of the login page

`FallbackPolicy = DefaultPolicy` applies to *"requests served by other middleware after the authorization middleware, **such as static files**"*. So a logged-out user requesting Angular's `index.html` gets **401**, and can never reach the form that would log them in. It also blocks `MapFallbackToFile` and health checks — and `/api/auth/login` itself, which rev. 1 never marked `[AllowAnonymous]`.

Required: `[AllowAnonymous]` on login, and `.AllowAnonymous()` on the SPA fallback and static files. (SignalR's `negotiate` being blocked is correct — leave it.)

### 8.3 SignalR

Replaces `DataChangedMessage` (`WeakReferenceMessenger` — in-process only, so today two users never see each other's changes without reloading).

- Hub at `/hubs/data`; clients joined to a **group per team** (preserving the R6 no-leak rule).
- After each successful mutation, broadcast `DataChanged(DataKind, teamId)`.
- `DataKind` keeps all 11 values; the listener table in the inventory (§0.3) ports 1:1.

**[REV2] Two hub facts that bite silently:**
- **Group membership is lost on every reconnect.** Rejoin in `OnConnectedAsync`, or cross-user sync dies after any Wi-Fi blip — and *appears* to work until then.
- **`IHubContext` has no `Others`.** The editing user receives their own `DataChanged` echo, which can re-fetch and clobber the very conflict dialog §7.3 just raised. Exclude the caller's connection.

### 8.4 [REV2] `busy_timeout` does not bound how long a request hangs

Microsoft.Data.Sqlite **auto-retries** busy/locked errors up to `CommandTimeout` — **default 30 s**. Measured with rev. 1's exact settings: a blocked writer failed after **33,940 ms**, not 5,000. Add **`DefaultTimeout=5`** to the connection string (a valid keyword; it flows into every Dapper call, so no repository changes) and lower `busy_timeout` to 1000 → worst-case hang ≈ 5.2 s.

Also: `BeginTransaction()` already defaults to **`BEGIN IMMEDIATE`**, not deferred, so `UpsertBatchAsync` is already free of the lock-upgrade trap. **Never use the `deferred: true` overload** — it reproduces `SQLITE_BUSY_SNAPSHOT` (517), which hangs for the full timeout on a conflict that can never resolve.

`RetentionService` is the outlier: it holds one `BEGIN IMMEDIATE` across six bulk DELETEs, blocking every writer app-wide. One admin click can 500 everyone else. It must run in a maintenance window, or be moved out of the request path.

## 9. [REV2] BLOCKER — backup is incompatible with the WAL decision

This is the finding that most justifies having done the research. `BackupService.cs:9` states its own safety precondition verbatim:

> *"File-level `File.Copy` **is safe at idle** — the app uses **short connections + journal_mode=DELETE**."*

**§6.3 deletes both premises.** Five sites `File.Copy` the live `.db`: `BackupService:47,103,105`, `DbBackupHelper:36` (**runs before every bulk write**), and `PruneArchiver:164`.

[sqlite.org/wal](https://www.sqlite.org/wal.html): copying a WAL database **without its `-wal` file** means *"transactions that were previously committed … might be lost, or the database file might become corrupted."*

And `PruneArchiver` validates its snapshot like this:

```csharp
File.Copy(dbPath, snapPath, overwrite: false);
return File.Exists(snapPath) && new FileInfo(snapPath).Length > 0 ? snapPath : null;
```

Exists, non-empty → **"snapshot OK"** → `RetentionService` then **permanently DELETEs** the originals it just "archived". Under WAL that snapshot can be stale or corrupt, and nothing checks. **Three months of real data destroyed, with a backup that cannot restore it.** Nobody finds out until they try.

**Fixes:**
- Replace every live-DB `File.Copy` with **`SqliteConnection.BackupDatabase()`** (the SQLite online-backup API; 3.41.2 is bundled) or `VACUUM INTO`.
- **Restore must be offline.** With pooling on, `File.Copy(backup, dbPath, overwrite:true)` throws `IOException` on the pooled handles, or replays a stale `-wal` onto the new file. Restore-while-serving cannot be done by file copy at all.
- **[REV2/H5] The OneDrive scaffolding is not inert.** `IDbBackupHelper` and `IJournalWarningSink` are **constructor-injected** into `TimeLogService`, `RetentionService`, `TeamBootstrapService`, `DefaultTaskSyncService` and `PruneArchiver`. Left alone on the host, that means a **full-database copy on every Smart Fill request**, and an XC-09 journal check that passes forever because it looks for a `-journal` file that WAL never creates. The API registers **no-op implementations** of both — a DI swap, zero service changes. WPF keeps the real ones until M8.10.

## 10. Auth

### 10.1 Mechanism

`PasswordHasher<User>` used **standalone** from `Microsoft.Extensions.Identity.Core` — the hasher class only, no Identity stack, no schema, no EF Core. .NET 8 defaults: PBKDF2 / HMAC-SHA512 / **100,000 iterations**, 128-bit salt, 256-bit subkey. It needs no DI (`new PasswordHasher<User>()`; the `user` argument is ignored in the method bodies).

```csharp
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o => {
            o.ExpireTimeSpan      = TimeSpan.FromDays(30);
            o.SlidingExpiration   = true;
            o.Cookie.HttpOnly     = true;
            o.Cookie.SameSite     = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // D8: HTTP, accepted risk
            o.Events.OnRedirectToLogin        = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
services.AddAuthorization(o => {
    o.FallbackPolicy = o.DefaultPolicy;
    o.AddPolicy("Admin", p => p.RequireClaim("is_admin", "1"));   // see §10.2
});
```

`POST /api/auth/login { username, password, rememberMe }` → verify hash → **check `is_active`** → `SignInAsync` with `IsPersistent = rememberMe`. That flag *is* the "stay logged in" mechanism.

**[REV2] Login must check `is_active`.** Rev. 1 omitted it: a soft-deleted (deactivated) user who still has a password could log in.

`CurrentUserService` is **not modified** — it already takes its identity through a `Func<string>` seam:
```csharp
new CurrentUserService(users, () => Environment.UserName)                      // WPF, unchanged
new CurrentUserService(users, () => ctx.HttpContext!.User.Identity!.Name!)     // API
```

### 10.2 [REV2] Two silent auth failures

**Data Protection is missing from the design entirely** — the highest-severity gap research found, because it is invisible until production. The cookie is encrypted with a Data Protection key ring. Unconfigured, that ring lives **in memory**, so **every app restart logs everyone out**. On the host machine — a workstation that reboots, updates, and recycles — that is mass logouts, roughly daily, **with nothing in the logs**, directly defeating the "stay logged in" goal in §3. Persist the key ring to disk (`PersistKeysToFileSystem`) in a folder that survives restarts and is backed up with the database.

**`RequireClaim("is_admin", "1")` compares with `StringComparer.Ordinal`.** If the claim is written from a `bool` (`.ToString()` → `"True"`), the policy **always fails** and the admin gets a **silent 403** with nothing logged. Write the claim value as the literal `"1"`, and test that a real admin passes.

### 10.3 Setting initial passwords

The Users screen already has *Add user*. Extend it with a password field and a *Set password* action.

**Bootstrap.** Migration v10 promotes the first user (lowest `id`) to `is_admin = 1`, but they have no `password_hash` either — so nobody could log in and nobody could set one.

**[REV2] Resolution — no persistent secret.** Rev. 1 read a bootstrap password from configuration; that puts a credential in a file that tends to get committed. Instead: on startup, if the designated admin's `password_hash IS NULL`, the API **generates a random password, applies it, and writes it once to the console/log**. The operator reads it, logs in, changes it. There is no secret to leak, because it exists only in that one log line.

Constraints:
- Applied **only** when `password_hash IS NULL`, as a single atomic `UPDATE … WHERE password_hash IS NULL` — which also neutralises the race under an overlapped service restart.
- No "first login claims the account" flow: on a shared internal network that lets anyone claim a colleague's account before they do.

### 10.4 Admin-only endpoints

Three, chosen because they are **destructive**, not merely privileged:

| Endpoint | Damage if mis-clicked |
|---|---|
| `POST /api/ops/retention/run` | Permanently deletes all data older than N months |
| `POST /api/ops/backup/restore` | Overwrites the whole database |
| `PATCH /api/teams/{id}/deactivate` | The team vanishes from every screen |

Everything else — logging hours, editing backlogs, standup, task list, reports — stays open to any authenticated user, matching today's behaviour.

## 11. Testing

- The **548 existing tests** stay in `TimesheetApp.Tests` (`net8.0-windows`, referencing Core **and** WPF) and must stay **100% green** through the extraction (§6.4). This is the real safety net, not a formality.
- New in `TimesheetApp.ApiTests` (net8.0, Web SDK): auth (login, bad password, inactive user, persistent cookie, admin policy denies non-admin), concurrency, `WebApplicationFactory` integration.

**[REV2] Two testing traps:**

- **The concurrency test does not need threads.** The conflict window *is* the stale version number, so "two simultaneous updates" reproduces perfectly sequentially: both clients read v=1; apply A (succeeds); apply B with expected=1 (409); assert A's value survived. Deterministic, fast, never flaky. A `Barrier`-based parallel test is a supplementary safety net that asserts the *invariant*, not timing.
- **Never use `:memory:` for a concurrency test.** Each connection gets its **own** database, so a conflict can never occur — the test passes while asserting nothing. The existing 548 already use temp-file databases; keep that convention.
- `ClearAllPools()` is process-global and appears in three teardowns. Harmless today (`Pooling=false`); a flake factory the moment pooling is on [H6].

## 12. [REV2] Development friction

Rev. 1's advice to "run dev over HTTPS" is void — D8 chose HTTP throughout, which removes both the certificate problem and the `WebApplicationFactory` trap where a `Secure` cookie is silently dropped over `http://localhost`.

What remains: Angular's dev server runs on `:4200` and the API on its own port, so the browser will not attach the cookie across origins. Use Angular CLI `proxy.conf.json` to proxy `/api` **and `/hubs`** — the latter needs `"ws": true` for the WebSocket upgrade, or SignalR silently degrades to long-polling.

## 13. Bugs found during survey — fixed in this slice, in Core

All three are fixed **in Core**, not at their current call sites. That distinction is the point: two of them exist *because* business logic leaked into a WPF ViewModel, where the web client cannot reuse it and is free to reinvent the same mistake.

| Bug | Detail | Fix lands in |
|---|---|---|
| **Two Smart Fill implementations** | `SmartInputService` is DI-registered, injected into `TimesheetViewModel`, and **never used**. The live logic is `SmartInputPanelVm.BuildPlan` — in a ViewModel. It does **not** exclude holidays when building the preview, while `ValidateSmartFillAsync` **does**, so a range containing a holiday renders a cell that then fails validation and blocks Apply, with no way for the user to act on the error. | Delete `BuildPlan`; make the already-holiday-aware `SmartInputService` **the** implementation. |
| **`DAYS LOGGED` can never read `3 / 5`** | `span = WeeklyRows.Count`, and `WeeklyRows` only holds days that *have* logs — numerator and denominator move together. **It has zero test coverage**, which is how a statistic that can only display `N / N` reached production. | Move the computation from `ReportsViewModel` into `ReportAggregator` (it is business arithmetic, not presentation) and fix it there, so M8.7 inherits the correct version. |
| **`LastNWorkingDays` ignores holidays** | Contradicts `WorkingDayCalculator`, so the "hasn't logged in N days" banner counts public holidays against people. | `TimeLogService` — delegate to `IWorkingDayCalculator`. It already injects `IHolidayRepository`, so **no constructor change** and no cascade through the 26 `TimeLogServiceTests`. |

## 14. Dead code dropped during extraction

| Item | Status |
|---|---|
| `SelectUserDialog` | Wired in `App.xaml.cs` but `selectUser` is never invoked (auto-provision replaced it). The dialog can never appear. |
| `TimesheetViewModel.SaveCommand` | No Save button exists (auto-save replaced it). Retained only for tests. |

## 15. Porting surface — when SQLite is replaced

SQLite is **interim** (D3), so the exit cost is a number we know rather than discover. Surveyed, not guessed:

| SQLite-specific | Where | SQL Server equivalent |
|---|---|---|
| `ON CONFLICT (…) DO UPDATE` | `TimeLogRepository:132`, `HolidayRepository:52` | `MERGE` (Postgres keeps `ON CONFLICT`) |
| **`INSERT … SELECT … WHERE … ON CONFLICT`** | §7.3 — **new in v10** | `MERGE` with a matched/not-matched predicate |
| `INSERT OR REPLACE` | `SettingsRepository:23` | `MERGE` |
| `INSERT OR IGNORE` | `TeamRepository:112` | `IF NOT EXISTS` |
| `last_insert_rowid()` | 10 sites across 9 repositories | `SCOPE_IDENTITY()` (Postgres: `RETURNING id`) |
| `RETURNING` | §7.3 | `OUTPUT` |
| `PRAGMA user_version` | `DatabaseInitializer` (3 uses) | a `SchemaVersion` table |
| `INTEGER PRIMARY KEY AUTOINCREMENT`, `REAL` | DDL | `INT IDENTITY`, `FLOAT` |

**What is absent matters more.** There are **no** SQLite date functions anywhere — no `strftime`, no `julianday`, no `datetime()`. Every date is computed in C# and stored as ISO `TEXT`. Dialect-specific date arithmetic is normally the most expensive thing to port, and this codebase never took the dependency.

**Deliberately not abstracted now.** No `ISqlDialect`, no query builder: an enumerated list of call sites behind an already-abstract `IConnectionFactory` is bounded work when the day comes, whereas building the abstraction today would be guessing at the shape of an engine we have not chosen. What this slice owes the future is that the list above **stays accurate** — and per rev. 1's own commitment, §7.3's new construct has been added to it.

## 16. Sequencing after this slice

| Slice | Content | Needs UI design? |
|---|---|---|
| M8.4 | Angular shell + Log Work · **plus** `TeamFilter`, `TagPicker`, login screen, 409-conflict UX, and OpenAPI-generated TS models (the vendored bundle's models are UI-shaped: `User` has no `id`, `TaskCard` has no `ScheduleState`, and `HoursMap` is keyed by array index) | ✅ |
| M8.5 | Backlog + Task List (cards, tags, holidays, Gantt, continue) | ✅ |
| M8.6 | Daily Report (Input + Board) | ✅ |
| M8.7 | Reports | ✅ |
| M8.8 | Admin (Users + Settings) | ✅ |
| M8.9 | Export / Backup / Retention → host-side | ⚠️ minimal |
| M8.10 | Delete WPF; drop the OneDrive scaffolding from Core | ❌ |
