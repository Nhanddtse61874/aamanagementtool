# B3 — Attack on the recommendation: "Option 1, scoped to jobs 1 and 2"

**Read-only pass. No build, no test run, no SQLite file opened.** Every claim carries `file:line`.
`[VERIFIED]` = I opened that line in this pass. `[ASSUMED]` = inferred, and labelled as such.

**Bottom line up front:** the recommendation's *conclusion* may well be defensible, but its **stated reason is
built on a deployment that this repository explicitly records as not existing**. The single argument that kills
Option 2 — "on a long-lived server 'on startup' means 'once, in September'" — assumes a long-lived server. There
isn't one, and the project's own state file names its absence as a blocker. That inverts the ranking. Worse,
the only realistic way to *get* the long-lived server Option 1 argues for (a Windows Service / IIS AppPool
identity) breaks the per-user config resolution the scheduler depends on to find the database at all — so
Option 1 is at its most dangerous in exactly the deployment that justifies it.

---

## H1 — FATAL. The decisive argument against Option 2 assumes a server the project records as nonexistent.

The recommendation's rationale item (2), and the "DECISIVE FLAW" in its Option 2 write-up, both rest on:

> "A server stays up for weeks: boot on day 1, then no backup on days 2–60."

**There is no server.** [VERIFIED]

- `.planning/STATE.md:274` — *"Production hosting collides with the deferred **'the company has no server'**
  blocker."*
- `.planning/ROADMAP.md:11` — *"**Remote hosting** — still unsolved and still blocking everyone but the user."*
- `.planning/ROADMAP.md:53` — same, verbatim, naming the deferred blocker again.
- `.planning/STATE.md:169` — single-process deploy *"partially retires the ... blocker **for local/single-host**
  ... Remote multi-user hosting still open."*

And the deployment artifacts agree:

- `deploy-local.bat:38` — `dotnet run -c Release --urls http://localhost:5080`, **in the foreground of the
  console the operator launched it from**. [VERIFIED]
- `deploy-local.bat:12-14` — *"To let OTHER machines on the LAN reach it, change the `--urls` ... That is the
  **deferred "who hosts it" decision**, so this script defaults to localhost only."* [VERIFIED]
- `start-web.bat:39` — `start "Worklog API  (do not close)" cmd /k ... dotnet run`; `start-web.bat:51` — *"To
  stop: close both windows."* [VERIFIED]

So the API process lifetime today **is one human's working session on one machine** — started from a `.bat`,
ended by closing a console window. That is *the same lifetime the WPF app had*. `App.xaml.cs:21` `OnStartup`
ran once per launch; `Program.cs:174-209` runs once per launch. [VERIFIED]

**Consequences the recommendation gets backwards:**

1. **Option 2 is not a degraded port of job 1 — it is an exact one.** `App.xaml.cs:63` calls
   `AutoBackupIfDueAsync()` once per app start. Option 2 calls it once per API start. Under the real
   deployment those are the same event, on the same machine, for the same single user. The recommendation's
   claim that Option 2 "only looks correct in a diff" is true *against a server* and false against this
   deployment.
2. **Option 1's hourly `PeriodicTimer` is a behaviour *increase*, not a preservation.** The desktop never
   backed up more than once per launch. A ~1h tick is up to 8–10 evaluations per working day. The once-per-day
   guard at `BackupService.cs:65` absorbs that for job 1 — but job 2 has **no guard at all** on its DB copy
   (`ExportHubService.cs:145`), which is the very hazard the recommendation itself identifies. Option 1
   therefore *introduces* the exposure it then spends a marker mechanism defending against; Option 2 does not
   have the exposure in the first place (one run per launch).
3. **The recommendation never opened STATE.md or ROADMAP.md.** Its "Open, not closed" list has five entries and
   none of them is "what is the deployment?" — the one fact its entire ranking pivots on. [VERIFIED — the
   hosting blocker appears in both planning files and is cited nowhere in `B3-scheduled-options.md`]

**Honest counter, which I will not hide.** Option 2 inherits one genuine desktop flaw: a console left running
across several days backs up only once, and `start-web.bat:39` titles the window *"do not close"*, which
actively encourages long-running windows. On day 2..N of such a window Option 1 is strictly better. That is a
real gap — but it is the **status quo** gap, on the user's own machine, that the WPF app has always had and
that nobody has reported. Whether it is worth a day of work, a new architectural pattern, and the new silent-
failure surfaces below is a judgement call for a human, not a settled matter.

---

## H2 — FATAL. Option 1's rationale is self-undermining: the deployment that justifies it is the one that breaks it.

Suppose the hosting blocker is resolved the ordinary Windows way — a Windows Service or an IIS AppPool — which
is the only way to get the "stays up for weeks" process Option 1 is designed for. The scheduler then runs under
a **service identity**, and:

- `JsonAppConfig.cs:174-175` — `DefaultConfigPath()` = `Environment.SpecialFolder.ApplicationData` +
  `TimesheetApp\appsettings.json`, i.e. **`%APPDATA%` of the running account**. [VERIFIED]
- `JsonAppConfig.cs:180-181` — `DefaultDbPath()` = `SpecialFolder.MyDocuments` + `TimesheetApp\timesheet.db`.
  [VERIFIED]
- `JsonAppConfig.cs:161` — `LoadModel` returns `null` when the file does not exist. [VERIFIED]
- `JsonAppConfig.cs:57,59,60` — with `model == null`: `DbPath = defaultDbPath`, `BackupFolderPath = ""`,
  `AutoBackupEnabled = false`. [VERIFIED]

Chain under a service account: no `appsettings.json` in that profile → `DbPath` falls back to the *service
account's* Documents folder → `Program.cs:176` `InitializeAsync()` **creates a brand-new empty database there**
→ the scheduler's `AutoBackupIfDueAsync` hits `BackupService.cs:61` (`AutoBackupEnabled` false) and returns
`false` **forever**. [VERIFIED for each link; [ASSUMED] that a service account is the chosen hosting route —
that is precisely the undecided question]

The result: the operator has a running app, a registered scheduler, no errors, **no backups, and not even the
right database**. This repository has already met this failure and wrote it down —

> `Program.cs:52-56` — *"A second machine that 'cannot log in' almost always means the API opened a DIFFERENT
> database than expected (there is no appsettings.json here, so the path comes from JsonAppConfig defaults,
> **invisibly**)."* [VERIFIED]

— and its only defence is `Console.WriteLine` at `Program.cs:57-60`, which **has no console under a Windows
Service and no captured stdout under IIS by default**. The one diagnostic that catches this is blind in exactly
the deployment where it matters. [VERIFIED for the mechanism; [ASSUMED] for IIS stdout defaults]

This is the brief's forbidden failure mode — *operator believes something worked when it did not* — and Option 1
is the option that installs it, because Option 1 is the only option that claims to provide an unattended
guarantee. Options 2 and 3 make no such claim, so they cannot break it silently.

**Order of operations this implies:** per-user config resolution must be fixed **before** any unattended
scheduler is trusted, not after. Nothing in the recommendation sequences these.

---

## H3 — SERIOUS. "Log loudly" is not implementable under the "No Core change" constraint the same option asserts.

The recommendation promises both, one paragraph apart: *"logged rather than swallowed to a Trace warning"* /
*"the strongest argument for logging loudly"* — and *"No Core change, no Angular change."*

`AutoBackupIfDueAsync` cannot support that. `BackupService.cs:59-69` [VERIFIED]:

```
if (!_config.AutoBackupEnabled) return false;                          // :61  disabled
if (string.IsNullOrWhiteSpace(_config.BackupFolderPath)) return false; // :62  unconfigured
if (ListBackups().Any(b => b.Timestamp.Date == today)) return false;   // :65  already done
var path = await BackupNowAsync(); return path is not null;            // :67-68 ran / no-op
```

**Five distinct outcomes, one `bool`, and `false` means four of them.** A caller cannot distinguish "the admin
disabled backups", "nobody configured a folder", "already backed up today" and "the folder is unreachable so
`ListBackups()` returned empty and `BackupNowAsync` returned null" (`BackupService.cs:43-44` returns `null` for
a blank folder or a missing `DbPath`, *without throwing*). The `try/catch` the option specifies catches
**nothing** in the two most likely failure paths, because they do not throw — they return `false`.

So the scheduler can log exactly one true sentence: *"AutoBackupIfDueAsync returned false."* That is not louder
than the `Trace.TraceWarning` at `App.xaml.cs:64`; it is the same silence with a timestamp. Making it
informative requires changing the Core return type — the change the option rules out. This is not a nitpick:
"log loudly" is the recommendation's own named mitigation for its own worst risk, and it does not exist.

---

## H4 — SERIOUS. The open question it declined to answer is already answered by a test in this repo.

The recommendation's first "Open, not closed" item, and a risk item under Option 1, is whether
`BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source. It says *"something I could
not determine"* and builds a risk narrative on it (*"a nightly backup that always collides with traffic fails
every night — silently"*).

`src/TimesheetApp.Tests/Services/WalBackupSafetyTests.cs` tests exactly this. [VERIFIED]

- `:57` `Backup_during_an_open_write_transaction_in_WAL_survives_restore_intact`
- `:61` a connection held **open** so the WAL is never checkpointed
- `:65-67` a **second** connection holding an **open write transaction**, uncommitted
- `:70` `BackupNowAsync()` taken *right there* — the exact contended state in question
- `:75,78` committed rows present, uncommitted row absent
- `:84` `PRAGMA integrity_check` == `"ok"`, SQLite's own verdict
- `:22` the class header: *"the state a pooled server sits in all day"*

The file sits in the same directory as `BackupServiceTests.cs`, and its name contains both "Wal" and "Backup".
Missing it inflated Option 1's risk profile and was used to argue for extra scope (a "was there a backup
today?" surface).

**Caveat I will state rather than let the correction over-swing:** this test is **single-process, same-machine,
temp-file**. It does not cover two *separate processes* on the live DB — the state `start-web.bat:16-18`
explicitly warns about (*"CLOSE THE WPF APP FIRST. Both write to the same SQLite file"*). Cross-process
contention remains genuinely undetermined. The recommendation's separate observation that
`SqliteOnlineBackup.Open` sets no `busy_timeout`/`DefaultTimeout` (`SqliteOnlineBackup.cs:92-105`, vs the Server
profile's `busy_timeout=1000` + `DefaultTimeout=5`) is [VERIFIED] and still stands as a real gap for that case.

---

## H5 — SERIOUS. Porting job 2 silently drops a documented ordering precondition, and `File.Exists` makes the damage permanent.

`App.xaml.cs:73-75` states the precondition in the code, in words [VERIFIED]:

> *"Runs AFTER `EnsureBootstrappedAsync` + `DefaultTaskSync` so teams and their synced default tasks exist
> **before any export is built**."*

`IDefaultTaskSyncService.SyncAsync()` is `App.xaml.cs:71`. It is **absent from the API's startup block**
(`Program.cs:174-209` runs `InitializeAsync` → `EnsureBootstrappedAsync` → `AdminBootstrap` → conditional second
`EnsureBootstrappedAsync`, and nothing else). [VERIFIED] On the web it fires only after a DefaultTasks write
(`SettingsEndpoints.cs:249,673,689,709`).

The recommendation *notices* this and files it under "adjacent parity gaps, **out of B3 scope**". It is not out
of scope: it is a stated precondition of the exact job the recommendation proposes to schedule.

Why it is not merely untidy — the guard is content-blind and one-way:

- `ExportHubService.cs:112` `if (backfillOnly && File.Exists(path)) continue;` (tasklist)
- `ExportHubService.cs:124` same (timesheet), `:136` same (daily) [VERIFIED]
- `StandupArchiveService.cs:81`, `TaskListArchiveService.cs:148` — same `File.Exists` shape [VERIFIED per the
  prior evidence pass; cited lines re-read as consistent]

A run that produces markdown which is **non-null but under-populated** writes the file
(`ExportHubService.cs:113-116`: `md is null` → skip, otherwise write). Every later backfill sees `File.Exists`
and skips it. **Nothing in the system ever revisits it.** The empty-database case is safe (`md is null` → no
file), but the *partially populated* case — tasks missing because `DefaultTaskSync` had not run, a team
bootstrapped after the tick, a period mid-write — produces a permanently wrong archive with no error and no
repair path.

Option 1 makes this worse than Option 2 does, because Option 1 runs unattended on a timer whose first tick is
~60s after boot, with no ordering relationship to anything. Option 2, placed in `Program.cs:174-209`, can at
least be sequenced after the bootstrap calls in the same explicit block the desktop used.

---

## H6 — SERIOUS. The Testing gate deletes the only integration coverage of the thing being built.

Option 1 registers `AddHostedService<ScheduledJobsService>()` gated on
`!builder.Environment.IsEnvironment("Testing")`. Both test factories set that environment:

- `ApiFactory.cs:112` — `builder.UseEnvironment("Testing");` [VERIFIED]
- `SignalRTestFactory.cs:65` — same [VERIFIED — a second factory the recommendation does not mention; it
  happens to be covered by the same gate, by luck rather than design]

So the hosted service **never starts in any test host**. The proposed "3–6 new tests" can only exercise marker
logic against hand-built objects. Nothing asserts that the service is registered, that it resolves, that it
starts, or that it survives `ValidateOnBuild` — and `Program.cs:22-26` sets `ValidateScopes = true` +
`ValidateOnBuild = true`, always on. [VERIFIED] A registration mistake that throws at `Build()` in production
is **green in CI**, because in CI the registration never happens.

This is a structural argument against Option 1 relative to Option 3: ops routes run inside `ApiFactory` and are
testable end-to-end exactly like the four existing `/api/ops/*` routes already are.

(The recommendation's underlying trap-detection is correct and worth keeping: without a gate, jobs 3/4 would
execute in every test host. `ApiFactory.cs:31` is `WebApplicationFactory<Program>` running the real
`Program.cs`. [VERIFIED] The problem is that the fix costs the coverage.)

---

## H7 — MINOR. The marker store falsifies the "no writes" premise the whole design rests on.

`SettingsRepository.SetAsync` is `INSERT OR REPLACE INTO Settings(key, value)` via `ExecuteAsync`
(`SettingsRepository.cs:20-25`) — a real write, on a short connection from the same factory
(`SettingsRepository.cs:8-10`). [VERIFIED]

The recommendation's headline is *"none of the four jobs issues a write statement"* — true of the four jobs
[VERIFIED for `BackupService`, `ExportHubService`; consistent with the prior pass for the other three] — but
Option 1 then adds an hourly write of its own, on the Server profile (`busy_timeout=1000`, `DefaultTimeout=5`,
`SqliteConnectionFactory.cs:58-67`). Partial-failure path: job 2 completes, the marker write throws under
contention, the exception is caught, **the marker is not written**, and the next tick re-runs the entire
12-month × 12-week backfill *and* another ungated whole-DB copy at `ExportHubService.cs:145`. The
write-on-success-only rule the recommendation adopts to avoid a lying marker is what creates this loop. It is
bounded and self-correcting (the markdown is `File.Exists`-skipped), so: minor — but it is the option's own
mitigation biting back, and it should be sized before it is chosen.

---

## H8 — MINOR. Two independent prune windows are described as one.

- `ListBackups()` reads **only** `_config.BackupFolderPath` (`BackupService.cs:73-74`). [VERIFIED]
- Job 2 copies into `Path.Combine(root, "db")` (`ExportHubService.cs:145`). [VERIFIED]
- `Prune(folder, keep)` runs per-folder (`BackupService.cs:54,130-144`). [VERIFIED]

These are **two separate 30-deep windows**, not one. Follow-on facts the recommendation's phrasing obscures
(*"five entries chewed out of the 30-deep prune window ... Job 1's own `ListBackups()` check absorbs this for
the backup folder"*):

1. Job 1's once-per-day guard gives `{root}/db` **no protection whatsoever** — different folder, never
   enumerated by `ListBackups()`.
2. The `{root}/db` snapshots are **invisible to the Settings backup list and to `RestoreAsync`'s picker**,
   because that list is `ListBackups()`. An operator counting backups sees only one of the two sets.
3. The merge case is the dangerous one and is unmentioned: if an operator sets `BackupFolderPath` **to**
   `{root}\db`, every export run's `Prune` competes with the daily backups in one window, and the once-per-day
   guard at `BackupService.cs:65` starts seeing export copies as "today's backup" — suppressing the real one.
   [VERIFIED that the paths would coincide and both call the same `Prune`; [ASSUMED] that an operator would do
   this — nothing prevents it, the Settings UI takes a free-text folder]

---

## What survives, and what I would put in its place

**The recommendation does not survive as reasoned.** Its ranking is driven by a server that
`.planning/STATE.md:274` and `.planning/ROADMAP.md:11,53` record as an open, deferred blocker (H1), and its
preferred option is most dangerous precisely in the deployment that would justify it (H2). Two of its own named
mitigations do not exist as specified (H3) or delete their own test coverage (H6). One of its open questions
was already answered in-repo (H4).

Parts that **do** survive and should be carried forward regardless of what is chosen:

- Correction 1 is right and important: `POST /api/ops/backup/run` → `BackupNowAsync()`
  (`SettingsEndpoints.cs:1051-1059` → `BackupService.cs:35-36`) bypasses **both** `AutoBackupEnabled` and the
  once-per-day guard. [VERIFIED] Any scheduling of that route is a semantic change.
- `ExportHubService.cs:145` is an ungated whole-DB copy reachable today via `POST /api/ops/export/run`
  (`SettingsEndpoints.cs:1041-1049` → `ExportNowAsync()` → `RunAsync`). [VERIFIED] Pre-existing, real, worth
  telling someone about independent of M10.
- The DI analysis is sound: all four services are singletons with singleton graphs (`Program.cs:104,107,108,112`),
  `IClock`/`ISettingsRepository` singletons (`Program.cs:78,87`), so no captive dependency under
  `ValidateScopes`. [VERIFIED]
- The ApiFactory trap is real. [VERIFIED]

**What I would pick — for a human to accept or reject, not a resolution:**

> **Option 2b now**, plus **one route from Option 3** (`POST /api/ops/backup/auto` → `AutoBackupIfDueAsync()`),
> and **re-open Option 1 only as part of resolving the hosting blocker**, with the config-identity fix (H2)
> sequenced *before* it.

Reasoning: under the deployment that actually exists, Option 2b is a faithful port of `App.xaml.cs:63,83,100,104`
— same trigger, same branching, same once-per-launch cadence, ~20 lines in a file that already has a startup
block (`Program.cs:174-209`), no new pattern, no new marker, no new silent-failure surface. The added ops route
covers the long-running-window gap that Option 2 inherits from the desktop (H1's honest counter) **with an
operator in the loop**, which is the right shape while the process is a console window on that operator's own
desk. It is also testable inside `ApiFactory`, unlike a Testing-gated hosted service (H6).

**What this does NOT resolve, and needs an owner:**

- **The hosting decision itself.** Everything above is conditional on it. If the answer becomes "a real
  always-on host", Option 1 becomes correct — but H2 must be fixed first, or the scheduler backs up an empty
  database it created itself, silently, forever.
- **Whether the long-running-window gap is acceptable in the interim.** Option 2b means a machine left running
  Friday-to-Tuesday backs up once. That is the status quo, not a regression — but it is a real hole and calling
  it acceptable is a data-safety judgement, not a technical finding.
- **Whether `AutoBackupEnabled` (default `false`, `JsonAppConfig.cs:60`) should gate an automated backup at
  all.** Under any option, if it is false in the live `%APPDATA%\TimesheetApp\appsettings.json`, *every* option
  produces zero backups and says nothing (H3). Somebody should read that file before this is decided. **I did
  not read it — it sits beside the production database and rule 1 forbids it.**
- **Jobs 3/4 on shared storage.** The recommendation's point stands and is unresolved: `null` teamIds
  (`StandupArchiveService.cs:58`, `TaskListArchiveService.cs:54`) means whole-organisation markdown. Under the
  current one-user-one-machine deployment this is a non-issue; under any shared host it is a new cross-team
  disclosure. It is a hosting-conditional decision too.
- **`DefaultTaskSync` ordering (H5).** Whichever option ships, job 2 needs its documented precondition or an
  explicit written decision that it no longer applies.
