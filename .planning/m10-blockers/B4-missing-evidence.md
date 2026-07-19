# B4-missing — Triage of the MISSING behaviors (evidence only)

Scope: every row in the 🔴 MISSING table of `M10-COVERAGE-AUDIT.md` (lines 47-82), one disposition each —
**PORT / ACCEPT / OBSOLETE**. This file is the complete triage; two items in it (backup Restore, the four
scheduled jobs) have their own deeper evidence files already — `B2-restore-evidence.md` and
`B3-scheduled-evidence.md` — and are only summarized here, cross-referenced, not re-derived.

**Method.** The audit table has 33 rows; it collapses to **28 distinct behaviors** here (not the audit's
own "29" — the difference is grouping judgment, not disowned rows: every one of the 33 rows is accounted
for below, several of them travel together as one story — e.g. the four active-team-switcher rows are one
port, not four). Every disposition below was re-checked against the current source tree today with
`Grep`/`Read`, not copied from the audit's "Suggested disposition" column unread. Tags:
- **[VERIFIED]** — I read the cited line(s) myself this pass.
- **[CITED: COVERAGE-AUDIT.md]** — I did not re-derive it; the audit's own two-pass adversarial citation
  is the evidence, referenced by its line number in that file.

No solution design below — where a PORT item's *shape* (CLI vs. UI vs. background service) is genuinely
undecided, that is stated as an open question, not resolved.

---

## Scoreboard

| Disposition | Count |
|---|---:|
| **PORT** | 16 |
| **ACCEPT** | 11 |
| **OBSOLETE** | 1 |
| **Total distinct behaviors** | **28** |

---

## PORT — must exist on web before WPF dies

### 1. Active-team switcher (control + persistence + reset propagation + visibility rule)
*Collapses audit rows: Active-team switcher; persistence + resolution-on-load; team-switch resets TeamFilter; visibility rule.*

A multi-team user has **no way to change their active team on the web at all**.

- The sidebar's team `<select>` is dead mockup markup, no binding, no handler:
  `<select class="select" style="width:100%;"><option>Architect Improvement</option><option>Plus Team</option></select>`
  [VERIFIED: `src/timesheet-web/src/app/components/sidebar/sidebar.component.html:47-50`]
- `WorklogService.setActiveTeam(teamId)` exists and calls `PUT /api/me/active-team`, but has **zero
  production callers** — its only reference outside its own definition is a spec file.
  [VERIFIED: `grep -rn setActiveTeam src/timesheet-web/src` → hits only
  `worklog.service.ts:1239` (definition), `worklog.service.spec.ts:736` (test)]
- The code says so itself: `team-filter.component.ts:142-144` — *"because the web app has no team switcher
  yet: `WorklogService.setActiveTeam()` exists and has no caller … the server will not tell us, by
  design."* [VERIFIED]
- The consuming half (every screen reloading when the active team changes) is already built and unit-tested,
  waiting on the producer. [CITED: COVERAGE-AUDIT.md:51 → `team-filter.component.ts:146-148 reload()`]
- Server plumbing is real: `PUT /api/me/active-team` route exists. [CITED: COVERAGE-AUDIT.md:50 →
  `ApiCurrentTeamService.cs:75-84`]
- A real bug bundled into the same port: `ApiCurrentTeamService.InitializeAsync` computes the active-team
  fallback but never writes it back, so a stale `active_team_id` snaps a user back to a team that was
  reactivated. [CITED: COVERAGE-AUDIT.md:50,123 → `ApiCurrentTeamService.cs:66-69`]

**Cost shape**: a real `<select>` bound to `setActiveTeam()` + wiring the existing `reload()` consumers +
the hidden/read-only/switcher-by-team-count conditional + the fallback write-back fix. One coherent feature,
not four.

### 2. Backlog screen has no team visibility (filter + column)
*Collapses audit rows: TeamFilter toolbar on Backlog; TEAM pill column on Backlog grid.*

- `backlog.component.html` has exactly four filter `<select>`s — project, type, assignee, month — and no
  team control. [VERIFIED: `src/timesheet-web/src/app/pages/backlog/backlog.component.html:21-27`]
- `getBacklogList()` takes **no parameters at all**: `backlogListFn(this.http, this.rootUrl, {})`.
  [VERIFIED: `src/timesheet-web/src/app/services/worklog.service.ts:463-465`] — confirms the audit's claim
  that no `teamIds` is ever sent, so the server defaults to every team the caller belongs to
  (broader than WPF's default, not a leak, but nothing narrows it).
- `teamId` is present on the wire (`BacklogListItemDto.teamId?: number | null`)
  [VERIFIED: `src/timesheet-web/src/app/api/models/backlog-list-item-dto.ts:11`] but a search of
  `backlog-list.ts` for `teamId`/`TEAM` returns nothing — the field is never rendered.
  [VERIFIED: `grep -n "teamId|TEAM" src/timesheet-web/src/app/pages/backlog/backlog-list.ts` → no matches]

**Cost shape**: one more filter control (same pattern as the existing four) + one grid column reading a
field already on the DTO.

### 3. Log Work: jump-to-any-week control
Web toolbar is Prev / Next / This-week only:
`<button (click)="prevWeek()">◀ Prev</button> … <button (click)="nextWeek()">Next ▶</button> <button (click)="thisWeek()">This week</button>`
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/log-work.component.html:17-21`] — no date-jump input
anywhere in the file. Reaching a week N away costs N clicks.

**Cost shape**: one date input wired to the existing week-state signal — small, self-contained.

### 4. Log Work: Month/Year filter on the grid
No `PeriodMonth`/period filter control exists on this screen — grep for `PeriodMonth`/`period month` inside
`pages/log-work/` returns only internal move-month plumbing (`move-month.ts`, used for the "move to next
month" action, not a filter). [VERIFIED: `grep -in "PeriodMonth|period.?month"
src/timesheet-web/src/app/pages/log-work` → only `move-month.ts`/`move-month.spec.ts`/
`log-work.component.spec.ts`/`log-work.component.ts:607-619`, none of which is a filter UI] On a long
backlog list every group renders regardless of period.

### 5. Log Work: holiday day-column rendering
Zero matches for "holiday" in the component's template or stylesheet — the only hit in the whole
`log-work` page is a code comment describing the 400 the server can return.
[VERIFIED: `grep -rli holiday src/timesheet-web/src/app/pages/log-work` → only `log-work.component.ts`;
`grep -n holiday log-work.component.ts` → line 362, a comment: *"400 `{ error }` — a business rule said
no (8h cap, holiday, >1 decimal, hours <= 0)"*] The **rule** still holds server-side; the user just
discovers a holiday by typing into it and being refused, with no visual cue beforehand.

### 6. Log Work: footer day-total >8h styling
`log-work.component.scss` only has a `.zero` state on the footer cell
(`.grid__foot .c-day.zero { color: var(--faint); }`) — no over-cap rule.
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/log-work.component.scss:7,38,45,72` — all four `.zero`
hits, no red/bold/over-eight rule anywhere in the file] The only over-cap visual warning on the grid is
gone; the cap itself still triggers a server 400 on save.

### 7. Task List: PROJECT label on the card in Team-grouped mode
The template says so itself: `` `project` is NOT here: the band above already shows it. ``
[VERIFIED: `src/timesheet-web/src/app/pages/task-list/task-list.component.html:126`] — true when grouped
by Project, **false** when grouped by Team (≥2 teams checked): the band then shows Team, and no element
shows Project. With ≥2 teams in view, no web user can see which project a backlog belongs to. The data is
already on the wire. [CITED: COVERAGE-AUDIT.md:64 → `Dtos.cs:146`]

### 8. Task List: Gantt "no dated backlogs" caption
The only empty-state text in the Gantt/grid template is "Nothing here / No backlogs in this period."
[VERIFIED: `src/timesheet-web/src/app/pages/task-list/task-list.component.html:69-71`] — fires on zero
rows, a **different** condition from "every backlog is undated." No text for the latter case anywhere in
the file — an undated-only month renders a silent empty Gantt card.

### 9. Task List: on-disk monthly markdown archive + startup backfill
Verified no production caller of either method:
- `ExportMonthAsync` (disk-writing) is explicitly *not* what the export route calls — the route's own
  comment: *"`BuildMonthMarkdownAsync`, NOT `ExportMonthAsync` … `ExportMonthAsync(year, month)` -> WRITES A
  FILE ON THE SERVER and returns a SERVER PATH. Useless [to the browser]."*
  [VERIFIED: `src/TimesheetApp.Api/Endpoints/TaskListEndpoints.cs:87-96`]
- `BackfillMissingMonthsAsync` has no reference anywhere in `TimesheetApp.Api`.
  [VERIFIED: `grep -rn BackfillMissingMonthsAsync src/TimesheetApp.Api` → no matches]

This is one of the four jobs whose only production trigger today is `App.xaml.cs`; full mechanics,
idempotency, and concurrency evidence in **`B3-scheduled-evidence.md`** (job 4 there). Losing it ends the
accumulating month-by-month archive and its self-healing backfill of months nobody manually exported.

### 10. Daily Report: startup backfill of missing weekly archives (DR-09)
The API's own doc comment says it outright: *"`Program.cs` never calls `BackfillMissingWeeksAsync` (only
the WPF `App.xaml.cs` does), so on a web-only deployment nothing ever wrote it."*
[VERIFIED: `src/TimesheetApp.Api/Endpoints/AdminEndpoints.cs:16-21`] Confirmed independently: no other
reference to `BackfillMissingWeeksAsync` in `TimesheetApp.Api`.
[VERIFIED: `grep -rn BackfillMissingWeeksAsync src/TimesheetApp.Api` → only the `AdminEndpoints.cs` comment]
Second of the four `B3` jobs — see that file for full mechanics. Any week nobody manually archives via
`POST /api/standup/archive?date=` never gets a snapshot, silently, forever.

### 11. Daily Board: issue SOLUTION TEXT
The team board template renders only `i.issueText`, never `i.solutionText`, even though the field is on
the DTO and the entry form on the same page consumes it:
```
<div class="issue sm" [class.ok]="hasSolution(i)" [class.warn]="!hasSolution(i)">
  {{ hasSolution(i) ? '✓' : '⚠' }} {{ i.issueText }}
</div>
```
[VERIFIED: `src/timesheet-web/src/app/pages/daily-report/daily-report.component.html:196-200`] —
`solutionText` is defined on `StandupIssueDto` [VERIFIED: `src/timesheet-web/src/app/api/models/standup-issue-dto.ts:10`]
and is written/read elsewhere on the same page (`daily-report.component.ts:430,449,458`;
`standup-entry.ts:103-121`) — pure template omission. On the board you can see *that* an issue resolved
(the ✓), never *how*.

### 12. Smart Fill: multi-task one-shot fill
- Wire format already accepts multiple tasks in one request:
  `SmartFillRequest(IReadOnlyList<SmartFillTaskRequest> Tasks)` where
  `SmartFillTaskRequest(int TaskId, IReadOnlyList<SmartFillCellRequest> Cells)`.
  [VERIFIED: `src/TimesheetApp.Api/Endpoints/TimesheetEndpoints.cs:456,459,461`]
- The web's request builder only ever constructs a single-task array:
  `buildSmartFillRequest(taskId: number, isoDates, totalHours): SmartFillTaskRequest[]` returns
  `[{ taskId, cells }]` — one element, always. [VERIFIED: `src/timesheet-web/src/app/pages/log-work/smart-fill.ts:43-56`]
- **Correction to the audit's framing**: the split math is not something the web can get "for free" by
  calling an existing endpoint. `/api/smartfill/apply` takes literal pre-computed `(date, hours)` cells —
  *"there is no 'distribute evenly' mode on the wire, so whoever calls it must have already decided what
  each cell gets"* [VERIFIED: `smart-fill.ts:4-8` file-level comment]. The day-splitting arithmetic
  (`distributeHours`, round-to-1dp + drift-to-last-day) is **reimplemented in TypeScript** in this same
  file, not delegated to Core's `SmartInputService`. Core's `BuildPlan` tier-1/tier-2 method
  (`SmartInputService.cs:75-127`, which *does* do multi-task splitting) has no route calling it — `/api/smartfill/validate`
  calls `ValidateSmartFillAsync`, a different method, and has no component caller either
  [CITED: COVERAGE-AUDIT.md:192 → `B1` downgrade row]. So porting this means extending the **client-side**
  `distributeHours`-style logic to also split across N tasks (tier 1), plus a multi-select task-checklist
  UI — not a one-line wire change.

### 13. Settings: backup RESTORE (BK-05) + self-restore guard
`grep -rn RestoreAsync src/TimesheetApp.Api` returns **zero hits** except the doc comment explaining why:
*"`IBackupService.RestoreAsync` is NOT exposed in M8.3: it overwrites the live .db in place while the API
holds open connections, which corrupts live readers."* [VERIFIED: `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:43-44`]
Full mechanics of what `RestoreAsync` actually does (self-restore guard, pre-restore safety copy, pool
clearing, WAL sidecar deletion) are in **`B2-restore-evidence.md`** — not re-derived here. The exclusion
is correct engineering (confirmed independently: the API's connection factory uses a pooled, WAL,
`SqliteProfile.Server` profile per `Program.cs:75-76`, exactly the kind of live handle `RestoreAsync`'s own
doc-comment says must be cleared first) — but **no replacement path (CLI, script, runbook) exists anywhere
in the repo today.** Deleting WPF deletes the only working restore path in the product.

**Open question this file does not resolve**: what the replacement takes the *shape* of (offline CLI tool,
documented stop-the-service runbook, or a gated admin route) is undecided — see `B2-restore-evidence.md`
and the coverage audit's own Open Question 2.

### 14. Settings: backup list — "Refresh" + display of existing backups
`ListBackups()` has exactly one caller in production code anywhere in the repo:
```
src\TimesheetApp\ViewModels\SettingsViewModel.cs:287:  foreach (var b in _backup.ListBackups() ?? Array.Empty<BackupInfo>()) Backups.Add(b);
```
[VERIFIED: `grep -rn ListBackups src` → every other hit is `BackupServiceTests.cs` (tests) or the
interface/implementation itself] From the browser you can trigger a backup (`POST /api/ops/backup/run`
exists) but never see what backups exist, when, or how big — compounds item 26 below (backup-now silently
"succeeding" on a null).

### 15. Backup: once-per-day auto-backup trigger (BK-03)
No `IHostedService`/`BackgroundService` is registered — or exists — anywhere in the codebase:
`grep -rn "IHostedService|BackgroundService|AddHostedService" src/TimesheetApp.Api` → **no matches**.
[VERIFIED] `AutoBackupIfDueAsync()`'s only caller is `App.xaml.cs:63` (WPF). This is the highest-priority
of the four `B3` jobs: it is a data-safety net, not an audit-trail nicety — losing it means the automatic
daily backup silently stops the day WPF is deleted, and `AutoBackupEnabled` becomes a dead flag. Full
mechanics (guard mechanism, TOCTOU gap, WAL-safety of the online-backup call) in `B3-scheduled-evidence.md`
job 1.

### 16. ExportHub: 12-month/12-week catch-up backfill
`BackfillAsync()` is registered in DI (`Program.cs:112`, confirmed by the same hosted-service grep above
showing no scheduler exists to call it) but has zero call sites in `TimesheetApp.Api`.
[CITED: COVERAGE-AUDIT.md:78, independently corroborated by my own hosted-service grep above] Only
"Export now" (current + previous month/week) remains reachable, and only on manual click. Third/fourth of
the `B3` jobs (mechanics: `B3-scheduled-evidence.md` job 2). A server with a newly configured export root,
or one that had a downtime gap, never self-heals.

---

## ACCEPT — the loss is fine; reason stated

Every item below has a workaround, a documented deliberate rationale in the code itself, or a genuine
data-safety argument for staying manual. Flagged **[SIGN-OFF]** where the loss is a real product/security
decision a human should explicitly confirm, not just a technical footnote.

### 17. Banner: OneDrive conflict-copy warning (XC-08) — **[SIGN-OFF]**
Detection survives in Core and still aborts a retention run [CITED: COVERAGE-AUDIT.md:55 →
`RetentionService.cs:88-91`], but nothing surfaces it to any web client — no route, no component
(confirmed: `grep -rin "ConflictCopy|conflict" src/TimesheetApp.Api` returns only unrelated
concurrency-conflict (409) infrastructure, nothing about OneDrive/file conflicts).
[VERIFIED: see grep output above] **Why acceptable**: the scenario this guarded against (multiple
per-user OneDrive clients syncing the *same* locally-stored `.db` file, producing "conflicted copy"
duplicates) requires a locally-synced file with multiple writers in the first place. A single
server-hosted DB removes the "multiple writers" half of that precondition structurally. **Caveat this
file does not resolve**: if the server's data directory is *itself* placed under a OneDrive-synced folder
on the host machine, the same failure mode returns — this depends on where the production DB is actually
hosted, not verified here.

### 18. Banner: journal-integrity warning + Dismiss (XC-09)
API registers `TraceJournalWarningSink` — trace log only, no response field, no banner:
[VERIFIED: `src/TimesheetApp.Api/Program.cs:79` — `builder.Services.AddSingleton<IJournalWarningSink, TraceJournalWarningSink>();`]
**Why acceptable**: under the API's connection profile, `journal_mode=WAL` is set explicitly
[VERIFIED: `Program.cs:68,73` comments — *"then: (1) journal_mode=DELETE, so readers block writers…"* is
the *Desktop* profile being described as wrong for this use, and the container asserts
`PRAGMA journal_mode = wal`], and under WAL the `<db>-journal` rollback file this guard watches for is
never created — the guard is structurally inert on the web regardless of whether it is wired to a UI.

### 19. Log Work: entry-target user picker + read-only team-view mode + amber badge — **[SIGN-OFF]**
The API deliberately refuses this capability, in its own words: *"Deliberately does NOT accept a `userId`
query param: unlike WPF's EntryTarget picker, the web API never lets one user view another named user's
individual grid — only their own, or the already-aggregated (and read-only) team view."*
[VERIFIED: `src/TimesheetApp.Api/Endpoints/TimesheetEndpoints.cs:56-62`] This is a genuine, intentional
privacy narrowing (one user can no longer open a named colleague's private per-cell grid), not an
oversight. **This is a product call, not a technical footnote — the coverage audit's own Open Question 5
asks for an explicit owner sign-off, which this file does not supply.**

### 20. Smart Fill: backlog-code search → multi-select task checklist
Web Smart Fill can only target tasks already on the visible week's grid — confirmed: `allTasks` is built
from `this.groups()`, the currently loaded week's data, not a separate search.
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/log-work.component.ts:483-484`]
**Why acceptable**: the workaround is two already-covered steps, not a dead end — add the task to the grid
via the existing add-task dialog [CITED: COVERAGE-AUDIT.md:135, listed COVERED], then it is "on the
grid" and reachable by the on-grid Smart Fill that already exists. One extra step, not a lost capability.

### 21. Smart Fill: "Full 8h" mode
Zero matches for `Full8h` anywhere in `timesheet-web`.
[VERIFIED: `grep -rn Full8h src/timesheet-web/src` → no matches] Core's `FillFull8h`/`BuildPlan`
Full-8h branch stamps every working day to exactly 8h, split evenly across checked tasks with remainder on
the first tasks [VERIFIED: `src/TimesheetApp.Core/Services/SmartInputService.cs:22,51-58,117-122`].
**Why acceptable**: the *same result* is already reachable through the existing single-task DistributeEven
mode by setting Total = 8 × (number of selected days) — `distributeHours(40, 5)` on the web's own rounding
rule yields `[8,8,8,8,8]` exactly, no rounding drift, because 40/5 divides evenly.
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/smart-fill.ts:19-30`, traced by hand] The gap is a
missing shortcut (a checkbox that pre-fills the multiply-by-day-count math), not a missing capability.

### 22. Smart Fill: arbitrary From/To date range
Web is hard-capped to the currently displayed Mon–Fri week — `sfDays` is seeded from `this.days()` (the
displayed week) and every toggle is filtered back through that same week's ordering.
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/log-work.component.ts:186,463,473-478`]
**Why acceptable, with a caveat**: a workaround exists — navigate week-by-week (Prev/This/Next, all
COVERED) and Smart Fill or type each week individually. This is the closest call of the ACCEPT list: for a
large multi-week backfill (e.g., after time off) it turns one operation into N, which is real tedium, not
a hard block. Flagging it as the item most likely to get pushback if a heavy multi-week-backfill user
exists.

### 23. Smart Fill: explicit Preview-then-Confirm gate
Web applies straight from the form: `applySmartFill()` calls `smartFillApply(...)` directly and merges the
response into the grid on success, reverting + toasting on 400.
[VERIFIED: `src/timesheet-web/src/app/pages/log-work/log-work.component.ts:494-521`] `smartFillValidate`
is wired in the service layer (`worklog.service.ts:304-307`) but has no component caller.
[VERIFIED: `grep -rn smartFillValidate src/timesheet-web/src` → hits only the service definition, the
generated `fn/`, and a spec file] **Why acceptable**: this is the same attempt-then-revert-on-error pattern
the rest of Log Work already uses for every other write (single-cell save/clear) — Smart Fill lacking a
preview is consistent with, not a regression from, the rest of this screen's established design. Worth
noting for later: since `/api/smartfill/validate` already exists and is fully wired end to end except for
a UI call site, this would be the cheapest of the five Smart Fill items to add later if a human decides
the value is there.

### 24. Settings: SharePoint destination Verify button (SP-01)
`ISharePointDestinationValidator` appears in `TimesheetApp.Api` **only** as a DI registration line — no
route calls `.Verify(...)`. [VERIFIED: `grep -rn ISharePointDestinationValidator src/TimesheetApp.Api` →
only `Program.cs:106`; `grep -in "Verify|SharePoint" SettingsEndpoints.cs` → no matches at all]
**Why acceptable**: this is a pre-flight convenience, not the only safety net — the export operation
itself still hard-validates the destination at export time and blocks on `Level: Error`
[VERIFIED: `src/TimesheetApp.Core/Services/ExportHubService.cs:62-68`]. Losing the ability to check a
destination *before* relying on it is an operational inconvenience, not a silent-failure risk — a bad
destination still surfaces, just one export cycle later than before. (Note: the `Warning`-level message is
separately discarded by the client today — a real bug, but it lives in the PARTIAL table, not this MISSING
one, and is out of this file's scope.)

### 25. Retention: automatic unattended run at startup — **[SIGN-OFF]**
No scheduler exists to call `EnsureRetentionAsync()` outside a request
(`grep -rn "IHostedService|BackgroundService" src/TimesheetApp.Api` → no matches, same evidence as item 15).
The only caller in the API is the manual admin button:
[VERIFIED: `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:1029` — `try { await
retention.EnsureRetentionAsync(); }` inside the `POST` handler for "run retention now"]. Separately
confirmed: `RetentionService` never reads `RetentionEnabled` at all —
[VERIFIED: `grep -rn RetentionEnabled src/TimesheetApp.Api` → **no matches**] — the flag's only production
readers anywhere in the repo were `App.xaml.cs:91` and the WPF `SettingsViewModel`; both die with WPF, so
the toggle becomes fully inert regardless of this disposition. **Why I'd lean acceptable**: retention is
default-OFF and destructive (bulk deletes under one transaction, per the codebase's own characterization
cited elsewhere in the audit); requiring a human to explicitly click "run retention now" is arguably safer
than a silent unattended purge at every server restart, not merely a downgrade. **This is a genuine
product trade-off, flagged for explicit confirmation, not decided by this file** — the audit's own Open
Question 4 asks the same thing.

### 26. Identity: auto-provision an unmapped user on first run — **[SIGN-OFF]**
`AuthSetup.cs` fails closed on a NULL/empty password hash — *"A NULL `password_hash` means 'has never had
a password set' => CANNOT LOG IN. Never treat…"* followed by `return Results.Unauthorized();`.
[VERIFIED: `src/TimesheetApp.Api/Auth/AuthSetup.cs:51,167,171,173,177,181`] There is no code path that
creates a `Users` row on an unrecognized login attempt. **Why I'd lean acceptable**: WPF's auto-provision
granted access with *no credential at all* — the intended change here (real accounts, real passwords) is
the entire point of adding auth to the web app; keeping the old behavior would defeat it. **Coupled,
unresolved dependency**: the coverage audit's Open Question 1 — whether existing non-admin employees
already have web passwords set — is **not answered by this file** (the production DB was not opened, per
this task's constraints). If the answer is no, accepting this loss at the same moment WPF is deleted locks
every such person out with no self-recovery path (`AuthEndpoints.cs:91-93` blocks it). That is an
operational gate on the *timing* of this ACCEPT, not a reason to change the disposition itself.

### 27. Identity: first-run auto-join to the active team (TM-09)
`AddMemberAsync`'s only production caller anywhere in the repo is WPF:
[VERIFIED: `grep -rn AddMemberAsync src` → `src\TimesheetApp\ViewModels\MainViewModel.cs:234` is the only
non-test, non-repository-definition hit] **Why acceptable**: this behavior only had meaning paired with
item 26 (auto-provisioning) — once an account requires an admin to create it with a password, adding that
same admin flow to also assign a team is a small, already-manual step, not a new one. Record it as an
admin-onboarding checklist item, not a code gap.

---

## OBSOLETE — WPF/Windows-only, no web meaning

### 28. Window chrome (1180×760, min 980×620, CenterScreen)
[CITED: COVERAGE-AUDIT.md:57 → `MainWindow.xaml:6,8`] A browser tab has no equivalent concept — the host
OS/browser owns window sizing and placement; there is nothing for a web app to "port." (Distinct from
responsive min-width layout guards, which are a real web concern but not what this row describes — no
per-page `min-width` container guard exists either, confirmed by scanning every `min-width` in
`timesheet-web/src` [VERIFIED: `grep -rn "min-width" src/timesheet-web/src`], but that is a design
question for the new build, not a lost WPF behavior, so it stays out of scope here.)

---

## Cross-references

- Backup Restore mechanics (item 13): `.planning/m10-blockers/B2-restore-evidence.md`
- All four scheduled/background jobs (items 9, 10, 15, 16): `.planning/m10-blockers/B3-scheduled-evidence.md`
- Source table this file triages: `.planning/M10-COVERAGE-AUDIT.md` lines 47-82 (🔴 MISSING), and the
  per-section detail files in `.planning/m10-audit/` (A1, A2, A3, A4, A5, A9, B1, B3, B7, B8, B9, B11 were
  the sections contributing MISSING rows).

No code was edited, no build/test was run, and no SQLite file was opened at any point in producing this file.
