# B3 — Multi-team + R6 anti-leak rule — ADVERSARIAL REFUTATION

Auditor claimed 7 behaviors COVERED. Result: **6 survive, 1 refuted to PARTIAL.**

All verdicts below are `[VERIFIED]` — both the WPF side and the web side were opened and read.

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| `AvailableTeams = UserTeams(user) ∩ Teams(is_active=1)` | COVERED | No | **COVERED** | WPF `Services/CurrentTeamService.cs:109-112` DIES. Reproduced verbatim in `TimesheetApp.Api/Infrastructure/ApiCurrentTeamService.cs:61-64` (same `GetTeamIdsForUserAsync().ToHashSet()` ∩ `GetActiveAsync()`). Wired, not orphaned: `Program.cs:139` registers it scoped, `ClientContextFilter.cs:60` calls `InitializeAsync(user.Id)`, and `Program.cs:250` applies that filter to the whole API group. Client side recomputes the same intersection at `team-filter.component.ts:188-189`. |
| ActiveTeamId resolution (persisted-if-valid : first-available : 0) | COVERED | No | **COVERED** | WPF `CurrentTeamService.cs:60-63` DIES. Identical three-way fallback at `ApiCurrentTeamService.cs:66-69`, reached once per request via `ClientContextFilter.cs:60`. Zero-team edge (`→ 0`) preserved. |
| Live re-resolve on `DataKind.Teams` + `_suppressReentry` guard | COVERED | **YES** | **PARTIAL** | See breakdown below. |
| Screen scope: Task List = TeamFilter (multi) | COVERED | No | **COVERED** | WPF `TaskListViewModel.cs:66-67,182-183,208`. Web: `task-list.component.html:29` mounts `<app-team-filter>`; `task-list.component.ts:265-267` `onTeams()` → `teamIds` signal → `load()` returns **before** the HTTP call when `ids.length === 0` (`:211-214`), and `noTeams()` (`:134`) renders the empty state locally at `task-list.component.html:56-60`. The three-valued `undefined`/`[]`/`[1,2]` contract is honoured. WPF's `ShowTeamColumn`→`GroupByTeam` adaptive banding (`:208-217`) also ported: `teamMode()` (`:129`) + `bands` + `teamNames` (`:106,130,192-193`). Export path scoped too (`:302`). |
| Screen scope: Daily Board = TeamFilter (multi) | COVERED | No | **COVERED** | WPF `DailyReportViewModel.cs:38-42,82-84` (`GetTeamStandupAsync(date, CheckedTeamIds)`). Web: `daily-report.component.html:154` mounts the filter; `daily-report.component.ts:244-247` `onTeamSelection()` → `teamIds`; `:213-217` skips `getStandupBoard()` and sets `board=[]` when `boardBlocked()` (`:149`). Input tab stays active-team-only in both (WPF `:37`; web `pickableBacklogs(backlogs, activeTeamId)` `:222`). |
| Screen scope: Reports / Export = TeamFilter (multi) | COVERED | No | **COVERED** | The highest-risk claim, and it holds. WPF uses **one** TeamFilter for both the report reads (`ReportsViewModel.cs:168-172`) and the Excel export (`:185`, `ExportFilter(..., CheckedTeamIds)`). Web does the same through a single resolver: `reports.component.ts:176-184` `filter()` carries `teamIds`, and `exportExcel()` (`:338-345`) passes `this.filter()` — **and** is gated on `teamEmpty()` (`:339`) plus disabled in markup (`reports.component.html:52`). The export does *not* silently widen to all teams. |
| DEFAULT backlog 1-per-team (TM-04) on team create | COVERED | No | **COVERED** | Full round trip. Core service survives (`TimesheetApp.Core/Services/DefaultTaskSyncService.cs`). WPF `SettingsViewModel.cs:566-569` DIES; `SettingsEndpoints.cs:248-249` calls the same `EnsureDefaultBacklogIdAsync(id)` + `SyncAsync()` pair, admin-gated (`:254`). Real UI caller exists: `settings.component.ts:424-432` `addTeam()` → `api.createTeam(name)`. Rename/activate/membership siblings also present (`:440-472`). |

---

## The refutation — claim 3 is two behaviors, and only one crossed over

The auditor's note ("different mechanism, same outcome: no self-triggered reload loop") is *logically* correct but **vacuous**, because the path it reasons about is unreachable in the web app.

**Half that IS covered — the broadcast re-resolve.**
`CurrentTeamService.cs:38-42` registers on `DataKind.Teams` and `:97` re-resolves `AvailableTeams`. The web genuinely does this: `team-filter.component.ts:132-134` subscribes to `realtime.dataChanged`, filters `DataKind.Teams`, and calls `reload()` → `load()` (`:174-202`), which re-fetches and re-intersects. A team an admin deactivates does stop being checkable. ✔

**Half that is NOT covered — the self-switch reset.**
`_suppressReentry` only exists because `SetActiveTeamAsync` (`CurrentTeamService.cs:85-87`) broadcasts to *other* subscribers. In WPF that broadcast is load-bearing: `TeamFilterViewModel.cs:96-102` `OnActiveTeamChanged` → `Reload()` + `SelectionChanged` resets all four screens' filters to `{new active team}` (resolved decision F-Q3). The API deliberately never broadcasts (`SettingsEndpoints.cs:127-139`), and the documented compensation is "whoever wires the switcher calls `reload()`".

**Nobody wired the switcher.** The cited web evidence `SettingsEndpoints.cs:97-137` is a route with **zero production callers**:

- `worklog.service.ts:1239` `setActiveTeam()` is invoked only by a route-coverage spec — `worklog.service.spec.ts:736`. No component calls it.
- The sidebar team picker is **hard-coded mockup markup**: `sidebar.component.html:45-52` is a bare `<select>` with two literal `<option>` labels ("Architect Improvement" / "Plus Team") — no `@for`, no `[value]`, no `(change)`, no binding of any kind.
- `TeamFilterComponent.reload()` (`team-filter.component.ts:146`) has no production caller either. The component's own doc admits it at `:141-145`: *"the web app has no team switcher yet: `WorklogService.setActiveTeam()` exists and has no caller, and the sidebar's team `<select>` is still hard-coded mockup markup."*

So WPF (`MainWindow.xaml:60` + `MainViewModel.cs:127-132`) lets a user change their active team; the web does not. Deleting `src/TimesheetApp/` removes the only working active-team switcher in the product.

**Secondary divergence, same claim.** WPF *persists* the fallback when the active team goes invalid — `CurrentTeamService.cs:101-102` writes `SetActiveTeamIdAsync(userId, newId)`. `ApiCurrentTeamService.InitializeAsync:66-69` computes the same fallback but **never writes it back**. Per-request resolution masks this, but the stored `Users.active_team_id` stays stale: if the deactivated team is later reactivated, the API snaps the user back to it while WPF would have stayed on the fallback.

**Recommendation:** do not treat claim 3 as delete-safe. The `DataKind.Teams` re-resolve can go; the active-team switcher must be built on the web before `src/TimesheetApp/` is removed, or the behavior is lost with no oracle left to compare against.
