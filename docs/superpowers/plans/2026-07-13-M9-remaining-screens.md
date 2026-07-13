# M9 — The four remaining screens · Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.

**Goal:** Wire Task List, Daily Report, Reports, Users and Settings against the real API — and repay M8.6's TagPicker debt on the Backlog editor. **No screen in the app is fake when this lands.**

**Architecture:** **PHASE 1 (sequential, one owner)** writes every shared file — because five of them are needed by every screen and owned by none. **PHASE 2 (five agents, true parallel)** owns five disjoint page directories.

**Spec:** `docs/superpowers/specs/2026-07-13-m9-remaining-screens-design.md`
**Recon:** `.planning/research/M8.7-M8.10-recon.md` · `.planning/research/M8.8-daily-report-recon.md`
**Branch:** `feature/m9-remaining-screens-2026-07-13`, base `main` @ `28fd406`
**Baseline:** **1142 green** — 658 Core/WPF + 210 API + 274 Angular. 0 warnings.

---

## 🔴 THE RULES. Restated verbatim in every brief, because an agent sees its brief and nothing else.

### R1 — The API must NEVER run against the real database
`Program.cs` defaults `DbPath` to `C:\Users\Admin\Documents\TimesheetApp\timesheet.db` — the user's **live company data**. Startup runs migrations + bootstrap against whatever it points at.
**Pin ALL THREE seams:** `TimesheetApp:DbPath` · `:ConfigPath` · `:KeyRingPath`.
🔴 **`DbPath` alone is NOT enough** — `Program.cs:34` uses `||`, so `DbPath` without `ConfigPath` **silently falls back to the fully-default production config**.
**Prove it:** grep the startup log for the substring **`no users yet, nothing to bootstrap`**. **Grep the SUBSTRING** — the line reads *"**Admin bootstrap:** …"* (space, lowercase `b`); `AdminBootstrap` is only the logger category. **No match → kill it and report BLOCKED.**
**Do NOT** try to prove safety via the absence of a `-wal` sidecar. A live WAL database always has one.

### R2 — The .NET gate lies in BOTH directions
- **False GREEN:** a running API holds `TimesheetApp.Core.dll` open → `dotnet test` fails to *build* the API project **and still exits 0 printing `Passed!`** for whichever assembly did build. **Nothing may listen on 5080. BOTH `Passed!` lines must appear.** An absent `TimesheetApp.ApiTests.dll` line **is a failed gate**.
- **False RED:** a pre-existing race in the ApiTests host-startup path. A lone `SqliteException: no such table: Backlogs` is **not a regression — re-run.** **Run the baseline BEFORE touching the tree.**

### R3 — `.Produces<T>()` is what makes a route exist to the client
ApiExplorer **cannot infer a schema from `Results.Ok(x)` returning `IResult`.** An unannotated route is described as an empty `200`, carries the default assembly tag `TimesheetApp.Api`, and is **omitted entirely**.
🔴 **`void` is NOT a red flag.** Seven generated functions are legitimately `void` — their routes declare 204 via the **non-generic** `.Produces(StatusCodes.Status204NoContent)` overload. **The invariant is: `void` only where the route has no body.**
🔴 **An annotated route that is ADMIN-GATED is a different lie: correctly typed, and it 403s.** Check `.RequireAuthorization(AuthSetup.AdminPolicy)` before you tag anything, and make sure a screen a non-admin can reach never calls an admin route.

### R4 — A green build proves nothing about wiring
M8.5: an agent mutated a template to the broken form and **six of seven tests stayed GREEN against a completely dead feature.**
M8.6/T7: `npm run build` **exits 0 with `{{ noSuchSymbol() }}` in a component nobody imports yet** — AOT never type-checks it.
**Where behaviour depends on wiring, assert the wiring.** And **mutation-check the load-bearing tests: break the code, watch the test go red.** If it stays green, the test is decorative.

### R5 — Every `async` handler bound to a template output needs a `catch` that never re-throws
Anything that escapes is an **unhandled promise rejection**: console only, **nowhere the user can see.** *(Shipped three times in M8.5, in a plan that diagnosed the hazard.)*

### R6 — Never `!`, never `0`, on a `rowVersion`
`requireRowVersion(rowVersion: number | undefined): number` — exported from `src/app/services/worklog.service.ts:343`. It **throws** rather than defaulting.

### R7 — 🔴 THE WHOLE-RECORD TRAP. Third milestone running.
A checked `PUT` **replaces the whole record.** If the form shows only *some* fields, the request **must be built from the loaded DTO and overridden only where the form actually owns a value.** Build it from the form and every hidden field is written as `NULL`.
**It has already: nulled a `team_id` (M8.3) making a backlog invisible forever; nulled six Backlog fields (M8.6); and it is waiting in Task List's `CommitBacklogEditAsync`, which writes ALL FOUR of Type/Assignee/PCA/Progress whenever any ONE changes.**
🔴 **TypeScript will not catch it** — the generated DTOs are all-optional, so a dropped field **compiles**.

### R8 — The test gate is "no existing test breaks", NOT "the count stays put"
**Four times in M8.6 I wrote a gate as a fixed number and four times it was wrong.** A task that changes a contract **must** move the tests pinning it. A task that adds a mutation **must** add tests for it. **Add what the work needs; report what you added and why.**
🔴 **Corollary, and it bit twice:** a test can stay **green while asserting nothing** — `System.Text.Json` binds record ctor params by NAME and **defaults the missing ones**, so a DTO swap leaves old tests deserialising happily into `default`. **A gate that cannot move is a gate that cannot notice.**

---

# PHASE 1 — the shared spine. **Nothing fans out until this is merged.**

## Task P1 — Core: the Gantt move, the admin write, the Task List read model

<model>opus</model>

**Files:**
- Move: `src/TimesheetApp/ViewModels/TaskListViewModel.cs` (`BuildGantt`, `:314-376`) → **new** `src/TimesheetApp.Core/Services/GanttBuilder.cs`
- Move: `src/TimesheetApp.Tests/ViewModels/GanttModelTests.cs` → `.../Services/GanttBuilderTests.cs`
- Modify: `src/TimesheetApp.Core/Data/Repositories/IUserRepository.cs` + `UserRepository.cs`
- Create: `src/TimesheetApp.Core/Services/ITaskListService.cs` + `TaskListService.cs` (+ tests)
- Modify: `src/TimesheetApp/ViewModels/TaskListViewModel.cs` — call the moved function

### P1a — Move `BuildGantt` into Core

It is **`internal static`** at `TaskListViewModel.cs:314-376` — **a pure function in the wrong project.** It already depends only on `WorkingDayCalculator` (Core). **This is a MOVE, not a rewrite. Do not change its behaviour.**

🔴 **`GanttModelTests.cs:23` calls `TaskListViewModel.BuildGantt(...)` directly.** **The test file moves with it.**

🔴 **THE 658 BASELINE WILL MOVE, AND THAT IS CORRECT.** The tests relocate; the count should not change, but their *assembly* and *class* do. **This is the R8 exception, third occurrence — the contract genuinely moved.** If the count changes, say by how much and why. **Do not delete a test to hit a number.**

### P1b — `IUserRepository.SetIsAdminAsync(int userId, bool isAdmin, long expectedVersion)` → `Task<long>`

Checked (`Users` has `row_version`). **`grep -rn SetIsAdminAsync src/` returns nothing today** — verify that before you write it. The **only** statement in the entire codebase that writes `is_admin` is the v10 migration (`DatabaseInitializer.cs:337`).

### P1c — `ITaskListService.GetRowsAsync(int year, int month, IReadOnlyList<int> teamIds)`

🔴 **This is the only way to make Task List work.** `ITimeLogRepository.GetLoggedHoursByBacklogAsync()` is **called by NO endpoint** — verify with `grep -rn GetLoggedHoursByBacklogAsync src/TimesheetApp.Api/` (expect **zero hits**). So **logged-hours-per-backlog is unobtainable by any route**, and without it a client **cannot compute `ScheduleState`**. `IScheduleStateService` is DI-registered in the API (`Program.cs:88`) and **called by nothing**.

**Assemble in ONE round-trip** (so the client cannot see three different snapshots), replicating `TaskListViewModel.LoadAsync` (`:172-296`), per backlog:
- `SearchAsync(null, teamIds)` → **drop `BacklogCode == "DEFAULT"`** → filter to the month (`allMonths || b.PeriodMonth == monthKey`)
- `GetLoggedHoursByBacklogAsync()` · `GetTagIdsForAllAsync()` · `GetActiveByBacklogsAsync(ids)` — **all three are batched. Do not N+1.**
- `estimate = OfficialEstimateHours ?? RoughEstimateHours`
- 🔴 **`isDone = tasks.Count > 0 && tasks.All(t => t.Status == "Done")` — ZERO TASKS IS NOT DONE.**
- `ScheduleState` via `IScheduleStateService.Evaluate(...)`
- The **Gantt model** via the moved `GanttBuilder`

**Tests:** past its internal deadline → **`Late`**. Behind pace inside the 2-working-day window → **`Warning`**. Zero tasks → **not done** → and therefore not automatically `Normal` for the wrong reason. **A client-side reimplementation would silently disagree — that is the entire point of this read model.**

- [ ] **Step 1:** Run the baseline (R2). Record both numbers.
- [ ] **Step 2:** P1a — the move. **Run the gate.** The 658 must still be 658 (the tests moved, not vanished). **Commit separately** — a pure move is the one thing worth isolating.
- [ ] **Step 3:** P1b + tests. **Step 4:** P1c + tests. **Step 5:** Full .NET gate — **BOTH `Passed!` lines**.
- [ ] **Step 6: Commit.** `feat(core): move BuildGantt to Core, add SetIsAdminAsync, add the TaskList read model (M9 P1)`

---

## Task P2 — API: annotate ALL 49 routes in `SettingsEndpoints.cs`

<model>opus</model>

**Depends on P1.** **Files:** `Endpoints/SettingsEndpoints.cs` · `Contracts/Dtos.cs` · `ApiTests/SettingsEndpointsTests.cs` · `ApiTests/OpenApiContractTests.cs`

**49 routes. 4 annotated (M8.6). 45 to go.** This one file serves **Users · Settings · Task List · Daily Report · Reports · Backlog**. **It is the single highest-collision file in the repo, and that is why one agent owns all of it.**

### 🔴 P2a — Three response types are `internal` and WILL NOT COMPILE with `.Produces<T>()`
`SettingsUserStandup`, `SettingsStandupEntryView`, `SettingsOpsResult` are **`internal sealed record`** (`:837-843`). **Make them `public` first.**

### P2b — Tag every route, by group
`Users` · `Tags` · `Teams` · `PcaContacts` · `Templates` · `Holidays` · `DefaultTasks` · `Standup` · `Ops` · `Me`

🔴 **Read EVERY handler and record what it ACTUALLY returns** — several return a **bare `int`** (`POST /api/standup/entries`, `/quick-import`, `.../issues`) and one returns a **bare `int[]`** (`GET /api/teams/{id}/members`, `GET /api/backlogs/{id}/tags`). **Do not assume a DTO exists. A wrong `.Produces<T>()` generates a TYPED LIE, which is worse than no method.**

🔴 **Record `.RequireAuthorization(AuthSetup.AdminPolicy)` per route** — most write routes have it. **Tag them anyway** (the Users/Settings screens *are* admin screens), but note them: **a screen a non-admin can reach must never call one.** *(An annotated admin route reaching a non-admin client is a correctly-typed 403 — the lie that BLOCKed M8.6's plan.)*

### 🔴 P2c — `POST /api/teams` skips the TM-04 bootstrap. Fix it.
WPF (`SettingsViewModel.cs:566-569`) calls `EnsureDefaultBacklogIdAsync(newId)` **and** `SyncAsync()`. The route (`:199-211`) calls **neither** — so **a team created from the web gets no `DEFAULT` backlog and no default tasks.** Nobody has noticed because no client can call the route.
**Test:** create a team → assert a `DEFAULT` backlog exists for it.

### P2d — `POST /api/default-tasks/sync` (new, one line)
WPF's "Sync default tasks" button calls `IDefaultTaskSyncService.SyncAsync()`. The API only calls it as a *side-effect* of two other routes. **There is no standalone route.** Add one. Admin-only.

- [ ] Baseline (R2) → tests → implement → contract rows for **all 49** → full gate → commit.
- [ ] **Commit:** `feat(api): annotate all 49 settings routes, fix the teamless team bootstrap (M9 P2)`

---

## Task P3 — API: Reports/Export + four new routes

<model>opus</model>

**Depends on P2** (shares `Dtos.cs` + `OpenApiContractTests.cs`). **Files:** `Endpoints/TimesheetEndpoints.cs` · **new** `Endpoints/TaskListEndpoints.cs` · `Contracts/Dtos.cs` · `Program.cs` (**one line**) · tests

### P3a — The 5 Reports/Export routes (`TimesheetEndpoints.cs:211-287`)
All exist, all work, **every handler ends bare at `});`**. **Zero routes to invent.** Tag `Reports` / `Export`.

🔴 **`GET /api/export/excel` returns `Results.File` (binary); `/markdown` returns `Results.Text`.** **Neither is JSON.** `ng-openapi-gen` cannot type them usefully. **Do NOT add `Export` to `includeTags`** — the Angular side hand-writes a blob download. Annotate them anyway for the OpenAPI document's honesty, but **keep them out of the generated client.**

🔴 **`EffectiveTeamIds` (`:356-368`) must stay EXACTLY as it is.** It reads `HttpContext.Request.Query` **by hand** because `[FromQuery] int[]?` **never binds to `null`** — both "absent" and "empty" become an empty array — while `null` means **EVERY TEAM** to the repository. **Two passing tests prove it fails closed. Do not "simplify" it.**

### P3b — `GET /api/tasklist?year=&month=&teamIds=` — **NEW**
Calls `ITaskListService` (P1c). Uses `EffectiveTeamIds`. Lives in a **new file** so it does not collide with anything.
🔴 **`Program.cs` gets ONE line** (`app.MapTaskListEndpoints();`). That file's header says *"Wave 2 fills in exactly one file each and NEVER TOUCHES THIS FILE — which is precisely what makes four parallel agents safe."* **This is Phase 1. The controller touches it, once, deliberately, and no Phase-2 agent ever does.**

### P3c — `PUT /api/users/{id}/admin` — **NEW**, admin-only, checked
Takes `{ isAdmin, expectedVersion }` → `SavedBody` · 400 · 404 · 409.

🔴 **Test BOTH halves of the 30-day hole, so nobody "fixes" one and breaks the other:** a demoted admin **still passes `AdminPolicy`** (the cookie claim is fixed at login, 30-day sliding) **but is rejected by `/api/ops/*`** (which additionally check the DB-fresh `ctx.IsAdmin`).

### P3d — `GET` + `PUT /api/settings/{key}` — **NEW**
**No route touches `ISettingsRepository` today**, so SET-02's "not-logged warning window" (`chua_log_n_days`, default 3) **cannot be read or written from the web at all** — and **Reports reads the same key server-side**. `GET` open; `PUT` admin-only.

### P3e — `POST /api/standup/archive?date=` — **NEW**
**DR-09 has no API surface at all**, and `Program.cs` never calls `BackfillMissingWeeksAsync` — it is WPF-only. Calls `IStandupArchiveService.ExportWeekAsync(date)`. Returns the path, or a 400 *"No standup data this week to archive."* Admin-only.

- [ ] Baseline → tests → implement → contract rows → full gate → commit.
- [ ] **Commit:** `feat(api): annotate reports+export, add tasklist/admin/settings/archive routes (M9 P3)`

---

## Task P4 — Regenerate the client. ONCE.

<model>sonnet</model>

**Depends on P3.** **Files:** `ng-openapi-gen.json` (**one line**) · `src/app/api/**` (**generated — never hand-edit**)

```json
"includeTags": ["Auth","Timesheet","SmartFill","Backlogs","Tasks","Users","PcaContacts",
                "Tags","Teams","Templates","Holidays","DefaultTasks","Standup","Reports","Ops","Me","TaskList"]
```
🔴 **`Export` is deliberately ABSENT** — those two routes return binary/text, not JSON (P3a).

🔴 **R1 applies in full. Pin all three seams. Prove the empty database by grepping `no users yet, nothing to bootstrap`.** No match → **kill it and report BLOCKED**.

🔴 **Verify:** no generated function is `void` **unless its route genuinely has no body** (R3). `user-list-all` and `pca-contact-list-all` must remain **ABSENT** — they are admin-only and a contract test enforces it.

- [ ] **Commit:** `feat(web): regenerate the client for every remaining screen (M9 P4)`

---

## Task P5 — `WorklogService`: every method, once

<model>sonnet</model>

**Depends on P4.** **File:** `src/app/services/worklog.service.ts` **only**.

**The stub block (`:309-329`) is one contiguous region every screen rewrites. One owner writes all of it.**

🔴 **Reads on `http`. Writes on `mutatingHttp`** (`ConnectionIdHttpClient`, `:65` — stamps `X-Connection-Id` so the server excludes you from your own SignalR echo). **A write sent on the read client emits a BYTE-IDENTICAL request in a hub-less TestBed** — undetectable by every test in the repo **except a connected-hub one**. The spec file says so three times. **Test each mutation in a connected-hub `describe`.**

🔴 **Excel/markdown export is a HAND-WRITTEN blob download**, not a generated function: `this.http.get(url, { responseType: 'blob' })`. Never an absolute URL — `SameSite=Lax` dies (`worklog.service.ts:38-42`).

🔴 **Delete the `// TODO: GET /api/reports/metrics` at `:315`. That route does not exist and does not need to** — all four stat cards are client-side arithmetic over the weekly response.

- [ ] **Commit:** `feat(web): wire every remaining service method (M9 P5)`

---

## Task P6 — The shared Angular pieces nobody owns

<model>opus</model>

**Depends on P5.** **Files:** **new** `components/team-filter/` · **new** `components/tag-picker/` · **new** `core/admin.guard.ts` · `core/realtime.service.ts` · `app.routes.ts` · `components/sidebar/`

### P6a — `team-filter` — **four screens need it and it does not exist**
🔴 Contract, from `TeamFilterViewModel.cs:57-102`:
- `checkedTeamIds: number[]` — **an empty array means NO TEAMS, NEVER "all"**. A test must fail if anyone "helpfully" treats `[]` as unfiltered.
- Default = **the active team only** (from `MeResponse.activeTeamId`)
- **Hidden when `memberTeamIds.length <= 1`**
- Header `Teams (N)`; on active-team change, **reset** to `{new active team}`
- Wire format `?teamIds=1&teamIds=2` — **a repeated key.** That is what `EffectiveTeamIds` parses.

### P6b — `tag-picker` — Backlog editor + Task List
Checkable chips (icon + colour + text), type-to-filter, **replace-all on save**.
🔴 **`BacklogTags` has NO `row_version` of its own — the version rides the PARENT.** `SetTagsCheckedAsync` checks and bumps the **backlog's** version, so a tag write **can 409** on a stale backlog version.

### P6c — `admin.guard.ts`
`/users` and `/settings` sit behind **`authGuard` only** today. **`AuthService.currentUser().isAdmin` exists and is read by NOTHING** — grep the whole `src/`. Without a guard, a non-admin navigates in and **every call 403s: the screen looks broken rather than hidden.** **The sidebar must hide the entries too.**

### 🔴 P6d — Fix `realtime.service.ts:63`
```typescript
hub.on(DATA_CHANGED, () => this._dataChanged.next());   // the server sends (kind, teamId). BOTH DISCARDED.
```
`dataChanged` is `Observable<void>`, so **no screen can filter what changed** — every change anywhere refreshes everything. **Make it `Observable<{kind: string, teamId: number}>`.**
🔴 **`log-work.component.ts` and `backlog.component.ts` already subscribe to it.** Their subscriptions must keep working. **This is a contract change: their tests move with it (R8).**

- [ ] **Commit:** `feat(web): team filter, tag picker, admin guard, and a realtime feed that says WHAT changed (M9 P6)`

---

# PHASE 2 — five agents. TRUE parallel. Zero file overlap.

**Every shared file was written in Phase 1. Each agent owns exactly one directory.**

| Agent | Owns | Model |
|---|---|---|
| **A — Task List** | `pages/task-list/**` | `opus` |
| **B — Daily Report** | `pages/daily-report/**` | `opus` |
| **C — Reports** | `pages/reports/**` | `sonnet` |
| **D — Users + Settings** | `pages/users/**` + `pages/settings/**` | `opus` |
| **E — Backlog editor debt** | `pages/backlog/backlog-editor.component.*` | `sonnet` |

**Each agent's rules are in the spec (§5). The load-bearing ones, per agent:**

- **A:** `isDone` needs **> 0 tasks** · at most **ONE** system chip · progress is on the **BACKLOG**, not the task · **a deadline change REQUIRES a reason note** · 🔴 **R7 — `CommitBacklogEditAsync` writes ALL FOUR of Type/Assignee/PCA/Progress whenever ONE changes; round-trip the other three from the DTO** · Gantt: axis = **working days only**, `BarEnd = DeadlineInternal ?? EndDate`, `BarStart = StartDate ?? BarEnd`.
- **B:** **add + delete only — NO edit-entry affordance** (`StandupEntries` has no `row_version`; two tabs are the same user) · **issues ARE exempt from the lock and the owner gate — do not "fix" that** · the picker is **active-team-only** · `DEFAULT` excluded.
- **C:** **no charts** · the tree is **`TeamNode[]` — an array of roots, five heterogeneous levels, no node ids (synthesise them)** · **no `metrics` route** — compute the four cards client-side · **port the two team-scoping quirks faithfully, do not "fix" them**.
- **D:** 🔴 **a created user has `username = null` and no password — they CANNOT LOG IN.** The screen must do **all three**: create → set username → set password. **Otherwise it creates ghosts.** · Tags are **HARD**-deleted; PCA contacts are **SOFT**-deleted · **the five infrastructure sections are DROPPED** (spec §2.1) · keep the four `/api/ops/*` buttons.
- **E:** TagPicker + template applier on the Backlog editor. **Templates auto-apply on select** and **append** on a different pick.

**Gate for every Phase-2 agent:** `npm test` — **all existing pass**, plus yours (R8). `npm run build` clean. **And R4: mutation-check your load-bearing tests.**

---

## Final gate

```
# nothing on 5080 (R2)
dotnet test src/TimesheetApp.sln --nologo    # BOTH assemblies
cd src/timesheet-web && npm test -- --watch=false && npm run build
```
**1142 → target ≈ 1400.** Zero warnings.

## Manual checks — no test catches these

- **OT-20** — Task List: change **only** the progress % on a card. Reload. 🔴 **Type, Assignee and PCA must all survive.** *(R7, third occurrence.)*
- **OT-21** — Task List: a backlog past its internal deadline shows **⚠ Late**; one behind pace within 2 working days shows **⚠ At risk**. Toggle to **Gantt** — the bars must skip weekends and holidays.
- **OT-22** — Users: create a user, set their username, set their password. **Log in as them.** *(If any step is missing, you have made a ghost.)*
- **OT-23** — Users: **log in as a NON-admin.** `/users` and `/settings` must be **hidden from the sidebar and unreachable by URL.**
- **OT-24** — Settings: create a **new team**. It must get a **`DEFAULT` backlog** — check Log Work shows its default tasks. *(Today it does not.)*
- **OT-25** — Daily Report: yesterday and today are editable; **the day before yesterday is not.**
