# M9 — The four remaining screens · Implementation Plan · **rev. 2**

> **For agentic workers:** REQUIRED SUB-SKILL: `superpowers:subagent-driven-development`.

**Goal:** Wire Task List, Daily Report, Reports, Users and Settings against the real API — and repay M8.6's TagPicker debt on the Backlog editor. **No screen in the app is fake when this lands.**

**Architecture:** **PHASE 1 (sequential, one owner)** writes every shared file. **PHASE 2 (five agents, true parallel)** owns five disjoint page directories. **PHASE 3** deletes what Phase 2 orphaned.

**Spec:** `docs/superpowers/specs/2026-07-13-m9-remaining-screens-design.md`
**Recon:** `.planning/research/M8.7-M8.10-recon.md` · `.planning/research/M8.8-daily-report-recon.md`
**Branch:** `feature/m9-remaining-screens-2026-07-13`, base `main` @ `28fd406`
**Baseline:** **1142 green** — 658 Core/WPF + 210 API + 274 Angular. 0 warnings.

---

## What rev. 1 got wrong — the Plan Checker returned BLOCK. Read this; it is not preamble.

| # | Rev. 1 said | The code says |
|---|---|---|
| **B1** | *(nothing — `BacklogEndpoints.cs` appears in NO task's file list)* | **It has 8 unannotated routes**, and Task List needs `PUT /tasks/{id}/status`, `/extended`, `GET/PUT /tasks/{id}/tags`, `POST /backlogs/{id}/continue`; the TagPicker needs `GET/PUT /backlogs/{id}/tags`. **Both headline deliverables were unimplementable.** *(THIRD consecutive occurrence of this defect.)* And rev. 1 told P2 that `GET /backlogs/{id}/tags` lives in `SettingsEndpoints.cs`. **It does not.** |
| **B2** | *"Tag every route, by group… **Tag them anyway.**"* | **`/api/users/all` and `/api/pca-contacts/all` are `AdminPolicy` AND deliberately untagged**, enforced by `Admin_gated_list_is_NOT_tagged_…`. An agent following P2 tags them, **two tests go red — and R8 licenses it to "move the tests pinning it."** R8, the rule I added because my gates were wrong four times, **now licenses deleting a security guard.** |
| **B3** | *"Phase 2: zero file overlap."* | **`worklog.models.ts` is ONE file holding every screen's view models, and `Tag` is imported by `task-list.component.ts` (Agent A) AND `settings.component.ts` (Agent D).** And P5 as scoped **cannot compile** — the service file itself says so: *"**THEY ARE NOT DEAD CODE YOU MAY RETYPE IN PLACE.** Changing a stub's return type … **BREAKS THE BUILD of a component the current task is not allowed to touch.**"* I quoted that lesson in M8.6 and then violated it here. |
| **B4** | *"The stub block (`:309-329`)."* · *"`requireRowVersion` … `:343`."* · *"`ConnectionIdHttpClient`, `:65`."* | **`:309-329` is `setTaskOrder` + `setTaskActive`** — live, tested, bump-only code carrying *"Do not 'harden' this by adding a version."* The stubs are at **`:464-483`**. `requireRowVersion` is at **`:497`**. `mutatingHttp` at **`:96`**. **An agent told "one owner rewrites all of it" rewrites two working mutation methods.** |
| **#5** | `app.MapTaskListEndpoints()` | Must be **`api.MapTaskListEndpoints()`**. `app.Map…` **bypasses `ClientContextFilter`**, so `IClientContext` is never populated — and the handler reads `ctx.MemberTeamIds`. |
| **#6** | *"`internal sealed record` **WILL NOT COMPILE** with `.Produces<T>()`."* | **FALSE — proven by building it.** A generic type argument needs accessibility only at the *call site*, same assembly. The repo already proves it (`ILogger<SettingsOpsMarker>`, internal, compiles today). **And the cited lines `:837-843` are the WRONG THREE records** — the real ones are `:884`, `:886`, `:890`. An agent would have made the wrong types public, chasing a compile error that does not exist. |
| **#7** | *"Hidden when `memberTeamIds.length <= 1`."* | **`ShowFilter => AvailableTeams.Count > 1`.** And `SettingsEndpoints.cs:109-110` spells out the trap: **`ctx.MemberTeamIds` has NO `is_active` filter; `AvailableTeams` = active ∩ memberships — strictly NARROWER.** `MeResponse` hands the client the **wider** set. As pinned, the filter would **list a deactivated team WPF hides** — and rev. 1 pinned it *with a test*, baking the bug in. |
| **#8** | *"their tests move with it"* | The **production** consumers survive (a zero-arg callback is assignable). The breakage is in **typed spec stubs** — `backlog.component.spec.ts:96-99` and `log-work.component.spec.ts:169-172` declare `dataChanged … as Observable<void>` → **TS2322**, and `ng test` type-checks specs. **Three spec files P6 did not own.** |

**Verified CORRECT and unchanged:** R1's safety proof (the log substring genuinely exists) · `Program.cs:34` uses `||` · **`BuildGantt` is genuinely movable** (`static`, closes over nothing; every type it touches is already in Core; `InternalsVisibleTo` already covers the test project) · `SetIsAdminAsync` has zero hits · `GetLoggedHoursByBacklogAsync` has **zero hits in the API** · `POST /api/teams` really does skip the bootstrap · the 5 report routes really do end bare · excluding `Export` from `includeTags` is right.

---

## 🔴 THE RULES. Restated verbatim in every brief, because an agent sees its brief and nothing else.

### R1 — The API must NEVER run against the real database
`Program.cs` defaults `DbPath` to `C:\Users\Admin\Documents\TimesheetApp\timesheet.db` — the user's **live company data**.
**Pin ALL THREE seams:** `TimesheetApp:DbPath` · `:ConfigPath` · `:KeyRingPath`. 🔴 **`DbPath` alone is NOT enough** — `Program.cs:34` uses `||`.
**Prove it:** grep the startup log for the substring **`no users yet, nothing to bootstrap`**. *(Grep the SUBSTRING — the line reads "**Admin bootstrap:** …", space, lowercase `b`.)* **No match → kill it and report BLOCKED.**

### R2 — The .NET gate lies in BOTH directions
- **False GREEN:** a running API holds `TimesheetApp.Core.dll` open → the API project fails to *build* **and `dotnet test` still exits 0 printing `Passed!`**. **Nothing on 5080. BOTH `Passed!` lines must appear.**
- **False RED:** a pre-existing race. A lone `SqliteException: no such table: Backlogs` is **not a regression — re-run.** **Run the baseline BEFORE touching the tree.**

### R3 — `.Produces<T>()` is what makes a route exist to the client
🔴 **`void` is NOT a red flag** — seven functions are legitimately `void` (routes declaring 204 via the **non-generic** `.Produces(StatusCodes.Status204NoContent)` overload). **The invariant is: `void` only where the route has no body.**
🔴 **An annotated ADMIN-GATED route is a different lie: correctly typed, and it 403s.** Check `.RequireAuthorization(AuthSetup.AdminPolicy)` before tagging, and **a screen a non-admin can reach must never call one.**

### R4 — A green build proves nothing about wiring
M8.5: six of seven tests stayed GREEN against a completely dead feature. M8.6/T7: `ng build` **exits 0** with `{{ noSuchSymbol() }}` in a component nobody imports yet — AOT never type-checks it.
**Assert the wiring. And MUTATION-CHECK the load-bearing tests: break the code, watch the test go red.** If it stays green, the test is decorative.

### R5 — Every `async` handler bound to a template output needs a `catch` that never re-throws
Anything that escapes is an **unhandled promise rejection**: console only, **nowhere the user can see.**

### R6 — Never `!`, never `0`, on a `rowVersion`
**`requireRowVersion(rowVersion: number | undefined): number` — `src/app/services/worklog.service.ts:497`.** It **throws**.

### R7 — 🔴 THE WHOLE-RECORD TRAP. Third milestone running.
A checked `PUT` **replaces the whole record.** If the form shows only *some* fields, **build the request from the loaded DTO and override only what the form owns.** Build it from the form and every hidden field is written `NULL`.
**Already: nulled a `team_id` (M8.3 — invisible forever); nulled six Backlog fields (M8.6). It is waiting in Task List's `CommitBacklogEditAsync`, which writes ALL FOUR of Type/Assignee/PCA/Progress whenever any ONE changes.**
🔴 **TypeScript will NOT catch it** — the generated DTOs are all-optional, so **a dropped field COMPILES**.

### R8 — The gate is "no existing test breaks", NOT "the count stays put"
**Four times in M8.6 I wrote a gate as a fixed number and four times it was wrong.** A task that changes a contract **must** move the tests pinning it.
🔴 **BUT R8 IS NOT A LICENCE TO DELETE A GUARD.** The Plan Checker caught rev. 1 doing exactly that. **If a test you are about to "move" is asserting a SECURITY property — that a route is admin-gated, that an admin route is absent from the client — STOP AND REPORT. Do not move it. That is a plan decision, not yours.**

### R9 — 🔴 CITE BY SYMBOL, NOT BY LINE
**Every line number in a brief for a file a previous task edited is stale.** M8.6/T2: an entire file's citations were off by exactly **+49**. M9/rev. 1: the "stub block" citation pointed at two working methods. **Grep for the symbol. Do not trust a line number in this plan.**

---

# PHASE 1 — the shared spine. **Nothing fans out until this is merged.**

## Task P1 — Core: the Gantt move · the admin write · the Task List read model

<model>opus</model>

**Files:** move `BuildGantt` (`TaskListViewModel.cs`) → **new** `Core/Services/GanttBuilder.cs` · move `Tests/ViewModels/GanttModelTests.cs` → `Tests/Services/GanttBuilderTests.cs` · `Core/Data/Repositories/IUserRepository.cs` + `UserRepository.cs` · **new** `Core/Services/ITaskListService.cs` + `TaskListService.cs` (+ tests) · `TaskListViewModel.cs` (call the moved fn)

### P1a — Move `BuildGantt` into Core. **A MOVE, not a rewrite.**
**Verified movable:** it is `internal static`, **closes over nothing** (`BarEnd`/`BarStart` are static local functions; `NearestIndex` closes only over the local `axis`), and **every type it touches is already in Core** (`GanttModel`/`GanttBar` in `ReadModels.cs`, `ScheduleState`, `IWorkingDayCalculator`). **`TimesheetApp.Core.csproj` already declares `<InternalsVisibleTo Include="TimesheetApp.Tests" />`**, so it survives staying `internal`.
**Exactly three references:** the declaration · `TaskListViewModel.cs:293` · `GanttModelTests.cs:23`. **The test file moves with it.**
🔴 **The 658 baseline: the tests RELOCATE, they do not vanish. The count should hold. If it changes, say by how much and why. DO NOT delete a test to hit a number.** *(R8 — the contract genuinely moved.)*

### P1b — `IUserRepository.SetIsAdminAsync(int userId, bool isAdmin, long expectedVersion)` → `Task<long>`
Checked (`Users` has `row_version`). **Verify first: `grep -rn SetIsAdminAsync src/` returns NOTHING today**, and the **only** production writer of `is_admin` is the v10 migration (`DatabaseInitializer.cs:337`).

### P1c — `ITaskListService.GetRowsAsync(int year, int month, IReadOnlyList<int> teamIds)`
🔴 **The only way Task List can work.** **Verify: `grep -rn GetLoggedHoursByBacklogAsync src/TimesheetApp.Api/` → ZERO hits.** So logged-hours-per-backlog is **unobtainable by any route**, and without it a client **cannot compute `ScheduleState`**. `IScheduleStateService` is DI-registered (`Program.cs:88`) and **called by nothing**. The maths is pure and already in Core; **only the projection is missing.**

**One round-trip**, replicating `TaskListViewModel.LoadAsync`, per backlog:
`SearchAsync(null, teamIds)` → **drop `BacklogCode == "DEFAULT"`** → filter to the month · `GetLoggedHoursByBacklogAsync()` · `GetTagIdsForAllAsync()` · `GetActiveByBacklogsAsync(ids)` — **all three batched. Do not N+1.** · `estimate = Official ?? Rough` · 🔴 **`isDone = tasks.Count > 0 && tasks.All(Status == "Done")` — ZERO TASKS IS NOT DONE** · `ScheduleState` · the Gantt model.

**Tests:** past its internal deadline → **`Late`**. Behind pace inside the 2-working-day window → **`Warning`**. Zero tasks → **not done**. *(A client-side reimplementation would silently disagree — that is the point.)*

- [ ] Baseline (R2) → P1a (**commit the move alone**) → P1b → P1c → full gate (**BOTH `Passed!`**) → commit.
- [ ] `feat(core): move BuildGantt to Core, add SetIsAdminAsync, add the TaskList read model (M9 P1)`

---

## Task P2 — `SettingsEndpoints.cs`: annotate 43, leave 2 alone, fix the team bootstrap

<model>opus</model>

**Depends on P1.** **Files:** `Endpoints/SettingsEndpoints.cs` · `Contracts/Dtos.cs` · `ApiTests/SettingsEndpointsTests.cs` · `ApiTests/OpenApiContractTests.cs`

**49 routes. 4 already annotated. 43 to tag. 🔴 TWO ARE A PLAN DECISION — see P2a.**

### 🔴 P2a — `/api/users/all` and `/api/pca-contacts/all`: TAG THEM. And REPLACE the guard, do not delete it.

They are `.RequireAuthorization(AuthSetup.AdminPolicy)` and are currently kept **out** of the generated client by `Admin_gated_list_is_NOT_tagged_and_so_never_joins_the_generated_client`.

**That decision was made in M8.6 on the rationale "no admin-only screen exists." M9 CREATES one.** The Users screen **must** list inactive users (USR-01 — *"Users tab shows inactive too"*), and **"Activate" is impossible without them.**

**So: tag them.** And because you are removing a guard, **you must put a stronger one in its place:**

1. **DELETE** `Admin_gated_list_is_NOT_tagged_…`.
2. **ADD** `Admin_gated_lists_STILL_require_the_admin_policy` — assert `/api/users/all` and `/api/pca-contacts/all` **return 403 for a NON-admin**. 🔴 **`ApiFactory.SeedUserAsync(…, isAdmin)` lets you seed an admin and go green. Seed a NON-admin deliberately.**
3. **ADD** `A_non_admin_screen_never_calls_an_admin_route` — see P6c; the `adminGuard` is the client-side half.

**This is the ONE test in the milestone you are allowed to delete, it is authorised HERE and nowhere else, and only because it is being replaced by a test of the actual security property (the route is admin-gated) rather than a proxy for it (the route is absent from the client).** **R8 does not license anything else like this.**

### P2b — Tag the other 41, by group
`Users` · `Tags` · `Teams` · `PcaContacts` · `Templates` · `Holidays` · `DefaultTasks` · `Standup` · `Ops` · `Me`

🔴 **Read EVERY handler and record what it ACTUALLY returns.** Several return a **bare `int`** (`POST /api/standup/entries`, `/quick-import`, `.../issues`) and one a **bare `int[]`** (`GET /api/teams/{id}/members`). **Do not assume a DTO exists.** A wrong `.Produces<T>()` generates a **typed lie**, which is worse than no method.
*(`GET /api/backlogs/{id}/tags` is **NOT in this file** — rev. 1 said it was. It is in `BacklogEndpoints.cs`, and it belongs to **P2.5**.)*

🔴 **~29 of the 49 carry `.RequireAuthorization(AuthSetup.AdminPolicy)`.** Enumerate them in your report. **Tag them** — the Users/Settings screens *are* admin screens — but **a screen a non-admin can reach must never call one** (the `adminGuard` in P6c is the enforcement).

### P2c — Three `internal sealed record`s → `public`
`SettingsUserStandup` (**`:884`**) · `SettingsStandupEntryView` (**`:886`**) · `SettingsOpsResult` (**`:890`**). **Verify by symbol, not by line (R9).**
🔴 **This is a CONVENTION, not a compile requirement.** Rev. 1 claimed `.Produces<T>()` would not compile on an internal type. **That is FALSE** — a generic type argument needs accessibility only at the call site, same assembly, and the repo already proves it (`ILogger<SettingsOpsMarker>`, internal, compiles today). **You will get no compile error to steer by.** Do it because every other wire contract in `Contracts/Dtos.cs` is public.

### 🔴 P2d — `POST /api/teams` skips the TM-04 bootstrap. Fix it.
WPF (`SettingsViewModel.cs:566-569`) calls `InsertAsync` → **`EnsureDefaultBacklogIdAsync(newId)`** → **`SyncAsync()`**. The route calls **neither** — so **a team created from the web gets no `DEFAULT` backlog and no default tasks.** Nobody has noticed because no client can call the route.
**Test:** create a team → **assert a `DEFAULT` backlog exists for it.**

### P2e — `POST /api/default-tasks/sync` (new, one line, admin-only)
WPF's "Sync default tasks" button calls `IDefaultTaskSyncService.SyncAsync()`. The API calls it only as a *side-effect* of two other routes. **No standalone route exists.**

- [ ] Baseline (R2) → tests → implement → contract rows for all 47 tagged → full gate → commit.
- [ ] `feat(api): annotate 43 settings routes, tag the admin lists behind a stronger guard, fix the teamless team bootstrap (M9 P2)`

---

## 🔴 Task P2.5 — `BacklogEndpoints.cs`: the 8 routes rev. 1 forgot entirely

<model>sonnet</model>

**Depends on P2** (shares `OpenApiContractTests.cs`). **Files:** `Endpoints/BacklogEndpoints.cs` · `ApiTests/OpenApiContractTests.cs` · `ApiTests/BacklogEndpointsTests.cs`

**Without this task, Task List's inline edits and the ENTIRE TagPicker are unimplementable.** *(Third consecutive milestone in which the route carrying the headline feature's data was never annotated.)*

**Tags `Backlogs` / `Tasks` are already in `includeTags` — no config change.** 🔴 **Find each route by its path, not by the line numbers below (R9).**

| Route | Returns | Needed by |
|---|---|---|
| `GET /api/backlogs/{id}/tags` | **bare `int[]`** → `.Produces<List<int>>()` · 404 | Agent E, Agent A, P6b |
| `PUT /api/backlogs/{id}/tags` | `SavedBody` · 404 · **409** | Agent E, Agent A |
| `POST /api/backlogs/{id}/continue` | `BacklogDto` · 400 · 404 | **Agent A** (the Continue button) |
| `GET /api/tasks/{id}` | `TaskItemDto` · 404 | Agent A |
| `PUT /api/tasks/{id}/status` | `SavedBody` · 400 · 404 · **409** | **Agent A** (inline status) |
| `PUT /api/tasks/{id}/extended` | `SavedBody` · 404 · **409** | **Agent A** (inline type/assignee) |
| `GET /api/tasks/{id}/tags` | **bare `int[]`** · 404 | Agent A |
| `PUT /api/tasks/{id}/tags` | `SavedBody` · 404 · **409** | Agent A |

🔴 **Read each handler and confirm the statuses before declaring them.** Declaring one a route cannot return is its own lie.
🔴 **The `[InlineData]` path strips route constraints** — `"/api/tasks/{id}/tags"`, never `"{id:int}"`. *(Verified empirically in M8.6.)*

- [ ] `feat(api): annotate the 8 remaining backlog/task routes (M9 P2.5)`

---

## Task P3 — Reports/Export + five new routes

<model>opus</model>

**Depends on P2.5.** **Files:** `Endpoints/TimesheetEndpoints.cs` · **new** `Endpoints/TaskListEndpoints.cs` · `Contracts/Dtos.cs` · `Program.cs` (**one line**) · tests

### P3a — The 5 Reports/Export routes
All exist, all work, **every handler ends bare at `});`**. **Zero routes to invent.** Tag `Reports` / `Export`.
🔴 **`/api/export/excel` returns `Results.File` (binary); `/markdown` returns `Results.Text`.** **Neither is JSON.** **Do NOT add `Export` to `includeTags`** — Angular hand-writes a blob download. Annotate for the document's honesty; keep them out of the client.
🔴 **`EffectiveTeamIds` must stay EXACTLY as it is.** `[FromQuery] int[]?` **never binds to `null`** (both "absent" and "empty" become an empty array), while `null` means **EVERY TEAM** to the repository. **Two passing tests prove it fails closed. Do not "simplify" it.**

### P3b — `GET /api/tasklist?year=&month=&teamIds=` — **NEW**, in a new file
Calls `ITaskListService` (P1c). Uses `EffectiveTeamIds`.
🔴 **Register it as `api.MapTaskListEndpoints();`, NOT `app.Map…`.** `Program.cs` builds `var api = app.MapGroup("").AddEndpointFilter<ClientContextFilter>();` — **`app.Map…` bypasses that filter, so `IClientContext` is never populated**, and your handler reads `ctx.MemberTeamIds`. *(Rev. 1 got this wrong.)*
*(`Program.cs`'s header says Wave-2 agents never touch it. **This is Phase 1. The controller touches it once, deliberately. No Phase-2 agent ever does.**)*

### P3c — `PUT /api/users/{id}/admin` — **NEW**, admin-only, checked
`{ isAdmin, expectedVersion }` → `SavedBody` · 400 · 404 · 409.
🔴 **Test BOTH halves of the 30-day hole, so nobody "fixes" one and breaks the other:** a demoted admin **still passes `AdminPolicy`** (the cookie claim is fixed at login, 30-day sliding) **but is rejected by `/api/ops/*`** (they additionally check the DB-fresh `ctx.IsAdmin`).

### P3d — `GET` + `PUT /api/settings/{key}` — **NEW**
**No route touches `ISettingsRepository` today**, so SET-02 (`chua_log_n_days`, default 3) **cannot be read or written from the web at all** — and **Reports reads the same key server-side**. `GET` open; `PUT` admin-only.

### P3e — `POST /api/standup/archive?date=` — **NEW**, admin-only → **Agent D's Settings/Ops panel**
DR-09 has **no API surface at all**. Calls `IStandupArchiveService.ExportWeekAsync(date)`. Returns the path, or 400 *"No standup data this week to archive."*
*(Rev. 1 added this route and gave it to nobody. **It belongs to Agent D**, not to Daily Report — Daily Report is a non-admin screen.)*

### 🔴 P3f — `GET /api/tasklist/export?year=&month=` — **NEW.** TL-09 had no route and rev. 1 added none.
`ITaskListArchiveService` is DI-registered (`Program.cs:97`) and **called by zero endpoints**.
🔴 **`ExportMonthAsync` writes to the SERVER'S DISK and returns a SERVER PATH — useless to a browser.** Use **`BuildMonthMarkdownAsync`** (content-only), which exists for exactly this, and return `Results.Text(markdown, "text/markdown")`. **Not in `includeTags`** — blob download, same as Excel.

- [ ] Baseline → tests → implement → contract rows → full gate → commit.
- [ ] `feat(api): annotate reports+export, add tasklist/export/admin/settings/archive routes (M9 P3)`

---

## Task P4 — Regenerate the client. ONCE.

<model>sonnet</model>

**Depends on P3.** **Files:** `ng-openapi-gen.json` (**one line**) · `src/app/api/**` (**generated**)

```json
"includeTags": ["Auth","Timesheet","SmartFill","Backlogs","Tasks","Users","PcaContacts",
                "Tags","Teams","Templates","Holidays","DefaultTasks","Standup","Reports","Ops","Me","TaskList"]
```
🔴 **`Export` is deliberately ABSENT** — those routes return binary/text, not JSON (P3a, P3f).
🔴 **`user-list-all` and `pca-contact-list-all` SHOULD NOW APPEAR** — that is the P2a decision, and it is correct. They are admin-only; the `adminGuard` (P6c) is the client-side enforcement.
🔴 **R1 in full: pin all three seams; prove the empty DB by grepping `no users yet, nothing to bootstrap`. No match → kill it and report BLOCKED.**
🔴 **`void` is fine where the route has no body (R3).** Verify each `void` against its route before accepting it.

- [ ] `feat(web): regenerate the client for every remaining screen (M9 P4)`

---

## Task P5 — `WorklogService`: **ADD every method. RETYPE NOTHING.**

<model>opus</model>

**Depends on P4.** **File:** `src/app/services/worklog.service.ts` **only**.

### 🔴 READ THIS FIRST. Rev. 1's scoping of this task could not compile.

The file's own header says why:
> *"🔴 STILL STUBS … **AND THEY ARE NOT DEAD CODE YOU MAY RETYPE IN PLACE.** Each is still CONSUMED by a screen that binds the VENDORED view model, under `strictTemplates` … **So changing a stub's return type does not 'wire up a screen' — it BREAKS THE BUILD of a component the current task is not allowed to touch.**"*

**The 20 stubs are at `:464-483`** *(verify by symbol — R9)*. Each is bound by a Phase-2 component that **does not exist yet**:
`getUsers()` ← `users.component.ts` · `getTaskCards()`/`getTags()` ← `task-list.component.ts` · `getDailyEntries()`/`getTeamBoard()` ← `daily-report.component.ts` · `getMetrics()`/`getMissing()`/`getWeekly()`/`getMonthly()`/`getDrilldown()` ← `reports.component.ts` · `getTemplates()`/`getContacts()`/`getTeams()`/`getHolidays()` ← `settings.component.ts`

**So: LEAVE ALL 20 STUBS EXACTLY AS THEY ARE.** Add new, differently-named, **DTO-typed** methods beside them. **Each Phase-2 agent deletes the stubs it orphans, when it rewrites its own component.** This is precisely the pattern that worked in M8.6/T5 (`getBacklogList()` beside the untouched `getBacklogs()`).

**Phase 1 must end GREEN. Retyping anything here guarantees it does not.**

### The methods to add
Task List (`getTaskListRows`, `saveBacklogEdit`, `setTaskStatus`, `setTaskExtended`, `getBacklogTags`, `setBacklogTags`, `getTaskTags`, `setTaskTags`, `continueBacklog`, `exportTaskListMonth`) · Daily Report (`getStandupEntries`, `getStandupBoard`, `addStandupEntry`, `deleteStandupEntry`, `reorderStandupEntry`, `quickImportStandup`, `addStandupIssue`, `updateStandupIssue`, `deleteStandupIssue`) · Reports (`getWeeklyReport`, `getMonthlyReport`, `getMissingLogs`, `exportExcel`) · Users/Settings (`getUsersAll`, `createUser`, `renameUser`, `setUserUsername`, `setUserActive`, `setUserAdmin`, `setUserPassword`, `getTagsAll`, `createTag`, `updateTag`, `deleteTag`, `getTeamsAll`, `createTeam`, `renameTeam`, `setTeamActive`, `getTeamMembers`, `setTeamMembers`, `getPcaContactsAll`, `createPcaContact`, `renamePcaContact`, `setPcaContactActive`, `getTemplatesAll`, `createTemplate`, `deleteTemplate`, `deleteTemplateByName`, `getHolidaysAll`, `upsertHoliday`, `deleteHoliday`, `getDefaultTasks`, `createDefaultTask`, `setDefaultTaskActive`, `syncDefaultTasks`, `getSetting`, `setSetting`, `runBackup`, `runExport`, `previewRetention`, `runRetention`, `archiveStandupWeek`) · `setActiveTeam`

🔴 **Reads on `http`. Writes on `mutatingHttp`** (`ConnectionIdHttpClient` — **the field is at `:96`**). It stamps `X-Connection-Id` so the server excludes you from your own SignalR echo. **A write on the READ client emits a BYTE-IDENTICAL request in a hub-less TestBed — undetectable by every test in the repo except a connected-hub one.** The spec says so three times. **Test each mutation in a connected-hub `describe`.**

🔴 **Excel and the Task List month export are HAND-WRITTEN blob downloads**, not generated functions: `this.http.get(url, { responseType: 'blob' })`. **Never an absolute URL** — `SameSite=Lax` dies.

🔴 **`getMetrics()`'s `// TODO: GET /api/reports/metrics` is WRONG. That route does not exist and does not need to** — all four stat cards are client-side arithmetic over the weekly response. **Leave the stub (Agent C deletes it) but fix the comment.**

- [ ] `npm run build` clean · `npm test` — **all 274 pass** *(they will: nothing was retyped)*.
- [ ] `feat(web): add every remaining service method (M9 P5)`

---

## Task P6 — The shared Angular pieces nobody owns

<model>opus</model>

**Depends on P5.** **Files:** **new** `components/team-filter/` · **new** `components/tag-picker/` · **new** `core/admin.guard.ts` · `core/realtime.service.ts` **+ `realtime.service.spec.ts`** · 🔴 **`pages/backlog/backlog.component.spec.ts`** · 🔴 **`pages/log-work/log-work.component.spec.ts`** · `app.routes.ts` · `components/sidebar/`

### 🔴 P6a — `team-filter`. And rev. 1 pinned the wrong source.
**`ShowFilter => AvailableTeams.Count > 1`** — **NOT `memberTeamIds`.** `SettingsEndpoints.cs:109-110` spells out the trap:
> *"`ctx.MemberTeamIds` = every `UserTeams` row, with **NO `is_active` filter**. `AvailableTeams` = `GetActiveAsync()` ∩ memberships — **strictly NARROWER**."*

**`MeResponse` hands the client the WIDER set.** Pin it as `memberTeamIds` and the filter **lists a deactivated team that WPF hides.**
**→ The client computes `GET /api/teams` (active) ∩ `me.memberTeamIds`.** That intersection **is** `AvailableTeams`.

The rest of the contract (`TeamFilterViewModel.cs:57-102`):
- `checkedTeamIds: number[]` — 🔴 **an empty array means NO TEAMS, NEVER "all".** A test must fail if anyone treats `[]` as unfiltered.
- Default = **the active team only** · header `Teams (N)` · on active-team change, **reset** to `{new active team}`
- Wire format `?teamIds=1&teamIds=2` — **a repeated key.** That is what `EffectiveTeamIds` parses.

### P6b — `tag-picker`
Checkable chips (icon + colour + text), type-to-filter, **replace-all on save**.
🔴 **`BacklogTags` has NO `row_version` of its own — the version rides the PARENT.** `SetTagsCheckedAsync` checks and bumps the **backlog's** version, so a tag write **can 409** on a stale backlog version.

### P6c — `admin.guard.ts`
`/users` and `/settings` sit behind **`authGuard` only**. **`AuthService.currentUser().isAdmin` exists and is read by NOTHING** in production. Without a guard a non-admin navigates in and **every call 403s: the screen looks broken rather than hidden.**
**The sidebar already separates `admin: NavItem[]`** — hide it.
🔴 **This is the client-side half of P2a's replaced guard.** It is now load-bearing security. **Test it.**

### 🔴 P6d — `realtime.service.ts`: make the feed say WHAT changed
```typescript
hub.on(DATA_CHANGED, () => this._dataChanged.next());   // the server sends (kind, teamId). BOTH DISCARDED.
```
`dataChanged` is `Observable<void>`, so **no screen can filter what changed.** Make it `Observable<{ kind: string; teamId: number }>`.

🔴 **The PRODUCTION consumers survive** — `backlog.component.ts` and `log-work.component.ts` both `.subscribe(() => this.refresh.next())`, and a zero-arg callback is assignable to `(v: T) => void`.
🔴 **The SPEC STUBS DO NOT.** `backlog.component.spec.ts:96-99` and `log-work.component.spec.ts:169-172` declare `dataChanged: dataChanged.asObservable() as Observable<void>` over a `Subject<void>` → **TS2322**, and **`ng test` type-checks specs.** **Both spec files are in your file list. Fix them.** *(Rev. 1 said "their tests move with it" and did not own the files.)*

- [ ] `feat(web): team filter, tag picker, admin guard, and a realtime feed that says WHAT changed (M9 P6)`

---

# PHASE 2 — five agents. TRUE parallel. Zero file overlap.

🔴 **`src/app/models/worklog.models.ts` is a SHARED FILE, and `Tag` is imported by Agent A AND Agent D.**

**The rule that makes them disjoint: BIND THE GENERATED DTOs (`api/models`) DIRECTLY. Do not rewrite the vendored view models.** That is what Log Work and Backlog already do.

- **Delete only the `worklog.models.ts` imports YOUR component uses**, and only if **no other component imports the same symbol.**
- 🔴 **`Tag` is used by BOTH `task-list.component.ts` and `settings.component.ts`. NEITHER of you deletes it.** Phase 3 does.
- **Delete the `worklog.service.ts` stubs YOUR component orphaned** — and only those.

| Agent | Owns | Model |
|---|---|---|
| **A — Task List** | `pages/task-list/**` | `opus` |
| **B — Daily Report** | `pages/daily-report/**` | `opus` |
| **C — Reports** | `pages/reports/**` | `sonnet` |
| **D — Users + Settings** | `pages/users/**` + `pages/settings/**` | `opus` |
| **E — Backlog editor debt** | `pages/backlog/backlog-editor.component.*` | `sonnet` |

**Load-bearing rules, per agent** *(full detail in spec §5)*:

- **A:** `isDone` needs **> 0 tasks** · **at most ONE** system chip · progress is on the **BACKLOG**, not the task *(the mock's `ProgressMap`/`overall()`/`doneCount()` are invented — delete them)* · **a deadline change REQUIRES a reason note**; start/end do not · 🔴 **R7 — `CommitBacklogEditAsync` writes ALL FOUR of Type/Assignee/PCA/Progress whenever ONE changes. Round-trip the other three from the DTO.** · Gantt: axis = **working days only**; `BarEnd = DeadlineInternal ?? EndDate`; `BarStart = StartDate ?? BarEnd` · **Export month is a blob download.**
- **B:** **add + delete only — NO edit-entry affordance.** `StandupEntries` has **no `row_version`** (by design); two browser tabs are the same user, and WPF was single-process. **Adding an edit button creates a race the desktop never had.** · **Issues ARE exempt from the lock and the owner gate — do not "fix" that** · the picker is **active-team-only** · `DEFAULT` excluded, client-side.
- **C:** **no charts** · the tree is **`TeamNode[]` — an ARRAY of roots, FIVE heterogeneous levels, NO node ids (synthesise them)** · **no `metrics` route** — compute the four cards client-side from the weekly response · **port the two team-scoping quirks faithfully, do not "fix" them** · the project filter is missing `PlusArcs` · **the tree header label is stale in BOTH apps** (it is Team → Project → Backlog → Task → Date).
- **D:** 🔴 **A created user has `username = null` and NO password — THEY CANNOT LOG IN.** The screen must do **all three**: create → set username → set password. **Otherwise it makes ghosts.** · **Tags are HARD-deleted; PCA contacts are SOFT-deleted** · **the five infrastructure sections are DROPPED** (spec §2.1); keep the four `/api/ops/*` buttons **plus the standup-archive button (P3e)** · 🔴 **`GET /api/default-tasks` is ACTIVE-ONLY and `IDefaultTaskRepository` has no `GetAllAsync`** — you can deactivate a default task and **never see it again to re-activate.** **WPF has the identical hole — this is parity, not a regression. Do not build a toggle you cannot round-trip; make it a delete-style action, or say plainly that it is one-way.**
- **E:** TagPicker + template applier on the Backlog editor. **Templates auto-apply on select and APPEND on a different pick.**

**Gate for every Phase-2 agent:** `npm test` — **all existing pass**, plus yours (R8). `npm run build` clean. **R4: mutation-check your load-bearing tests.**

---

# PHASE 3 — cleanup

## Task P7 — delete what Phase 2 orphaned

<model>sonnet</model>

**Files:** `models/worklog.models.ts` · `services/worklog.service.ts` · any orphaned spec.

Every vendored view model and every `of([])` stub that no component imports any more. **`Tag` and `TaskTemplate` are the ones no Phase-2 agent could delete** (shared A↔D). Verify with `grep` before each deletion; **an unused export is not the same as an unimported one.**

- [ ] `refactor(web): delete the vendored view models and stubs that nothing binds any more (M9 P7)`

---

## Final gate

```
# nothing on 5080 (R2)
dotnet test src/TimesheetApp.sln --nologo    # BOTH assemblies
cd src/timesheet-web && npm test -- --watch=false && npm run build
```
**1142 → target ≈ 1450.** Zero warnings.

## Manual checks — no test catches these

- **OT-20** — Task List: change **only** the progress % on a card. Reload. 🔴 **Type, Assignee and PCA must all survive.** *(R7, third occurrence.)*
- **OT-21** — Task List: a backlog past its internal deadline shows **⚠ Late**; one behind pace within 2 working days shows **⚠ At risk**. Toggle to **Gantt** — the bars must **skip weekends and holidays**.
- **OT-22** — Users: create a user → set username → set password → **log in as them.** *(If any step is missing, you made a ghost.)*
- **OT-23** — **Log in as a NON-admin.** `/users` and `/settings` must be **hidden from the sidebar AND unreachable by URL.**
- **OT-24** — Settings: create a **new team**. It must get a **`DEFAULT` backlog** — check Log Work shows its default tasks. *(Today it does not.)*
- **OT-25** — Daily Report: yesterday and today are editable; **the day before yesterday is not.**
