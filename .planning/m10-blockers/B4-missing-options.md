# B4 — Triage of the MISSING behaviors, and options for the M10 scope

**Nothing here is decided.** This file assigns exactly one disposition to every row of the 🔴 MISSING table
in `M10-COVERAGE-AUDIT.md` (lines 47-82), groups them into work packages, and offers three concrete shapes
for the M10 scope. A human picks the shape.

Every claim carries `file:line`. **[VERIFIED]** = I opened that file and read that line in this pass.
**[CITED]** = I relied on the coverage audit's own two-pass citation without re-deriving it, referenced by
its line in that file. No build was run, no test was run, no SQLite file was opened, nothing was edited.

**This revision supersedes the 22:09 version of this file.** It was attacked by a later pass
(`B4-missing-attack.md`); I re-derived the attack's findings independently rather than adopting them, and
three of them hold and change the scope. I also found one thing neither prior pass has: **the remedy the
attack proposes for its own most severe finding is explicitly forbidden by a documented architectural rule in
this codebase.** That fork is now the single most consequential open decision in M10, and it is at the bottom
under "The config fork".

---

## Counting, reconciled

The MISSING table has **33 rows** [VERIFIED: 33 table rows between `M10-COVERAGE-AUDIT.md:49` and `:81`].
The audit's prose says "29 distinct behaviors"; the earlier evidence pass said 28. Both are grouping
judgments over the same 33 rows — nothing is disowned by either. This file groups them into **22 work items**,
grouped by *what one person would build in one sitting*, which is the unit a scope decision actually needs.

| Disposition | Work items | Audit rows covered |
|---|---:|---:|
| **PORT** | 10 | 20 |
| **ACCEPT** | 11 | 12 |
| **OBSOLETE** | 1 | 1 |
| **Total** | **22** | **33** |

Plus **one derived blocker (P11) that is not an audit row at all** — it is a consequence of the deletion that
the coverage audit could not see, because it is not a behavior WPF had, it is a *capability the product loses
when WPF's Settings screen goes*. It is the reason the previously-recommended Gate 1 does not work. See P11.

---

## PORT — the real remaining work

Ordered by *what happens if it is missing*, not by size. The first four fail silently while nobody is
watching. The rest are discovered by a user on first use, which is its own kind of safety.

### Tier 1 — the losses that accrue silently

**P1 — Auto-backup trigger (BK-03) + export-hub catch-up backfill.**
*Audit rows: "Backup: once-per-day auto-backup trigger", "ExportHub: 12-month/12-week catch-up backfill".*

No `IHostedService` / `BackgroundService` / `AddHostedService` / `PeriodicTimer` exists **anywhere in `src/`**
[VERIFIED: `grep -rn "AddHostedService\|BackgroundService\|IHostedService\|PeriodicTimer" src --include=*.cs`
→ exit 1, no matches]. `AutoBackupIfDueAsync`'s only caller is `App.xaml.cs:63` [VERIFIED: read at
`src/TimesheetApp/App.xaml.cs:62-63`, inside a best-effort try/catch]. **This is the only item in the whole
MISSING table where the loss is not recoverable later**: a markdown archive skipped in July can be
regenerated from the database in September; a backup not taken on the day the data was correct cannot.

*Cost*: one new `src/TimesheetApp.Api/Infrastructure/ScheduledJobsService.cs`, one `Program.cs` registration
line, 3-6 tests. No Core change, no Angular, no client regen. ~1 day. Design worked out in
`B3-scheduled-options.md`.

⚠ **P1 does not work without P11.** See below. Building P1 alone produces a job that returns `false` on line
61 or 62 of `BackupService.cs` and logs nothing.

**P11 — Backup configurability. [DERIVED BLOCKER — not an audit row]**

`AutoBackupIfDueAsync` opens with two silent, unlogged early returns:

```
BackupService.cs:61    if (!_config.AutoBackupEnabled) return false;
BackupService.cs:62    if (string.IsNullOrWhiteSpace(_config.BackupFolderPath)) return false;
```
[VERIFIED: `src/TimesheetApp.Core/Services/BackupService.cs:59-69`]

Both settings default to off/blank [VERIFIED: `JsonAppConfig.cs:60-62` — `_backupFolderPath = model?.BackupFolderPath ?? ""`,
`_autoBackupEnabled = model?.AutoBackupEnabled ?? false`], and **the WPF Settings view-model is the only
production writer of either, anywhere in the repo**:

```
src/TimesheetApp/ViewModels/SettingsViewModel.cs:264    _config.SetBackupFolderPath(BackupFolder);
src/TimesheetApp/ViewModels/SettingsViewModel.cs:265    _config.SetAutoBackupEnabled(AutoBackupEnabled);
src/TimesheetApp/ViewModels/SettingsViewModel.cs:266    _config.SetBackupKeepCount(keep);
```
[VERIFIED: `grep -rn "SetBackupFolderPath\|SetAutoBackupEnabled\|SetBackupKeepCount" src --include=*.cs` —
every other hit is the `IAppConfig` interface, the `JsonAppConfig` implementation, or a test fake. **Zero
callers in `TimesheetApp.Api`.**]

So `git rm -r src/TimesheetApp/` deletes the only code path in the product that can turn backup on or point
it at a folder. Afterwards they are reachable only by hand-editing `%APPDATA%\TimesheetApp\appsettings.json`
on the host — and because `JsonAppConfig` loads the file **once, in its constructor**
[VERIFIED: `JsonAppConfig.cs:53-67`] and is registered as a singleton [VERIFIED: `Program.cs:49`
`builder.Services.AddSingleton(appConfig);`], even the hand-edit needs an API restart. There is no reload path.

**Why this matters more than it sounds:** the resulting failure is not "the backup errored". It is "the backup
returned `false` before doing anything, silently" — and `ListBackups()` reads the same blank
`BackupFolderPath` and returns `Array.Empty<BackupInfo>()` [VERIFIED: `BackupService.cs:71-75`], so P3's list
renders empty *for the same reason*. An operator sees an empty list on day one and cannot distinguish
"backups are not configured" from "configured, none taken yet". That is the third instance in this audit of
"the operator believes something worked", and a Gate 1 built without P11 **introduces** it.

*Cost*: depends entirely on shape, and **the shape collides with a documented rule — see "The config fork"
below.** 0.5-2 days depending on which side of the fork is taken.

**P2 — A restore path of some kind.**
*Audit row: "Settings: backup RESTORE (BK-05) + self-restore guard".*

`RestoreAsync` has no caller in the API; the only hit is the comment explaining the exclusion: *"`IBackupService.RestoreAsync`
is NOT exposed in M8.3: it overwrites the live .db in place while the API holds open connections, which
corrupts live readers."* [VERIFIED: `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:43-44`]. The
exclusion is correct engineering. The problem is that **no replacement exists anywhere in the repo** — no CLI,
no script, no runbook. Deleting WPF deletes the only working restore path in the product, at the same moment
P1's automatic backups stop.

**Three mechanical constraints any replacement must respect** — I read the implementation rather than assuming:

1. `RestoreAsync` calls `SqliteOnlineBackup.ClearPools()`, which is `SqliteConnection.ClearAllPools()` —
   **process-local** [VERIFIED: `src/TimesheetApp.Core/Data/SqliteOnlineBackup.cs`, `ClearPools()`]. An
   out-of-process tool therefore **cannot** clear the API's handles. Any offline shape *must* mandate stopping
   the API first, and the runbook must say so as a hard precondition, not a suggestion.
2. `Copy()` begins by deleting the destination **and its sidecars** — `File.Delete(dbPath)`,
   `dbPath + "-wal"`, `dbPath + "-shm"` [VERIFIED: `SqliteOnlineBackup.DeleteWithSidecars`]. Its own remark
   states why this is load-bearing: *"a `-wal` left over from the REPLACED database is replayed by SQLite over
   the newly restored file on the next open"* [VERIFIED]. **A hand-written "stop the service, swap the file in
   Explorer" runbook — the cheapest shape, and the one the audit's Open Question 2 floats — silently restores
   the data you were trying to discard.** It also skips the `.pre-restore_{stamp}.bak` safety copy
   [VERIFIED: `BackupService.cs:121-122`].
3. `Copy()` finishes with `PRAGMA journal_mode=DELETE` so the artifact is one self-contained file
   [VERIFIED: `SqliteOnlineBackup.Copy`].

*Cost*: a runbook is ~0.5 day to write; a small console project wrapping `SqliteOnlineBackup` is ~1-2 days.
**The honest cost is the rehearsal, not the writing.** And the rehearsal has a trap of its own: a rehearsal
against a quiet copy with no API attached has no pooled WAL handles on the destination, so it never exercises
constraint (1) — the one thing that will decide the real attempt. **The rehearsal must run against a copy with
a live API process attached and serving**, or it proves nothing.

**P3 — Backup visibility.**
*Audit row: "Settings: backup list — Refresh + display of existing backups".*

`ListBackups()` has exactly one production caller in the entire repo and it is the WPF view-model
[VERIFIED: `grep -rn ListBackups src --include=*.cs` → the only non-test, non-definition hit is
`src/TimesheetApp/ViewModels/SettingsViewModel.cs`]. From the browser you can *trigger* a backup but never see
whether one exists, when, or how big. Paired with the audit's PARTIAL finding that "Backup now" reports a
backup that did not happen as success [CITED: `M10-COVERAGE-AUDIT.md:92`], the post-M10 state is: a button
that always says it worked, and no way to check.

*Cost*: **cheaper than previously costed.** The route has an obvious, already-established home — four
`/api/ops/*` admin routes already exist in exactly this shape [VERIFIED: `SettingsEndpoints.cs:1002`
retention/preview, `:1022` retention/run, `:1041` export/run, `:1051` backup/run]. Adding
`GET /api/ops/backup/list` is pattern-matching, not design. ~0.5 day including the Settings list.

**This is what makes P1 and P2 trustworthy** — but only if it distinguishes *"not configured"* from
*"configured, none yet"*. Without that distinction it inherits P11's ambiguity and becomes a second
false-reassurance surface rather than the check on the first.

### Tier 2 — the archive-continuity pair (recoverable, but only if someone remembers)

**P4 — Weekly standup archive backfill (DR-09) and monthly task-list archive backfill.**
*Audit rows: "Daily Report: startup backfill of missing weekly archives", "Task List: on-disk monthly markdown
archive + startup backfill".*

The API documents its own gap: *"`Program.cs` never calls `BackfillMissingWeeksAsync` (only the WPF
`App.xaml.cs` does), so on a web-only deployment nothing ever wrote it."* [VERIFIED:
`src/TimesheetApp.Api/Endpoints/AdminEndpoints.cs:16-21`]. The task-list export route deliberately calls the
content-only builder, not the disk-writing `ExportMonthAsync` [VERIFIED: `TaskListEndpoints.cs:87-96`].

**Conditional-PORT, and I verified the condition rather than inferring it.** `App.xaml.cs` runs these two
jobs in the **`else` branch** of an export-root test:

```
App.xaml.cs:80-82   if (!string.IsNullOrWhiteSpace(config.ExportRoot1Path) ||
                        !string.IsNullOrWhiteSpace(config.ExportRoot2Path))
                    {  ...BackfillAsync(); ...EnsureRetentionAsync();  }
App.xaml.cs:98-107  else
                    {  ...BackfillMissingWeeksAsync(); ...BackfillMissingMonthsAsync();  }
```
[VERIFIED: read in full at `src/TimesheetApp/App.xaml.cs:79-107`; the comment at `:76-79` says the structured
hub "supersedes the legacy flat archives (EX-06)"]

**So if the production deployment configures an export root, the desktop never ran these two jobs**, and
porting them would newly materialise an all-teams flat archive on the shared host: an addition, not a
preservation. One fact I do not have settles this. *Cost if ported*: same file as P1, +~0.5 day.

Note the same `if` also gates retention, which is why ACCEPT A9's "retention was automatic" framing is only
true on an export-root-configured machine.

### Tier 3 — the ones a user hits on day one

All client-side, all cheap, and — this matters for sequencing — **none needs the WPF app alive to build.**
Git history and the coverage audit preserve the spec.

**P5 — Active-team switcher.** *4 audit rows: the switcher, persistence + resolution-on-load, team-switch
resets every TeamFilter, the visibility rule (hidden at 0 teams / read-only at 1 / switcher at 2+).*

The sidebar's team `<select>` is literally mockup markup — two hardcoded `<option>` elements reading
"Architect Improvement" and "Plus Team", no `[value]`, no `(change)`, no binding of any kind
[VERIFIED: `src/timesheet-web/src/app/components/sidebar/sidebar.component.html:46-52`].
`WorklogService.setActiveTeam()` has zero production callers — its only references are its own definition, a
spec, and a comment saying so outright [VERIFIED: `grep -rn setActiveTeam src/timesheet-web/src/app` →
`worklog.service.ts:1239` (definition), `worklog.service.spec.ts:736` (spec), `team-filter.component.ts:142-144`
(a comment reading *"the web app has no team switcher yet: `WorklogService.setActiveTeam()` exists and has no
caller"*)]. **A multi-team user has no way to change their active team on the web at all.**

**The server half is fully built.** `PUT /api/me/active-team` exists [VERIFIED: `SettingsEndpoints.cs:97`],
and `ApiCurrentTeamService.SetActiveTeamAsync` persists it and rejects a team the user is not a member of
[VERIFIED: `ApiCurrentTeamService.cs:73-84`]. This is a pure client port.

Ride-along fix: `InitializeAsync` computes the fallback active team and **never writes it back**
[VERIFIED: `ApiCurrentTeamService.cs:57-70` — `ActiveTeamId = _available.Any(...) ? persisted : (…_available[0].Id : 0)`,
with no `SetActiveTeamIdAsync` call], so a stale `active_team_id` snaps the user back when a deactivated team
is reactivated.

*Cost*: ~4 files (sidebar component + template, the service call, the write-back fix). 1-2 days. **Largest of
the UI ports, and the one most likely to be reported as a bug rather than a missing feature.**

**P6 — Backlog team visibility (filter + TEAM column).** *2 audit rows.*

`getBacklogList()` takes no parameters at all — `backlogListFn(this.http, this.rootUrl, {})`
[VERIFIED: `src/timesheet-web/src/app/services/worklog.service.ts:463-465`], so the server defaults to every
team the caller belongs to — *broader* than WPF's default, with nothing able to narrow it. Not a leak (the
server still scopes to membership), but with >1 team in view nobody can tell which team owns a row.

**Correcting the previous costing's axis of uncertainty.** The prior version flagged "[ASSUMED] the generated
`backlogList` client fn accepts `teamIds`; if not, add a regen". That is the wrong question. The method's own
doc-comment forbids the server-side approach outright:

> *"`rebuildOptions` derives the four dropdowns' contents FROM THE LOADED ROWS. Filter on the server and every
> keystroke in the search box would silently delete entries out of the Project / Type / Assignee / Month
> dropdowns — the user would watch their own filters vanish as they type. If you ever do need the server-side
> filter, it needs its own method and its own row set, not this one."*
[VERIFIED: `worklog.service.ts:452-462`, immediately above `getBacklogList()`]

The correct port is a **client-side** team filter over the already-loaded rows, consistent with the other four
dropdowns. **No regen, and probably cheaper than costed.** *Cost*: ~3 Angular files, 0.5 day.

**P7 — Log Work grid affordances.** *4 audit rows: jump-to-any-week control, Month/Year filter, holiday
day-column rendering, footer >8h styling.*

The toolbar is Prev / label / Next / This week / Smart fill and nothing else [VERIFIED:
`log-work.component.html:15-24`]. There is no holiday rendering in the page at all — the only `holiday` hits
are two spec assertions about the server's 400 and one code comment [VERIFIED: `grep -rin holiday
src/timesheet-web/src/app/pages/log-work/` → `log-work.component.spec.ts:400,405,529,535` and
`log-work.component.ts:438`]. The footer stylesheet has `.zero` states and no over-cap rule [VERIFIED:
`log-work.component.scss:7,38,45,77`].

**Correcting the previous version's one showcase verification.** It claimed *"`GET /api/holidays` is
deliberately unauthenticated"*. **That is wrong.** `AuthSetup.cs:146` sets `o.FallbackPolicy = o.DefaultPolicy`,
which is `.RequireAuthenticatedUser()` [VERIFIED: `AuthSetup.cs:130-147`], and the only three
`.AllowAnonymous()` call sites in the API are login (`AuthSetup.cs:207`), `/health` (`Program.cs:238`) and the
SPA fallback (`Program.cs:282`) [VERIFIED: `grep -rn AllowAnonymous src/TimesheetApp.Api --include=*.cs`].
Holidays is authenticated. The client comment the prior pass leaned on means "not *admin*-gated", contrasting
with the `[ADMIN]` tags on the writes below it.

**The conclusion survives** — a Log Work user is logged in, so holiday shading remains a pure client-side
change with no API work and no regen — but the reason was wrong, and this document becomes the frozen spec, so
the wrong reason is corrected here rather than propagated.

*Cost*: 3 Angular files, all four items together, 1-2 days. The holiday one is the highest value here — today
a user discovers a holiday by typing into the cell and being refused.

**P8 — Task List display gaps.** *2 audit rows: PROJECT label on the card in team-grouped mode, Gantt
all-undated caption.*

The template asserts *"`project` is NOT here: the band above already shows it"* [VERIFIED:
`task-list.component.html:125-126`] — true when grouped by Project, **false** when grouped by Team, and with
≥2 teams checked no web user can see which project a backlog belongs to. The only empty-state branch fires on
zero rows [CITED: `M10-COVERAGE-AUDIT.md:65` → `task-list.component.html:68-72`], a different condition from
"every backlog is undated", which renders a silent empty Gantt card.

*Cost*: 1-2 files, template-level. ~2 hours.

**P9 — Daily Board issue solution text.** *1 audit row.*

The board renders `i.issueText` and never `i.solutionText`, though the field is on the DTO and the *same page*
consumes it 90 lines earlier in the input tab [VERIFIED: `daily-report.component.html:198` renders
`{{ hasSolution(i) ? '✓' : '⚠' }} {{ i.issueText }}`; `:107` binds `[value]="i.solutionText || ''"`]. You can
see *that* an issue resolved (the ✓), never *how*.

*Cost*: **one template line.** Highest value-per-effort in the entire audit.

### Tier 4 — the one PORT with a design fork inside it

**P10 — Smart Fill multi-task one-shot fill.** *1 audit row.*

The wire already accepts N tasks [CITED: `M10-COVERAGE-AUDIT.md:70` → `TimesheetEndpoints.cs:461`]; the client
always builds a single-element array [VERIFIED: `smart-fill.ts:43-56` — `buildSmartFillRequest(taskId: number, …)`
ends `return [{ taskId, cells }]`].

**The divergence, re-traced by hand in this pass rather than inherited.** The web did not delegate the split
math to Core — it reimplemented it in TypeScript, and *the two implementations do not agree.*

- Web `distributeHours(8, 3)` [VERIFIED: `smart-fill.ts:19-30`]: `per = round1(8/3) = 2.7`;
  `drift = round1(8 − 2.7×3) = round1(−0.1) = −0.1`; last day `round1(2.7 − 0.1) = 2.6` → **[2.7, 2.7, 2.6]**.
- Core `DistributeEven` [VERIFIED: `SmartInputService.cs:25-49`]: `totalTenths = 80`;
  `baseTenths = 80/3 = 26`; `remainder = 80 % 3 = 2`, added to the **last** day → **[2.6, 2.6, 2.8]**.

Both sum to exactly 8.0 and both respect the 1-decimal rule, so neither is wrong on the wire — but
`smart-fill.ts:8`'s own comment claims *"this is the same rule"*, and it is not [VERIFIED].

Porting multi-task by extending the TypeScript deepens a divergence nobody appears to have decided on. Porting
it by routing through Core's `BuildPlan` (which has no route reaching it today) makes Core the single source
of truth but is a bigger change and changes a shipped distribution result.

*Cost*: 1-2 days extending the client math + a checklist UI; more if the Core-routing path is chosen.
**The fork is a real decision, not an implementation detail.**

---

## ACCEPT — the loss is fine, and why

**[SIGN-OFF]** marks a loss that is a real product or security decision, not a technical footnote. Those need
a named person's yes, not silence.

| # | Behavior | Why the loss is acceptable |
|---|---|---|
| A1 | **OneDrive conflict-copy banner (XC-08)** **[SIGN-OFF]** | The failure mode needs a locally-synced `.db` with multiple writers. One server-hosted database removes the multiple-writers half structurally. Detection still survives in Core and still aborts a retention run [CITED: `M10-COVERAGE-AUDIT.md:55`]. **Caveat I could not close: if the host's own data directory sits under a synced folder, the failure mode returns.** |
| A2 | **Journal-integrity banner (XC-09)** | The API runs WAL, under which the `<db>-journal` rollback file this guard watches for is never created — the guard is structurally inert on the web whether or not it has a UI. `TraceJournalWarningSink` is registered as trace-log-only [CITED: `M10-COVERAGE-AUDIT.md:56` → `Program.cs:79`]. |
| A3 | **Log Work entry-target user picker + read-only team view + amber badge** *(2 rows)* **[SIGN-OFF]** | The API refuses this *by design*, in its own words: *"Deliberately does NOT accept a userId query param: unlike WPF's EntryTarget picker, the web API never lets one user view another named user's individual grid — only their own, or the already-aggregated (and read-only) team view."* [VERIFIED: `TimesheetEndpoints.cs:56-62`]. A deliberate privacy narrowing. Audit Open Question 5 asks for an owner's yes; this file does not supply one. |
| A4 | **Smart Fill: backlog-code search → task checklist** | The workaround is one extra already-covered step: add the task to the grid via the existing add-task dialog, then it is reachable by the on-grid Smart Fill. One extra click, not a lost capability. [CITED: `M10-COVERAGE-AUDIT.md:69` → `log-work.component.ts:483-484`] |
| A5 | **Smart Fill: "Full 8h" mode** | The identical outcome is reachable today: single-task DistributeEven with Total = 8 × day-count. Hand-traced `distributeHours(40, 5)` → `[8,8,8,8,8]`, no drift, because 40/5 divides evenly [VERIFIED: `smart-fill.ts:19-30`]. A missing shortcut, not a missing capability. **Caveat, and it is real: `day-count` means working days *excluding holidays*, which the user cannot see until P7 ships** [VERIFIED: `SmartInputService.cs:33,52` both enumerate via `_calc.WorkingDaysBetween(from, to, holidays)`]. The ACCEPT is sound; its workaround is awkward until P7 lands. Noted in passing: Core's comments call this a "top up to 8h", but `FillFull8h` is `days.Select(d => new CellAssignment(d, 8m))` — it reads no existing logs, it is a flat assignment [VERIFIED: `SmartInputService.cs:51-59`]. |
| A6 | **Smart Fill: arbitrary From/To date range** | **The closest call on this list.** Workaround exists — navigate week by week and fill each. For a multi-week backfill after time off that turns one operation into N. Real tedium, not a block. I have no usage data to settle whether anyone does large backfills; **if this gets pushback, it is the item.** [CITED: `M10-COVERAGE-AUDIT.md:72`] |
| A7 | **Smart Fill: Preview-then-Confirm gate** | Consistent with, not a regression from, the rest of the screen: every other write on Log Work already uses attempt-then-revert-on-400. Worth recording that `/api/smartfill/validate` is wired end to end and lacks only a call site, so this is the cheapest Smart Fill item to add later if someone wants it. [CITED: `M10-COVERAGE-AUDIT.md:73`] |
| A8 | **SharePoint destination Verify button (SP-01)** | A pre-flight convenience, not the only safety net — the export itself still hard-validates and blocks on `Level: Error` [CITED: `M10-COVERAGE-AUDIT.md:77` → `ExportHubService.cs:62-68`]. A bad destination still surfaces, one export cycle later. `ISharePointDestinationValidator` appears in the API only as a DI line [CITED: `M10-COVERAGE-AUDIT.md:77` → `Program.cs:106`]. |
| A9 | **Retention: automatic unattended run at startup** **[SIGN-OFF]** | Retention is default-OFF and destructive. Requiring a human to click "run retention now" is arguably *safer* than a silent purge at every server restart — a genuine trade-off, not merely a downgrade. Also: `RetentionEnabled` becomes a dead flag either way — `RetentionService` never reads it and `App.xaml.cs:91` was its sole consumer [VERIFIED: `grep -rn RetentionEnabled src/TimesheetApp.Api` → exit 1, no matches; the only production readers repo-wide are `App.xaml.cs:88,91` and `SettingsViewModel.cs:172,315`]. **Refinement on the audit's framing:** retention was only automatic on a machine with an export root configured — it sits inside the same `if` as P4 [VERIFIED: `App.xaml.cs:79-96`]. Audit Open Question 4. |
| A10 | **Identity: auto-provision an unmapped user on first run** **[SIGN-OFF]** | WPF granted access with *no credential at all*; requiring real accounts is the entire point of adding auth. Keeping the old behavior would defeat it. `AuthSetup.cs:175-177` fails closed on a NULL hash, with the comment *"Never treat it as 'any password matches': that is an authentication bypass, and it is exactly the state every user is in on a freshly migrated database"* [VERIFIED]. **This ACCEPT is safe as a disposition and unsafe as a schedule — see "Gate zero" below.** |
| A11 | **Identity: first-run auto-join to the active team (TM-09)** | Only ever meaningful paired with A10. Once an admin creates the account anyway, assigning a team is one more field in a flow that is already manual. Record it as an onboarding checklist item. |

---

## OBSOLETE

**O1 — Window chrome (1180×760, min 980×620, CenterScreen).** [CITED: `M10-COVERAGE-AUDIT.md:57`] The browser
owns window sizing and placement; there is nothing to port. (Responsive min-width guards are a real web
concern but a different one — a design question for the new build, not a lost WPF behavior.)

---

## Gate zero — the precondition that sits outside all three options

**Nobody should pick an option until Open Question 1 is answered, and answering it requires opening the
production database, which I was instructed not to do.**

- Login hard-fails a NULL hash [VERIFIED: `AuthSetup.cs:175-177`].
- Self-recovery is deliberately blocked — *"No password is set for this account. Ask an administrator to set
  one."* [CITED: `M10-COVERAGE-AUDIT.md:96` → `AuthEndpoints.cs:91-93`].
- `DatabaseInitializer.cs:331` added `password_hash` with no default and no backfill; `:337` promoted only
  `MIN(id)` to admin [CITED: `M10-COVERAGE-AUDIT.md:96`].
- No bulk provisioning path exists — `SetPasswordHashAsync` has two production callers, both one user at a
  time [CITED: `M10-COVERAGE-AUDIT.md:96`].

**If existing non-admin employees have NULL password hashes, deleting WPF locks the company out of its own
timesheet tool on the first working day, with no self-service recovery.**

**Gate zero is two items, not one.** *Answer it* — `SELECT COUNT(*) FROM Users WHERE password_hash IS NULL`
against a **copy** of production — and, *if the answer is non-zero*, **build or script the provisioning sweep
and schedule the human comms before the deletion date.** That second half is work that does not exist yet and
appears in no plan. "The check came back non-zero" on the morning of the deletion is a schedule event, not a
five-minute one.

---

## The config fork — the decision I cannot make and neither can the attack

P11 needs a way to set `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount` after WPF is gone. The
obvious answer is an admin route. **The codebase explicitly forbids it**, in a doc-comment at the top of the
very file such a route would live in:

> *"**Never call any `IAppConfig.Set*` from an endpoint.** It is a process-wide singleton with ten setters; on
> a server every one of them is cross-user state — one user toggling dark mode flips it for everyone, and
> `SetDbPath` repoints the whole server's database."*
[VERIFIED: `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:46-49`]

The setters do persist — each writes the whole config file synchronously [VERIFIED: `JsonAppConfig.cs:92-108`
each call `Save()`; `Save()` at `:140-157` serialises all ten fields and `File.WriteAllText`s them]. So an
admin route would *work*. The prohibition is architectural, not mechanical.

**The honest tension:** the rule's stated rationale is "cross-user state", and it is strongest for dark mode
(genuinely per-user) and `SetDbPath` (catastrophic). For the three backup settings the rationale is weakest —
on a single-server deployment there *is* only one backup folder, so "process-wide" is the correct scope, not a
bug. But the rule as written is unconditional, and `Save()` rewrites all ten fields from in-memory state, so a
partial-write bug in a new route could clobber `DbPath`.

Three shapes, and **a human picks one**:

| Shape | What it is | Cost | What could go wrong |
|---|---|---|---|
| **(a) Admin route** — relax the rule for 3 named settings | `POST /api/ops/backup/config` alongside the four existing `/api/ops/*` routes, whitelisting only the three backup setters | ~0.5 day | Sets a precedent against a documented rule; `Save()` rewrites all ten fields, so a bug here can repoint `DbPath` — the one setting the rule most wants protected. Needs an explicit "never expose `SetDbPath`" guard and a test pinning it. |
| **(b) Host config + restart** — obey the rule, document the file | Operator edits `%APPDATA%\TimesheetApp\appsettings.json`, restarts the API. No code, one runbook page. | ~0.25 day | **The Settings UI currently points operators at the wrong file** — it says "appsettings.json on the host" while `TimesheetApp.Api` has none [CITED: `M10-COVERAGE-AUDIT.md:121,272`]. Fix that copy first or an operator edits a file that does not exist. Also: config loads once in the constructor [VERIFIED: `JsonAppConfig.cs:53-67`] behind a singleton [VERIFIED: `Program.cs:49`], so **a restart is mandatory, not optional**. |
| **(c) Read-only surfacing** — do not make it settable, make it *visible* | P3's list route also returns "backup is enabled: yes/no, folder: X" so a misconfiguration is loud | ~0.25 day, composes with (a) or (b) | Does not fix the problem, only stops it being silent. **On its own this is not enough** — but paired with (b) it is probably the cheapest honest answer. |

I would pair **(b) + (c)**: obey the documented rule, fix the wrong pointer in the Settings copy, and make the
state visible so a blank folder is loud rather than silent. But (a) is defensible and faster for an operator
who will have to do this more than once, and **whether the rule bends for three settings is an architectural
call with an owner, not mine.**

---

## The hosting question — a precondition, not a detail

P1 assumes something runs the scheduler. **Nothing supervises the API today.** The repo contains exactly two
scripts [VERIFIED: `ls *.bat` → `deploy-local.bat`, `start-web.bat`], and the deployment is an unsupervised
foreground process:

```
deploy-local.bat:38    dotnet run -c Release --urls http://localhost:5080
```
[VERIFIED]

with its own header conceding the gap verbatim: *"To let OTHER machines on the LAN reach it, change the
`--urls` at the bottom… **That is the deferred 'who hosts it' decision**, so this script defaults to localhost
only."* [VERIFIED: `deploy-local.bat:11-13`]. No Windows Service wrapper, no IIS/`web.config`, no scheduled
task, no container, no restart-on-crash anywhere.

Compare the two triggers honestly:

| | WPF today | Ported `BackgroundService` |
|---|---|---|
| Fires when | every launch of the app, by every user, every working day [VERIFIED: `App.xaml.cs:62-63`] | only while one console window stays open on one machine |
| Survives a reboot | yes — next user opens the app | no — until someone re-runs the `.bat` |
| Independent chances/day | one per user per launch | zero if nobody started the process |

The once-per-day guard in `AutoBackupIfDueAsync` [VERIFIED: `BackupService.cs:65`] exists *precisely because*
it fires so often. That redundancy — N users × daily launches — is the property that made the desktop safety
net work. **A hosted service on an unsupervised `dotnet run` has strictly worse liveness than the thing it
replaces.** Porting the trigger without answering "who hosts it" produces a safety net that is not one.

An out-of-process trigger — Windows Task Scheduler invoking a small console project — sidesteps this, and the
**same binary can carry the restore (P2)**, since an offline restore must stop the API anyway (`ClearPools()`
is process-local). That is one build serving two of the three Tier-1 items.

Also carry forward, unresolved: whether `SqliteConnection.BackupDatabase` can throw `SQLITE_BUSY` against an
actively-written WAL source (`SqliteOnlineBackup.Open` sets no `busy_timeout` [VERIFIED — the connection
string sets `DataSource`, `Mode`, `Pooling` only]). If it can, a scheduled backup colliding with traffic fails
nightly, and **P3 is what would reveal it.**

---

## Option A — Full port gate: build everything, then delete

**What gets built.** P1-P11, before `git rm -r src/TimesheetApp/`. Concretely: one
`ScheduledJobsService.cs` + `Program.cs` registration; the config fork resolved and built; a restore mechanism
(runbook or CLI) plus a rehearsal against a live-API copy; `GET /api/ops/backup/list` + Settings list; the
archive-backfill jobs if the export-root question resolves that way; the sidebar switcher +
`ApiCurrentTeamService` write-back; backlog team filter + column; four Log Work affordances; two Task List
template fixes; the Daily Board line; multi-task Smart Fill.

**Rough cost.** ~10-14 working days of implementation across `TimesheetApp.Api` (1-2 new files, 1-2 edited),
`timesheet-web` (~12 files across 5 pages), no Core change, no client regen (P6 is client-side). Plus gate
zero, plus the hosting decision, plus the restore rehearsal — none of which is code.

**What could go wrong, especially to production data.** The largest risk is not any single package — it is
**duration**. For those 10-14 days plus review, WPF stays alive as a second writer against the production
database, which is the precondition for the OneDrive conflict-copy scenario (A1) that the web architecture
otherwise removes. The two front-ends also keep drifting: every one of the 45 downgraded COVERED claims
happened because someone verified a happy path against a moving target. Separately, the scheduler carries the
prune hazard — `ExportHubService.cs:145` copies the whole live DB into `{root}/db` on **every** run with
`BackupKeepCount` (default 30) [VERIFIED: `ExportHubService.cs:144-145`], so a scheduler firing more often
than daily silently eats backup depth. That defect is live on `POST /api/ops/export/run` today, independent of
M10.

**Undo cost.** Cheap per package and near-zero for the UI ports — additive template and component changes. The
scheduler is one file plus one line. The genuinely awkward reversal is P10 if it routes through Core, because
that changes a shipped distribution result. Deleting WPF itself remains `git revert`-able for as long as
anyone remembers the tag exists.

---

## Option B — Safety-net gate: make the data survivable, delete, then ship affordances

**What gets built before `git rm`** — the amended Tier-1 set, which is larger than the previous version of
this file claimed:

0. **Gate zero, both halves** — the `password_hash` count against a copy, *and* the provisioning sweep + user
   comms if it comes back non-zero.
0b. **The hosting answer** — Windows Service, scheduled task, or supervised host. Until the API survives a
   reboot, no scheduled backup is trustworthy. This is a blocker, not a deferred detail.
1. **P1** — the scheduler (auto-backup + export-hub backfill), registered gated on `!IsEnvironment("Testing")`,
   with `TimesheetApp:ConfigPath`/`:DbPath` pinned explicitly rather than inheriting the per-account
   `%APPDATA%` default [VERIFIED: `Program.cs:34-36` takes the `JsonAppConfig()` default whenever *either*
   override is blank; `JsonAppConfig.DefaultConfigPath()` at `:172-175` resolves to the **calling account's**
   roaming profile, and `DefaultDbPath()` at `:177-181` to `Documents\TimesheetApp\timesheet.db`. Whichever
   Windows account runs the process therefore silently decides which database is opened — `Program.cs:52-53`
   says exactly this]. Plus: log at **Error** when the job returns early because backup is disabled or
   unconfigured.
2. **P11** — the config fork, resolved. Without it the deletion strands three settings with no writer.
3. **P3** — the backup-list route + Settings display, distinguishing *"not configured"* from *"none yet"*.
4. **P2** — a restore procedure, **rehearsed once end to end against a copy with a live API attached**, going
   through `SqliteOnlineBackup` (pre-restore copy + sidecar deletion), never a plain file swap.
5. **`Palette.Dark.xaml` extraction** — copy the hex values into a doc or SCSS token file before deletion
   [CITED: `M10-COVERAGE-AUDIT.md:119,273`]. ~30 minutes, and **the only item on the whole list where deletion
   destroys information rather than relocating it into git history.**

**Then delete WPF.** Then ship P5-P10 as a normal post-M10 milestone, working from this document and
`M10-COVERAGE-AUDIT.md` as the spec. P4 is decided by the export-root question and can land in either gate.

**Two shapes for items 0b/1/4, and they are genuinely different builds:**

- **B-i, in-process:** an `IHostedService` in the API + a restore runbook. Idiomatic, but inherits the API's
  liveness — which today is an unsupervised console window.
- **B-ii, out-of-process:** a small console project invoked by Windows Task Scheduler, carrying **both** the
  backup trigger and the restore. Survives reboots independently of the API, and the restore half is the one
  shape that can legitimately stop the API first. Probably *less* code than B-i, and it makes the hosting
  question smaller rather than answering it.

**Rough cost.** Gate: ~4-6 days of code, plus the hosting decision, the rehearsal and the DB check — which are
calendar time rather than engineering time. Post-M10: the remaining ~5-7 days, unblocked and parallelisable.

**What could go wrong, especially to production data.**
- **The affordance gaps ship to users.** For however long the post-M10 milestone takes, a multi-team user
  cannot switch teams (P5), nobody can see which team owns a backlog row (P6), and holidays are invisible
  until the server refuses the write (P7). Visible, reportable, non-destructive — but real, and calling them
  "deferred" does not make them not-shipped.
- **"After" can become "never."** A deferred list with no milestone attached is exactly how the four scheduled
  jobs got into this state — each a Core method whose only trigger was `App.xaml.cs`, unnoticed for an entire
  web build. **If this option is chosen, the post-M10 milestone should be created in the same sitting as the
  deletion commit.**
- **The rehearsal may fail.** Discovered inside the gate rather than during an incident — that is the point of
  putting it there, but it can push the schedule.

**Undo cost.** Same as Option A per package. The split itself is free to abandon in either direction: any
deferred package can be promoted into the gate, or demoted out, up until the deletion commit.

---

## Option C — Delete now, port against the frozen record

**What gets built.** Nothing, first. Tag the current commit (`wpf-final` or similar), freeze
`M10-COVERAGE-AUDIT.md` and this file as the spec, delete `src/TimesheetApp/` and make the four project-file
edits the audit specifies [CITED: `M10-COVERAGE-AUDIT.md:255-260`]. Then work the PORT list in priority order
against the tag.

**Rough cost.** Near zero up front — the deletion plus `TimesheetApp.sln`, the `TimesheetApp.Tests.csproj`
project reference, and the dead `InternalsVisibleTo`. The full ~10-14 days still has to happen; it happens
later.

**What could go wrong, especially to production data.** **This is the option with a genuine data-safety hole,
and it should not be chosen without naming who accepts it.** Between the deletion commit and P1+P11 shipping
there is **no automatic backup running at all** — WPF's daily trigger is gone, nothing replaces it, and after
P11's analysis, *nothing in the product can even turn one on*. Between the deletion commit and P2 there is
**no restore path in the product**. Those two windows overlap. A disk or corruption event inside that window
is recovered from whatever manual copy someone happens to have made, or not at all.

A sharper version of the same point: if the production `appsettings.json` *already* carries
`AutoBackupEnabled: true` and a folder — set at some point from the WPF Settings screen — then the safety net
survives the deletion **by accident, on a config value written by a deleted application, which nobody can
subsequently change or verify from the product**. That is not a safety net anyone should sign off, and it
looks identical from the outside to the case where it is blank.

The secondary risks are milder: WPF stops being a live second writer immediately (a real gain, the mirror of
Option A's main risk), and the audit is a good spec — but it is a spec written by agents who were wrong 45
times out of 193 on their first pass, and the WPF binary that could settle a disagreement is gone. One
concrete example already visible: the audit's PARTIAL row claiming *"No invalid-cell styling exists in
`log-work.component.scss`"* is **stale** — `log-work.component.scss:52-55` adds `.cell input.invalid` with the
`--danger-*` tokens, labelled M9.2 [VERIFIED]. That row is outside my scope, but it shows the document ages.

**Undo cost.** The deletion is `git revert` for as long as anyone remembers the tag. The risk is not technical
reversibility; it is that **nothing in the running product will signal that the backup safety net is missing**
until someone needs a backup.

---

## What I would pick, and why — for a human to accept or reject

**Option B, shape B-ii (out-of-process CLI + Task Scheduler), with the post-M10 milestone created in the same
sitting as the deletion commit.**

The reasoning:

1. **The PORT list is not one kind of thing, and treating it as one hides the only part that matters.**
   Sixteen of the twenty PORT rows are affordances a user discovers on first use and reports. Four —
   the backup trigger, the ability to *configure* the backup, the restore path, and the visibility that makes
   all three trustworthy — fail *silently*, and one of them loses data that cannot be reconstructed later. A
   gate that blocks on all twenty treats a missing template line as equal in kind to a missing backup. It is
   not. A gate that blocks on none of them is Option C.

2. **Deleting WPF does not make the deferred work harder.** Every Tier-3 item is a client-side change against
   an API that already exists — P5's server half is fully built and tested [VERIFIED: `SettingsEndpoints.cs:97`,
   `ApiCurrentTeamService.cs:73-84`], P6 is client-side by the service's own documented design, P7 needs no API
   work, P9 is one line. All specced by this document and recoverable from git history. The only artifact where
   deletion destroys information rather than relocating it is `Palette.Dark.xaml`, which is why extraction is
   in the gate at a cost of half an hour. **If that were not true — if the deferred work needed a running WPF
   to reverse-engineer — I would pick Option A instead.**

3. **B-ii rather than B-i because the liveness problem is real and the CLI solves two items at once.** A
   hosted service inside an unsupervised `dotnet run` has strictly worse liveness than the WPF trigger it
   replaces, which is the opposite of the gate's purpose. A Task Scheduler entry survives reboots, and the same
   binary is the only honest home for a restore that must stop the API first. This also makes the deferred
   "who hosts it" decision smaller instead of silently depending on it.

4. **Option A's cost is not the 10-14 days, it is what happens during them.** Two front-ends against one
   production database, drifting, with the desktop client reintroducing the exact multi-writer precondition
   the web architecture removes. Paying that to ship a Gantt caption at the same time as a backup service is
   the wrong trade.

5. **Option C asks someone to accept a window with no backup, no way to configure one, and no restore,
   simultaneously.** Everything else in this audit is a degradation. That is an exposure, and I would not take
   it to save two days.

**I would also revise one thing from the previous version of this recommendation, and say so plainly:** that
version put ~2-3 days on the gate. Having verified P11 and the hosting gap, ~4-6 days plus two non-engineering
decisions is the honest figure. The split-gate logic survives; the price tag on it did not.

---

## The trade-offs I am deliberately not resolving

These need an owner. Each one can flip part of the recommendation above.

- **Whether the affordance gaps are acceptable to ship.** I have no usage data. **If multi-team users are the
  common case rather than the exception, P5 stops being a deferrable affordance and belongs in the gate** — it
  is the one deferred item that leaves a whole class of user unable to reach their own data. A five-minute
  answer from whoever knows the team structure changes this recommendation.
- **The config fork (P11): does the "never call `IAppConfig.Set*` from an endpoint" rule bend for three backup
  settings, or does the operator edit a file and restart?** I lean (b)+(c). It is an architectural call with an
  owner, and the rule exists for a stated reason I did not overrule.
- **Who hosts the API.** Not a detail to settle later — it is the precondition that decides whether a scheduled
  backup means anything.
- **What shape the restore replacement takes.** Runbook, offline CLI, or a gated admin route differ in who can
  execute a restore under pressure and how badly it can go wrong. **I would not accept a written procedure that
  has never been run, and I would not accept a rehearsal without a live API attached** — but the shape is not
  mine.
- **Whether the archive backfills (P4) should run server-side at all.** They write whole-organisation data to
  shared host storage where the desktop wrote per-user, and **if an export root is configured the desktop never
  ran them either** [VERIFIED: `App.xaml.cs:79-107`]. Only someone who knows whether teams are meant to see
  each other's records can answer.
- **The Smart Fill math divergence (P10).** Extending the TypeScript is cheaper; routing through Core makes one
  source of truth. Both are defensible. The current state — two implementations, one comment claiming they
  agree, and a verified case where they do not — is the only option that is not.
- **A6 (Smart Fill date range) is the ACCEPT most likely to be wrong.** Accepted on a workaround argument with
  no usage data behind it.
- **Whether anyone accepts the window between deletion and the provisioning sweep** if gate zero comes back bad.

---

## Open, not closed

- Whether existing non-admin employees have non-NULL `password_hash` in production. **Gate zero for every
  option.** Not resolvable without opening a copy of the production database.
- Whether the production `appsettings.json` already has `AutoBackupEnabled: true` and a `BackupFolderPath` —
  decides whether the post-deletion backup gap is immediate or merely unmanageable. Same constraint: I did not
  open it.
- Which Windows account will run the API in production — decides which `%APPDATA%` config and which database
  the process actually opens [VERIFIED: `JsonAppConfig.cs:172-181`, `Program.cs:34-36,52-53`].
- Whether the production deployment configures an export root — decides whether P4 is a port or an addition,
  and whether retention was ever automatic.
- Where the production DB file physically lives, and specifically whether the host's data directory could sit
  under a synced folder — decides whether A1 is fully or only mostly safe.
- Whether `SqliteConnection.BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source —
  carried over unresolved from `B3-scheduled-options.md`.
- Whether WPF's `SmartInputPanelVm` calls Core's `BuildPlan` today or retains an inline duplicate — irrelevant
  to P10's disposition, relevant to its cost. Not traced.

---

*Cross-references: `B2-restore-evidence.md` / `B2-restore-options.md` (P2 mechanics), `B3-scheduled-evidence.md`
+ `B3-scheduled-options.md` (P1/P4 mechanics and scheduler design), `B1-auth-evidence.md` (gate zero),
`B4-missing-evidence.md` (prior evidence-only triage), `B4-missing-attack.md` (the adversarial pass on the
previous version of this file; H1/H2/H3/H4/H6/H7 independently re-derived and upheld here, and its proposed
Gate-1 remedy for H1 corrected against `SettingsEndpoints.cs:46-49`). Source table:
`M10-COVERAGE-AUDIT.md` lines 47-82.*

*No code was edited, no build or test was run, and no SQLite file was opened in producing this file.*
