# M9 — The four remaining screens: Task List · Daily Report · Reports · Users + Settings

**Date:** 2026-07-13
**Status:** approved
**Base:** `main` @ `28fd406` — M8.6 merged. **1142 tests green** (658 Core/WPF + 210 API + 274 Angular).
**Branch:** `feature/m9-remaining-screens-2026-07-13`

**Recon (read these before planning anything):**
- `.planning/research/M8.7-M8.10-recon.md`
- `.planning/research/M8.8-daily-report-recon.md`

---

## 1. Why this is ONE milestone, not four

Five screens are still the vendored design's shells. **Task List**'s progress edits are silently swallowed (`saveProgress()` → `of(void 0)`). **Daily Report** has a **hardcoded date** (`'7/12/2026'`) and its ◀▶ buttons have **no handler at all**. **Users**' `addUser()` is *literally* `this.toast.show('User added')` — it does not even read the name. **Settings**' calendar is **hardcoded to July 2026** and every input is a `value="3"` literal.

They cannot be done as four independent milestones, **and they cannot be fanned out naively**, because **five files are needed by every one of them and owned by none**:

| File | Who needs it |
|---|---|
| 🔴🔴 **`Api/Endpoints/SettingsEndpoints.cs`** — **49 routes, ONE file, 4 annotated** | Users · Settings · Task List (tags, holidays) · Daily Report (**all 10 standup routes**) · Reports (teams) · Backlog |
| **`ng-openapi-gen.json`** `includeTags` | **one line**, every screen |
| **`ApiTests/OpenApiContractTests.cs`** | same three `[Theory]` blocks |
| **`app/services/worklog.service.ts`** | **one contiguous stub block** |
| **`app/api/**`** (generated) | **a regen is a GLOBAL event** — two agents regenerating in parallel, and the second silently clobbers the first |

This is exactly the failure that cost M8.2 an entire consolidation wave, recorded verbatim:

> *"A file that N parallel agents need is a file the controller writes FIRST. `Models/Entities.cs` was not — four agents each needed it, none owned it, so the whole mechanism was unreachable."*

And M8.2/W3 is the sharper warning: **four parallel agents produced ZERO merge conflicts and TWO INCOMPATIBLE APIs.** Zero conflicts, and still wrong.

**→ PHASE 1 (controller, sequential, single owner) writes every shared file. PHASE 2 fans out on disjoint page directories.**

---

## 2. The user's decisions (2026-07-13)

| | Decision |
|---|---|
| **Gantt** | **BUILD IT.** Move `BuildGantt` from the WPF ViewModel into Core first. |
| **Settings infrastructure config** | **DROP from the web entirely.** Keep the four `/api/ops/*` action buttons. |
| **Tags + Templates** | **PULL IN.** Also repays M8.6's deferred TagPicker debt on the Backlog editor. |
| **Admin promote/demote** | **ADD IT.** |
| **Dark mode** | **`localStorage`.** No schema change. |

### 2.1 The five Settings sections that are DROPPED, and why

From `SettingsEndpoints.cs:45-47`, verbatim:
> *"Never call any `IAppConfig.Set*` from an endpoint. It is a process-wide singleton with ten setters; on a server every one of them is **cross-user state** — one user toggling dark mode flips it for everyone, and `SetDbPath` **repoints the whole server's database**."*

**Dropped:** DB path (SET-01) · archive path (SET-05) · both export roots (EX-01) · backup folder/auto/keep-count (BK-01) · retention enable + months (RT-01). **They belong in `appsettings.json` on the host machine.**

**Kept:** the four `/api/ops/*` actions — run backup, run export, preview retention, run retention. **Admin-only. They already exist.**

🔴 **Dark mode is NOT in this list.** It is a **per-user UI preference**, and `ThemeService` **already persists it to `localStorage` correctly** (plus an accent picker WPF does not have). WPF put it in `IAppConfig` because WPF runs *on your machine*, where "my setting" and "the app's setting" are the same thing. The web separates them, and separates them correctly. **Do not touch it.**

### 2.2 Admin promote/demote — and the 30-day hole it does not close

**The entire codebase contains exactly ONE statement that writes `is_admin`**: the v10 migration (`DatabaseInitializer.cs:337` — `UPDATE Users SET is_admin = 1 WHERE id = (SELECT MIN(id) FROM Users)`). There is **no `SetIsAdminAsync` anywhere in Core.** So there is exactly one admin, forever, unless somebody edits the `.db` by hand.

**Add:** `IUserRepository.SetIsAdminAsync` (Core, checked) + `PUT /api/users/{id}/admin` + a toggle on the Users screen.

🔴 **State this in the UI, because it is a real security property:** the `AdminPolicy` gates on a **cookie claim written once at login**, into a **30-day sliding** cookie. **A demoted admin keeps admin for up to 30 days** on 45 of the 49 routes. Only the **four `/api/ops/*` routes** additionally check the DB-fresh `ctx.IsAdmin` and reject immediately. **Demoting someone does not lock them out today.** *(Closing that properly means a token-version claim or a shorter cookie — out of scope, and recorded as an open risk.)*

---

## 3. PHASE 1 — the shared spine. Sequential. One owner. Nothing fans out until this is merged.

### 3.1 Core (`TimesheetApp.Core`) — the only Core changes in this milestone

**a) Move `BuildGantt` from WPF into Core.**
It is `internal static` at `TaskListViewModel.cs:314-376` — **a pure function in the wrong project.** It already depends only on `WorkingDayCalculator` (which is in Core). This is a **move**, not a rewrite.
🔴 **`GanttModelTests.cs:23` calls `TaskListViewModel.BuildGantt(...)` directly.** The test file moves with it. **The 658 Core/WPF baseline will move — that is correct and expected here**, because the tests relocate. *(This is the "do not edit a test to reconcile a gate" exception, third time: the contract genuinely moved.)*

**b) `IUserRepository.SetIsAdminAsync(int userId, bool isAdmin, long expectedVersion)`** — checked (`Users` has `row_version`).

**c) A Task List read model.** `ITaskListService.GetRowsAsync(year, month, teamIds)` → the projection WPF's `TaskListViewModel.LoadAsync` performs client-side today.
🔴 **This is the ONLY way.** `GetLoggedHoursByBacklogAsync()` is **called by no endpoint** (`grep` confirms: only `TaskListViewModel` and `TaskListArchiveService`), so **logged-hours-per-backlog is unobtainable by any route** — and without it a client **cannot compute `ScheduleState`**. `IScheduleStateService` is DI-registered in the API (`Program.cs:88`) and **called by nothing.** The maths is pure, in Core, and already wired. **Only the projection is missing.**

The service assembles, per backlog: the batched task map · logged hours · `estimate = official ?? rough` · `isDone = tasks.Count > 0 && tasks.All(Status == "Done")` (**zero tasks is NOT done**) · `ScheduleState` · tag ids · the Gantt model. **All of it in one round-trip**, so the client cannot see three different snapshots.

### 3.2 API — annotate EVERYTHING, in one pass, by one owner

**`SettingsEndpoints.cs` — all 49 routes.** 4 are annotated (M8.6). The other 45 are not.

🔴 **`SettingsUserStandup`, `SettingsStandupEntryView` and `SettingsOpsResult` are `internal sealed record`** (`:837-843`). **They must become `public` before `.Produces<T>()` will compile.**

🔴 **Check `.RequireAuthorization(AuthSetup.AdminPolicy)` on EVERY route before tagging it.** An annotated-but-admin-gated route reaching a non-admin client is **a correctly-typed client that 403s** — a class of lie the `.Produces<T>()` discipline does not catch on its own, and the one that BLOCKed M8.6's plan. **Tag admin routes too** (the Users/Settings screens are admin screens), **but the Angular `adminGuard` must hide those screens**, and a screen that a non-admin *can* reach must never call an admin route.

**`TimesheetEndpoints.cs` — the 5 Reports/Export routes.** All exist, all work, **none is annotated** — every handler ends bare at `});`. **Zero routes to invent.**
🔴 `GET /api/export/excel` returns `Results.File` (binary) and `/markdown` returns `Results.Text`. **Neither is JSON.** `ng-openapi-gen` cannot type them usefully → **hand-write a blob download in `WorklogService`**, and do **not** add them to `includeTags`.

**New routes:**

| Route | Why |
|---|---|
| `GET /api/tasklist?year=&month=&teamIds=` | the read model (§3.1c). **Task List has none today.** |
| `PUT /api/users/{id}/admin` | §2.2 |
| `GET` + `PUT /api/settings/{key}` | **SET-02** (`chua_log_n_days`). **No route touches `ISettingsRepository` today**, so the "not-logged warning window" cannot be read or written from the web at all — and **Reports reads the same key**. |
| `POST /api/standup/archive?date=` | **DR-09.** The weekly markdown archive has **no API surface at all**, and `Program.cs` never calls `BackfillMissingWeeksAsync` — it is WPF-only. |
| `GET /api/teams` *(annotate)* | Reports' team filter needs team **names**; `/api/me` returns **ids only**. **Owned by nobody until now.** |

**Bug fix:** `POST /api/teams` **skips the TM-04 bootstrap.** WPF calls `EnsureDefaultBacklogIdAsync(newId)` + `SyncAsync()` (`SettingsViewModel.cs:566-569`); the route calls **neither** — so **a team created from the web gets no `DEFAULT` backlog and no default tasks.**

### 3.3 Regenerate the client — ONCE

🔴 **The API must NEVER run against the real database.** `Program.cs` defaults `DbPath` to the user's live data, and `Program.cs:34` uses `||`, so **`DbPath` without `ConfigPath` silently falls back to the fully-default production config.** Pin **all three** seams (`DbPath`, `ConfigPath`, `KeyRingPath`) to a temp dir, and **prove it** by grepping the startup log for the substring **`no users yet, nothing to bootstrap`**. *(Grep the substring. The line reads "**Admin bootstrap:** …" — space, lowercase `b`. `AdminBootstrap` is only the logger category.)*

🔴 **`void` is not a red flag.** Seven generated functions are legitimately `void` — their routes declare 204 via the **non-generic** `.Produces(StatusCodes.Status204NoContent)` overload. **The invariant is: `void` only where the route has no body.**

### 3.4 `WorklogService` — every method, once

🔴 **The stub block is one contiguous region.** Every screen rewrites part of it. **One owner writes all of it.**

🔴 **Reads on `http`. Writes on `mutatingHttp`** (`ConnectionIdHttpClient` — stamps `X-Connection-Id` so the server excludes you from your own SignalR echo). **A write on the read client emits a BYTE-IDENTICAL request in a hub-less TestBed** — undetectable by every test in the repo except a connected-hub one. The spec file says so three times.

### 3.5 The shared Angular pieces nobody owns

**a) `components/team-filter/`** — **four screens need it** (Task List · Daily Report board · Reports · and Backlog when it gains one). **It does not exist**; `components/` holds exactly `shell/` and `sidebar/`.
🔴 Its contract, from `TeamFilterViewModel.cs:57-102`: `checkedTeamIds: number[]` where **an empty array means NO teams, NEVER "all"** · default = **the active team only** · hidden when `memberTeamIds.length <= 1` · header `Teams (N)` · on active-team change, **reset** to `{new active team}` · wire format `?teamIds=1&teamIds=2` (**repeated key** — that is what `EffectiveTeamIds` parses).

**b) `components/tag-picker/`** — Backlog editor + Task List. Checkable chips (icon + colour + text), type-to-filter, **replace-all on save**, and **the version rides the PARENT** (`BacklogTags` has no `row_version` of its own; `SetTagsCheckedAsync` checks and bumps the **backlog's**).

**c) `core/admin.guard.ts`** — `/users` and `/settings` sit behind **`authGuard` only** today. `AuthService.currentUser().isAdmin` **exists and is read by nothing.** Without a guard, a non-admin navigates in and **every call 403s — the screen looks broken rather than hidden.** The sidebar must hide the entries too.

**d) Fix `realtime.service.ts:63`.**
```typescript
hub.on(DATA_CHANGED, () => this._dataChanged.next());   // the server sends (kind, teamId). BOTH DISCARDED.
```
`dataChanged` is `Observable<void>`, so **no screen can filter what changed** — every change anywhere refreshes everything. WPF's ViewModels filter on specific `DataKind`s; the web cannot. **Fix it now: it gets more expensive with every screen that depends on the broken shape.**

---

## 4. PHASE 2 — five agents, TRUE parallel, zero file overlap

| Agent | Owns | Notes |
|---|---|---|
| **A** | `pages/task-list/` | **The biggest.** Cards + section bands (adaptive: team when >1 checked, else project) · chips · inline progress · status · deadlines **with a mandatory reason note** · Continue · **Gantt** · Export month |
| **B** | `pages/daily-report/` | **Add + delete only — NO edit-entry affordance** (§5.2) |
| **C** | `pages/reports/` | **Easiest.** Zero routes to invent, no charts |
| **D** | `pages/users/` + `pages/settings/` | Admin-only screens |
| **E** | `pages/backlog/backlog-editor.component.*` | **M8.6's debt:** TagPicker + template applier |

**Zero overlap.** Every shared file was written in Phase 1.

---

## 5. Per-screen rules that must not be got wrong

### 5.1 Task List

- **`isDone` = `tasks.Count > 0 && tasks.All(Status == "Done")`.** **Zero tasks is NOT done.**
- **`ScheduleState` precedence** (`ScheduleStateService.cs:9-45`): done → Normal · no internal deadline → Normal · **past deadline → `Late` (wins)** · no start date → Normal · no estimate → Normal · **working days to deadline > 2 → Normal** · then `behind = logged × total < elapsed × estimate` → `Warning`.
- **At most ONE system chip** (`Late` **or** `At risk` — `if/else if`), then custom tags **ordered by `Tag.Id`**.
- **Progress is on the BACKLOG** (`Backlog.ProgressPercent`), **not the task.** *(The Angular mock keys it per-task and invents `overall()`/`doneCount()` arithmetic with no WPF counterpart. Delete that.)*
- 🔴 **A deadline change REQUIRES a reason note.** Start/End do not. Cancel reverts the picker.
- 🔴 **Every WPF write here is UNCHECKED; the API exposes only `*CheckedAsync`.** Every web write needs an `expectedVersion`, and can 409.
- 🔴 **`CommitBacklogEditAsync` writes ALL FOUR fields** (Type, Assignee, PCA, Progress) whenever **any one** changes. The web must round-trip the other three from the DTO — **the whole-record trap, third time.**
- **Grid ⇄ Gantt.** `BarEnd = DeadlineInternal ?? EndDate`; `BarStart = StartDate ?? BarEnd`. Axis = **working days only** (weekends + holidays excluded). No dates at all → `startIdx=0, span=0`. Colour: `Late`→danger, `Warning`→amber, else accent. No start date → a faint dashed placeholder across the plot. External deadline → a red marker + dashed line.

### 5.2 Daily Report

- 🔴 **`StandupEntries` has NO `row_version`** — verified three ways. Entries are **unversioned last-write-wins BY DESIGN**; the guard is the owner gate + edit-lock, and the argument is that **two users can never write the same row.**
  **But two browser tabs are the same user, and WPF was single-process.** So: **add + delete only. NO edit-entry affordance** — WPF has none either, and adding one creates a race the desktop never had.
- **The DR-06 edit-lock IS server-side** (`StandupService.CanEditDay`, gating all five entry writes; the API re-checks it to turn silent no-ops into honest 400s). **A web client cannot bypass it.**
- **Issues are DELIBERATELY EXEMPT** from the lock and the owner gate — *"anyone, anytime"* (DR-06). **Do not "fix" this.** Issues **do** have `row_version` and a real 409 path.
- 🔴 **On a day with ZERO entries there is no `Editable` flag in the response** — the lock has no representation. Recompute `today`/`yesterday` client-side **only for enabling the Add button**, and let the server's 400 be the authority.
- **The backlog picker is scoped to the ACTIVE team only** (`SearchBacklogsAsync` passes `new[] { ActiveTeamId }`) — **not** the member-team set. *The board is multi-team; the picker is not.*
- **`DEFAULT` is excluded from the picker, client-side.**

### 5.3 Reports

- **No charts.** Four stat cards, two tables, one **five-level** tree (Team → Project → Backlog → Task → Date). **The Angular tree widget is already correct** — keep it; only the data mapping changes. `TreeNode` is single-root and homogeneous; the API returns **`TeamNode[]`** — an *array* of roots, **five heterogeneous levels**, **no node ids** (synthesise them).
- **`GET /api/reports/metrics` does not exist and does not need to.** The `// TODO` at `worklog.service.ts:315` is **wrong — delete it.** All four stat cards are client-side arithmetic over the weekly response.
- **The team-scoping trap is already defused and proven** by two passing tests. `EffectiveTeamIds` reads the raw query collection, defaults to `ctx.MemberTeamIds`, intersects, and **never returns `null`**. **Do not "simplify" it.**
- **Two behavioural facts to port faithfully, not "fix":** `/api/reports/missing-logs` is **`ActiveTeamId`-scoped**, so the banner **ignores the team filter** (in WPF too); and `[FromQuery] int? userId` is **unvalidated**, so you can read any user's hours **within your own teams** — which matches WPF, whose target dropdown lists **all active users company-wide**.
- **The project filter is missing `PlusArcs`** in the mock. Drive it from `BacklogProjects.All`.
- **The tree header label is stale in BOTH apps** — it says *"Project → Backlog → Task → Date"*; the tree is **Team →** Project → Backlog → Task → Date. Fix the label.

### 5.4 Users + Settings

- 🔴 **A user created by `POST /api/users` has `username = null` and no password hash — they CANNOT LOG IN.** The Users screen must do **all three**: create → set username → set password (`POST /api/auth/users/{id}/set-password`). **Otherwise it creates ghosts.**
- **Deactivate is soft** (TimeLogs preserved). The web **also offers Activate** — WPF does not, but `PUT /api/users/{id}/active` takes a bool, so it is free and correct.
- **Tags are HARD-deleted** (cascades `BacklogTags` in one transaction). **PCA contacts are SOFT-deleted** ("historical backlogs keep the reference"). Do not confuse them.
- **A template edit = `DELETE ?templateName=old` then N × `POST`.** **Not transactional across calls.** Surface a failure honestly.
- **`RestoreAsync` is deliberately NOT exposed** — *"it overwrites the live `.db` in place while the API holds open connections, which corrupts live readers."* **Keep it that way.** There is also no backup-list route, so **the web cannot offer a restore even if one existed.**
- **Holidays:** 42-cell (6×7) grid, **Monday-first** (`offset = ((int)first.DayOfWeek + 6) % 7`). Click toggles upsert/delete. *(The mock is hardcoded to July 2026 and blocks weekends; WPF allows clicking a weekend.)*
- **`POST /api/default-tasks` and `PUT /api/default-tasks/{id}/active` already call `SyncAsync()`** as a side-effect. There is **no standalone sync route**; WPF's "Sync default tasks" button has no equivalent. **Add one** (`POST /api/default-tasks/sync`) — it is one line and the button exists.

---

## 6. Testing

Beyond each screen's own tests, the ones that would otherwise ship silently:

- 🔴 **Task List's whole-record write.** Change **only** the progress; assert `Type`, `AssigneeUserId` and `PcaContactId` **all survive** in the request. *(Same trap as M8.6, third occurrence.)*
- 🔴 **`GET /api/tasklist` returns a `ScheduleState` a client could not compute.** Seed a backlog past its internal deadline → `Late`. Seed one behind pace inside the 2-working-day window → `Warning`. **A client-side reimplementation would silently disagree; that is the point of the read model.**
- 🔴 **A team created via `POST /api/teams` gets a `DEFAULT` backlog** — assert it exists afterwards. *(Today it does not.)*
- 🔴 **`/api/users/names` is readable by a NON-admin** — and `ApiFactory.SeedUserAsync(…, isAdmin)` lets you seed an admin and go green. **Seed a non-admin deliberately.**
- **The team filter's empty list means NO teams, never "all"** — a test that fails if someone "helpfully" treats `[]` as unfiltered.
- **A demoted admin still passes `AdminPolicy`** (the 30-day cookie) **but is rejected by `/api/ops/*`** (DB-fresh). Pin both halves so nobody "fixes" one and breaks the other.
- **`adminGuard` blocks a non-admin from `/users` and `/settings`**, and the sidebar hides them.

---

## 7. Out of scope

- **Restoring a backup**, and listing backups. Deliberate (§5.4).
- **The five infrastructure Settings sections.** They live in `appsettings.json` on the host (§2.1).
- **Closing the 30-day demoted-admin window.** Recorded as an open risk (§2.2).
- **Deleting the WPF project** — that is M10.
- **Deployment.** Still the standing blocker: **nobody but the user can reach this app**, and no amount of screen-wiring changes that.
