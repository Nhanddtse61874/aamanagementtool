# B1 — Smart Input / Smart Fill — Adversarial Refutation

Scope reminder: M10 deletes `src/TimesheetApp/` (ViewModels, Views, App.xaml.cs, MainWindow).
`src/TimesheetApp.Core/` survives entirely. Verdicts below answer "can deleting the WPF shell lose this?"

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| `ValidateSmartFillAsync` business rules (≥1 cell>0, no weekend, no holiday, 8h day-cap with checked-task overwrite-not-accumulate) | COVERED | No | **CORE-SURVIVES** | Rules live entirely in `src/TimesheetApp.Core/Services/TimeLogService.cs:161-192` — Core, not deleted. Exercised by `src/TimesheetApp.Api/Endpoints/TimesheetEndpoints.cs:156` and re-run inside apply at `TimeLogService.cs:196`. **[VERIFIED]** Caveat below on the auditor's cited route. |
| Apply pipeline: re-validate → `DbBackupHelper.BackupAsync()` before bulk write (XC-10) → `UpsertBatchAsync` one transaction (SI-05) → **SQLite journal-gone check (XC-09)** | COVERED | **YES** | **PARTIAL** | First three elements genuinely survive + run on web. Fourth is **inert and unsurfaced** on the web. See detail below. |
| Reload + broadcast after apply | COVERED | No | **COVERED** | Traced end to end. See detail below. |

---

## Claim 1 — Validation rules → CORE-SURVIVES **[VERIFIED]**

WPF side: `src/TimesheetApp.Core/Services/TimeLogService.cs:161-192`. Pure Core, no WPF dependency.
Deleting `src/TimesheetApp/` cannot reach it. The auditor's verdict is right; the correct label is
CORE-SURVIVES rather than COVERED-by-web.

Two things the auditor's evidence got wrong or omitted — neither changes the verdict, both worth recording:

1. **The cited web route is dead from the UI.** `POST /api/smartfill/validate`
   (`TimesheetEndpoints.cs:146-156`) is wrapped by `src/timesheet-web/src/app/services/worklog.service.ts:304`
   but **no component calls `smartFillValidate`** — the only other reference is a spec
   (`worklog.service.spec.ts:167`). "A route that exists but no Angular component calls is NOT coverage."
   The rules still execute because `ApplySmartFillAsync` re-validates (`TimeLogService.cs:196`), so nothing
   is lost — but the auditor's stated evidence is not what makes the claim true.

2. **The pre-apply preview gate dies with no web equivalent.** WPF validated *before* enabling Apply and
   showed the violation inline: `src/TimesheetApp/ViewModels/SmartInputPanelVm.cs:146-148` sets
   `PreviewError` and gates `CanApply`. The web only discovers a violation as a 400 toast *after* the user
   presses apply (`log-work.component.ts:512-514`). Rule enforcement: preserved. Rule *feedback timing*:
   narrowed. Not feature loss in the deletion sense, but a UX regression to note.

Also note the "checked-task overwrite-not-accumulate" multi-task semantics at `TimeLogService.cs:174-190`
is only ever exercised single-task from the web: `log-work.component.ts:495` reads one `sfTaskId()`, and
`smart-fill.ts:43-55 buildSmartFillRequest` emits exactly `[{ taskId, cells }]`. The rule survives in Core
but has no web caller that can reach its multi-task branch.

## Claim 2 — Apply pipeline → **PARTIAL** (refuted) **[VERIFIED]**

What genuinely holds:
- `ApplySmartFillAsync` is Core (`TimeLogService.cs:194-213`) and is called by the API
  (`TimesheetEndpoints.cs:179`). **CORE-SURVIVES.**
- Backup-before-write (`TimeLogService.cs:199`) is real on the web: the API registers the concrete
  helper — `src/TimesheetApp.Api/Program.cs:103` `AddSingleton<IDbBackupHelper, DbBackupHelper>()`.
- Atomic batch `UpsertBatchAsync` (`TimeLogService.cs:205`) — same Core call, unchanged.

**What does not hold — the journal-gone check the claim itself lists:**

1. **It is structurally inert under the API's SQLite profile.**
   `SqliteMaintenance.IsJournalGone` is literally `!File.Exists(dbPath + "-journal")`
   (`src/TimesheetApp.Core/Data/SqliteMaintenance.cs:31-32`). The API constructs its connection factory
   with `SqliteProfile.Server` (`Program.cs:76`), and the Server branch issues
   `PRAGMA journal_mode=WAL` (`src/TimesheetApp.Core/Data/SqliteConnectionFactory.cs:58-65`). In WAL mode
   SQLite never creates a `<db>-journal` rollback journal — it uses `-wal`/`-shm`. So `IsJournalGone`
   returns `true` unconditionally on the web and the XC-09 guard at `TimeLogService.cs:207` can never fire.
   The WPF side used `SqliteProfile.Desktop`, whose comment at `SqliteConnectionFactory.cs:15-19` states
   `journal_mode=DELETE (NOT WAL)` — i.e. the guard was designed for, and only works under, the profile
   the surviving app does not use.

2. **Even if it fired, nothing surfaces it to a user.** API registers
   `AddSingleton<IJournalWarningSink, TraceJournalWarningSink>()` (`Program.cs:79`) — trace output only,
   no API response field, never reaches the browser. WPF registered the UI-facing sink
   (`src/TimesheetApp/App.xaml.cs:144-147`) and wired it to a shell banner at
   `App.xaml.cs:115-117` (`journalSink.WarningRaised += ... mainVm.JournalWarning = ...`).
   `UiJournalWarningSink` the *class* lives in Core (`Core/Services/UiJournalWarningSink.cs`) and survives,
   but its **registration and its only consumer both live in `src/TimesheetApp/` and die**, with no
   replacement on the web.

Net: "backup before write" and "one transaction" are safe. "SQLite journal-gone check" as a *user-observable
integrity warning* is lost by this deletion and was already unreachable on the web path. PARTIAL, not COVERED.

## Claim 3 — Reload + broadcast after apply → **COVERED** **[VERIFIED]**

WPF side is larger than the inventory says. `SmartInputPanelVm.cs:79` declares `Applied`, `:164` raises it,
and the handler at `src/TimesheetApp/ViewModels/TimesheetViewModel.cs:64-68` does **two** things:
`await ReloadAsync()` **and** `_messenger.Send(new DataChangedMessage(DataKind.Logs))` for
Reports/Task List refresh. All of this dies.

Web replacement traced end to end:
- Broadcast: `TimesheetEndpoints.cs:182-183` — `foreach (var teamId in teamIds) await notifier.DataChangedAsync(DataKind.Logs, teamId, ctx.ConnectionId)`. (Auditor cited 182-183; the call is on 183. Fine.)
  Signature `src/TimesheetApp.Api/Infrastructure/IChangeNotifier.cs:19` confirms the third arg is
  `exceptConnectionId` — the caller is deliberately excluded from its own echo.
- Caller's own refresh: `log-work.component.ts:508` `this.cells.update(c => mergeSmartFill(c, rows))`,
  implemented at `grid-state.ts:168-181`. This is the compensating path for the self-exclusion, and the
  endpoint comment at `TimesheetEndpoints.cs:185-196` documents exactly why the flat re-fetch exists.
- Other clients: `log-work.component.ts:228`
  `this.realtime.dataChanged.pipe(takeUntilDestroyed()).subscribe(() => this.refresh.next())`.
  `reports.component` subscribes and filters on `DataKind.Logs` too (`reports.component.spec.ts:440`).

**Caveat the auditor did not state (does not overturn the verdict):** the web never does a *full* reload
after its own apply — it patches only the returned `(taskId, workDate)` pairs. That is sufficient *only
because* web Smart Fill is confined to rows already on the grid (`log-work.component.ts:495` picks from
`allTasks()`, which is derived from `groups()`) and to the displayed week (`sfDays()` from `days()`).
WPF's `ReloadAsync()` was load-bearing precisely because its fill could target tasks found by backlog
search and an arbitrary From/To range outside the visible week
(`SmartInputPanelVm.cs:99-114`, `:66-67`). The reload mechanism is covered; the **fill scope** is not —
that gap belongs to the multi-task / backlog-search / mode inventory items, not to this claim.
