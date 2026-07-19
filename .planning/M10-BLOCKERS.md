# M10 — Blocker dispositions (decisions required)

M10 (delete `src/TimesheetApp/`) is parked. The coverage audit returned **DO NOT DELETE YET**, and four
follow-up investigations — each written by one agent, then attacked by a second agent whose only job was to
break it — have narrowed the parking to a specific set of choices. This document is the decision surface.
Nothing below is decided. Where an options file and its attack file disagree, **the attack file wins** (the
attacker read the code a second time looking for holes); where they disagree and neither is provably right,
it is in "What is still unknown" rather than averaged into a false middle. `TimesheetApp.Core/` is untouched
by M10 throughout — one recommendation below asks whether that constraint should be broken.

**Honest read on timeline:** the safety-net gate is ~4–6 days of code plus three decisions that are not
engineering (who hosts the API, gate zero, the config fork). The full port is ~10–14 days. If hosting can be
answered in a week, M10 is weeks away. If hosting needs a machine that does not exist yet, M10 is months
away, and the honest thing is to say so now rather than discover it at the deletion commit.

---

## What we are asking you to decide

1. **Gate zero — who runs `SELECT COUNT(*) FROM Users WHERE password_hash IS NULL` against a *copy* of
   production, and when?** No option below can be sequenced until this number exists.
2. **Who hosts the API, on what machine, supervised how?** Today it is `dotnet run` in a foreground console
   window on localhost (`deploy-local.bat:38`). After M10 the whole company's ability to work depends on that
   window staying open, with no fallback.
3. **Flag day or overlap?** Do all users move off WPF at one announced moment, or do both front-ends run
   until the last person is through? This is a data-safety call, not a convenience one.
4. **Does the auth visibility work ship before the provisioning sweep** (so the app can tell you who still
   cannot log in), or does the sweep run against a hand-maintained spreadsheet?
5. **What shape does restore take** — offline CLI + runbook, runbook only, or a gated admin route — and does
   the mandatory `IsIntact()` fix to Core (`BackupService`) block M10 or ship with it?
6. **Scheduled jobs: startup-only port now, or a real scheduler?** And is the scheduler gated on the hosting
   answer (decision 2), or built speculatively before it?
7. **The config fork:** after WPF dies, nothing in the product can turn backup on or point it at a folder. Do
   we (a) add an admin route against a documented prohibition, (b) hand-edit `appsettings.json` + restart, or
   (c) at minimum surface the current state read-only?
8. **Does M10 gate on the whole PORT list, or only the four items that fail silently?** (A split gate is what
   every pass recommends — but the split point is yours.)
9. **Do the whole-organisation archive jobs run server-side at all?** On the desktop they wrote to one user's
   disk; on a shared host they materialise cross-team data nobody chose to share.
10. **Three explicit sign-offs**, each a product/security decision rather than a technical footnote: no
    cross-user timesheet grid (A3), retention becomes manual-only (A9), no auto-provisioning of users (A10).
11. **Who absorbs the 190 deleted tests?** `git rm -r src/TimesheetApp/` breaks `TimesheetApp.Tests` outright
    and removes 29% of it. This is priced in no plan.

---

## Blocker 1 — Auth cutover

### What is true today (cited)

- Schema migration v10 adds `password_hash TEXT` with **no default and no backfill**
  (`DatabaseInitializer.cs:331`), then promotes exactly one user — `MIN(id)` — to admin
  (`DatabaseInitializer.cs:336-337`). That admin's hash is also NULL. [VERIFIED]
- Login **fails closed** on a NULL/empty hash (`AuthSetup.cs:176-177`), by explicit design: *"Never treat it
  as 'any password matches': that is an authentication bypass, and it is exactly the state every user is in
  on a freshly migrated database."* [VERIFIED]
- Self-recovery is deliberately blocked (`AuthEndpoints.cs:87-93`, 400 *"Ask an administrator to set one"*).
  The admin reset (`POST /api/auth/users/{id}/set-password`, `AuthEndpoints.cs:116-138`) is the **only** way
  out of the NULL-hash state — one user, one call. [VERIFIED]
- `AdminBootstrap` generates a password **only for admin rows** (`AdminBootstrap.cs:47,87-124`), prints it
  once to the console, and persists it nowhere (`AdminBootstrap.cs:16,114-123`). If nobody captured that one
  startup's output, the only documented recovery is nulling the column directly in the database
  (`AdminBootstrap.cs:115`). Every non-admin stays at NULL indefinitely. [VERIFIED]
- **There is no bulk provisioning path.** `password_hash` has exactly two write sites in the entire codebase,
  both single-row (`UserRepository.cs:202-208`, `:235-243`). [VERIFIED — exhaustive grep]
- **The admin UI actively lies about this.** `canLogIn()` checks `username !== ''` only
  (`users.component.ts:122`), and the template renders the username as the affirmative "this account works"
  signal, with an explicit "No login" badge on the negative branch (`users.component.html:90-97`). On a
  migrated database — every user carrying a username from WPF's auto-provision, every hash NULL — the screen
  makes a **positive false claim about the entire population**. The component's own docstring says it means
  to check *"no username, **or created before a password was ever set**"* and implements only the first half.
  [VERIFIED by the adversarial pass]
- For contrast: WPF requires no credential at all — `Environment.UserName` → `Users.username`
  (`CurrentUserService.cs:25-37`), auto-provisioning a new account on no match
  (`MainViewModel.cs:268-287`). WPF is currently the only practically-working front door.

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **A — Procedure only** | Zero code. An admin sets N passwords one row at a time through the existing Users screen, distributes them out of band, tracks completion on paper. | The exit condition is a spreadsheet checked against a screen that contradicts it (`users.component.ts:122`). | Somebody is missed. They discover it *after* WPF is deleted, behind a 401, with no self-service route back in. |
| **B — Make the gap visible, then run A** | Project `has_password` in `UserRepository`, add it to an admin-only DTO, fix `canLogIn`, add an "N users cannot log in yet" banner. Core + API + Angular, no new write route. | Compile-time blast radius on the `User` record; a small information disclosure to admins who can already reset any password. | Cheap and total to revert — it is a projection and a boolean. No production data is written by any of it. |
| **C — Bulk-provision endpoint** | New admin route generating and returning `{username, password}` for every hashless user in one response. | **One HTTP call returns every credential in the company**, behind an admin gate that trusts a claim frozen into a persistent cookie without re-reading `is_admin` (`AuthEndpoints.cs:131`; `IsPersistent = true` at `AuthSetup.cs:203`). | The code reverts; the passwords do not. A CSV of live credentials now exists on somebody's laptop. |

### What the adversarial pass found

**The choice of B over A and C survives, and its central argument is understated** — the attack confirmed the
UI does not merely omit the signal, it asserts the opposite. What does *not* survive is the plan wrapped
around it: three of its four "prerequisites" are the actual blocker, and two can fail silently.

- **F1 — There is no deployment target, and `dotnet run` is not one.** `deploy-local.bat:38` runs in the
  foreground of a console window. Repo-wide grep for `UseWindowsService`, `New-Service`, `nssm`, `web.config`,
  IIS: **no hosting artifact of any kind**. WPF ran on every user's machine; after M10 the company depends on
  one console window surviving reboots, Windows Update, and someone tidying their taskbar — with the fallback
  deleted. The plan priced this availability change at zero. [VERIFIED]
- **F2 — The backup prerequisite can silently produce an unusable backup.** Under WAL, a `File.Copy` of the
  live `.db` yields a file that may not be a backup — the repo says so in its own words
  (`SqliteOnlineBackup.cs:11-16`). The safe procedure (stop the API, copy `.db` + `-wal` + `-shm` together, or
  drive `SqliteOnlineBackup.Copy`) is written down nowhere and reachable by no operator-facing path. This is
  the safety net every other risk in the plan leans on.
- **S1 — `DbPath` decides more than flag-day-vs-overlap.** `SqliteConnectionFactory.cs:21` states the Server
  profile's precondition outright: *"Single writer process **on the host's local disk** -> WAL is safe and
  wanted."* On a synced folder, running the API violates a precondition the code states about itself
  **permanently, flag day or not** — the `-wal`/`-shm` sidecars are created on every connection and sync out
  of band forever. "Move the database to local disk first" is not a parenthetical; on a synced path it is the
  first task.
- **S2/S3 — Option B's own implementation sketch ships a leak and a lie.** Projecting `has_password` in
  `GetAllAsync` only leaves it silently `false` in the other three `UserRepository` SELECTs
  (`:34,:50,:58`, one shared mapper at `:255-256`) — and adding it "last with a default" is precisely what
  removes the compile-time check the plan calls its safety. Separately, there is exactly one `User → UserDto`
  mapper (`Dtos.cs:204-205`), used by both the admin-gated `/api/users/all` (`SettingsEndpoints.cs:453-454`)
  **and the un-gated `/api/users`** (`:421-422`). Following the plan literally puts the field on the ungated
  route while believing the caveat was honoured.
- **S4 — Every API restart silently re-grants team membership.** `TeamBootstrapService.cs:50-57` re-runs
  `INSERT OR IGNORE INTO UserTeams … SELECT id, @t FROM Users` (`:111-113`) on **every** boot, against the
  lowest-id team, for every user. An admin's removal via `PUT /api/teams/{id}/members` is undone at the next
  restart, silently and unlogged — and the cutover guarantees restarts. Membership is the authorization bound.
- **S5 — No transport security, and this is not a differentiator.** `--urls http://…` everywhere;
  `AuthSetup.cs:102` sets `CookieSecurePolicy.SameAsRequest` with a comment conceding *"HTTP is a knowingly
  accepted risk on the internal network."* The procedure common to A, B and C is N passwords crossing the LAN
  in cleartext. Worth stating plainly, because it is currently the unexamined constant against which C is
  judged harshly and A and B kindly.
- **S6/M4 — Two smaller honesty defects inside the loop.** `savePassword` toasts *"X can now log in"* then
  re-reads a DTO that cannot confirm the write (`users.component.ts:284-285`), and the claim is false for a
  user whose username is NULL. And the banner needs an `is_active` filter nobody specified, or it counts every
  departed employee and never reaches zero.

### Recommendation (and what it depends on)

**Option B as the code choice — but not "Option B paired with a flag day" as an executable plan.** The
sequencing the attack proposes, each step ordered because it makes the next survivable:

1. **Read the startup banner** (`Program.cs:57-60`) and record which database the API actually opened. If it
   is on a synced root, moving it to local disk is the first task, not a footnote (S1). If already local, the
   gate closes free and overlap becomes a genuine option you can weigh.
2. **Establish a *verified* backup procedure** before anything else touches the database — stop the API, copy
   all three files, confirm the copy opens. Do not use the Settings "Backup now" button as the cutover backup.
3. **Settle hosting properly** — a supervised service that survives reboot, not a console window (F1). Most
   likely to be larger than the code work.
4. **Then Option B's code**, with four corrections to its own sketch: project `has_password` in all four
   SELECTs or type it `bool?` (S2); give `/api/users/all` its own DTO rather than adding a field to the shared
   `ToDto` (S3); filter the banner on `is_active` (M4); fix the `savePassword` toast (S6).
5. **Then the provisioning procedure**, gated on the banner reading 0, with S4's restart behaviour understood
   in advance.

**Flag day vs overlap remains yours and gets easier after step 1**, not before. On local disk, the repo's own
line (`start-web.bat:16-18`, *"you will not lose data"*) makes overlap defensible and restores the fallback
the brief asked for. On a synced root neither is safe until the database moves — which is why step 1 is step 1.

---

## Blocker 2 — Backup restore has no web path

### What is true today (cited)

- `RestoreAsync` (`BackupService.cs:97-126`): validate → self-restore guard (`:106-108`) → `ClearAllPools()`
  → pre-restore safety copy to `{dbPath}.pre-restore_{stamp}.bak` (`:121-122`) → overwrite (`:124`). Both
  copies go through the SQLite **online-backup API**, never `File.Copy`. [VERIFIED]
- `SqliteOnlineBackup.Copy` **deletes the destination and its sidecars first** (`:41`, `:85-90`), then opens
  the source (`:43`), then copies, then leaves the artifact `journal_mode=DELETE` (`:52`). Deleting the
  sidecars is the load-bearing half — a stale `-wal` from the replaced database is otherwise replayed over
  the restored file on next open. [VERIFIED]
- **There is no restore route and no list route.** `/api/ops/*` is exactly four routes
  (`SettingsEndpoints.cs:1000-1060`). The exclusion is deliberate and documented:
  *"`IBackupService.RestoreAsync` is NOT exposed in M8.3: it overwrites the live .db in place while the API
  holds open connections, which corrupts live readers."* (`SettingsEndpoints.cs:43-44`) [VERIFIED]
- `RestoreAsync` and `ListBackups` have **zero callers** outside WPF and tests — their only production callers
  are `SettingsViewModel.cs:287` and `:298`, the files M10 deletes. [VERIFIED]
- The API runs `Pooling=true`, `journal_mode=WAL` (`SqliteConnectionFactory.cs:60,65`), chosen explicitly at
  `Program.cs:75-76`. There is **no drain, no maintenance mode, no graceful-shutdown hook** anywhere in the API
  project; `/health` is a static `Results.Ok` (`Program.cs:237-240`). [VERIFIED]
- The project already drew the line this decision sits on: host-level state belongs in `appsettings.json`
  where *"exactly one person can change them and a restart makes it so"* (`settings.component.ts:45-60`,
  enforced by a test).

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **A — Runbook only** | One markdown file: stop the API, copy the live set aside, delete `.db` + `-wal` + `-shm`, copy the backup in, restart, verify. ~1 hour. | Entirely human, and both failure modes are silent: skip the sidecars and the restore is reverted on next open; skip the safety copy and there is no undo at all. | Recoverable only if the operator performed the step they skipped. |
| **B — Offline CLI in the API host** | `--list-backups` / `--restore <path>` branch in `Program.cs` before Kestrel starts, plus a runbook. ~½–1 day. No route, no contract, no client regen. | Runs `RestoreAsync` in the only regime its tests cover — quiesced, no concurrent connections. | Reverts in one commit. An executed restore is undone from the printed `.pre-restore_*.bak`. |
| **C — Admin route behind maintenance mode** | Middleware + state service + in-flight accounting + 4 routes + 2 contracts + Angular + OpenAPI regen. Several days. | Must guarantee **zero** open handles at the instant of `DeleteWithSidecars` — against fire-and-forget background work a request counter cannot see (`SettingsEndpoints.cs:1027`, `Program.cs:103`) and SignalR clients that auto-reconnect and re-query (`DataHub.cs:20-23,39`). | Corruption of production data triggered by a browser click, in the regime the code has no test for. |

### What the adversarial pass found

The attack ran two passes and reversed one of its own findings. **Option B's shape survives; its
specification does not.**

- **HOLE 1 — FATAL. Restore destroys the live database *before* validating the source.** `Copy` deletes the
  destination first (`SqliteOnlineBackup.cs:41`), opens the source second (`:43`), and `RestoreAsync`'s only
  checks are `File.Exists` and not-the-live-db (`BackupService.cs:99,106-108`). Point it at a truncated or
  half-synced file and: safety copy written, live `.db` + `-wal` + `-shm` deleted, *then* throw. **There is now
  no production database.** On restart, `ReadWriteCreate` (`SqliteConnectionFactory.cs:53`) creates one,
  `InitializeAsync` builds the v11 schema, `AdminBootstrap` seeds `admin`/`admin` — and the browser shows a
  working, logged-in, **completely empty** application. The codebase already contains the validator:
  `SqliteOnlineBackup.IsIntact` (`:64-79`), whose doc-comment says *"'Exists && Length > 0' is not evidence
  that a file is a usable database — six arbitrary bytes pass it."* Its only production caller is
  `PruneArchiver.cs:178`. **`RestoreAsync` never calls it.** Found independently by the B4 attack as N4.
- **HOLE 2 — The evidence for "restore is safe when quiesced" lives in files M10 deletes.**
  `TimesheetApp.Tests.csproj:21` references the WPF project, so `WalBackupSafetyTests` and `BackupServiceTests`
  are inside the blast radius. Relocating them is not free — `TimesheetApp.ApiTests` has no Moq reference.
- **HOLE 10 — The recommended exclusivity guard cannot detect a running server.** In WAL mode `BEGIN
  EXCLUSIVE` is defined to be identical to `BEGIN IMMEDIATE` — it takes the write lock and does not exclude
  readers. An API host between requests, sitting on idle pooled handles, has no write transaction open, so the
  probe returns "clear" against a fully running production server. The rejected port-bind probe fits this
  single-process, fixed-port deployment better.
- **HOLE 11 — FATAL as specified. The CLI can restore into the wrong database and report success.**
  `JsonAppConfig` resolves per-Windows-user paths (`:172-181`). Placing the CLI branch before the startup
  banner (`Program.cs:57-60`) skips the one diagnostic that would reveal it — a banner that exists precisely
  because this failure class already bit this project (`Program.cs:52-53`). And if the resolved `dbPath` does
  not exist, `RestoreAsync` **skips the safety copy** (`BackupService.cs:121`) and creates a fresh database —
  so the case where the wrong path was used is exactly the case where nothing revealing is printed.
- **HOLE 12 — "There are no backups to restore from" is false, and the reason is a live bug.**
  `NoOpDbBackupHelper` documents itself as *"the `IDbBackupHelper` the API host registers"* and warns that a
  full snapshot per bulk write is *"a disaster"* on a server — **but it is registered nowhere.**
  `Program.cs:103` registers the real one. So the API takes a full online backup before every Smart Fill apply,
  DefaultTask sync, retention run and team bootstrap, and **eleven restorable `.bak` snapshots exist right
  now** in the DB folder. This deserves its own ticket independent of M10, and it means the argument that
  "a restore path with nothing to restore from is ceremony" does not hold.
- **HOLE 9 — cuts *for* B.** The accessibility argument for C ("a non-technical admin needs a button") is
  inert today: `deploy-local.bat` and `start-web.bat` are localhost-only, so the web admin and the console
  operator are the same person at the same keyboard. Re-open C if hosting ever makes this LAN-reachable.

### Recommendation (and what it depends on)

**Option B, with six mandatory amendments, sequenced after the backup-production decision:**

1. `IsIntact(backupPath)` as a hard precondition **inside `RestoreAsync`** (not the CLI wrapper, so A and any
   future C inherit it), plus `IsIntact(dbPath)` after and a non-zero exit code. ⚠ This changes surviving Core
   code, which M10 was scoped to leave untouched. **Whether that blocks M10 or ships with it is your call.**
2. Move the CLI branch to after `Program.cs:60`; echo the resolved `DbPath` and config path; refuse to proceed
   when the live `.db` is absent rather than creating one. Non-negotiable.
3. Drop `BEGIN EXCLUSIVE` as the guard; use a port-bind probe, presented to the operator as **advisory**. The
   runbook's "close the server window and confirm it is gone" step stays load-bearing.
4. Strike "quiescence by construction" from the rationale — B earns in-process quiescence by construction and
   cross-process quiescence by operator discipline, same as A. Say so in the runbook.
5. Decide the test relocation before writing the command, and budget it.
6. The runbook names the **real** DB folder resolved from config, not `%APPDATA%` (which holds only
   `appsettings.json`), and invokes the built assembly rather than `dotnet run`.

**What it depends on:** whether `BackupFolderPath` gets set and what replaces the daily auto-backup — B's
discovery half is dead on arrival until that is answered. And before choosing B over C, run the ten-minute
experiment in "still unknown" below: if `File.Delete` throws on an open handle, C's data-safety objection
collapses to a cost objection, and the operational-capability side of the trade gets materially stronger.

---

## Blocker 3 — Scheduled jobs whose only caller is `App.xaml.cs`

### What is true today (cited)

Four jobs run inline in WPF's `OnStartup`, all best-effort try/catch (`App.xaml.cs:59-106`):

| # | Job | Call site | Gate |
|---|---|---|---|
| 1 | Auto-backup (BK-03) | `App.xaml.cs:63` | none — attempted on every launch |
| 2 | Export-hub 12-month/12-week backfill | `App.xaml.cs:83` | **only if** an export root is configured (`:80-82`) |
| 3 | Weekly standup archive backfill | `App.xaml.cs:100` | **only in the `else`** — i.e. only when NO export root is set |
| 4 | Monthly task-list archive backfill | `App.xaml.cs:104` | same `else` branch — mutually exclusive with job 2 |

- All four services are already `AddSingleton`-registered in the API (`Program.cs:104,107,108,112`) with
  singleton graphs — a background service could inject them directly, no scope juggling. [VERIFIED]
- **No `IHostedService` / `BackgroundService` / `AddHostedService` exists anywhere in `src/`.** [VERIFIED]
- None of the four issues a write statement; the one live-DB touch is `SqliteOnlineBackup.Copy`, and
  `WalBackupSafetyTests.cs:57-84` proves it is safe against an open, uncommitted WAL write transaction, ending
  in `PRAGMA integrity_check == "ok"`. [VERIFIED — this answers a question the options pass left open]
- **A live defect, independent of M10:** `ExportHubService.cs:145` copies the whole live database into
  `{root}/db` **unconditionally on every run**, outside the three `File.Exists` guards, and `Prune` keeps only
  `BackupKeepCount` (default 30). Reachable today via `POST /api/ops/export/run` — thirty clicks evicts every
  older snapshot. Someone should be told regardless of which option is chosen. [VERIFIED]
- **`POST /api/ops/backup/run` is not a drop-in for job 1** — it calls `BackupNowAsync()`, bypassing both the
  `AutoBackupEnabled` kill-switch and the once-per-day guard (`BackupService.cs:59-69` vs `:35-36`). Scheduling
  that route is a semantic change. [VERIFIED]

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **1 — `BackgroundService` + persisted markers** | One new file (~150–200 lines), one `Program.cs` line gated on `!IsEnvironment("Testing")`, 3–6 tests. ~1 day. | Introduces an hourly cadence the desktop never had, against job 2's ungated DB copy. The Testing gate means the service **never starts in any test host**. | A lying marker: a failed backup reads as "done today", and nobody notices until they need the backup. |
| **2b — Run once at API startup, off the request path** | ~15–25 lines in the existing startup block (`Program.cs:173-209`), following the `Task.Run` precedent at `SettingsEndpoints.cs:1027`. Cheapest to build and to undo. | "Once per day" becomes "once per restart". On a long-lived server that is once in September. | A console window left open Friday-to-Tuesday backs up once. |
| **3 — Ops routes + host scheduler** | 3 routes + tests + a runbook. Cost lands in operations, not the repo. Most reversible. | **A non-interactive caller needs a stored admin credential or a new API-key surface** — the single biggest new risk of the three. The schedule leaves the repository and is invisible to anyone reading the code. | A missing backup is invisible until needed. |

### What the adversarial pass found

**The recommendation's conclusion may be defensible; its stated reason is built on a deployment this
repository explicitly records as not existing.**

- **H1 — FATAL. The argument that kills Option 2 assumes a server the project records as nonexistent.**
  `.planning/STATE.md:274` — *"Production hosting collides with the deferred 'the company has no server'
  blocker."* `.planning/ROADMAP.md:11,53` — *"Remote hosting — still unsolved and still blocking everyone but
  the user."* The API process lifetime today **is one human's working session on one machine**, started from a
  `.bat` and ended by closing a window — the same lifetime WPF had. Under that deployment, Option 2 is not a
  degraded port of job 1, it is an **exact** one, and Option 1's hourly timer is a behaviour *increase* —
  up to 8–10 evaluations a working day against job 2's ungated DB copy, which is the very hazard the option
  then spends a marker mechanism defending against. The options pass never opened STATE.md or ROADMAP.md.
  *Honest counter, which the attack states itself:* a console left running across several days backs up only
  once, and `start-web.bat:39` titles the window *"do not close"*. That gap is real — but it is the status quo
  gap the WPF app always had.
- **H2 — FATAL, and self-undermining: the deployment that justifies Option 1 is the one that breaks it.**
  Under a Windows Service identity, `JsonAppConfig` resolves `%APPDATA%` and `MyDocuments` of the *service
  account* (`:172-181`), a missing config file is swallowed (`:161`), `DbPath` falls back to that account's
  Documents folder, `InitializeAsync` creates a brand-new empty database there, and `AutoBackupEnabled`
  defaults `false` so the scheduler returns `false` **forever**. The only defence is a `Console.WriteLine`
  (`Program.cs:57-60`) that has no console under a service. Result: running app, registered scheduler, no
  errors, no backups, and not even the right database. **Per-user config resolution must be fixed before any
  unattended scheduler is trusted, not after.**
- **H3 — "Log loudly" is not implementable under the "no Core change" constraint the same option asserts.**
  `AutoBackupIfDueAsync` collapses five outcomes into one `bool`, and the two most likely failures **return
  `false` rather than throwing** (`BackupService.cs:59-69`), so the specified `try/catch` catches nothing. The
  scheduler can log exactly one true sentence: *"it returned false."* That is the same silence with a
  timestamp — and "log loudly" was the option's own named mitigation for its own worst risk.
- **H5 — Porting job 2 silently drops a documented ordering precondition.** `App.xaml.cs:73-75` states it in
  words: the backfill runs *after* `DefaultTaskSync` so teams and their synced default tasks exist *"before
  any export is built"* — and `IDefaultTaskSyncService.SyncAsync()` is **absent from the API's startup block**.
  Because the guards are `File.Exists`-only (`ExportHubService.cs:112,124,136`), an under-populated markdown
  file is written once and **never revisited by anything**.
- **H6 — The Testing gate deletes the only integration coverage of the thing being built.** Both factories set
  `UseEnvironment("Testing")` (`ApiFactory.cs:112`, `SignalRTestFactory.cs:65`), so a registration mistake that
  throws at `Build()` in production is **green in CI**, because in CI the registration never happens.

### Recommendation (and what it depends on)

The attack proposes: **Option 2b now, plus one route from Option 3** (`POST /api/ops/backup/auto` →
`AutoBackupIfDueAsync()`, since the existing backup route is not equivalent), and **re-open Option 1 only as
part of resolving the hosting blocker**, with the config-identity fix (H2) sequenced *before* it.

Under the deployment that actually exists, 2b is a faithful port — same trigger, same branching, same
once-per-launch cadence, ~20 lines in a file that already has a startup block, no new pattern, no new marker,
no new silent-failure surface, and testable inside `ApiFactory` unlike a Testing-gated hosted service.

⚠ **Note a genuine disagreement between two attack passes**, which you should resolve rather than let me
average: the B3 attack says *do not build a scheduler until hosting is answered*; the B4 attack says *if you
build one, build it in-process rather than as a Task Scheduler CLI*, because an out-of-process job splits the
backup writer from the backup viewer and has no failure channel (N2/N6 below). These agree on the important
point — **the hosting decision comes first** — and differ only on what comes second.

**Also unresolved, needing an owner:** whether jobs 3/4 should run server-side at all. They pass `null`
teamIds (`StandupArchiveService.cs:58`, `TaskListArchiveService.cs:54`) = whole-organisation markdown, written
to one user's disk on the desktop and to a shared host on the web. And if production configures an export
root, **the desktop never ran jobs 3/4 either** — porting them is an addition, not a preservation.

---

## The 29 MISSING behaviors, triaged

**On the count:** the audit table has **33 rows** (`M10-COVERAGE-AUDIT.md:49-81`); its prose says "29 distinct
behaviors"; the evidence pass grouped them to 28; the options pass grouped them to 22 work items. All three are
grouping judgments over the same 33 rows — **nothing is disowned by any pass**. The grouping below is by *what
one person would build in one sitting*, because that is the unit a scope decision needs. The adversarial pass
re-read every disposition looking for one miscategorised in a way that changes the gate, and **found none**.

### PORT — must exist before, or shortly after, the deletion

| # | Item | Audit rows | Why it must be ported |
|---|---|---:|---|
| **P1** | Auto-backup trigger (BK-03) + export-hub catch-up backfill | 2 | The only item on the whole list where the loss is **not recoverable later** — a markdown archive skipped in July regenerates in September; a backup not taken on the day the data was correct cannot. |
| **P11** | **Backup configurability** — *derived blocker, not an audit row* | 0 | `SettingsViewModel.cs:264-266` is the **only production writer** of `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount` anywhere in the repo. Deletion strands all three with no writer, and both default to blank/off. **P1 does not work without it.** |
| **P2** | A restore path of some kind | 1 | Deleting WPF deletes the only working restore path in the product, at the same moment P1's automatic backups stop. |
| **P3** | Backup visibility (`GET /api/ops/backup/list` + Settings list) | 1 | `ListBackups()`'s only production caller is the WPF view-model. This is what makes P1 and P2 trustworthy — but only if it distinguishes "not configured" from "configured, none yet". |
| **P4** | Weekly standup + monthly task-list archive backfill | 2 | Conditional: the desktop only ran these when **no** export root was configured (`App.xaml.cs:80-82` vs `:97-106`). A port may be an addition rather than a preservation. |
| **P5** | Active-team switcher (+ persistence, TeamFilter reset, visibility rule) | 4 | A multi-team user **has no way to change their active team on the web at all** — the sidebar `<select>` is hardcoded mockup markup (`sidebar.component.html:46-52`) and `setActiveTeam()` has zero callers. Server half is fully built and tested. |
| **P6** | Backlog team visibility (filter + TEAM column) | 2 | With >1 team in view nobody can tell which team owns a row. Client-side by the service's own documented design (`worklog.service.ts:452-462`) — no regen, cheaper than first costed. |
| **P7** | Log Work grid affordances (week jump, month filter, holiday rendering, >8h styling) | 4 | Today a user discovers a holiday by typing into the cell and being refused. Highest-value item is the holiday shading. |
| **P8** | Task List display gaps (PROJECT label in team mode, all-undated Gantt caption) | 2 | With ≥2 teams checked, no web user can see which project a backlog belongs to. ~2 hours. |
| **P9** | Daily Board issue solution text | 1 | **One template line.** You can see *that* an issue resolved, never *how*. Highest value-per-effort in the audit. |
| **P10** | Smart Fill multi-task one-shot fill | 1 | Contains a real design fork: the web reimplemented the split math in TypeScript and **the two implementations disagree** — `distributeHours(8,3)` → `[2.7,2.7,2.6]` vs Core's `[2.6,2.6,2.8]`, while `smart-fill.ts:8` claims they are "the same rule". |

### ACCEPT — the loss is fine; reason stated

| # | Behavior | Why acceptable |
|---|---|---|
| A1 | OneDrive conflict-copy banner (XC-08) | Needs a locally-synced `.db` with multiple writers; one server-hosted DB removes half the precondition structurally. **Caveat: returns if the host's own data directory is synced.** |
| A2 | Journal-integrity banner (XC-09) | Under WAL the `<db>-journal` file this guard watches for is never created — the guard is structurally inert on the web whether or not it has a UI. |
| A3 | Log Work entry-target user picker + read-only team view **[SIGN-OFF]** | The API refuses it *by design* (`TimesheetEndpoints.cs:56-62`). A deliberate privacy narrowing that needs a named yes, not silence. |
| A4 | Smart Fill backlog-code search | Workaround is one already-covered extra step: add the task to the grid, then use the on-grid Smart Fill that exists. |
| A5 | Smart Fill "Full 8h" mode | Identical outcome via single-task DistributeEven with Total = 8 × day-count. **Awkward until P7 ships**, because day-count excludes holidays the user cannot see. |
| A6 | Smart Fill arbitrary From/To range | **The closest call on the list.** Turns one multi-week backfill into N. Accepted on a workaround argument with no usage data — if anything gets pushback, it is this. |
| A7 | Smart Fill Preview-then-Confirm gate | Consistent with every other write on the screen (attempt-then-revert-on-400). `/api/smartfill/validate` is wired end to end and lacks only a call site. |
| A8 | SharePoint destination Verify button | A pre-flight convenience, not the only safety net — the export itself still hard-validates and blocks on `Level: Error`. A bad destination surfaces one cycle later. |
| A9 | Retention automatic run at startup **[SIGN-OFF]** | Retention is default-OFF and destructive; requiring a click is arguably *safer*, not merely a downgrade. `RetentionEnabled` becomes a dead flag either way. |
| A10 | Auto-provision an unmapped user **[SIGN-OFF]** | WPF granted access with **no credential at all**; requiring real accounts is the entire point of adding auth. **Safe as a disposition, unsafe as a schedule — see gate zero.** |
| A11 | First-run auto-join to a team (TM-09) | Only ever meaningful paired with A10. Once an admin creates the account anyway, assigning a team is one more field in an already-manual flow. |

### OBSOLETE

| # | Behavior | Why |
|---|---|---|
| O1 | Window chrome (1180×760, min 980×620, CenterScreen) | The browser owns window sizing and placement; there is nothing for a web app to port. |

**M10's real remaining scope is the PORT list: 11 items** — 10 grouped from 20 audit rows, plus P11, which is
not an audit row at all because it is not a behavior WPF *had*; it is a capability the product *loses* when
WPF's Settings screen goes. **Four of the eleven (P1, P11, P2, P3) fail silently and are the honest content of
a gate; the other seven are discovered by a user on first use, which is its own kind of safety.**

### What the adversarial pass found about the scope itself

- **N5 — The deletion is not "four project-file edits".** `TimesheetApp.Tests` references the WPF project
  directly, and **18 test files import WPF namespaces, plus a 19th loading WPF resources by pack URI — 190
  tests, 29% of that project's 652.** The largest single file is `SettingsViewModelTests.cs` at 41 tests,
  covering `SettingsViewModel` — *the exact writer P11 exists to replace* — and no plan budgets tests for the
  replacement. This does not change which option is right; it changes who is surprised on the day.
- **N1 — Neither branch of the config fork is safe as written.** `JsonAppConfig` treats an unreadable config
  as an empty one: a `JsonException` returns `null` (`:166-169`) and **every field including `DbPath` reverts
  to its default** (`:57`) — and a wrong `DbPath` does not fail, it creates a new database and boots normally.
  Hand-editing JSON full of Windows paths has two silent failure modes (`"C:\data"` → parse error → repoint;
  `"C:\backups"` → `\b` is a *legal* escape → garbage path, no exception). Fork (a) fails the same way:
  `Save()` serialises all ten fields from in-memory state (`:140-157`), so the first admin route call
  **overwrites the good config with defaults**. A fail-fast config-load guard (~½ day) is a precondition for
  *both* branches — and it is in neither.
- **N3 — There is a fourth live "operator believes it worked", and P1 proposes to automate it.**
  `runExport()` renders `` `Export written to ${r.value}` `` plus toast *"Export complete"* unconditionally
  (`settings.component.ts:577-578`), so a failed export displays as **"Export written to failed: \\server\share
  — Access to the path is denied. ✓ Export complete"**. It sits immediately below `runBackup()`, which was
  fixed for exactly this defect in M9.2. Two-line client fix, on no list in M10.
- **N7 — One stated reason for deferring the UI ports is false.** `Palette.Dark.xaml` **is** git-tracked, so
  `git rm` relocates it into history like every other file. The extraction is still worth half an hour — but
  the thing actually worth preserving is `PaletteParityTests` (the automated Light/Dark key-set invariant),
  not the hex values copied into a document.
- **N8 — Two audit rows have already gone stale** (the "Backup now lies" PARTIAL was fixed in M9.2; so was
  `.cell input.invalid`). Any plan that freezes the audit as its spec should budget a re-verification pass at
  the moment the deferred work starts, not treat a three-month-old document as authoritative.

---

## What is still unknown

Nothing below was resolvable from the code, and each one changes a decision.

**Genuine contradictions between passes that I cannot adjudicate:**

1. **Does `File.Delete` on the live `.db` throw, or silently succeed, while another process holds an open
   handle?** The B2 options file lists this as COULD-NOT-DETERMINE in §1.8 — and then §1.9, its
   *"single most important inference"* and the load-bearing argument against Option C, **requires the delete
   to succeed**. Meanwhile `SqliteOnlineBackup.cs:36-37` asserts the opposite: *"otherwise this throws rather
   than silently corrupting it."* The two sections are never reconciled. If it throws, Option C's headline
   risk falls from *"silent corruption by a browser click"* to *"a 500 and an orphan file"* — the difference
   between "C is unsafe at any speed" and "C is merely expensive". **This is one ten-minute experiment on a
   scratch database** (open a WAL connection with `Pooling=true`, attempt `File.Delete`). Never on production.
2. **In-process scheduler or out-of-process CLI, if a scheduler is built at all?** The B3 attack argues 2b plus
   one ops route until hosting is answered; the B4 attack argues in-process over a Task Scheduler CLI, because
   splitting the backup writer from the backup viewer lets P3 report an *affirmative false answer* — a
   populated, four-month-stale list rendered as current, since `ListBackups()` parses timestamps out of
   filenames. Both agree hosting comes first; they differ on what comes second.

**Facts nobody could establish:**

3. **Whether existing non-admin employees have NULL password hashes in production.** Gate zero for every
   option. Requires opening a copy of the production database; no agent did, per the read-only constraint.
4. **Where the production `DbPath` actually points, and whether it is on a synced folder.** Partially known,
   and worth stating precisely: one agent read `%APPDATA%\TimesheetApp\appsettings.json` on **this developer
   machine** and reports `DbPath = C:\Users\Admin\Documents\TimesheetApp\timesheet.db`,
   `BackupFolderPath: null`, `AutoBackupEnabled: false`, `BackupKeepCount: 30`, and separately verified that
   Documents is **not** OneDrive-redirected here. A second agent, reading the same constraint more strictly,
   declined to open the file and built its entire flag-day-vs-overlap analysis on the premise that the value
   was unknowable. **Neither tells you what the production host's config says**, and the circumstantial
   evidence that this codebase expects a synced folder (`SqliteConnectionFactory.cs:15-18`,
   `SqliteMaintenance.FindConflictCopies`) remains unexplained.
5. **Which Windows account will run the API in production** — decides which `%APPDATA%` config and which
   database the process actually opens (`JsonAppConfig.cs:172-181`, `Program.cs:34-36,52-53`).
6. **Whether the production deployment configures an export root** — decides whether P4 is a port or an
   addition, and whether retention was ever automatic at all.
7. **What `SqliteConnection.ClearAllPools()` does to a connection that is currently checked out** (an
   in-flight request) versus one merely idle in the pool. `Microsoft.Data.Sqlite` is a NuGet dependency, not
   vendored source. The two plausible behaviours lead to materially different failure modes; neither was
   certified.
8. **Whether `PRAGMA journal_mode=DELETE` fails silently against a live WAL database.** This is the mechanism
   behind the flag-day-vs-overlap reasoning, and it is `[ASSUMED]` in both the options file and its attack.
   Settleable cheaply on a scratch copy — two processes, one pragma, outside production.
9. **Whether `BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source across two
   *processes*.** Answered for the single-process case by `WalBackupSafetyTests.cs:57-84`; the cross-process
   case — the one `start-web.bat:16-18` warns about — remains open, and `SqliteOnlineBackup.Open` sets no
   `busy_timeout` and no `DefaultTimeout` (`:92-105`), unlike the Server profile.
10. **How many users there are.** The A-vs-C trade on Blocker 1 is entirely a function of N, and nobody
    inspected the database.
11. **Whether multi-team users are the common case.** If they are, P5 stops being a deferrable affordance and
    belongs in the gate — it is the one deferred item that leaves a whole class of user unable to reach their
    own data. A five-minute answer from whoever knows the team structure changes the recommendation.
12. **Whether Windows Task Scheduler's defaults would even fire the job** — *"run as soon as possible after a
    missed start"* is off by default, *"only on AC power"* is on, and *"run only when the user is logged on"*
    reproduces the exact liveness problem the CLI shape was chosen to avoid. Platform behaviour, not
    verifiable from this repo; needs checking by whoever owns the host.
