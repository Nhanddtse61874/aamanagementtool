# B4 — Triage of the MISSING behaviors, and options for the M10 scope

**Nothing here is decided.** This file assigns one disposition to every row of the 🔴 MISSING table in
`M10-COVERAGE-AUDIT.md` (lines 47-82), groups them into work packages, and then offers three concrete
shapes for the M10 scope. A human picks the shape.

Every claim carries `file:line`. **[VERIFIED]** = I read that line in this pass. **[CITED]** = I relied on the
coverage audit's own two-pass citation without re-deriving it, referenced by its line in that file. No build
was run, no test was run, no SQLite file was opened.

---

## Counting, reconciled

The MISSING table has **33 rows**. The audit's prose says "29 distinct behaviors"; the prior evidence pass
(`B4-missing-evidence.md`) said 28. Both are grouping judgments over the same 33 rows — nothing is disowned by
either. This file groups them into **21 work items** (9 PORT, 11 ACCEPT, 1 OBSOLETE), and every one of the 33
rows is accounted for below. Where my grouping differs from the audit's, it is because I grouped by *what one
person would build in one sitting*, which is the unit a scope decision actually needs.

| Disposition | Work items | Audit rows covered |
|---|---:|---:|
| **PORT** | 9 | 20 |
| **ACCEPT** | 11 | 12 |
| **OBSOLETE** | 1 | 1 |
| **Total** | **21** | **33** |

---

## PORT — the real remaining work

Ordered by *what happens if it is missing*, not by size. The first three are the only ones where the loss
accrues silently while nobody is watching; the rest are discovered by a user on first use, which is its own
kind of safety.

### The ones that lose something unrecoverable

**P1 — Auto-backup trigger (BK-03) + export-hub catch-up backfill.** *Audit rows: "Backup: once-per-day
auto-backup trigger", "ExportHub: 12-month/12-week catch-up backfill".*
No `IHostedService` / `BackgroundService` / `AddHostedService` exists anywhere in `TimesheetApp.Api`
[VERIFIED: grep returned no matches]. `AutoBackupIfDueAsync`'s only caller is `App.xaml.cs:63` [CITED:
COVERAGE-AUDIT.md:76]. **This is the only item in the whole MISSING table where the loss is not recoverable
later**: a markdown archive skipped in July can be regenerated from the database in September; a backup not
taken on the day the data was correct cannot. The moment WPF is deleted, the automatic daily backup stops and
`AutoBackupEnabled` becomes a flag nothing reads.
*Cost*: one new `src/TimesheetApp.Api/Infrastructure/ScheduledJobsService.cs`, one `Program.cs` line, 3-6
tests. No Core change, no Angular, no regen. ~1 day. Full design already worked out in
`B3-scheduled-options.md` (its Option 1, scoped to jobs 1 and 2).

**P2 — A restore path of some kind.** *Audit row: "Settings: backup RESTORE (BK-05) + self-restore guard".*
`grep -rn RestoreAsync src/TimesheetApp.Api` returns exactly one hit and it is the comment explaining the
exclusion: *"`IBackupService.RestoreAsync` is NOT exposed in M8.3: it overwrites the live .db in place while
the API holds open connections, which corrupts live readers."* [VERIFIED:
`src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:43`]. The exclusion is correct engineering. The problem is
that **no replacement exists anywhere in the repo** — no CLI, no script, no runbook. Deleting WPF deletes the
only working restore path in the product, and it does so at the same moment P1's automatic backups stop.
*Cost*: depends entirely on shape, and **the shape is not decided** — offline CLI, documented stop-the-service
runbook, or a gated admin route. See `B2-restore-evidence.md` and the audit's Open Question 2. A runbook is
~0.5 day to write; **the honest cost is the rehearsal, not the writing** — a restore procedure nobody has ever
executed against a copy is not a replacement, it is a document.

**P3 — Backup visibility.** *Audit row: "Settings: backup list — Refresh + display of existing backups".*
`ListBackups()` has exactly one production caller in the entire repo and it is the WPF view-model
[VERIFIED: `grep -rn ListBackups --include=*.cs` → `TimesheetApp/ViewModels/SettingsViewModel.cs:287` is the
only non-test, non-definition hit]. From the browser you can *trigger* a backup but never see whether one
exists, when, or how big. Paired with the audit's PARTIAL finding that "Backup now" reports a backup that did
not happen as success [CITED: COVERAGE-AUDIT.md:92], the post-M10 state is: a button that always says it
worked, and no way to check.
*Cost*: one read-only route + a list in the Settings page. ~0.5 day. **This is what makes P1 and P2
trustworthy** — without it, a silently-failing backup job is indistinguishable from a working one.

### The archive-continuity pair (recoverable, but only if someone remembers)

**P4 — Weekly standup archive backfill (DR-09) and monthly task-list archive backfill.** *Audit rows: "Daily
Report: startup backfill of missing weekly archives", "Task List: on-disk monthly markdown archive + startup
backfill".*
The API documents its own gap: *"`Program.cs` never calls `BackfillMissingWeeksAsync` (only the WPF
`App.xaml.cs` does), so on a web-only deployment nothing ever wrote it."* [VERIFIED:
`src/TimesheetApp.Api/Endpoints/AdminEndpoints.cs:16-21`]. The export route deliberately calls the
content-only builder, not the disk-writing `ExportMonthAsync` [VERIFIED:
`src/TimesheetApp.Api/Endpoints/TaskListEndpoints.cs:87-96`].
*Cost*: same file as P1, +~0.5 day. **But `B3-scheduled-options.md` raises a point that should change the
disposition for a reader who accepts it**: `App.xaml.cs:76-82,97-106` runs these two jobs *only when no export
root is configured*, because the export hub supersedes them. If the production deployment configures an export
root — which is the whole SharePoint story — then **the desktop never ran these two jobs either**, and porting
them would newly materialise an all-teams flat archive on the shared host: an addition, not a preservation.
**I am flagging this as conditional-PORT, resolvable by one fact I do not have** (whether the production
deployment sets an export root).

### The ones a user hits on day one

These are all client-side, all cheap, and — this matters for sequencing — **none of them needs the WPF app
alive to build.** Git history and this audit preserve the spec.

**P5 — Active-team switcher.** *Audit rows: switcher, persistence + resolution-on-load, team-switch resets
every TeamFilter, visibility rule (4 rows, one feature).*
The sidebar's team `<select>` is literally mockup markup — two hardcoded `<option>`s, no binding, no
`(change)` handler [VERIFIED: `src/timesheet-web/src/app/components/sidebar/sidebar.component.html:47-50`].
`WorklogService.setActiveTeam()` has zero production callers — its only references are its own definition and
a spec [VERIFIED: `grep -rn setActiveTeam timesheet-web/src` → `worklog.service.ts:1239`,
`worklog.service.spec.ts:736`, plus a comment at `team-filter.component.ts:142` saying so outright]. **A
multi-team user has no way to change their active team on the web at all.** The consuming half is already
built and unit-tested, waiting on a producer [CITED: COVERAGE-AUDIT.md:51].
*Cost*: ~4 files — sidebar component + template, plus the `ApiCurrentTeamService.InitializeAsync` fallback
write-back fix that rides along [CITED: COVERAGE-AUDIT.md:50,123]. 1-2 days. **Largest of the UI ports, and
the one most likely to be reported as a bug rather than a missing feature.**

**P6 — Backlog team visibility (filter + TEAM column).** *2 audit rows.*
`getBacklogList()` takes no parameters at all — `backlogListFn(this.http, this.rootUrl, {})` [VERIFIED:
`src/timesheet-web/src/app/services/worklog.service.ts:463-465`], so the server defaults to every team the
caller belongs to — *broader* than WPF's default, with nothing able to narrow it. The four filter dropdowns
are project / type / assignee / month, no team [VERIFIED:
`src/timesheet-web/src/app/pages/backlog/backlog.component.html:20-27`]. `teamId` is already on the DTO and
never rendered [CITED: COVERAGE-AUDIT.md:54]. Not a leak — the server still scopes to membership — but with
>1 team in view nobody can tell which team owns a row.
*Cost*: ~3 Angular files. 0.5-1 day. **[ASSUMED]** the generated `backlogList` client fn already accepts a
`teamIds` param (the server does, per COVERAGE-AUDIT.md:53); if it does not, add a client regen to the cost.

**P7 — Log Work grid affordances.** *4 audit rows: jump-to-any-week control, Month/Year filter, holiday
day-column rendering, footer >8h styling.*
The toolbar is Prev / label / Next / This-week and nothing else [VERIFIED:
`log-work.component.html:15-21`]. There is no holiday rendering anywhere — the only "holiday" hits in the page
are a code comment and two spec assertions about the server's 400 [VERIFIED: `grep -rin holiday
pages/log-work`]. The footer stylesheet has a `.zero` state and no over-cap rule [VERIFIED:
`log-work.component.scss:77` and the full file scan].
**A cost driver I checked rather than assumed:** the holiday data is already reachable client-side —
`GET /api/holidays` is deliberately unauthenticated, and the service says why: *"`GET /api/holidays` is OPEN
(the week grid rejects a write on a holiday, so every user needs [it])"* [VERIFIED:
`SettingsEndpoints.cs:602-611` has no `.RequireAuthorization`; `worklog.service.ts:1008-1012`]. So holiday
shading is a pure template change with no API work and no regen. Someone anticipated exactly this.
*Cost*: 3 Angular files, all four items together. 1-2 days. The holiday one is the highest value here — today
a user discovers a holiday by typing into the cell and being refused.

**P8 — Task List display gaps.** *2 audit rows: PROJECT label on the card in team-grouped mode, Gantt
all-undated caption.*
The template asserts *"`project` is NOT here: the band above already shows it"* [VERIFIED:
`task-list.component.html:126`] — true when grouped by Project, **false** when grouped by Team, and with ≥2
teams checked no web user can see which project a backlog belongs to. The only empty-state branch fires on
`rows().length === 0` [VERIFIED: `task-list.component.html:68-72`], a different condition from "every backlog
is undated", which renders a silent empty Gantt card.
*Cost*: 1-2 files, template-level. ~2 hours.

**P9 — Daily Board issue solution text.** *1 audit row.*
The board renders `i.issueText` and never `i.solutionText`, though the field is on the DTO and the same page
consumes it elsewhere [VERIFIED: `daily-report.component.html:196-200`]. You can see *that* an issue resolved
(the ✓), never *how*.
*Cost*: **one template line.** Highest value-per-effort in the entire audit.

### The one PORT with a design fork inside it

**P10 — Smart Fill multi-task one-shot fill.** *1 audit row.*
The wire already accepts N tasks [CITED: COVERAGE-AUDIT.md:70 → `TimesheetEndpoints.cs:461`]; the client
always builds a single-element array [VERIFIED: `smart-fill.ts:43-56`, `buildSmartFillRequest(taskId: number,
…)` returns `[{ taskId, cells }]`].

**A finding the prior pass missed, which a human should see before costing this.** The web did not delegate the
split math to Core — it reimplemented it in TypeScript, and *the two implementations do not agree.*
`smart-fill.ts:19-30` rounds the per-day figure first and pushes drift onto the **last** day; Core's
`DistributeEven` floors in integer tenths and pushes the remainder onto the last day
[VERIFIED: `TimesheetApp.Core/Services/SmartInputService.cs:37-46`]. Hand-traced for 8h across 3 days: the web
yields **[2.7, 2.7, 2.6]**, Core yields **[2.6, 2.6, 2.8]**. Both sum to exactly 8.0 and both respect the
1-decimal rule, so neither is wrong on the wire — but `smart-fill.ts:8`'s own comment claims *"this is the same
rule"*, and it is not. Porting multi-task by extending the TypeScript deepens a divergence nobody appears to
have decided on; porting it by routing through Core's `BuildPlan` (which has no route reaching it today —
`SmartInputService.cs:75-127`) makes Core the single source of truth but is a bigger change.
*Cost*: 1-2 days extending the client math + a checklist UI; more if the Core-routing path is chosen. **The
fork is a real decision, not an implementation detail.**

---

## ACCEPT — the loss is fine, and why

**[SIGN-OFF]** marks a loss that is a real product or security decision, not a technical footnote. Those need
a named person's yes, not silence.

| # | Behavior | Why the loss is acceptable |
|---|---|---|
| A1 | **OneDrive conflict-copy banner (XC-08)** **[SIGN-OFF]** | The failure mode needs a locally-synced `.db` with multiple writers. One server-hosted database removes the multiple-writers half structurally. Detection still survives in Core and still aborts a retention run [CITED: COVERAGE-AUDIT.md:55]. **Caveat I could not close: if the host's own data directory sits under a synced folder, the failure mode returns.** |
| A2 | **Journal-integrity banner (XC-09)** | The API runs WAL, under which the `<db>-journal` rollback file this guard watches for is never created — the guard is structurally inert on the web whether or not it has a UI. `TraceJournalWarningSink` is registered as trace-log-only [CITED: COVERAGE-AUDIT.md:56 → `Program.cs:79`]. |
| A3 | **Log Work entry-target user picker + read-only team view + amber badge** (2 rows) **[SIGN-OFF]** | The API refuses this *by design*, in its own words: *"Deliberately does NOT accept a userId query param: unlike WPF's EntryTarget picker, the web API never lets one user view another named user's individual grid — only their own, or the already-aggregated (and read-only) team view."* [VERIFIED: `TimesheetApp.Api/Endpoints/TimesheetEndpoints.cs:56-62`]. A deliberate privacy narrowing. Audit Open Question 5 asks for an owner's yes; this file does not supply one. |
| A4 | **Smart Fill: backlog-code search → task checklist** | The workaround is one extra already-covered step: add the task to the grid via the existing add-task dialog, then it is reachable by the on-grid Smart Fill. One extra click, not a lost capability. [VERIFIED: `log-work.component.ts:483-484` builds the candidate list from the loaded week] |
| A5 | **Smart Fill: "Full 8h" mode** | The identical outcome is reachable today: single-task DistributeEven with Total = 8 × day-count. Hand-traced `distributeHours(40, 5)` → `[8,8,8,8,8]`, no drift, because 40/5 divides evenly [VERIFIED: `smart-fill.ts:19-30` against `SmartInputService.cs:51-59`]. A missing shortcut, not a missing capability. Note in passing: Core's comments call this a "top up to 8h", but the method takes no database and reads no existing logs — it is a flat 8h assignment [VERIFIED: `SmartInputService.cs:5,57`]. |
| A6 | **Smart Fill: arbitrary From/To date range** | **The closest call on this list.** Workaround exists — navigate week by week and fill each. For a multi-week backfill after time off that turns one operation into N. Real tedium, not a block. I have no usage data to settle whether anyone does large backfills; **if this gets pushback, it is the item.** [VERIFIED: `log-work.component.ts:186,463` hard-cap to the displayed week] |
| A7 | **Smart Fill: Preview-then-Confirm gate** | Consistent with, not a regression from, the rest of the screen: every other write on Log Work already uses attempt-then-revert-on-400. Worth recording that `/api/smartfill/validate` is wired end to end and lacks only a call site, so this is the cheapest of the Smart Fill items to add later if someone wants it. [VERIFIED: `smartFillValidate` hits only the service definition, generated `fn/`, and a spec] |
| A8 | **SharePoint destination Verify button (SP-01)** | A pre-flight convenience, not the only safety net — the export itself still hard-validates and blocks on `Level: Error` [CITED: COVERAGE-AUDIT.md:77 → `ExportHubService.cs:62-68`]. A bad destination still surfaces, one export cycle later. `ISharePointDestinationValidator` appears in the API only as a DI line [CITED: COVERAGE-AUDIT.md:77 → `Program.cs:106`]. |
| A9 | **Retention: automatic unattended run at startup** **[SIGN-OFF]** | Retention is default-OFF and destructive. Requiring a human to click "run retention now" is arguably *safer* than a silent purge at every server restart — a genuine trade-off, not merely a downgrade. Also: `RetentionEnabled` becomes a dead flag either way, because `RetentionService` never reads it and `App.xaml.cs:91` was its sole consumer [VERIFIED: `grep -rn RetentionEnabled TimesheetApp.Api` → no matches]. Audit Open Question 4. |
| A10 | **Identity: auto-provision an unmapped user on first run** **[SIGN-OFF]** | WPF granted access with *no credential at all*; requiring real accounts is the entire point of adding auth. Keeping the old behavior would defeat it. `AuthSetup.cs` fails closed on a NULL hash with no auto-create path [CITED: COVERAGE-AUDIT.md:80 → `AuthSetup.cs:162-181`]. **This ACCEPT is safe as a disposition and unsafe as a schedule — see the precondition below.** |
| A11 | **Identity: first-run auto-join to the active team (TM-09)** | Only ever meaningful paired with A10. Once an admin creates the account anyway, assigning a team is one more field in a flow that is already manual. Record it as an onboarding checklist item. |

---

## OBSOLETE

**O1 — Window chrome (1180×760, min 980×620, CenterScreen).** [CITED: COVERAGE-AUDIT.md:57] The browser owns
window sizing and placement; there is nothing to port. (Responsive min-width guards are a real web concern but
a different one — a design question for the new build, not a lost WPF behavior.)

---

## The precondition that sits outside all three options

**Nobody should pick an option until Open Question 1 is answered, and answering it requires opening the
production database, which I was instructed not to do.**

`AuthSetup.cs` returns `Unauthorized()` for a NULL or empty `password_hash` with no auto-provision path
[CITED: COVERAGE-AUDIT.md:80,96]. `DatabaseInitializer.cs:331` added the column with no default and no
backfill; `:337` promoted only `MIN(id)` to admin; self-recovery is explicitly blocked at
`AuthEndpoints.cs:91-93`; and no bulk provisioning path exists [all CITED: COVERAGE-AUDIT.md:96]. WPF is
today the only client that lets a credential-less employee in [CITED: COVERAGE-AUDIT.md:96 →
`CurrentUserService.cs:14,25-37`].

**If existing non-admin employees have NULL password hashes, deleting WPF locks the company out of its own
timesheet tool on the first working day, with no self-service recovery.** That is not a porting question and
no option below fixes it. It is a `SELECT COUNT(*) FROM Users WHERE password_hash IS NULL` against a **copy**
of production, plus a provisioning sweep if the answer is non-zero. Treat it as gate zero for every option.

---

## Option 1 — Full port gate: build all 9 PORT packages, then delete

**What gets built.** P1 through P10 above, in that order, before `git rm -r src/TimesheetApp/`. Concretely:
one `ScheduledJobsService.cs` + `Program.cs` line; a restore runbook in `docs/` plus one rehearsal against a
copy; a backup-list route + Settings list; the archive-backfill jobs (if the export-root question resolves
that way); the sidebar switcher + `ApiCurrentTeamService` write-back; backlog team filter + column; the four
Log Work affordances; two Task List template fixes; the Daily Board line; multi-task Smart Fill.

**Rough cost.** ~8-12 working days of implementation across `TimesheetApp.Api` (1 new file, 1 edited),
`timesheet-web` (~12 files across 5 pages), no Core change. Possibly one client regen (P6). Plus the auth
precondition and the restore rehearsal, neither of which is code.

**What could go wrong, especially to production data.** The largest risk here is not in any single package —
it is **duration**. For those 8-12 days plus review, WPF stays alive and stays a second writer against the
production database, which is the precondition for the OneDrive conflict-copy scenario (A1) that the web
architecture otherwise removes. The two front-ends also keep drifting: every one of the 45 downgraded COVERED
claims in the audit happened because someone verified a happy path against a moving target. Beyond that: the
scheduler carries `B3-scheduled-options.md`'s prune hazard — `ExportHubService.cs:145` copies the DB into
`{root}/db` on *every* run, ungated, and `Prune` keeps only the newest 30, so a scheduler firing more often
than daily silently eats backup depth. That defect is live on `POST /api/ops/export/run` today, independent
of M10.

**Undo cost.** Cheap per package and near-zero for the UI ports — they are additive template and component
changes. The scheduler is one file plus one line to remove. The one genuinely awkward reversal is P10 if it
routes through Core, because that changes a shipped distribution result. **Deleting WPF itself remains
reversible via git for as long as anyone remembers the tag exists.**

---

## Option 2 — Split gate: safety net before deletion, affordances after

**What gets built, before `git rm`:**
1. **P1** — `ScheduledJobsService.cs` covering auto-backup and export-hub backfill (`B3-scheduled-options.md`
   Option 1, jobs 1 and 2), plus the `Program.cs` registration gated on `!IsEnvironment("Testing")` to avoid
   the `ApiFactory` trap that file documents.
2. **P2** — a restore procedure, **rehearsed once end to end against a copy of production**, with the
   procedure written to `docs/` and its shape (CLI vs. runbook vs. gated route) chosen deliberately.
3. **P3** — the backup-list route + Settings display, so P1 and P2 are observable rather than assumed.
4. **Extraction, not porting:** copy `Views/Theme/Palette.Dark.xaml`'s hex values into a doc or SCSS token
   file before the file is deleted [CITED: COVERAGE-AUDIT.md:119,273 — it is the sole record of the intended
   dark palette for ~9 unmapped tokens and 23 hardcoded component colours]. This is ~30 minutes and it is the
   only item on the whole list where deletion genuinely destroys information rather than relocating it into
   git history.
5. **Gate zero** — the auth precondition above.

**Then delete WPF.** Then ship P5-P10 (switcher, backlog team visibility, Log Work affordances, Task List,
Daily Board, Smart Fill) as a normal post-M10 milestone, working from this document and
`M10-COVERAGE-AUDIT.md` as the spec. P4 (archive backfills) is decided by the export-root question and can
land in either gate.

**Rough cost.** Gate 1: ~2-3 days of code (1 new API file, 1 route, a handful of Angular lines) plus the
rehearsal and the DB check, which are calendar time rather than engineering time. Post-M10: the remaining
~5-7 days, unblocked and parallelisable.

**What could go wrong, especially to production data.** Three things, stated plainly:
- **The affordance gaps ship to users.** For however long the post-M10 milestone takes, a multi-team user
  cannot switch teams (P5), nobody can see which team owns a backlog row (P6), and holidays are invisible
  until the server refuses the write (P7). These are visible, reportable, and non-destructive — but they are
  real, and calling them "deferred" does not make them not-shipped.
- **"After" can become "never."** A deferred list with no milestone attached is how the four scheduled jobs
  got into this state in the first place — each one is a Core method whose only trigger was `App.xaml.cs`,
  and nobody noticed for an entire web build. **If this option is chosen, the post-M10 milestone should be
  created at the same moment, not afterwards.**
- **The rehearsal may fail.** If the restore procedure does not actually work when tried against a copy, that
  is discovered inside Gate 1 rather than during an incident. That is the point of putting it there, but it
  can push the schedule.

**Undo cost.** Same as Option 1 per package. The split itself is free to abandon in either direction: you can
promote any deferred package into Gate 1, or demote one out, up until the deletion commit.

---

## Option 3 — Delete now, port against the frozen record

**What gets built.** Nothing, first. Tag the current commit (`wpf-final` or similar), freeze
`M10-COVERAGE-AUDIT.md` and this file as the spec, delete `src/TimesheetApp/` and make the four project-file
edits the audit specifies [CITED: COVERAGE-AUDIT.md:255-260]. Then work the PORT list in priority order
against the tag.

**Rough cost.** Near zero up front — the deletion itself plus `TimesheetApp.sln`, the
`TimesheetApp.Tests.csproj` project reference, and the dead `InternalsVisibleTo`. The full ~8-12 days still
has to happen; it just happens later.

**What could go wrong, especially to production data.** **This is the option with a genuine data-safety hole,
and it should not be chosen without naming who accepts it.** Between the deletion commit and P1 shipping,
there is **no automatic backup running at all** — WPF's daily trigger is gone and nothing replaces it — and
between the deletion commit and P2 there is **no restore path in the product**. Those two windows overlap. A
disk or corruption event inside that window is recovered from whatever manual copy someone happens to have
made, or not at all. Everything else on the PORT list is a visible inconvenience; this pair is not.

The secondary risks are milder: WPF stops being a live second writer immediately (a real gain, the mirror of
Option 1's main risk), and the audit is a good spec — but it is a spec written by agents who were wrong 45
times out of 193 on their first pass, and the WPF binary that could settle a disagreement is gone. One
concrete example already visible: the audit's PARTIAL row claiming *"No invalid-cell styling exists in
`log-work.component.scss`"* is stale — `log-work.component.scss:52-55` adds `.cell input.invalid` with the
`--danger-*` tokens, labelled M9.2 [VERIFIED]. That row is outside my scope, but it shows the document ages.

**Undo cost.** The deletion is `git revert` for as long as anyone remembers the tag. The risk is not technical
reversibility; it is that **nothing in the running product will signal that the backup safety net is missing**
until someone needs a backup. That is the failure mode this whole audit exists to prevent.

---

## What I would pick, and why — for a human to accept or reject

**Option 2, with the post-M10 milestone created in the same sitting as the deletion commit, not after it.**

The reasoning:

1. **The PORT list is not one kind of thing, and treating it as one hides the only part that matters.**
   Seventeen of the twenty PORT rows are affordances a user discovers on first use and reports. Three of them
   — the backup trigger, the restore path, and the visibility that makes both trustworthy — fail *silently*,
   and one of those three loses data that cannot be reconstructed later. A gate that blocks on all twenty
   treats a missing template line as equal in kind to a missing backup. It is not.

2. **Deleting WPF does not make the deferred work harder.** Every remaining PORT item is a client-side change
   against the existing API, specced by this document and recoverable in full from git history. The only
   artifact where deletion genuinely destroys information rather than relocating it is `Palette.Dark.xaml`,
   which is why extraction is in Gate 1 at a cost of half an hour. If that were not true — if the deferred
   work needed a running WPF to reverse-engineer — I would pick Option 1 instead.

3. **Option 1's cost is not the 8-12 days, it is what happens during them.** Two front-ends against one
   production database, drifting, with the desktop client reintroducing the exact multi-writer precondition
   the web architecture removes. Paying that for the sake of shipping a Gantt caption at the same time as a
   backup service is the wrong trade.

4. **Option 3 asks someone to accept a window with no backup and no restore, simultaneously.** Everything
   else in this audit is a degradation. That is an exposure, and I would not take it to save two days.

**The trade-offs I am deliberately not resolving, because they are operational and data-safety calls that
need an owner:**

- **Whether the affordance gaps are acceptable to ship.** I have no usage data. If multi-team users are the
  common case rather than the exception, P5 (the switcher) stops being a deferrable affordance and belongs in
  Gate 1 — it is the one deferred item that leaves a whole class of user unable to reach their own data. **A
  five-minute answer from whoever knows the team structure changes this recommendation.**
- **What shape the restore replacement takes.** Runbook, offline CLI, or a gated admin route are meaningfully
  different in who can execute a restore under pressure and how badly it can go wrong. I would want the
  rehearsal regardless of shape, and I would not accept a written procedure that has never been run.
- **Whether the archive backfills (P4) should run on a server at all.** They write whole-organisation data to
  shared host storage, where the desktop wrote per-user. Only someone who knows whether teams are meant to see
  each other's records can answer that, and if an export root is configured the desktop never ran them either.
- **The Smart Fill math divergence (P10).** Extending the TypeScript is cheaper; routing through Core makes
  one source of truth. Both are defensible; the current state — two implementations, one comment claiming they
  agree, and a verified case where they do not — is the only option that is not.
- **A6 (Smart Fill date range) is the ACCEPT most likely to be wrong.** I accepted it on a workaround
  argument with no usage data behind it.

---

## Open, not closed

- Whether existing non-admin employees have non-NULL `password_hash` in production. **Gate zero for every
  option.** Not resolvable without opening a copy of the production database.
- Whether the production deployment configures an export root — decides whether P4 is a port or an addition.
- Where the production DB file physically lives, and specifically whether the host's data directory could sit
  under a synced folder — decides whether A1 is fully or only mostly safe.
- Whether `SqliteConnection.BackupDatabase` can throw `SQLITE_BUSY` against an actively-written WAL source
  (`SqliteOnlineBackup.Open` sets no `busy_timeout`) — carried over unresolved from
  `B3-scheduled-options.md`. If it can, a scheduled backup that always collides with traffic fails every
  night, and P3 (backup visibility) is what would reveal it.
- Whether the generated `backlogList` client fn already accepts `teamIds`, or P6 needs a regen. [ASSUMED it
  does]

---

*Cross-references: `B2-restore-evidence.md` (P2 mechanics), `B3-scheduled-evidence.md` + `B3-scheduled-options.md`
(P1 and P4 mechanics and scheduler design), `B4-missing-evidence.md` (the prior evidence-only triage this file
builds on and corrects in two places: the Smart Fill math divergence, and the Full-8h "top up" wording).
Source table: `M10-COVERAGE-AUDIT.md` lines 47-82.*

*No code was edited, no build or test was run, and no SQLite file was opened in producing this file.*
