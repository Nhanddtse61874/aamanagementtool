# B4-missing — Adversarial attack on the recommendation

**Target:** `B4-missing-options.md`'s recommendation — *Option 2 (split gate), with the post-M10 milestone
created in the same sitting as the deletion commit.*

**Method.** Every claim below was re-derived by opening the file. `[VERIFIED]` = I read that line in this
pass. `[ASSUMED]` = inferred. No build was run, no test was run, no SQLite file was opened, nothing was edited.

**Bottom line up front.** The *shape* of Option 2 is right and survives. The *contents of Gate 1 as
specified* do not — Gate 1 can be built exactly as written and still leave the product with no working
backup, no way to turn one on, and a green restore rehearsal for a procedure that will fail in the incident.
Two of the holes below are the same "operator believes something worked" failure the audit exists to prevent,
and Gate 1 **introduces** one of them rather than closing it. Option 2 needs three additions before it is
executable. Details and the amended gate are at the end.

---

## H1 — FATAL. After the deletion, nothing in the product can turn the backup on, and Gate 1 does not notice

This defeats the entire stated purpose of Gate 1.

`AutoBackupIfDueAsync` — the method the recommendation's P1 scheduler exists to call — opens with two
silent, unlogged early returns:

```
BackupService.cs:61    if (!_config.AutoBackupEnabled) return false;
BackupService.cs:62    if (string.IsNullOrWhiteSpace(_config.BackupFolderPath)) return false;
```
[VERIFIED: `src/TimesheetApp.Core/Services/BackupService.cs:61-62`]

Both of those settings default to off/blank:

```
JsonAppConfig.cs:59    _backupFolderPath  = model?.BackupFolderPath ?? "";
JsonAppConfig.cs:60    _autoBackupEnabled = model?.AutoBackupEnabled ?? false;
```
[VERIFIED: `src/TimesheetApp.Core/Config/JsonAppConfig.cs:59-60`]

And **the WPF Settings view-model is the only writer of either one, anywhere in the repo**:

```
src/TimesheetApp/ViewModels/SettingsViewModel.cs:264    _config.SetBackupFolderPath(BackupFolder);
src/TimesheetApp/ViewModels/SettingsViewModel.cs:265    _config.SetAutoBackupEnabled(AutoBackupEnabled);
src/TimesheetApp/ViewModels/SettingsViewModel.cs:266    _config.SetBackupKeepCount(keep);
```
[VERIFIED: `grep -rn "SetBackupFolderPath\|SetAutoBackupEnabled\|SetBackupKeepCount" src --include=*.cs`
returns these three lines plus the `IAppConfig` definitions and one doc-comment at
`TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:48`. **Zero callers in `TimesheetApp.Api`.**]

So `git rm -r src/TimesheetApp/` deletes the only code path in the product that can set the backup folder or
enable auto-backup. After that they are reachable only by hand-editing
`%APPDATA%\TimesheetApp\appsettings.json` on the host — and because `JsonAppConfig` loads the file **once, in
its constructor** [VERIFIED: `JsonAppConfig.cs:53-66`] and is registered as a singleton
[VERIFIED: `Program.cs:49  builder.Services.AddSingleton(appConfig);`], even the hand-edit needs an API
restart to take effect. There is no reload path.

**The consequence for Gate 1 specifically.** Build P1 and P3 exactly as the recommendation specifies, delete
WPF, and the resulting system is:

- a nightly job that calls `AutoBackupIfDueAsync`, hits line 61 or 62, returns `false`, and logs nothing;
- a Settings backup list that reads the same blank `BackupFolderPath`
  [VERIFIED: `BackupService.cs:73  var folder = _config.BackupFolderPath;` → `ListBackups()` returns
  `Array.Empty<BackupInfo>()` at `:74-75`], so it renders an **empty list**;
- an operator looking at an empty list on day one, which is indistinguishable from "no backups yet".

The recommendation's own justification for Gate 1 is that P3 "is what makes P1 and P2 trustworthy — without
it, a silently-failing backup job is indistinguishable from a working one." As specified, **P3 does not
achieve that**, because the failure mode is not "the job errored", it is "the job returned false before doing
anything" and the list reads empty for that same reason. This is the third instance of the exact failure the
brief warned against, and Gate 1 creates it.

Note this is not hypothetical-only: if the production `appsettings.json` *does* already carry
`AutoBackupEnabled: true` and a folder — set at some point from WPF — the safety net survives the deletion by
accident, on a config value written by a deleted application, which nobody can subsequently change or verify
from the product. That is not a safety net anyone should sign off.

**What Gate 1 must add:** either an admin route that writes these three settings (plus a config reload or a
documented restart), or — at minimum — the scheduler must log at **Error** and expose a status field when it
is disabled or unconfigured, and the P3 list must distinguish *"backups are not configured"* from
*"configured, none yet"*. Neither is in the recommendation.

---

## H2 — FATAL. There is no supervised host, so the ported scheduler has *worse* liveness than what it replaces

The recommendation treats "port the trigger to an `IHostedService`" as restoring the safety net. Against this
deployment it does not.

How the API actually runs [VERIFIED: `deploy-local.bat`]:

```
deploy-local.bat:38    dotnet run -c Release --urls http://localhost:5080
```

and its own header, verbatim:

> `To let OTHER machines on the LAN reach it, change the --urls at the bottom to http://0.0.0.0:5080 and open
> Windows Firewall for inbound TCP 5080. That is the deferred "who hosts it" decision, so this script defaults
> to localhost only.`
[VERIFIED: `deploy-local.bat:11-13`]

There is no Windows Service wrapper, no IIS/`web.config`, no scheduled task, no container, no
auto-start-on-boot and no restart-on-crash anywhere in the repo [VERIFIED: repo contains exactly two scripts,
`deploy-local.bat` and `start-web.bat`; `grep` for `AddHostedService|BackgroundService|IHostedService|
PeriodicTimer` across `TimesheetApp.Api` and `TimesheetApp.Core` returns **nothing**].

Now compare the two triggers honestly:

| | WPF today | Ported `BackgroundService` |
|---|---|---|
| Fires when | **every launch of the app, by every user, every working day** [VERIFIED: `App.xaml.cs:63`] | only while one console window stays open on one machine |
| Survives a reboot | yes — next user opens the app | no — until someone re-runs the .bat |
| Number of independent chances per day | one per user per launch | zero if nobody started the process |

`App.xaml.cs:63` runs `AutoBackupIfDueAsync()` inside the desktop startup path, and the once-per-day guard
(`ListBackups().Any(b => b.Timestamp.Date == today)`, `BackupService.cs:65`) exists precisely because it fires
so often. That redundancy — N users × daily launches — is the property that made the desktop safety net
actually work. A single unsupervised `dotnet run` has none of it.

So Gate 1 can be fully and correctly built, and the backup still does not happen on the day it mattered,
because the host machine rebooted on Tuesday and nobody re-ran the batch file until Thursday. **The
recommendation's central claim — that Gate 1 closes the one unrecoverable loss before deletion — is not true
against this deployment.** The unresolved "who hosts it" decision is not a detail to settle later; it is a
precondition of Gate 1 meaning anything, and it is exactly the kind of five-minute-answer-from-the-right-person
item the recommendation was willing to name for the team-switcher question but did not name here.

An out-of-process trigger (Windows Task Scheduler invoking a small CLI, which also serves H4's restore need)
sidesteps this entirely and is arguably *less* work than a hosted service. The recommendation never considers
it.

---

## H3 — SERIOUS. `%APPDATA%` is per-user: the scheduler may read a different config than WPF wrote

`JsonAppConfig`'s default constructor resolves the config path from the **calling account's** roaming profile:

```
JsonAppConfig.cs:172-175    private static string DefaultConfigPath()
                            { var appData = Environment.GetFolderPath(
                                  Environment.SpecialFolder.ApplicationData);
                              return Path.Combine(appData, "TimesheetApp", "appsettings.json"); }
```
[VERIFIED]

and `Program.cs` takes that default whenever the two override settings are absent — which is production:

```
Program.cs:34-36    IAppConfig appConfig = string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(dbPath)
                        ? new JsonAppConfig()
                        : new JsonAppConfig(configPath, dbPath);
```
[VERIFIED]

`Program.cs` says the quiet part itself:

> `A second machine that "cannot log in" almost always means the API opened a DIFFERENT database than
> expected (there is no appsettings.json here, so the path comes from JsonAppConfig defaults, invisibly).`
[VERIFIED: `Program.cs:52-53`]

Whichever Windows account runs `dotnet run` therefore decides `DbPath`, `BackupFolderPath`,
`AutoBackupEnabled` and `BackupKeepCount` — all four. If WPF was configured under one user's profile and the
API is later hosted under another (or as a service under a system account), the scheduler reads a config where
`BackupFolderPath` is `""` (→ H1, silent no-op) and `DbPath` falls back to
`Documents\TimesheetApp\timesheet.db` [VERIFIED: `JsonAppConfig.cs:177-181`] — **a fresh, empty database**.

This is a live hazard independent of M10, but Gate 1 depends on it and never names it. Any Gate 1 that ships a
scheduled backup must first pin the config path explicitly (`TimesheetApp:ConfigPath` /
`TimesheetApp:DbPath` are already supported seams, `Program.cs:31-32`) rather than inheriting an invisible
per-account default.

---

## H4 — SERIOUS. "Rehearse the restore against a copy" cannot exercise the failure that decides whether it works

Gate 1 item (b) is *"a restore procedure REHEARSED once end-to-end against a copy of production."* The
rehearsal as scoped validates the wrong thing.

What restore actually does [VERIFIED: `BackupService.cs:97-127`, `SqliteOnlineBackup.cs`]:

1. `SqliteOnlineBackup.ClearPools()` — which is `SqliteConnection.ClearAllPools()`, and that is
   **process-local** [VERIFIED: `SqliteOnlineBackup.cs`, `public static void ClearPools() =>
   SqliteConnection.ClearAllPools();`].
2. a safety copy of the current DB to `{dbPath}.pre-restore_{stamp}.bak` [VERIFIED: `BackupService.cs:121-122`];
3. `SqliteOnlineBackup.Copy(backupPath, dbPath)` — which **begins** by deleting the destination and its
   sidecars: `File.Delete(dbPath); File.Delete(dbPath + "-wal"); File.Delete(dbPath + "-shm");`
   [VERIFIED: `SqliteOnlineBackup.Copy` → `DeleteWithSidecars(destDbPath)`].

The `Copy` doc-comment states the precondition outright:

> `Callers must ensure no handle is still open on the destination (see ClearPools) — otherwise this throws
> rather than silently corrupting it.`
[VERIFIED: `SqliteOnlineBackup.cs`, `Copy` remarks]

Three consequences the recommendation does not address:

- **A rehearsal against a quiet copy has no API attached, therefore no pooled WAL handles on the destination,
  therefore it never exercises the one precondition that will decide the real attempt.** The rehearsal goes
  green and proves nothing about the incident. That is the audit's own signature failure mode, re-created in
  the mitigation.
- **`ClearPools()` being process-local means an out-of-process restore tool cannot clear the API's handles at
  all.** Any offline-CLI or manual-copy shape *must* mandate stopping the API first, and the runbook has to
  say so explicitly. The recommendation leaves the shape open (defensible) but does not record this constraint
  as binding on every shape (not defensible — it is the constraint that makes `SettingsEndpoints.cs:43-44`'s
  refusal correct in the first place).
- **A hand-written "stop the service, swap the file" runbook — the cheapest shape, and the one the audit's
  Open Question 2 floats — gets none of the safety in `RestoreAsync`.** No `.pre-restore` copy, and, worse, no
  sidecar deletion: the replaced database's `-wal` is left beside the restored file and SQLite replays it over
  the restore on next open. `SqliteOnlineBackup.cs` names this exact trap
  (*"a `-wal` left over from the REPLACED database is replayed by SQLite over the newly restored file on the
  next open"*) and `BackupService.cs:93-96` says a plain `File.Copy` "leaves the REPLACED database's `-wal`
  lying next to the new one". A naive runbook silently restores *the data you were trying to discard*.

**What Gate 1 must add:** the rehearsal must run against a copy **with a live API process attached and
serving**, and the accepted procedure must be the one that deletes sidecars and takes a pre-restore copy —
i.e. it must go through `SqliteOnlineBackup`, not through Explorer.

---

## H5 — SERIOUS. Gate zero is framed as "answer it", but answering it is not what unblocks the deletion

The recommendation elevates the auth question correctly and then under-scopes the response: *"answer whether
existing non-admin employees have non-NULL `password_hash`… That is not a porting question and no option
fixes it."*

True as far as it goes. But if the answer is *"they are all NULL"* — which
`DatabaseInitializer.cs:331` makes the default expectation (`ALTER TABLE Users ADD COLUMN password_hash TEXT;`
— no default, no backfill) and `:337` compounds (`UPDATE Users SET is_admin = 1 WHERE id = (SELECT MIN(id)
FROM Users)` — one admin promoted, nobody else touched) [both VERIFIED] — then the deletion is blocked by
**work that does not exist yet**, and that work is absent from Gate 1's five items.

The remediation surface, verified:

- login hard-fails a NULL hash: `AuthSetup.cs:175-177`, `if (string.IsNullOrEmpty(creds.PasswordHash)) return
  Results.Unauthorized();` [VERIFIED];
- self-recovery is deliberately blocked: `AuthEndpoints.cs:91-93` returns
  `"No password is set for this account. Ask an administrator to set one."` [VERIFIED];
- and there is **no bulk provisioning path**: `SetPasswordHashAsync` has exactly two production callers —
  `AuthEndpoints.cs:101` (self-service change, requires the current password) and `AuthEndpoints.cs:128`
  (admin reset), both strictly one user at a time [VERIFIED: `grep -rn SetPasswordHashAsync src --include=*.cs`].

So gate zero is not one item, it is two: *answer it*, and — if the answer is bad — *build or script the sweep,
and schedule the human comms*. Gate 1 should say so, because "the check came back non-zero" on the morning of
the deletion is a schedule event, not a five-minute one.

---

## H6 — MINOR. The recommendation's showcase verification is unsound

The recommendation offers one claim as proof it checked rather than assumed:

> *"I verified the one real exception, holiday shading, is already unblocked because `GET /api/holidays` is
> deliberately open (SettingsEndpoints.cs:602-611, worklog.service.ts:1008-1012)."*

`GET /api/holidays` is **not open**. It is authenticated.

- `AuthSetup.cs:146` sets `o.FallbackPolicy = o.DefaultPolicy`, and the default policy is
  `.RequireAuthenticatedUser()` at `AuthSetup.cs:133` [VERIFIED].
- `AuthSetup.cs:137` states the rule: *"FallbackPolicy applies to every endpoint WITHOUT its own authorization
  metadata"* [VERIFIED].
- `GET /api/holidays` carries no `.AllowAnonymous()` [VERIFIED: `grep -rn AllowAnonymous src/TimesheetApp.Api
  --include=*.cs` returns only `AuthSetup.cs:207` (login), `Program.cs:238` (`/health`), and `Program.cs:282`
  (SPA fallback) — holidays is not among them].
- It is also mapped inside the `api` group, which runs `ClientContextFilter` on every endpoint
  [VERIFIED: `Program.cs:250` `var api = app.MapGroup("").AddEndpointFilter<ClientContextFilter>();`,
  `Program.cs:260` `api.MapSettingsEndpoints();`].

The client comment the recommendation leaned on (`worklog.service.ts:1008-1010`, *"`GET /api/holidays` is
OPEN"*) means **"not admin-gated"**, contrasting with the `[ADMIN]` tags on the two writes just below it — not
"unauthenticated" [VERIFIED by reading the surrounding block].

**The conclusion survives**: a Log Work user is logged in, so holiday shading really is a pure client-side
change with no API work. But the *method* — inferring auth posture from the absence of `.RequireAuthorization`
— is invalid in a codebase with a `FallbackPolicy`, and it was applied to the one claim held up as verified.
Rated minor because nothing downstream changes; recorded because Option 2 freezes this document as the spec
for the deferred work, and a wrong reason in a frozen spec propagates.

---

## H7 — MINOR. P6's spec is wrong about the mechanism, and Option 2 freezes it as the spec

`B4-missing-options.md` costs P6 (Backlog team filter) as *"~3 Angular files, 0.5-1 day"* with
*"[ASSUMED] the generated `backlogList` client fn already accepts a `teamIds` param… if it does not, add a
client regen to the cost."*

That is the wrong axis of uncertainty. `getBacklogList`'s own doc-comment forbids the approach:

> `rebuildOptions derives the four dropdowns' contents FROM THE LOADED ROWS. Filter on the server and every
> keystroke in the search box would silently delete entries out of the Project / Type / Assignee / Month
> dropdowns — the user would watch their own filters vanish as they type. If you ever do need the server-side
> filter, it needs its own method and its own row set, not this one.`
[VERIFIED: `src/timesheet-web/src/app/services/worklog.service.ts:455-462`, immediately above
`getBacklogList()` at `:463-465`, which is confirmed to send `{}`]

The correct port is a **client-side** team filter over the already-loaded rows, consistent with the other four
dropdowns — probably cheaper than costed, and needing no regen. Low impact on the decision; real impact on
whoever picks this document up in three months as the frozen spec.

---

## H8 — MINOR. One ACCEPT is invalidated by the same option's deferral

ACCEPT A5 (Smart Fill "Full 8h") is justified by an equivalent workaround: *"single-task DistributeEven with
Total = 8 × day-count"*. I re-traced it and the arithmetic is right — `distributeHours(40, 5)` → `[8,8,8,8,8]`
[VERIFIED: `smart-fill.ts:19-30`].

But `day-count` here means **working days excluding holidays** — `FillFull8h` and `DistributeEven` both
enumerate via `_calc.WorkingDaysBetween(from, to, holidays)` [VERIFIED:
`SmartInputService.cs:33,53` region]. Under Option 2, holiday rendering (P7) ships *after* the deletion, so
during that window the user cannot see which days in the week are holidays and therefore cannot compute the
multiplier the workaround depends on. The ACCEPT is sound; its stated workaround is not available until the
deferred item lands.

---

## What I checked that HOLDS

Stated so the amendments below are not read as a general indictment — most of the document is solid, and two
of its corrections are real finds.

- **The Smart Fill math divergence is real and correctly traced.** Web: `per = round1(8/3) = 2.7`,
  `drift = round1(8 − 8.1) = −0.1`, last day `2.6` → **[2.7, 2.7, 2.6]** [VERIFIED: `smart-fill.ts:19-30`].
  Core: `totalTenths = 80`, `baseTenths = 80/3 = 26`, `remainder = 2` on the last day → **[2.6, 2.6, 2.8]**
  [VERIFIED: `SmartInputService.cs:37-46`]. `smart-fill.ts:8`'s *"this is the same rule"* is false. Good catch.
- **The "Full 8h top-up" correction is right.** `FillFull8h` is
  `days.Select(d => new CellAssignment(d, 8m))` — no DB, no read of existing logs, a flat assignment
  [VERIFIED: `SmartInputService.cs:56-58`], despite the "tops up to" wording at `:10`.
- **`log-work.component.scss:52-55` really does add `.cell input.invalid`, labelled M9.2** [VERIFIED] — the
  audit row claiming no invalid-cell styling exists is genuinely stale, as Option 3's risk section says.
- **The `!IsEnvironment("Testing")` guard is correct for this repo.** Both test factories set it:
  `ApiFactory.cs:112` and `SignalRTestFactory.cs:65`, and both also override `TimesheetApp:ConfigPath` /
  `:DbPath` to a temp root [VERIFIED: `ApiFactory.cs:104-106`, `SignalRTestFactory.cs:55-57`]. A hosted
  service gated this way will not fire in tests, and would not reach production data even if it did.
- **The four "silent" losses are real.** No `AddHostedService` / `BackgroundService` / `IHostedService` /
  `PeriodicTimer` anywhere in `TimesheetApp.Api` or `TimesheetApp.Core` [VERIFIED: grep returns nothing].
- **P4's conditional-PORT flag is correct, and better-founded than stated.** `App.xaml.cs:79-107` runs the
  export-hub backfill *only when* an export root is configured, and the two flat archive backfills only in the
  `else` branch [VERIFIED] — so if production sets an export root, the desktop never ran them. The related
  "whole-organisation data on shared storage" worry is also **already true today** via the manual route:
  `ExportHubService.RunAsync` iterates `_teams.GetAllAsync()`, every team, regardless of caller
  [VERIFIED: `ExportHubService.cs:55`]. Scheduling it adds frequency, not scope.
- **The prune hazard is real and correctly located** — `ExportHubService.cs:145` copies the live DB into
  `{root}/db` on every run with `BackupKeepCount` (default 30) [VERIFIED: `ExportHubService.cs:145`,
  `JsonAppConfig.cs` `DefaultBackupKeepCount = 30`]. It does **not** cross-prune the user backup folder
  (different folder) and it cannot eat the live DB (`Prune`'s pattern is `timesheet_*.db`, which the live
  `timesheet.db` does not match) [VERIFIED: `BackupService.cs:130-144`]. Bounded by scheduler frequency, as
  claimed.
- **Every Angular claim I spot-checked is accurate**: sidebar mockup `<select>` with two hardcoded
  `<option>`s and no binding [VERIFIED: `sidebar.component.html:47-49`]; `daily-report.component.html:196-200`
  renders `i.issueText` and never `solutionText` [VERIFIED]; `task-list.component.html:126`'s
  *"`project` is NOT here"* comment [VERIFIED]; `ApiCurrentTeamService.InitializeAsync` computes the fallback
  and never writes it back [VERIFIED: `ApiCurrentTeamService.cs:57-70`].

---

## Verdict

**Option 2's shape survives. Its Gate 1 does not, as written.**

The split-gate reasoning is sound and I did not break it. The PORT list genuinely is two kinds of thing;
deletion genuinely does not make the deferred UI work harder; Option 1's duration risk and Option 3's
overlapping no-backup/no-restore window are both fairly stated. Nothing I found argues for Option 1 or
Option 3 instead.

What I found is that Gate 1 does not do what it claims. Built exactly as specified, it produces a scheduled
backup that (H1) cannot be enabled or configured from the product and no-ops silently, (H2) runs on an
unsupervised manually-started process with strictly worse liveness than the WPF trigger it replaces, (H3)
may be reading a different account's config and therefore a different database, and (H4) is paired with a
restore rehearsal that cannot exercise the precondition that will decide the real restore. H1 and H4 are each
a fresh instance of "the operator believes it worked" — the failure this audit exists to prevent.

**Replace the recommendation with: Option 2, amended Gate 1.** Gate 1 becomes:

0. **Gate zero, expanded** — answer the `password_hash` question against a copy, *and* if the answer is bad,
   build or script the provisioning sweep and schedule the user comms. Two items, not one (H5).
0b. **Answer "who hosts it"** — this is now a blocker, not a deferred decision. Until the API runs under
   something that survives a reboot (Windows Service / scheduled task / supervised host), no scheduled backup
   is trustworthy. An out-of-process Task Scheduler + small CLI is a legitimate answer and probably cheaper
   than a hosted service — and the same CLI can carry the restore (H2, H4).
1. **P1 scheduler** — as specified, plus: pin `TimesheetApp:ConfigPath`/`:DbPath` explicitly rather than
   inheriting the per-account `%APPDATA%` default (H3), and log at **Error** with a status field whenever the
   job returns early because backup is disabled or unconfigured (H1).
2. **Backup configurability** — an admin route (or a documented host-config step plus restart) for
   `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount`. Without this the deletion strands three
   settings with no writer (H1). This item is **new** and is the one I would refuse to drop.
3. **P3 backup visibility** — as specified, plus it must distinguish *"not configured"* from *"none yet"* (H1).
4. **P2 restore** — rehearsed against a copy **with a live API attached**, and the accepted procedure must go
   through `SqliteOnlineBackup` (pre-restore copy + sidecar deletion), never a plain file swap (H4).
5. **Palette.Dark.xaml extraction** — unchanged, still correct, still ~30 minutes.

Cost impact: Gate 1 moves from ~2-3 days to roughly ~4-6 days plus the hosting decision. That is still well
short of Option 1, and the split-gate logic is unaffected.

**Not mine to decide, and I am not deciding them:** whether the affordance gaps are acceptable to ship (the
recommendation's own five-minute team-structure question stands, unanswered); what shape the restore takes;
who hosts the API; whether the archive backfills should run server-side at all; and whether anyone accepts the
window between deletion and the provisioning sweep if gate zero comes back bad.

---

*No code was edited, no build or test was run, and no SQLite file was opened in producing this file.*
