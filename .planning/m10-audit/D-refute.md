# Section D — Adversarial Refutation

**Section:** D — WPF/Windows dependencies that do not port directly
**Method:** Every web citation opened and traced end-to-end (route → DTO → service → component → template).
Every WPF citation re-opened against the source, not the inventory.
**Read-only.** No build, no run, no DB touched.

**Result: 19 claims examined — 2 REFUTED (→ PARTIAL), 2 re-classified CORE-SURVIVES, 15 hold as COVERED.**

---

## Verdict table

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| OpenFolderDialog x4 + OpenFileDialog | COVERED | No | COVERED | WPF `SettingsTab.xaml.cs:58-112` (5 handlers, all die). Web decision is real and enforced, not merely commented: `SettingsEndpoints.cs:46` "**Never call any `IAppConfig.Set*` from an endpoint**" — there is no route to set a path. UI comments at `settings.component.html:36-39,346-349`. Caveat below. |
| SaveFileDialog (Excel export, EXP-01) | COVERED | No | COVERED [VERIFIED] | Full trace: `TimesheetEndpoints.cs:305-318` (`MapGet /api/export/excel` → `Results.File`) → `worklog.service.ts:694-699` (`responseType:'blob'`) + `:719-725` `exportParams` passes `userId`/`project`/`teamIds` → `reports.component.ts:338-368` → button `reports.component.html:56`. Filename logic ported at `report-model.ts:125-130` incl. invalid-char strip. WPF originals `ReportsViewModel.cs:179-195`. |
| Windows identity → real auth | COVERED | No | COVERED [VERIFIED] | WPF `MainViewModel.cs:50` `() => Environment.UserName` dies. Web: `AuthSetup.cs:41-70` (PBKDF2 hasher + fail-closed null-hash guard), `:92-128` cookie auth + 401-not-302 fix, `:130-147` AdminPolicy + `FallbackPolicy` secure-by-default, `:157-211` login (unknown/inactive/null-hash all → 401). Consumed: `Program.cs:131-137`, guards applied at `app.routes.ts:18,53,59`, client `auth.service.ts:44`. Strictly stronger. |
| SelectUserDialog | COVERED | No (conclusion) — **citation is false** | CORE-SURVIVES | **The cited location does not exist.** `App.xaml.cs` is **218 lines** — there is no 222-229. No `SelectUserDialog.*` file exists anywhere under `src/`. The only references are two Core comments saying it is never opened (`CurrentUserService.cs:7`, `ICurrentUserService.cs:6`). The real capability lives in Core: `ReadModels.cs:86` `CurrentUserOutcome.NeedsSelection` + `CurrentUserService.cs:32`, consumed server-side at `ClientContextFilter.cs:49`. Deletion loses nothing. |
| TimesheetViewModel.SaveCommand | COVERED | No | COVERED | Confirmed unreachable: grep for `SaveCommand` across all WPF `.xaml`/`.cs` returns only `TimesheetViewModel.cs:395` (`NotifyCanExecuteChanged`) — no XAML binds it (`DailyInputTab.xaml:91` / `RequestsTab.xaml:145` bind *different* VMs). Body at `:437-444` is only a loop over `SaveCellAsync`. The business rule it gated (`CanSave`, `:446`) lives in Core: `TimeLogService.cs:13` `private const decimal DayCap = 8m`. Web per-cell write `log-work.component.ts:305-321` (+ `clearCell` `:333-345`). |
| TagPicker.TagsCommitted | COVERED | No — **stronger than claimed** | COVERED | Auditor says "only used in Backlog editor". It is used **nowhere**: `TagsCommitted` has zero subscribers in all of `src/` — only its own declaration/raise at `TagPicker.xaml.cs:65,72`. Web superset confirmed: `tag-picker.component.ts` (replace-all `selectionChange`, parent owns the checked write), consumed at `backlog-editor.component.html:145` and in-grid `task-list.component.html:205,270` → `task-list.component.ts:595-623`. |
| **WeakReferenceMessenger → SignalR** | COVERED | **YES** | **PARTIAL** | Transport is ported (`SignalRChangeNotifier.cs:33-44`, `DataChangedMessage.cs` DataKind is **Core**, so the contract survives). **But the subscriber set is narrower.** WPF registers 5 recipients: `DailyReportViewModel.cs:45`, `MainViewModel.cs:90`, `ReportsViewModel.cs:68`, `TimesheetViewModel.cs:79`, **`TaskListViewModel.cs:79-87`**. The Angular Task List subscribes to nothing — `task-list.component.ts:68-69` injects only `WorklogService` + `ToastService`, and `RealtimeService` is absent from its imports. WPF live-reloads that grid on `Backlogs/Tasks/Logs/Tags/Holidays/PcaContacts`; the web Task List does not refresh until manually reloaded. |
| ResourceDictionary theming → CSS vars | COVERED | No | COVERED [VERIFIED] | WPF `ThemeService.cs:15-37` swaps `Palette.Light/Dark.xaml`; dark-only. Web is a **superset**: `styles.scss:8-27` `:root` tokens + `:29-42` `.dark` override, `theme.service.ts:18-31` toggles the class **and** sets `--accent` from a 4-colour palette, persisted to localStorage. Citation nit: WPF theme file is `Views/Theme/Theme.xaml`, not `Services/`. |
| **Modal dialog blocking (`ShowDialog`)** | COVERED [ASSUMED] | **YES** | **PARTIAL** | The architectural point is trivially true, but **the auditor's own cited call site is a feature with no web presence**. `SettingsTab.xaml.cs:115-144` is the backup **restore** confirm + restart flow, and restore is deliberately unexposed: `SettingsEndpoints.cs:43` — "`IBackupService.RestoreAsync` is **NOT exposed** in M8.3". Grep for `restore` across `timesheet-web/src/app` finds no backup-restore UI. Capability CORE-SURVIVES (`BackupService.cs:97`) but is unreachable by any user. Other confirms **are** ported async: `ConfirmDialogComponent` used at `settings.component.html:429` + `log-work.component.html:225`; retention is even gated harder than WPF (preview *then* confirm, `settings.component.ts:570-600`). |
| Drag & drop + trash zone | COVERED [presence only] | No | COVERED [VERIFIED] | Payload semantics checked, not just presence. WPF: `TimesheetTab.xaml.cs:12,65` (`"timesheetTask"`) + trash `TimesheetTab.xaml:258`; `DailyInputTab.xaml.cs:12,67` (`"standupEntry"`) + trash `DailyInputTab.xaml:128`. Web has **both** trash zones: `log-work.component.html:179-180` (`id="trash"`, `onTrash($event)`) and `daily-report.component.html:139-142`. WPF's same-group-only rule (`TimesheetViewModel.cs:410`) is enforced structurally on the web — each group is its own `cdkDropList` connected **only** to trash (`log-work.component.html:91-92`), so a cross-group drop cannot occur. |
| Gantt index-math | COVERED | No | CORE-SURVIVES | **The math is not in the WPF project at all.** `TimesheetApp.Core/Services/GanttBuilder.cs:5-14` — "MOVED VERBATIM out of TaskListViewModel (WPF) — behaviour unchanged", `:22-25` `BuildGantt`. Called on the server path the web uses: `TaskListService.cs:128`. Deletion cannot lose it. Rendering side confirmed at `task-list.component.html:83-105` + `task-list.component.ts:707-717` (pure `index * COL`). |
| BindingProxy : Freezable | COVERED | No | COVERED | `Views/Controls/BindingProxy.cs:8-21` is pure `Freezable` plumbing. Its one user-facing purpose — per its own doc, "hide the TEAM column when only one team is selected" — **is** ported: WPF `TeamFilterViewModel.cs:74` `ShowTeamColumn => CheckedTeamIds.Count > 1` → web `task-list.component.ts:127-129` `teamMode = (teamIds()?.length ?? 0) > 1`, comment explicitly cites `ShowTeamColumn`. |
| TeamFilterViewModel lazy-seed | COVERED [ASSUMED] | No | COVERED [VERIFIED] | Upgraded from ASSUMED — init order traced. WPF hack at `TeamFilterViewModel.cs:57-70` (lazy seed in the `CheckedTeamIds` getter to dodge the Measure-pass re-entry). Angular equivalent is a from-scratch design with its own documented init-order rule: `team-filter.component.ts:103-116` seeds in `ngOnInit`, **not** the constructor, with a regression test named for it; failure path deliberately leaves the filter unloaded (`:162-175`) so a network error never reads as "user unchecked everything". |
| CurrentTeamService `ActiveTeamChanged` suppression | COVERED | No | COVERED | `ApiCurrentTeamService.cs:22-26` — never raising it "IS the contract" (a per-user switch must not broadcast to the team group); hand-written empty accessors `:45-50`. WPF `CurrentTeamService.cs:27,85-95` `_suppressReentry` has no server analogue (no UI thread, no Measure pass). *Adjacent gap, outside this row:* no web caller for `setActiveTeam` (`team-filter.component.ts:142` — "the sidebar's team `<select>` is still hard-coded mockup markup"). |
| DatePicker/ComboBox write-back bug | COVERED | No | COVERED [VERIFIED] | Traced the **whole** flow, not just the input elements. WPF `TaskListTab.xaml.cs:89-116`: only a genuine focused pick opens `DeadlineNoteDialog`; OK → commit with note; Cancel → revert guarded by `_suppressDeadlineChange`. Web ports all three: `task-list.component.ts:456-467` (park, don't write), `:469-474` `cancelDeadline` "**CANCEL REVERTS THE PICKER**", `:481-497` `confirmDeadline` sends the note. Status/date selects `task-list.component.html:147-154,231-237,249-250`. |
| TagSelectDialog workaround | COVERED | No | COVERED | Citation nit: the file is `Views/Controls/TagSelectDialog.xaml(.cs)`, not `Views/Dialogs/`. Invoked at `TaskListTab.xaml.cs:136`, rationale at `:118-121`. Web needs no workaround — same `app-tag-picker` used in-grid at `task-list.component.html:205,270`, writes at `task-list.component.ts:595-623` (checked writes, fresh GET per chain so it cannot 409 against its own tag write). |
| 9 re-entrancy guard flags | COVERED [ASSUMED, flagged] | No | COVERED [VERIFIED] | **Spot-checked exactly as requested.** The two flags carrying real business semantics are both explicitly ported: (1) `_suppressProgressCommit` (`TaskListViewModel.cs:582,599,607-609`) guards *Escape restores without persisting* + *invalid input never commits* — web ports both, citing the WPF line: `task-list.component.html:158-168` (`keydown.escape`/`keydown.enter`/`blur`), `task-list.component.ts:391-410`. (2) `_suppressDeadlineChange` — see the DatePicker row. The remaining flags are init/render-only: `_suppressTotals` (`TimesheetViewModel.cs:295,332`, guards a group rebuild), `_suppressCommit` (seeding), `_suppressChange` (bulk re-seed), `_autoLoad` (`ReportsViewModel.cs:100`), `_loadingTheme` (`SettingsViewModel.cs:147`). Two notes: the list is really **10** — `MainViewModel.cs:34` `_suppressActiveTeamSet` is unlisted; and `_suppressSelfReload` is moot on the web only because the Task List has no realtime subscription at all (see the SignalR row). |
| Scrollbar forced Visible + 25px margin | COVERED | No | COVERED | `TimesheetTab.xaml:269-270` — `VerticalScrollBarVisibility="Visible"` with the comment about header/footer reserving "a matching 25px right gutter". Pure XAML layout, no VM logic, no business rule. Dies with the app; CSS grid has no such failure mode. |
| N+1 `GetTagIdsAsync` in a loop | COVERED | No | COVERED [VERIFIED] | WPF loop confirmed at `TaskListViewModel.cs:267-271` (`await _tasks.GetTagIdsAsync(t.Id)` per task). Fixed on the path the web actually uses: `TaskListService.cs:9-14` — "NO N+1 … the desktop VM issues one `GetTagIdsAsync` per TASK … deliberately not replicated here"; `BacklogEndpoints.cs:42-43` "ONE `IN` query for every backlog on the page. Do not loop this into an N+1." Web fetches task tags **lazily on picker open only** (`task-list.component.ts:583-589`), so the loop never recurs. |
| Mixed Vietnamese/English strings | COVERED | No | COVERED [VERIFIED] | Reproduced independently with an explicit diacritic character class. WPF: **4** files hit — `TimesheetTab.xaml`, `TaskListTab.xaml`, `RequestsTab.xaml`, `Controls/TagSelectDialog.xaml`. Angular `timesheet-web/src/app`: **0** files. Formal i18n scaffolding for a future language remains out of scope, as noted. |

---

## The two refutations, stated plainly

**1. SignalR is the right mechanism, but one screen never got wired to it.**
Deleting `src/TimesheetApp/` removes `TaskListViewModel.cs:79-87`, the handler that live-reloads the Task
List whenever backlogs, tasks, logs, tags, holidays or PCA contacts change anywhere. The Angular Task List
has no `RealtimeService` injection at all. The transport exists and four other screens use it, so this is a
small wiring gap rather than a missing subsystem — but it is *behavior present in WPF and absent on the web*,
and after M10 there is no running WPF app left to notice it against. Claiming this row COVERED would have
buried it.

**2. The "no blocking dialogs" row cited a flow that does not exist on the web.**
`SettingsTab.xaml.cs:115-144` — confirm-restore, restore, offer restart — was offered as evidence that
blocking dialogs are covered by Angular's async patterns. Restore is not async on the web; it is *absent*,
by an explicit and defensible engineering decision (`SettingsEndpoints.cs:43`: restoring overwrites the live
`.db` under open connections). `IBackupService.RestoreAsync` survives in Core, so nothing is unrecoverable —
but the only UI that ever invoked it dies here. This belongs on the M10 known-gaps list, not in the COVERED
column. The general architectural claim (a browser cannot block a UI thread) is of course true, and every
*other* confirmation flow checked is genuinely ported.

## Citation defects worth fixing in the inventory (verdicts unaffected)

- **SelectUserDialog:** cited `App.xaml.cs:222-229`. That file has 218 lines, and no such dialog exists in
  the repo. The claim's *conclusion* is right for a stronger reason than the auditor gave.
- **TagsCommitted:** cited as "only used in Backlog editor". It has no subscriber anywhere.
- **TagSelectDialog:** lives in `Views/Controls/`, not `Views/Dialogs/`.
- **Theme.xaml:** lives in `Views/Theme/`, not `Services/`.
- **Gantt / N+1:** both were described as WPF-resident; the load-bearing code (`GanttBuilder`,
  `TaskListService`) is already in Core and cannot be lost by this deletion.
- **Guard flags:** there are 10, not 9 (`MainViewModel.cs:34` `_suppressActiveTeamSet` is unlisted).
- **Folder dialogs:** the settings UI points operators at "`appsettings.json` on the host". The API project
  has **no** `appsettings.json` — `Program.cs:53` says so outright, and the paths resolve through
  `JsonAppConfig` defaults (`%APPDATA%\TimesheetApp\appsettings.json`) unless `TimesheetApp:ConfigPath` is
  set. The capability is real; the pointer is imprecise, and an operator following it looks in the wrong
  directory.
