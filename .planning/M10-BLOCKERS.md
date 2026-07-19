# M10 — Blocker dispositions (decisions required)

M10 (delete `src/TimesheetApp/`, the WPF app) is parked. The coverage audit returned **DO NOT DELETE YET** on the strength of 33 MISSING rows, and a second pass narrowed those to three real blockers plus a triage list. Nothing below is a coding problem — the code is understood. Every item is a decision about operational procedure, data safety, or accepting a permanent loss of behavior. This document exists to make those decisions possible, not to make them. `TimesheetApp.Core/` is untouched by M10 throughout; one open question asks whether that constraint should be broken.

**Provenance and its limits.** Each blocker has an evidence file; two of the three were then attacked by a second agent reading the code adversarially. **Blocker 1 (auth) was never attacked and has no options file** — its options table below is assembled by me from the evidence file and is therefore the least-tested analysis in this document. Where an attack file contradicts its options file, the attack wins. I independently re-verified the four claims that most affect a decision (marked ✅ below).

---

## What we are asking you to decide

Answer these and M10 unparks. Several are one sentence from the right person.

1. **Has anyone run `SELECT COUNT(*) FROM Users WHERE password_hash IS NULL` against a copy of production?** If not, someone must — every downstream decision here is shaped by that number, and nobody has it.
2. **If that number is large: who sets N passwords one at a time, over what window, and how are users told?** There is no bulk provisioning path and no way to build one without new code.
3. **Do you accept that after M10 nobody can change their own password?** The self-service route exists on the server and has no UI anywhere in the SPA; only an admin reset can change a password.
4. **What shape does backup restore take — documented runbook, offline CLI command, or a gated admin route?** This determines who can execute a restore under pressure, and how badly it can go wrong.
5. **Does the `IsIntact` fix to `BackupService` block M10, or ship after it?** It is a change to surviving Core code, which M10 was scoped to leave untouched — but without it a restore can destroy the live database before discovering the replacement file is not a database.
6. **Do you fix "no backups are being produced at all" before building a restore path, or after?** On the config this repo resolves against, `BackupFolderPath` is null and `AutoBackupEnabled` is false.
7. **What runs the API — a console window someone opens, or something that survives a reboot?** Called a "deferred decision" in `deploy-local.bat:11-13`. The attack argues it is now a precondition: a scheduled backup inside an unsupervised `dotnet run` has *worse* liveness than the WPF trigger it replaces.
8. **After WPF is deleted, how does anyone turn the backup on?** ✅ The WPF Settings screen is the only code in the product that writes `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount`. Options: an admin route, or a documented host-file edit plus an API restart.
9. **Does the production deployment configure an export root?** One fact, and it decides whether two of the four scheduled jobs are a port or a brand-new behavior.
10. **Are the whole-organisation markdown archives acceptable on shared host storage?** On the desktop they landed on one user's own disk. Only someone who knows whether teams may see each other's records can answer.
11. **Which gate shape: build all 16 PORT items before deleting, split the gate (safety net before, affordances after), or delete now and port against a git tag?**
12. **Four ACCEPTs need a named person's yes, not silence:** removing the entry-target user picker (a deliberate privacy narrowing), retention becoming manual-only, removing WPF's credential-less auto-provisioning, and losing the OneDrive conflict-copy banner.
13. **Are multi-team users common or rare?** If common, the active-team switcher stops being a deferrable affordance — it is the one deferred item that leaves a class of user unable to reach their own data.

---

## Blocker 1 — Auth cutover

> ⚠️ Evidence only. No options file, no adversarial pass. Read this section more sceptically than the other two.

### What is true today (cited)

**Nobody can log into the web app without a password hash, and migration created none.** Schema migration v10 adds `password_hash TEXT` with no default and no backfill (`DatabaseInitializer.cs:331`), then promotes exactly one user — `MIN(id)` — to admin (`DatabaseInitializer.cs:336-337`). Login hard-401s an empty hash and the code says why: *"A NULL password_hash means 'has never had a password set' => CANNOT LOG IN. Never treat it as 'any password matches': that is an authentication bypass, and it is exactly the state every user is in on a freshly migrated database"* (`AuthSetup.cs:176-177`). **[VERIFIED]**

**Exactly one person has a documented way in, and it may already have been lost.** `AdminBootstrap.EnsureAdminPasswordAsync` (`AdminBootstrap.cs:41-127`, run at every startup via `Program.cs:181-184`) generates a random 24-char password for admins whose hash is NULL, atomically claims the slot (`UserRepository.cs:235-243`, `WHERE password_hash IS NULL`), and **logs it once** (`AdminBootstrap.cs:116-123`). The class states: *"There is no secret to leak, because it exists only in that one log line."* (`AdminBootstrap.cs:16`). If nobody captured that startup's console output, the only documented recovery is nulling the column directly in the database (`AdminBootstrap.cs:115`). It touches admins only — the loop iterates a list filtered to `u.IsAdmin` (`AdminBootstrap.cs:47,87-124`). **[VERIFIED]**

**Everyone else needs an admin, one at a time.** Self-service set-password requires the current password, which nobody has — the endpoint returns 400 *"No password is set for this account. Ask an administrator to set one."* (`AuthEndpoints.cs:87-93`). The admin reset (`POST /api/auth/users/{id}/set-password`, `AuthEndpoints.cs:116-138`) is the only exit, and the test suite says so in as many words (`AuthEndpointsTests.cs:296-298`). Both write sites are single-row (`UserRepository.SetPasswordHashAsync`, `TryBootstrapAdminPasswordAsync`); an exhaustive grep for `password_hash` writes found no others. **[VERIFIED — absence]**

**Two capability gaps found that nobody had recorded:**

- **No self-service password change exists in the SPA.** The generated client function `api/fn/auth/auth-set-password.ts` has zero callers anywhere in `timesheet-web/src/app`. A user given a password by an admin cannot change it themselves — ever. **[VERIFIED — absence]**
- **Nobody can see who still needs a password.** `UserDto` (`Dtos.cs:61-62`) carries no password indicator; `password_hash` lives on the separate `UserCredentials` record returned only by `GetCredentialsAsync(username)` (`UserRepository.cs:190-199`), which no `/api/users*` route calls. Worse, the admin UI's own `canLogIn` helper checks `username !== ''` and nothing else (`users.component.ts:121-122`) — so on a migrated database **every user is displayed as able to log in when none of them can.** **[VERIFIED]**

**For scale:** WPF requires no credential at all. `CurrentUserService.ResolveAsync` maps `Environment.UserName` straight to a row (`CurrentUserService.cs:25-37`), and `MainViewModel.ResolveCurrentUserAsync` auto-provisions a new user if there is no match (`MainViewModel.cs:268-287`), then auto-joins them to the lowest-id team (`MainViewModel.cs:224-240`). Anyone who can launch the exe is in. **That is today's only practically-working front door for the whole user base.**

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **A — Click through everyone first.** Admin sets each password via the Users screen "Password" button before deletion; distribute out-of-band. | N × (open dialog, type, save) using an existing, tested path (`users.component.ts:273-284`, `users.component.spec.ts:265-273`). Zero code. | Scales linearly and has no progress view — the admin cannot tell who is done (see the `canLogIn` defect). Fine at N=15; grim at N=150. | Someone is missed. They are locked out on day one with no self-recovery, and no screen shows who. |
| **B — Build a bulk provisioning path first.** A CSV import or an admin sweep endpoint. | New endpoint + UI + tests. No existing surface to extend — the two write sites are strictly single-row. Multi-day. | New authentication-adjacent surface, built under schedule pressure, handling credentials. | A provisioning bug writes wrong or weak hashes across the whole user table at once. |
| **C — Delete now, remediate reactively.** Users call the admin when locked out. | Zero. | Requires the admin's own password to exist and be known — see the one-time log line. Turns a migration into a helpdesk queue on the first working morning. | If the admin's one-time password was never captured, there is **no admin**, and recovery is direct database surgery. |
| **D — Keep WPF alive as a transition window.** Delete only after everyone has logged into the web at least once. | Zero code; M10 slips by the length of the window. | Two front-ends against one database for the duration — the multi-writer precondition the web architecture removes. | The window never closes, and M10 is parked indefinitely. |

### What the adversarial pass found

There was no adversarial pass on this blocker. The B4 attack file touches it in passing (H5) and makes one point worth carrying: **"answer the question" is not one gate item, it is two.** If the count comes back bad, the deletion is blocked by *work that does not exist yet* — the provisioning sweep and the user communications — and "the check came back non-zero" on the morning of the deletion is a schedule event, not a five-minute one.

### Recommendation (and what it depends on)

**Run the count first; it selects the option, and nothing else can.** With that number in hand I would pick **A for a small N and B for a large one**, and I would treat **C as unacceptable at any N** until it is confirmed that a working admin password exists on the production instance — because C's failure mode is "there is no administrator", recoverable only by editing the database by hand.

Two things I would do regardless of N, both small: **wire the existing self-service set-password route to a UI** (the endpoint is built and tested; it is missing only a screen), and **fix `canLogIn` or add a password-set indicator to `UserDto`**, so the admin doing the work can see who is left. Without the second one, option A is executed blind.

**I am not deciding whether the transition window in option D is worth its cost.** That trades M10's schedule against a period of dual-writer exposure, and how much that exposure matters depends on where the production database physically sits — which nobody has established (see *Still unknown*).

---

## Blocker 2 — Backup restore has no web path

### What is true today (cited)

**Restore exists in Core, is properly built, and has no caller that survives M10.** `RestoreAsync` (`BackupService.cs:97-126`) validates the path, refuses to restore the live DB onto itself (`:106-108`), then off-thread: clears the connection pool, writes a pre-restore safety copy to `{dbPath}.pre-restore_{stamp}.bak` (`:118-122`), and overwrites the live database (`:124`). Both copies go through SQLite's online-backup API, never `File.Copy`. Its only production callers are `SettingsViewModel.cs:287` and `:298` — the WPF app M10 deletes. **[VERIFIED]**

**The API's refusal to expose it is correct engineering, and documented:** *"`IBackupService.RestoreAsync` is NOT exposed in M8.3: it overwrites the live .db in place while the API holds open connections, which corrupts live readers."* (`SettingsEndpoints.cs:43-44`). The API runs `SqliteProfile.Server` — `Pooling=true`, `journal_mode=WAL` (`Program.cs:75-76`, `SqliteConnectionFactory.cs:58-66`) — so idle native file handles persist between requests. There is **no drain, no maintenance mode, no graceful-shutdown hook** anywhere in the API project, and `/health` is a static `Results.Ok` that cannot report readiness (`Program.cs:237-240`). **[VERIFIED — absence]**

**There is also no way to see what backups exist.** `ListBackups()` is fully implemented (`BackupService.cs:71-87`) with zero callers in the API or the SPA. `/api/ops/*` is exactly four routes, and the only backup one calls `BackupNowAsync()` (`SettingsEndpoints.cs:1051-1059`). **[VERIFIED]**

**And there may be nothing to restore.** On the config this environment resolves against, `BackupFolderPath` is null and `AutoBackupEnabled` is false, so `POST /api/ops/backup/run` writes no file. The scheduled auto-backup's only caller is `App.xaml.cs:63`. The only artifacts on disk are 11 files named `timesheet.db.{stamp}.bak` written by `DbBackupHelper`, which do **not** match `ListBackups`' `timesheet_*.db` glob. **[VERIFIED by two agents on this machine; not established for production.]**

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **A — Documented manual file-swap runbook.** Stop API, copy live set aside, delete `.db` + `-wal` + `-shm`, copy the artifact in, restart, verify. | One markdown file. Under an hour. | Every safety step is human-enforced. Skipping the sidecar deletion is silent — SQLite replays the *replaced* database's `-wal` over the restored file on next open. | The restore appears to work and silently restores the data you were discarding. No `.pre-restore` copy exists because no code ran. |
| **B — Offline CLI in the existing API host.** `-- --restore <path>` / `-- --list-backups`, dispatched before Kestrel starts. | One new file (~60-80 lines), ~15 lines in `Program.cs`, 2-3 tests, a runbook. No route, no OpenAPI regen, no Angular. ~1 day. | In-process quiescence is guaranteed by construction; **cross-process is not** — nothing stops it running against a live second instance. | With the other process's pool momentarily empty, the delete succeeds and process 1 recreates an empty database mid-copy. |
| **C — Admin routes behind a maintenance gate.** 503 middleware + in-flight accounting + list route + restore route + Angular UI. | Largest by far: middleware, state service, 2 contracts, 4 routes, OpenAPI regen, Angular component + specs. Several days. | Must guarantee **zero** open handles at the instant of deletion — and the process contains fire-and-forget background work invisible to a request counter (`SettingsEndpoints.cs:1027`, every `DbBackupHelper` call). SignalR clients auto-reconnect and re-query throughout the window (`DataHub.cs:20-23,39`). | An empty database auto-created at the production path — triggered by a browser click, on production data, in the regime the code has no test for. |

### What the adversarial pass found

The attack **upheld B's shape and demolished its specification.** Five findings change the decision:

- ✅ **FATAL — restore destroys the live database before checking the replacement is a database.** `SqliteOnlineBackup.Copy` deletes the destination and its sidecars at line 41, and only opens the source at line 43 (verified by me: `SqliteOnlineBackup.cs:41-44`, `DeleteWithSidecars` at `:85-90`). `RestoreAsync`'s only source checks are `File.Exists` and not-the-live-DB. The codebase **has** an integrity check — `SqliteOnlineBackup.IsIntact` (`:64-79`), whose own comment says *"six arbitrary bytes pass"* an existence check — and ✅ its only production caller is `PruneArchiver.cs:178`, gating permanent row deletion. **The more destructive operation is the one that skips the check.** The failure chain is worse than a crash: the operator restarts, `SqliteConnectionFactory.cs:53` opens `ReadWriteCreate` and creates an empty file, migrations build the v11 schema, team bootstrap runs, `AdminBootstrap` seeds `admin`/`admin` — and the browser shows a working, logged-in, completely empty application. This defect is in `RestoreAsync` itself, so **it applies to options A, B and C equally.**
- **Two of B's four rationales rest on tests M10 deletes.** `WalBackupSafetyTests` and `BackupServiceTests` live in `TimesheetApp.Tests`, which has `<ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />` (`TimesheetApp.Tests.csproj:21`). That reference dangles at deletion. Relocating them is not free: `TimesheetApp.ApiTests` has no Moq reference, and the fixtures are built on `Mock<IAppConfig>`.
- ✅ **The runbook half points at the wrong folder.** `DefaultConfigPath()` resolves from `ApplicationData` and `DefaultDbPath()` from `MyDocuments` — verified by me at `JsonAppConfig.cs:172-182`. **They are deliberately different directories.** A runbook anchored on `%APPDATA%` sends the operator somewhere with no database: the "copy it aside" and "delete the sidecars" steps become silent no-ops while only the destructive step lands.
- **The accessibility argument for option C is currently inert.** `deploy-local.bat:38` and `start-web.bat:39` bind localhost only, and the LAN decision is explicitly deferred (`deploy-local.bat:11-13`). **The only person who can open the web UI today is sitting at the host machine** — so C's entire justification, that a non-technical admin could execute a restore unaided, buys approximately nothing right now. This cuts *for* B.
- **One cited fact was misleading and should not consume budget.** "Backup now reports success on a null" was fixed in M9.2 — `settings.component.ts:554-557` shows *"Backup did not run — no file was written"*, locked by a regression test.

### Recommendation (and what it depends on)

**Option B, amended, and sequenced after the backup-production decision.** It is the only option that starts quiesced rather than having to achieve quiescence, it runs `RestoreAsync` in the only regime any test covers, and it is consistent with a ruling this project already made twice (`settings.component.ts:45-60`: host-level state changes belong at the host with a restart, not in a browser tab). It is deletable in one commit.

Four amendments are not optional in my reading: **`IsIntact` as a hard precondition inside `RestoreAsync`** (in Core, so every shape inherits it) plus a post-restore check and a non-zero exit code; **decide the test relocation before writing the command**; **the runbook names the real DB folder**, resolved from config and confirmed against the startup banner (`Program.cs:57-60`); and **drop the "quiescence by construction" framing** — B earns it in-process and, like A, by operator discipline across processes.

**Three things I am not deciding.** Whether amendment 1 justifies breaking M10's "Core untouched" scope. Whether to build a restore path before or after backups are actually being produced — I lean before-nothing-else, but "cheap insurance written before you need it" is a legitimate counter-argument and I am not overriding it. And whether to re-open option C when the hosting question is answered: today C buys nothing, on a LAN-reachable host it buys something real.

---

## Blocker 3 — Four scheduled jobs whose only caller is `App.xaml.cs`

### What is true today (cited)

Four Core services run only from WPF startup, all `try/catch`-swallowed to a trace warning:

| # | Job | Call site | Guard |
|---|---|---|---|
| 1 | Auto-backup (BK-03) | `App.xaml.cs:63` → `AutoBackupIfDueAsync()` | Unconditional |
| 2 | Export-hub 12-month/12-week backfill | `App.xaml.cs:83` → `BackfillAsync()` | Only when an export root is set (`:80-82`) |
| 3 | Weekly standup archive backfill (DR-09) | `App.xaml.cs:100` | Only in the **`else`** branch — i.e. only when **no** export root is set |
| 4 | Monthly task-list archive backfill | `App.xaml.cs:104` | Same `else` branch |

**[VERIFIED]** All four services are already `AddSingleton`-registered in the API (`Program.cs:104,107,108,112`) and resolve today — the on-demand routes call *different* methods, never these. The API documents its own gap: *"`Program.cs` never calls `BackfillMissingWeeksAsync` (only the WPF `App.xaml.cs` does), so on a web-only deployment nothing ever wrote it"* (`AdminEndpoints.cs:16-21`). No `IHostedService` / `BackgroundService` exists anywhere in `src/`. **[VERIFIED — absence]**

**The concurrency worry is the wrong worry.** None of the four writes to the database — zero `INSERT`/`UPDATE`/`DELETE` across all five service files, and the repository methods they call are `SELECT`-only. The one thing touching the live `.db` is the online-backup API, and `HostBootTests.cs:66-74` asserts `PRAGMA journal_mode = wal` from the API's own container. **The real difficulty is frequency, not locking.**

**A live defect bounds any scheduler.** `ExportHubService.cs:145` copies the whole database into `{root}/db` **unconditionally on every run**, outside the `File.Exists` guards that protect the markdown, and `BackupToFolderAsync` prunes to `BackupKeepCount` (default 30) on every invocation (`BackupService.cs:54,130-144`). A naive hourly scheduler destroys backup depth in a day. **This is reachable today** without M10, via `POST /api/ops/export/run`. **[VERIFIED]**

**Jobs 3 and 4 may not need porting at all.** They ran only when no export root was configured, because the export hub supersedes them (`App.xaml.cs:76-82,97-106`). If production sets an export root, **the desktop never ran them either** — porting would *add* an all-teams flat archive on shared host storage, not preserve one. They aggregate every team into one file (`BuildWeekMarkdownAsync(null, …)`, `BuildMonthMarkdownAsync(null, …)`).

### Options

| Option | What it takes | Risk | If it goes wrong |
|---|---|---|---|
| **1 — One `BackgroundService`, hourly tick, persisted markers.** One new file (~150-200 lines), one `Program.cs` line gated on `!IsEnvironment("Testing")`, 3-6 tests. ~1 day. | No Core change, no Angular, no regen. | If the last-run marker is written on any path other than confirmed success, a failed backup reads as "done today" and the safety net is silently gone. | Nobody notices until the day they need the backup. |
| **2 — Run them once in the existing `Program.cs` startup block.** ~15-25 lines, no new file. Cheapest. | "Once per day" silently becomes "once per restart". | A desktop was launched daily; a server stays up for weeks. Boot on day 1, then no backup on days 2-60. | The BK-03 safety net looks ported in the diff and is not. |
| **3 — Expose the missing triggers as `/api/ops/*` routes; schedule on the host.** 2 routes + 2 tests + a runbook. Cost lands in operations. | Most reversible; routes stay useful even if a scheduler is added later. | **A non-interactive caller needs an admin credential** — a stored service-account password or a new API-key surface. That is a new authentication surface, and the biggest risk in this option. | The schedule leaves the repository: nobody reading the code can see a nightly backup is expected, and it can be deleted in a host migration with no signal. |

### What the adversarial pass found

There is **no attack file for Blocker 3**. The B4 attack file, however, lands two findings squarely on it and both survive scrutiny:

- ✅ **After the deletion, nothing in the product can turn the backup on.** `AutoBackupIfDueAsync` returns false at `BackupService.cs:61-62` when `AutoBackupEnabled` is false or `BackupFolderPath` is blank — **silently, unlogged** — and both default off/blank (`JsonAppConfig.cs:59-60`). I verified the writer claim myself: `SetBackupFolderPath` / `SetAutoBackupEnabled` / `SetBackupKeepCount` have exactly **one** production caller in the entire repo, `src/TimesheetApp/ViewModels/SettingsViewModel.cs:264-266`. Every other hit is a test fake or the interface/implementation. **Zero callers in `TimesheetApp.Api`.** After `git rm`, those settings are reachable only by hand-editing the host config — and `JsonAppConfig` loads once in its constructor and is registered as a singleton (`Program.cs:49`), so even that needs an API restart. Build the scheduler exactly as specified and you get a nightly job that no-ops silently against a Settings screen showing an empty list, which is indistinguishable from "no backups yet". *(Note: the coverage audit does carry this, but as a PARTIAL row dispositioned "deliberate and documented" — audit line 121. The attack's point is that the moment you depend on those settings, "deliberate" stops being an adequate disposition.)*
- **A ported scheduler has worse liveness than what it replaces.** The API runs as `dotnet run -c Release --urls http://localhost:5080` from a batch file (`deploy-local.bat:38`) with no service wrapper, no auto-start, no restart-on-crash anywhere in the repo. WPF's trigger fired on **every launch by every user every working day** — that redundancy is why the once-per-day guard exists at all. A single unsupervised console window has none of it. An out-of-process trigger (Task Scheduler calling a small CLI — the same CLI that could carry Blocker 2's restore) sidesteps this and is arguably less work than a hosted service.

Also carried forward: `%APPDATA%` is per-user, so whichever Windows account runs the host decides `DbPath`, `BackupFolderPath` and `AutoBackupEnabled` — and `Program.cs:52-53` says the quiet part itself: *"A second machine that 'cannot log in' almost always means the API opened a DIFFERENT database than expected."* Any scheduled backup should pin `TimesheetApp:ConfigPath` / `:DbPath` explicitly rather than inherit an invisible per-account default.

### Recommendation (and what it depends on)

**Option 1, scoped to jobs 1 and 2 only, with jobs 3 and 4 decided by question 9 — but only after the hosting question (7) is answered.**

The four jobs are not equally urgent and bundling them hides that. A markdown archive skipped in July regenerates from the database in September; that is what the backfills are *for*. **A backup not taken on the day the data was correct is the only unrecoverable loss on the entire MISSING list.** Option 2 does not actually port job 1 — on a long-lived server "on startup" means "once, in September". Option 3's stored-admin-credential requirement is a bigger new risk than the whole of option 1.

But the attack's H2 is right that **option 1 does not restore the safety net on an unsupervised host**, and I would not ship it as though it did. If the answer to question 7 is "a console window someone opens", then option 3 plus a Windows scheduled task is the more honest design despite its credential problem — because at least the trigger survives a reboot.

**Not mine to decide:** where the once-per-day marker lives (filesystem fails toward taking an *extra* backup — the safe direction; a DB key is durable but fails toward a *silently missing* one; I lean on both, filesystem as the correctness guard and the DB key only to bound frequency, but that is a judgement about which failure you would rather have). Whether jobs 3/4 should run server-side at all. And whether the ungated DB copy at `ExportHubService.cs:145` — a live defect on a shipped route, independent of M10 — is fixed by this work or by whoever owns that route.

---

## The 29 MISSING behaviors, triaged

**On the number.** The audit's section table sums to 43 MISSING entries; the table itself has 33 rows (rows appear in several sections); the audit's prose calls this "29 distinct behaviors". The evidence pass regrouped to 28, the options pass to 21 work items. Nothing is disowned by any pass — all 33 rows are accounted for below. **The tables use the 28-item grouping (one disposition per behavior); the parenthetical `[Pn]` shows how they collapse into buildable work packages.** I am not reconciling the counts further because the grouping, not the total, is what a scope decision needs.

### PORT — must exist on the web before WPF dies

| # | Behavior | One-line reason |
|---|---|---|
| 1 | Active-team switcher (+ persistence, reset propagation, visibility rule) `[P5]` | A multi-team user has **no way to change their active team on the web at all** — the sidebar `<select>` is mockup markup with two hardcoded options and no binding (`sidebar.component.html:47-50`); `setActiveTeam()` has zero production callers. |
| 2 | Backlog team filter + TEAM column `[P6]` | `getBacklogList()` sends `{}` (`worklog.service.ts:463-465`), so the server returns every team the caller belongs to and nothing can narrow it; `teamId` is on the DTO and never rendered. |
| 3 | Log Work: jump-to-any-week control `[P7]` | Prev/Next/This-week only (`log-work.component.html:17-21`) — reaching a week N away costs N clicks. |
| 4 | Log Work: Month/Year filter `[P7]` | No period filter exists on the screen; every backlog group renders regardless of month. |
| 5 | Log Work: holiday day-column rendering `[P7]` | Zero "holiday" hits in the template or stylesheet — the rule still holds server-side, so a user discovers a holiday by typing into it and being refused. |
| 6 | Log Work: footer >8h styling `[P7]` | `log-work.component.scss` has a `.zero` state and no over-cap rule; the only visual cap warning is gone. |
| 7 | Task List: PROJECT label when grouped by Team `[P8]` | The template's *"the band above already shows it"* (`task-list.component.html:126`) is true in project mode and **false** in team mode — with ≥2 teams nobody can see which project a backlog belongs to. |
| 8 | Task List: Gantt "no dated backlogs" caption `[P8]` | The existing empty state fires on zero rows, a different condition — an undated-only month renders a silent empty card. |
| 9 | Task List: on-disk monthly archive + backfill `[P4]` | Zero API callers for either method; the export route deliberately returns content, not a file (`TaskListEndpoints.cs:87-96`). **Conditional — see question 9.** |
| 10 | Daily Report: weekly archive backfill (DR-09) `[P4]` | The API documents its own gap (`AdminEndpoints.cs:16-21`); any week nobody manually archives never gets a snapshot, silently, forever. **Conditional — see question 9.** |
| 11 | Daily Board: issue SOLUTION TEXT `[P9]` | The board renders `i.issueText` and never `solutionText` though it is on the DTO — you see *that* an issue resolved, never *how*. **One template line; highest value-per-effort in the audit.** |
| 12 | Smart Fill: multi-task one-shot fill `[P10]` | The wire accepts N tasks; the client always builds a one-element array (`smart-fill.ts:43-56`). **Carries a design fork — see below.** |
| 13 | Backup RESTORE `[P2]` | Blocker 2. Deleting WPF deletes the only working restore path in the product. |
| 14 | Backup list / visibility `[P3]` | `ListBackups()`'s only caller is the WPF view-model — from the browser you can trigger a backup but never see whether one exists. **This is what makes 13 and 15 trustworthy.** |
| 15 | Auto-backup trigger (BK-03) `[P1]` | Blocker 3, job 1. **The only item on this entire list whose loss is not recoverable later.** |
| 16 | ExportHub 12-month/12-week backfill `[P1]` | Blocker 3, job 2. A server with a new export root, or one that had a downtime gap, never self-heals. |
| **+1** | **Backup configurability — a writer for `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount`** | ✅ **Promoted from the audit's PARTIAL table by the adversarial pass.** WPF is the only writer (`SettingsViewModel.cs:264-266`; zero callers in the API). Item 15 is inert without it. |

Two footnotes a reader should not skip. **Item 12 hides a real decision:** the web reimplemented the split arithmetic in TypeScript rather than delegating to Core, and the two disagree — for 8h across 3 days the web yields `[2.7, 2.7, 2.6]` and Core yields `[2.6, 2.6, 2.8]` (`smart-fill.ts:19-30` vs `SmartInputService.cs:37-46`). Both sum to 8.0 and both respect the 1-decimal rule, so neither is wrong on the wire — but `smart-fill.ts:8` claims *"this is the same rule"*, and it is not. Extending the TypeScript deepens a divergence nobody decided on; routing through Core's `BuildPlan` makes one source of truth and is a bigger change. **And one item is not on this list because it is not a port at all:** extracting `Palette.Dark.xaml`'s hex values before deletion (~30 minutes) — it is the sole record of the intended dark palette for ~9 unmapped tokens and 23 hardcoded component colours, and **the only artifact where deletion destroys information rather than relocating it into git history.**

### ACCEPT — the loss is fine; reason stated

| # | Behavior | One-line reason |
|---|---|---|
| 17 | OneDrive conflict-copy banner (XC-08) **[SIGN-OFF]** | Needs a locally-synced `.db` with multiple writers; one server-hosted DB removes the multiple-writers half structurally — **unless the host's own data directory sits under a synced folder.** |
| 18 | Journal-integrity banner (XC-09) | Under the API's WAL profile the `<db>-journal` file this guard watches for is never created — structurally inert on the web whether or not it has a UI. |
| 19 | Log Work entry-target user picker + read-only team view + badge **[SIGN-OFF]** | The API refuses it *by design*, in its own words (`TimesheetEndpoints.cs:56-62`) — a deliberate privacy narrowing, not an oversight. |
| 20 | Smart Fill: backlog-code search → task checklist | Workaround is one extra already-covered step: add the task to the grid, then on-grid Smart Fill reaches it. |
| 21 | Smart Fill: "Full 8h" mode | The identical outcome is reachable via single-task DistributeEven with Total = 8 × day-count; `distributeHours(40,5)` → `[8,8,8,8,8]` with no drift. **But the workaround needs the user to know which days are holidays — i.e. it depends on PORT item 5 shipping.** |
| 22 | Smart Fill: arbitrary From/To range | **The closest call on this list.** Navigate week-by-week and fill each: real tedium for a multi-week backfill, not a block. No usage data behind this. If anything gets pushback, it is this. |
| 23 | Smart Fill: Preview-then-Confirm gate | Consistent with, not a regression from, the rest of Log Work, which already uses attempt-then-revert-on-400 for every write. `/api/smartfill/validate` is wired end to end and lacks only a call site if someone wants it later. |
| 24 | SharePoint destination Verify button (SP-01) | A pre-flight convenience, not the only safety net — the export still hard-validates and blocks on `Level: Error`. A bad destination surfaces one export cycle later. |
| 25 | Retention: automatic unattended run **[SIGN-OFF]** | Retention is default-OFF and destructive; requiring a click is arguably *safer* than a silent purge at every restart. `RetentionEnabled` becomes a dead flag either way — `RetentionService` never reads it. |
| 26 | Identity: auto-provision an unmapped user **[SIGN-OFF]** | WPF granted access with *no credential at all*; requiring real accounts is the entire point of adding auth. **Safe as a disposition, unsafe as a schedule — gated on Blocker 1.** |
| 27 | Identity: first-run auto-join to a team (TM-09) | Only ever meaningful paired with 26; once an admin creates the account anyway, assigning a team is one more field in an already-manual flow. Record it as an onboarding checklist item. |

### OBSOLETE

| # | Behavior | One-line reason |
|---|---|---|
| 28 | Window chrome (1180×760, min 980×620, CenterScreen) | The browser owns window sizing and placement; there is nothing to port. (Responsive min-width guards are a real web concern, but a different one.) |

**M10's real remaining scope is the PORT list: 16 items** — 17 once the promoted backup-configurability item is counted, grouping into 10 buildable work packages, of which **four (items 13-16 plus the promoted item) are the safety net and the rest are affordances a user discovers on first use and reports.** That split is the whole of the gate-shape decision in question 11.

---

## What is still unknown

Nothing below was resolvable from the code. Several are one question to the right person; two are not answerable without opening a copy of the production database, which no agent was permitted to do.

**Facts nobody has established**

1. **How many production users have a NULL `password_hash`.** Gate zero for everything. Requires a copy of production.
2. **Whether the one promoted admin's one-time generated password was ever captured.** If not, there is no administrator and recovery is direct database surgery (`AdminBootstrap.cs:115`).
3. **Whether the production deployment configures an export root.** Decides whether PORT items 9 and 10 are a port or a new behavior.
4. **Where the production database physically lives, and whether that directory is synced.** Decides whether ACCEPT 17 is fully or only mostly safe — and note the API's Server profile now writes `-wal`/`-shm` sidecars into exactly the folder the Desktop profile existed to keep them out of.
5. **Whether production ever runs more than one API process against one `.db`.** If it does, every option in Blocker 2 changes. Only `deploy-local.bat` and `start-web.bat` were available to inspect, and both are localhost single-process.
6. **What `TimesheetApp:SeedFirstAdmin` is set to on the live instance.** It defaults to true when unset (`AdminBootstrap.cs:36-37`), and no committed `appsettings.json` exists in the repo.

**Technical questions the code does not answer**

7. **What `SqliteConnection.ClearAllPools()` does to a connection that is currently checked out**, as opposed to idle in the pool. `Microsoft.Data.Sqlite` is a NuGet dependency, not vendored. The two plausible behaviours produce materially different failure modes for a concurrent reader. Neither agent certified either.
8. **Whether `File.Delete` on the live `.db` throws or silently succeeds while another handle is open**, under this SQLite build's Win32 VFS share flags. Asserted by a code comment (`SqliteOnlineBackup.cs:36-37`), demonstrated by no test.
9. **Whether `BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source.** `SqliteOnlineBackup.Open` sets no `busy_timeout` and no `DefaultTimeout`, unlike the Server profile. If it can, a nightly backup that always collides with traffic fails every night — silently, given the `try/catch`.
10. **Whether two concurrent `BackupToFolderAsync` calls into one folder** (scheduled job + admin button) can have one run's prune delete a file another is mid-write. Not demonstrated, not disproven.
11. **Whether `File.WriteAllTextAsync` to a network/SharePoint root can leave a truncated `.md`** that the `File.Exists` guards then permanently treat as complete. The guard being `File.Exists`-only is verified; the torn write is not.
12. **How .NET's command-line configuration provider pairs a value-less `--flag` with the next argument**, which affects whether a CLI guard block in `Program.cs` and the framework agree about what was passed. Reasoned, not executed.

**Where the files disagree and I cannot adjudicate**

13. **Test-relocation cost for Blocker 2.** The attack states `TimesheetApp.ApiTests` has no Moq package reference; no agent enumerated the actual options (add Moq / new net8.0 Core-test project / gut `TimesheetApp.Tests` to a Core-only reference) with a cost each. The claim that this work is missing from the "~1 day" estimate is sound; the size of the gap is not established.
14. **The MISSING count itself** — 29 / 28 / 21 across three passes, all over the same 33 rows. This is grouping judgement, not disagreement about facts, but no pass reconciled it, and the options file's own scoreboard (9 PORT) contradicts its own body (P1-P10, ten items).
15. **`ExportHubService.cs:145`'s ungated DB copy.** Every pass agrees it is a live defect on a shipped route today, independent of M10. No pass established whether it has already eaten backup depth in production, because that requires looking at the production export root.

**Two items the audit itself left `[ASSUMED]` and never closed:** D6's claim that every `ShowDialog()` flow was rewritten correctly (spot-checked only), and D9's per-flag trace of the re-entrancy guards (2 of 10 traced). Low risk, but not verified — and after M10 there is no WPF binary left to settle a disagreement against.

---

*Sources: `.planning/m10-blockers/{B1-auth-evidence, B2-restore-evidence, B2-restore-options, B2-restore-attack, B3-scheduled-evidence, B3-scheduled-options, B4-missing-evidence, B4-missing-options, B4-missing-attack}.md`; `.planning/M10-COVERAGE-AUDIT.md`; per-section detail in `.planning/m10-audit/`. Claims marked ✅ were re-verified against source while writing this memo. No code was edited, no build or test was run, and no SQLite file was opened.*
