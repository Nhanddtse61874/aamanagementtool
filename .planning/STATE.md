# Project State

## Current Position

**Phase:** Step 6 — Plan (M9; spec + plan written, **Plan Checker in flight**)
**Status:** in_progress
**Last updated:** 2026-07-13

## ▶ RESUME HERE

**Branch:** `feature/m9-remaining-screens-2026-07-13` @ `eff19a2`. Base `main` @ `28fd406` (M8.6 merged).
**Suite:** **1142 green** — 868 .NET (658 Core/WPF + 210 API) + 274 Angular. 0 warnings.
**The app is RUNNING** — API :5080 (**real DB**), `ng serve` :4200. **Kill the API before any .NET gate** (it locks `Core.dll`).

### 🔴 M9 — the FOUR remaining screens, as ONE milestone. Mode A.

**Spec:** `docs/superpowers/specs/2026-07-13-m9-remaining-screens-design.md`
**Plan:** `docs/superpowers/plans/2026-07-13-M9-remaining-screens.md`
**Recon (the design brief):** `.planning/research/M8.7-M8.10-recon.md` · `M8.8-daily-report-recon.md`

**The user said: "tự chạy cho xong, tôi sẽ test sau khi chạy xong toàn bộ."** Full autonomy. **They test only at the very end.**

**Their five decisions (2026-07-13):**
| | |
|---|---|
| **Gantt** | **BUILD IT.** *(My objection evaporated: the only reason to defer was to get Task List into their hands sooner, and they will not test until everything is done.)* |
| **Settings infra config** | **DROP from the web.** Five sections → `appsettings.json` on the host. Keep the four `/api/ops/*` buttons. |
| **Dark mode** | **`localStorage`, unchanged.** 🔴 **It is NOT one of the dropped sections** — it is a per-user preference and `ThemeService` already does it correctly. I wrongly lumped it in; the user caught it. |
| **Tags + Templates** | **PULL IN.** Also repays M8.6's deferred TagPicker debt. |
| **Admin promote/demote** | **ADD.** The entire codebase has **exactly one** statement that writes `is_admin`: the v10 migration. One admin, forever, unless someone edits the `.db` by hand. |

**Why ONE milestone, not four:** five files are needed by every screen and owned by none. **`SettingsEndpoints.cs` alone is 49 routes in ONE file (4 annotated)** serving Users + Settings + Task List + Daily Report + Reports + Backlog — **all 10 standup routes live there.** Plus `includeTags` (one line), `OpenApiContractTests.cs`, the single contiguous stub block in `worklog.service.ts`, and the generated `api/**` tree, where **a regen is a GLOBAL event**.

**PHASE 1 (sequential, one owner):** P1 Core (move `BuildGantt` out of the WPF ViewModel · `SetIsAdminAsync` · **the TaskList read model, which does not exist**) → P2 annotate all 49 settings routes + fix the teamless team bootstrap → P3 reports/export + 4 new routes → P4 regenerate ONCE → P5 every service method ONCE → P6 the shared Angular nobody owns (team-filter · tag-picker · adminGuard · **a realtime feed that finally says WHAT changed**).
**PHASE 2 (five agents, TRUE parallel):** `task-list` | `daily-report` | `reports` | `users`+`settings` | the Backlog editor's TagPicker debt.

**Good news on the Gantt:** `BuildGantt` is already **`internal static`** — a **pure function in the wrong project**. Moving it to Core is a **move, not a rewrite**, and `GanttModelTests` calls it directly so its tests come along.

**🔴 R7 — THE WHOLE-RECORD TRAP, THIRD MILESTONE RUNNING.** It has already nulled a `team_id` (M8.3 — a backlog invisible to everyone, forever) and six Backlog fields (M8.6). **It is waiting in Task List's `CommitBacklogEditAsync`, which writes ALL FOUR of Type/Assignee/PCA/Progress whenever any ONE changes.** TypeScript cannot catch it — the generated DTOs are all-optional, so **a dropped field COMPILES**.

### 🔴 R8 — "Don't edit a test to reconcile a gate" HAS AN EXCEPTION, and I got it wrong FOUR times in M8.6

The rule stops you deleting a test that caught a real regression. **It does not apply when you deliberately changed the contract the test was pinning.** Every time, the implementing agent proved it rather than arguing:

| Gate I wrote | Why it was wrong |
|---|---|
| T0: *"165 + yours, 0 failures"* | The task stops `onTrash` from writing; **five tests assert that it writes.** Rewritten. |
| T1: *"the 172 is a fixed baseline"* | Two tests deserialised the **old DTO**, and `System.Text.Json` **binds by NAME and defaults the missing** — they'd stay **green while asserting nothing.** The 172 wouldn't move. **That was the problem.** |
| T5: *"232 unchanged"* | `ConnectionIdHttpClient` only stamps its header **when a hub is connected** — so a write on the **wrong client** is **byte-identical** in a hub-less TestBed and **undetectable by every test in the repo.** |
| T7: *"`npm run build` clean"* | `ng build` **exits 0** with `{{ noSuchSymbol() }}` in a component **nobody imports yet** — AOT never type-checks it. |

**A gate that cannot move is a gate that cannot notice.**

---

### M8.6 — COMPLETE, merged to `main` (`90151d4`). 1142 green.

### M8.6 — the Backlog screen. COMPLETE. Mode A. Eight tasks.

| | | |
|---|---|---|
| **T0** confirm before drag-to-trash delete | ✅ `6bd0ecb` | 165 → **169** Angular |
| **T1** annotate list+create · `BacklogListItemDto`+TaskCount · **new** `GET /{id}/audit` · refuse the teamless create | ✅ `d973265` | 172 → **185** API |
| **T2** annotate `GET /api/tasks` + `PUT /api/tasks/{id}` · `POST /api/tasks` 400 · **2 new NON-ADMIN `/names` routes** | ✅ `6780790` | → **210** API |
| **T3** regenerate the client | ✅ `315d319` | 9 fns, 7 models. **Real DB byte-identical, proven.** |
| **T4** the pure functions — **both data-loss traps** | ✅ `3d37afb` | → **219** Angular |
| **T5** `WorklogService` — add 9, retype nothing | ✅ `eaef351` | → **232** Angular |
| **T7** the editor — create, edit, audit history | ✅ `57c0b90` | → **255** Angular |
| **T6** the list — **both toasts die here** | ✅ `ee54b02` | → **274** Angular |

**🔴 OPEN — the user has not clicked anything yet.** UAT: **OT-15 … OT-19** (below), plus M8.5's **OT-13 / OT-14**.

### The next milestone is NOT M8.7 alone — it is M8.7–M8.10 with a shared PHASE 1

**`.planning/research/M8.7-M8.10-recon.md`** and **`M8.8-daily-report-recon.md`** are the design briefs. Read them before planning anything.

**FIVE files are needed by every remaining screen and owned by none** — the exact failure that cost M8.2 an entire consolidation wave:
- 🔴🔴 **`SettingsEndpoints.cs` — 47 routes, ONE file, ZERO annotated**, needed by Users + Settings + Task List + Daily Report + Reports + Backlog. **All 10 standup routes live there.**
- `ng-openapi-gen.json`'s `includeTags` — **one line, N writers**
- `OpenApiContractTests.cs` — same three `[Theory]` blocks
- `worklog.service.ts` — **one contiguous stub block** every screen rewrites
- **`src/app/api/**` — a regen is a GLOBAL event.** Two agents regenerating in parallel: the second clobbers the first.

**So: PHASE 1 (controller, sequential) annotates EVERYTHING, regenerates ONCE, writes every service method ONCE, and builds the two shared things nobody owns — a team-filter component (FOUR screens need it; it does not exist in Angular) and `GET /api/teams` (Reports needs team NAMES; `/api/me` gives ids only). THEN four agents own four page directories in true parallel.**

**Also fix in Phase 1:** `realtime.service.ts:63` **discards the `(kind, teamId)` the server sends**, so **no screen can filter what it re-fetches.** It gets more expensive to fix with every screen that depends on the broken shape.

**Spec:** `docs/superpowers/specs/2026-07-13-backlog-screen-design.md` (`a9fb870`)
**Plan:** `docs/superpowers/plans/2026-07-13-M8.6-backlog-screen.md` (`a7c86fc`)

**Graph:** `T0 ∥ (T1 → T2 → T3 → T4 → T5 → {T6 ∥ T7})`

**Scope B** (user's choice): list + create + edit + assignee + PCA + task sub-editor + audit history. **Tags and templates deferred to their own slice.** No TEAM column (consistent with Log Work).

**The user approved fixing all six WPF bugs** — including the one the XAML comment *denies exists* (DEFAULT is visible AND editable on the Backlog list; its project isn't in the dropdown, so open+save writes `project = ''`).

**The two traps the whole plan is built around, both silent data loss:**
1. `toUpdateRequest` **must start from the loaded DTO, not the form.** `PUT` replaces the whole record and **six fields are hidden on edit**.
2. `PUT /api/tasks/{id}` writes `task_name` **AND `order_index` AND `status`** in one checked call. **The editor never shows status.** Build the request from the form → `status = NULL` → every `Todo`/`In-process`/`Done`/`Pending` wiped. The Task List is built on that column.

Trap 2 is also a gift: rename and reorder are **one** write, so the rename-before-reorder ordering hazard **cannot exist**.

**Also confirmed by the user:** a **confirm dialog** before drag-to-trash delete (T0, runs in parallel).

**M8.5 UAT:** drag & drop **confirmed working by the user**. OT-13/OT-14 (the two interaction checks) deferred — the user will do a full pass at the end.

### M8.5 — COMPLETE. All seven tasks merged.

| | | |
|---|---|---|
| 1 · annotate 5 C# routes | ✅ `0b3b091` | `.WithName` + `.WithTags` + `.Produces<T>()`. Metadata only. 815 → 830. |
| 2 · regenerate the TS client | ✅ `38431c1` | Tags `Backlogs` + `Tasks` added. |
| 3 · the two pure functions | ✅ `be80326` | `move-month.ts` · `reorder.ts`. 124 → 136. |
| 4 · **+ Add task** | ✅ `908a033` | → 137 |
| 5 · **Move to next month** | ✅ `ee10a29` | → 145 |
| 6 · **drag to reorder** | ✅ `3241eb5` | → 155 |
| 7 · **drag to trash** | ✅ `72cf0f4` (merge `ebfea32`) | → **165** |

🔴 **The user has the app OPEN right now.** An API on **:5080** (against their **real** database) and `ng serve` on **:4200**. Both are background processes of this session. **A `/compact` keeps them; a `/clear` kills them.** Admin password (printed once, already used): `tyxhHnPpVvygCXy_xEtFAnAW`.

### 🔴 The ONE thing still open on M8.5: the user has not clicked OT-13 / OT-14

Everything automated is green. These two are **interactions between the features in this one milestone**, and **no single-feature test catches either** — that is precisely why they need a human.

- **OT-13 — delete a task, THEN reorder, THEN reload.** The order must hold.
- **OT-14 — delete a task, THEN add a task, THEN reload.** The new one must be **last**.

Rev. 1 of the plan shipped the first as a silent corruption (`SetActiveAsync` leaves `order_index` alone, so a windowed reorder creates a **tie**, and `ORDER BY` with a tie is arbitrary). Rev. 2 shipped the second (`orderIndex = tasks.length` ties with a live row after any delete). **Both are fixed in code; both need a human to confirm.**

**3-second smoke test first:** pick up a row by its ⠿ grip — the trash box must turn **red** immediately, and DevTools must show **no** `could not find connected drop list with id "trash"`. If it stays amber, the CDK connection never resolved and nothing below it works.

### Known UX gap, flagged by Task 7's agent, not yet decided

**No undo, no confirmation. A mis-drop deletes a task instantly.** It *is* soft (`setTaskActive(id, true)` restores it, and that path is tested) — but **no screen calls it.** There is no restore UI. WPF has the identical hole. **The user has not yet been asked whether to add a confirm dialog.**

### After M8.5 — the user's chosen priority

**Wire the six remaining Angular screens, for real.** They are still the vendored design's shells: every button is a `toast.show(...)` that does nothing, and every list is empty because the service returns `of([])`. The user hit this by clicking *"+ New backlog"* and getting a toast and nothing else.

**The backend already serves all of them** — 80 routes. Each screen is now the same three steps, with **no architectural unknowns left**: **annotate its routes (`.WithName` + `.WithTags` + `.Produces<T>()`) → regenerate the client → wire the Angular.**

**M8.6 Backlog** → M8.7 Task List → M8.8 Daily Report → M8.9 Reports → M8.10 Users + Settings.

*(M8.5 already annotated the five Backlog/Task routes, so M8.6 inherits them.)*

---

**Next Action:** M8.6 — Backlog screen (brainstorm → spec → Mode Gate → plan → Plan Checker → execute), while the user clicks OT-13 / OT-14 on M8.5.

**Plan:** `docs/superpowers/plans/2026-07-13-M8.5-log-work-task-actions.md` (rev. 3, `25f5ee9`) — seven tasks, three sequential waves.

### M8 — SHIPPED. `main` has it.

**Branch:** `feature/m8.5-log-work-actions-2026-07-13`, cut from `main`.
**`main`:** M8.1 → M8.4 merged (`feature/m8-web-migration-2026-07-13`). 30 stale branches and 12 worktrees cleaned.
**Suite:** **939 passed, 0 failed, 0 warnings** — 658 `TimesheetApp.Tests` + 157 `TimesheetApp.ApiTests` + **124 Angular**. Was 548 at the start of M8.2.

| | | |
|---|---|---|
| **M8.1** | extract `TimesheetApp.Core` from the WPF project | 548 |
| **M8.2** | schema v10 · optimistic concurrency · the backup blocker · `ActiveTeamId` per-user | 628 |
| **M8.3** | ASP.NET Core API · cookie auth · **80 routes** · SignalR | 809 |
| **M8.4** | Angular: login, guarded shell, **Log Work working against the real API** | **939** |

**Waiting on the user:** ① click through the web app (`.planning/M8.4-UAT.md` — 🔴 **the admin password prints ONCE** on first API start) · ② click through the WPF app (M8.2's OT-8, which no test proves) · ③ **decide how the web app reaches anyone else's browser** — nobody but the user can use it until that is chosen.

### M8.5 — COMPLETE. The three Log Work controls M8.4/W4 removed are back, and real.

Spec: `docs/superpowers/specs/2026-07-13-log-work-task-actions-design.md` (`d97bd3e`).

`+ Add task`, `Move to next month`, and the drag-to-reorder / drag-to-trash zone. All three were **fake in the vendored design** (`toast.show('Task added')` with no handler), so the WPF app was the source of truth.

**The pattern M8.5 established, and the reason M8.6–M8.10 have no architectural unknowns left:** `BacklogEndpoints.cs` had **zero `.Produces<T>()`**, so OpenAPI described none of its routes and the generated TypeScript client **could not contain them**. Annotating the C# was Wave A, not optional cleanup. `OpenApiContractTests.cs` (15 cases) now **fails the build** if a route in the generated client's tag list lacks a schema, a tag, or an `operationId` — the invariant in `ng-openapi-gen.json` is no longer a comment.

**Every remaining screen is the same three steps: annotate its routes → regenerate the client → wire the Angular.**

### M8.3 — COMPLETE. The API exists, is authenticated, team-scoped, conflict-aware and live.

| Wave | | |
|---|---|---|
| **W0** — Core prerequisites | ✅ | Credential surface (auth had **nothing** to stand on: `password_hash`/`is_admin` were columns *nothing read*) + **the version-aware service layer M8.2 stopped one step short of**. 628 → 656. |
| **W1** — API shell + every shared contract | ✅ | Host boots with scope validation ON · scoped identity · `SqliteProfile.Server` · auth + Data Protection · **three** error channels · OpenAPI. 656 → 690. |
| **W2** — 4 endpoint agents in parallel | ✅ | **79 routes, zero collisions.** 690 → 795. |
| **W2.5** — honest fixtures | ✅ | **Mutation-tested: 5 kills, 0 false alarms.** 795 → 805. |
| **W3** — SignalR | ✅ | Group per team · rejoin on reconnect (**proven**: a forced reconnect yields a new `ConnectionId` and the message still arrives) · no self-echo. 805 → **809**. |

**79 API routes. 153 API tests.** OpenAPI is exposed — M8.4 generates its TypeScript client from it.

### The four traps M8.3 paid for (all found by an agent RUNNING it, none by reasoning)

1. **`[FromQuery] int[]?` never binds to `null`.** Minimal-API array binding turns *both* "key absent" and "key present but empty" into an **empty array**. So `teamIds is null ? memberships : intersect(...)` is **always false** → an empty list reaches the repository → matches nothing → **every unfiltered list endpoint silently returns zero rows.** Compiles clean. Found only because an *unrelated* search test failed with "the collection was empty."
2. **`teamIds = null` means EVERY TEAM.** `GetExportRowsAsync` has **no `userId` parameter at all**, and `ExportFilter.TeamIds` is a *trailing optional* — so the 4-arg ctor every WPF call site uses **compiles, looks complete, and exports the whole company**. On the wire the filter is attacker-controlled; `TM-06` doesn't catch it because it tests the WPF path, where the filter is membership-bounded by construction.
3. **A whole-record update overwrites every column.** `UpdateCheckedAsync(Backlog, …)` writes all 15, including `team_id`. A DTO that merely *omits* `teamId` → `team_id = NULL` → **the backlog drops out of every team and is invisible to everyone, permanently** — and every test still passes.
4. **`ApiFactory` cannot prove SignalR delivery.** It replaces `IChangeNotifier` with a recording double (correct, and necessary for the contract tests) — which means **no test built on it can ever observe the real hub firing.** The honest fixture for one layer is a lying fixture for the layer beneath it. W3 needed a second factory.

### Open, recorded, not blocking

- **A fresh install cannot bootstrap itself.** v10 promotes `MIN(id)` to admin, so a database with **zero users has zero admins** → `AdminBootstrap` no-ops → nobody can log in → and `/api/users` is admin-gated, so the first user cannot be created over HTTP. Safe today (M8.3 targets the *existing* desktop DB, which always has an admin). **Belongs in the deploy runbook.**
- **A demoted admin keeps admin for up to 30 days** — `RequireClaim("is_admin","1")` reads the *cookie* claim, fixed at login. The four destructive `/api/ops/*` routes additionally check `ctx.IsAdmin` (DB-fresh), so the blast radius is bounded.
- **`RestoreAsync` is deliberately NOT exposed.** It overwrites the live `.db` in place under open connections — it corrupts live readers. Needs its own design.
- **Retention runs on a bare `Task.Run`**, not a hosted queue (a queue needs `Program.cs`, which Wave 2 was forbidden). No shutdown awareness.
- 🔴 **THE FLAKE IS REPRODUCED AND NAMED** (2026-07-13, M8.6/T1). It was previously logged here as *"unreproduced, ~1 run in 4, a different test each time, never reproduces in isolation."* An agent hit it on a **clean tree, at HEAD, before editing anything**:
  ```
  Failed! - Failed: 1, Passed: 171, Total: 172 - TimesheetApp.ApiTests.dll
    SqliteException: SQLite Error 1: 'no such table: Backlogs'.
  ```
  Re-running ApiTests alone → 172/172. Re-running the full solution → green. **There is a pre-existing race in the ApiTests host-startup / migration path.**

  **It is the mirror of the build-lock bug below: that one is a false GREEN; this one is a false RED.**

  **Rule:** a lone `no such table: Backlogs` failure is **not** a regression — **re-run before concluding anything**, and **run the baseline before touching the tree** so a pre-existing flake can be told apart from something you caused. *(The message also appears in fully-green runs as a caught/logged exception, so its mere presence in the log means nothing.)*

  Still open: the root cause. `TestDb`'s process-global `ClearAllPools()` was removed (a **no-op there** — `Pooling=false` — that still reached into every other pool). `ApiFactory` still calls it, and there it **is** load-bearing (`Pooling=true`; it releases the file handle). That remains a hypothesis; the race is now the better lead.

### 🔴 A running API makes the .NET gate LIE — a FALSE GREEN

`TimesheetApp.Api` on :5080 **holds `TimesheetApp.Core.dll` open**, so `dotnet test` fails to *build* the API project (`MSB3027`) — **and still exits 0 and prints `Passed!`** for whichever assembly did build. On 2026-07-13 the gate reported `Passed! … 658 … TimesheetApp.Tests.dll` while **`TimesheetApp.ApiTests.dll` never appeared at all** — 172 tests silently did not run.

**Nothing may listen on 5080 during a gate. After it, BOTH `Passed!` lines must be present. An absent line is a failed gate, not a passed one.**

### 🔴 "Do not edit a test to reconcile a gate" — the rule has an exception, and I got it wrong TWICE in one milestone

The rule exists to stop you deleting a test that caught a real regression. **It does not apply when you deliberately changed the contract the test was pinning.** Both times, the implementing agent proved it rather than arguing:

- **M8.6/T0.** The gate said *"165 + yours, 0 failures."* **Impossible** — the task stops `onTrash` from writing, and **five existing tests assert that it writes.** They had to be **rewritten**, not preserved. 165 → 169.
- **M8.6/T1.** The gate said *"the 172 is a fixed baseline; movement is a bug."* **Also wrong** — two tests deserialised `GET /api/backlogs` into `List<BacklogDto>`, and after the route began returning `List<BacklogListItemDto>`, **`System.Text.Json` binds record ctor params by NAME and DEFAULTS the missing ones**, so both would have **stayed green while asserting nothing**. The 172 would not have moved — *that was the problem.* Proven: in the red phase the test failed **only** because the agent added a non-zero `TaskCount` assertion. **A bare retype would have stayed green.**

**A test gate that cannot move is a test gate that cannot notice.**

**Two W1 concerns awaiting a decision (neither blocks W2):**
1. **Greenfield deadlock (latent).** Migration v10 promotes `MIN(id)` to admin — so a database with **zero users has zero admins**. `AdminBootstrap` then no-ops, nobody can log in, and `/api/users` is admin-gated, so the first user cannot be created over HTTP. **Safe today** (M8.3 targets the existing desktop DB, which always has an admin) but **a fresh install cannot bootstrap itself.** Belongs in the deploy runbook.
2. **A demoted admin keeps admin for up to 30 days.** `RequireClaim("is_admin","1")` reads the **cookie** claim, fixed at login (30-day sliding). `IClientContext.IsAdmin` is DB-fresh, so the two can disagree. W2-D's brief now requires the four destructive `/api/ops/*` routes to check `ctx.IsAdmin` **as well as** the policy.

## Current Milestone

**Milestone:** M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular 17)
**Started:** 2026-07-12
**Target:** no deadline

| Slice | State |
|---|---|
| **M8.1** — extract `TimesheetApp.Core` (net8.0) | ✅ **DONE, gated.** 81 files moved, zero C# edited, one XAML line. 548→548 green, WPF still runs. |
| **M8.2 W1** — schema v10 | ✅ **DONE** (`4b00e66`). 560 green. |
| **M8.2 W2** — WAL profile · backup blocker · 3 bug fixes | ✅ **DONE, merged, gated** (`fe794c2`). **583 green.** 3 agents in parallel, 0 conflicts. |
| **M8.2 W3** — `row_version` across 8 repositories | ✅ **DONE, merged, gated** (`3a89801`). **616 green.** 4 agents in parallel, **0 merge conflicts** — but they built **two incompatible APIs**; see W3.5. |
| **M8.2 W3.5** — consolidation | ✅ **DONE, merged, gated** (`49cb9d0`). 621 green. One API; `row_version` reachable end to end. |
| **M8.2 W4** — `ActiveTeamId` per-user | ✅ **DONE, merged, gated** (`6ac5621`). **628 green.** |
| **M8.2** | ✅ **COMPLETE.** 548 → 628. Gate OT-1..OT-7 green; **OT-8 (WPF still launches) is a MANUAL check the user must do.** |
| **M8.3** — API + auth + SignalR | ✅ **COMPLETE, merged.** 80 routes · cookie auth · Data Protection · SignalR. **809 green.** |
| **M8.4** — Angular shell + Log Work | ✅ **COMPLETE, merged.** Login, guarded shell, **Log Work against the real API**. **939 green.** |
| **M8.5** — Log Work task actions | ✅ **COMPLETE** (`ebfea32`). Add task · Move to next month · drag-reorder + drag-to-trash. **995 green.** UAT OT-13/OT-14 open. |
| **M8.6** — **Backlog screen** | 🔄 **NEXT.** STEP 2 (brainstorm). |
| **M8.7–M8.10** — Task List · Daily Report · Reports · Users + Settings | ⬜ Same three steps each: annotate → regenerate → wire. |

Plan: `docs/superpowers/plans/2026-07-13-M8.5-log-work-task-actions.md` (rev. 3)
Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (rev. 3)

**The one M8.2 gate item still open: OT-8 — "the WPF app still launches and works."** No test proves it; **the user must click it.**

---

### The three lessons M8.2 paid for. Do not re-learn them.

1. **A file that N parallel agents need is a file the controller writes FIRST.** `ConcurrencyConflictException` was done right (controller wrote it up front). `Models/Entities.cs` was not — four agents each needed it, none owned it, so `Backlog`/`TaskItem`/`TimeLog` got no `RowVersion` at all and the whole mechanism was unreachable. That cost an entire consolidation wave.
2. **A decision in `STATE.md` is NOT a decision an agent knows.** "The checked methods are additive; originals stay bump-only" was recorded *before* Wave 3 ran. W3-A followed it. W3-B, W3-C and W3-D each re-derived a *different* API — not from disagreement, but because their briefs never repeated it. **Restate every cross-cutting decision verbatim in every brief that could touch it.**
3. **Route overlap is invisible to a file-overlap check.** M8.3 rev. 1 gave `tags` to two agents in two different files. Zero file overlap — and both would map `/api/tags`, giving `AmbiguousMatchException` → 500 on every tags request, found only at the merge gate. **Own routes, not just files.**

**The lesson, again:** a file shared by N parallel agents will be edited by none of them. Hand it to the controller *before* the fan-out, not after.

---

## Open Blockers

- **The concurrency mechanism is not yet reachable end-to-end.** The write path is done; the **read** path never projects `row_version`, and the records in `Models/Entities.cs` do not carry it — so a client has no way to *obtain* the `expectedVersion` it is required to send back. Fixed in **W3.5 / C-1**. **The API (M8.3) cannot call the checked path until this lands.**
- **UI design for M8.4–M8.8 is the user's to supply.** The Angular bundle is vendored at `src/timesheet-web/` (Angular 17, standalone, signals) but covers only ~35–45% — the shell, not the behaviour. No dialogs, no `TeamFilter`, no `TagPicker`, no Gantt, no login, no 409 UX. Its models are UI-shaped: `User` has no `id`, `TaskCard` has no `ScheduleState` (so the Late/At-risk chips *cannot* render), and `HoursMap` is keyed by **array index** — one filter or reorder and hours land on the wrong task. **Do not hand-maintain those types: expose OpenAPI from the API and generate the client.**
- **No host machine exists.** The company has no server. The design assumes **one designated workstation hosts the API** (spec §2), which is what restores the single-writer property SQLite needs. The user has deferred solving this. It does not block M8.2/M8.3, but M8.4's UAT needs it.

---

## ⚠ Things that will bite the next session

### The worktree gotcha — this cost three agents

**`isolation: "worktree"` on the Agent tool gave the correct base only 1 time in 3.** The other two got `e469fbd` — a commit from *before* M8 existed, where `TimesheetApp.Core` does not exist at all. Both agents correctly reported BLOCKED rather than adapting to the old paths, and one explained exactly why that mattered:

> *"The bug I was sent to fix does not exist on the base I was given. A green commit here would be a fix to code that never ships — `PruneArchiver` would keep its `File.Copy`, land next to WAL, and the data-loss route would stay wide open while the wave reports it closed. That is the single worst outcome available."*

**Do not use `isolation: "worktree"`. Create worktrees yourself with an explicit base, verify each one, then dispatch agents with the absolute path:**

```bash
git worktree add .worktrees/<name> -b <branch> feature/m8-core-extraction-2026-07-12
ls .worktrees/<name>/src/TimesheetApp.Core   # must exist
```

Every agent prompt should open with a **Step 0** that re-verifies the base and stops if it is wrong.

### TWENTY-PLUS of my own claims turned out false — every one caught by an agent *running* them

This is the most important lesson in this file. Do not trust a claim about **how a failure presents** unless it has been executed. Note the rate: this did not stop after I noticed it. It kept happening through M8.5, and every time it was caught by an agent that ran the thing instead of believing me.

**M8.5 added four more, and two are instructive beyond their own bug:**

| I claimed | Truth, measured |
|---|---|
| "A missing `DragDropModule` **builds clean** and nothing drags — a silent failure." | **Inverted.** Task 6's agent removed the module and ran it: the build goes **RED**, four `NG8002` errors. **A property *binding* is type-checked; only a bare *attribute* is silent.** The real hazard is `cdkDragHandle` — the one bare attribute — which, if dropped, makes CDK drag the whole row while everything still "works." I had warned about the safe half and missed the dangerous half. |
| The plan's own reorder write (rev. 1), windowed to the moved range | **Corrupts order after any delete.** `SetActiveAsync` soft-deletes and **leaves `order_index` alone**, so survivors sit at 1,2,3 — a **gap**. A windowed write then produces a **tie**, and `ORDER BY` with a tie is arbitrary. Worse: **my own second test asserted the buggy behaviour.** Fixed by rewriting **every** row (self-healing; matches WPF `TimesheetViewModel.cs:421`). |
| My DB-safety proof: "don't touch the live app on :5080" **and** "prove no `-wal` exists beside the real DB" | **Self-contradictory.** A live SQLite app in WAL mode **always** has a `-wal`, and checkpointing changes the file hash. Following my script literally would have aborted on a false alarm. The agent substituted **stronger** evidence: the startup log reading `AdminBootstrap: no users yet, nothing to bootstrap` — unforgeable proof it opened an *empty* DB. |
| `.mini-ghost` (a CSS class I used in the plan) | **Does not exist.** I invented it. The house classes are `btn btn-ghost btn-sm` (`styles.scss:91-102`). |

**And the fixture lesson, one more time, at its sharpest.** Task 7's agent mutated the template to the plan's broken `#trash` form and re-ran: **six of the seven `onTrash` tests stayed GREEN against a completely dead feature.** Only the test that asserted the *wiring* — that the drop list registers under the literal id `trash` — went red. Six green tests over a corpse. **A test that exercises a handler directly cannot tell you the handler is reachable.**

| I claimed | Truth, measured |
|---|---|
| `TeamBootstrapService`'s backfill must bump `StandupEntries.row_version` (I put it in a brief) | **`StandupEntries` has no `row_version` column.** v10 versions 8 tables and that is not one. An agent obeying me would have shipped `UPDATE StandupEntries SET row_version = row_version + 1` → **`no such column`** → **every user's app crashes at startup, every launch.** The agent checked the schema instead of trusting the brief. |
| "A checked write never takes an optional param and never returns void" — I wrote this as a **rule in a brief four agents were about to follow** | **False for the entity that matters most.** `UpsertCheckedAsync(TimeLog log, long? expectedVersion)` is nullable **by design** (the five-case table), and `DeleteCheckedAsync(...)` returns `Task`, not `Task<long>`. An agent obeying the rule would have "normalized" `TimeLogRepository` and **destroyed the five-case behaviour W3-A established by measurement.** |
| "Four test files hand-implement `IAppConfig` — that is the risk when removing a member" | **Right fact, wrong risk.** Those four fakes compiled untouched (an extra public member is legal). The actual breakage was **Moq mocks** — `SetupGet(c => c.ActiveTeamId)` — in four *different* files. "Run the full suite" was the right instruction for the wrong reason. |
| Dapper fails **silently** (returns `null`) if the column is renamed without the DTO | **Throws.** `SqliteException: no such column`, 28 tests red. **This was the sole reason the `UserRepository` fix was scheduled a wave *later* than the migration** — which would have committed a red tree and handed three parallel agents a broken baseline. Schema rename and the SQL that reads it are **atomically coupled**. |
| `busy_timeout` / `synchronous` are connection-string settings | **Not valid keywords.** Silently swallowed. They must be `PRAGMA`s. |
| `IDbBackupHelper` is ctor-injected into **5** services | **4.** (`IJournalWarningSink`: **2**.) `PruneArchiver` injects neither. M8.3's DI registration must use the real list. |
| Deleting `SmartInputPanelVm.BuildPlan` orphans **9 tests** | **0.** `BuildPlan` is `private`; the 9 tests go through public commands and still pass. Following me would have meant **deleting 8 good tests — including a security-boundary test — to hit a predicted number.** |
| `ConcurrencyConflictException`'s `Message` carries cell detail for `TimeLogs` | It does not. `BuildMessage` has no such parameter. **My bug, in the contract file I wrote to prevent this exact class of divergence.** Fixed in W3.5 / C-2. |

The corrected pitfall research carries a banner about this: a `[VERIFIED]` tag means the author *believed* they had checked it, not that they *executed* it.

**Agents are not exempt.** W3-D added `RowVersion` to three records in `Entities.cs` justifying it as *"following this file's existing convention for `Backlog`."* **There was no such convention** — `git show c9c26b0:…/Entities.cs | grep RowVersion` returns nothing; `Backlog` had no `RowVersion` at all. It had confused `ReadModels.cs` (which does set that precedent, and which the plan cites) with `Entities.cs`. The *action* was right and the *citation* was invented. Check the citation, not just the diff — a plausible reason is the easiest thing in the world to generate.

### Why 548 tests were green while a data-loss route sat wide open

W2-B found it. **Every pre-existing backup test faked the database with a text file** (`"LIVE-DB"`) or six `0x09` bytes. A text file has no pages, no header, and **cannot have a `-wal`** — it is precisely the fixture that let `File.Copy` look correct for four phases. The tests were *structurally incapable* of catching the bug. Fixtures are now real SQLite databases, and the bug appeared immediately:

```
Snapshot RetentionService trusts before PERMANENTLY DELETING:
  Expected: ["live-row", "alpha", "beta", "gamma"]
  Actual:   ["live-row"]          ← 3 of 4 committed rows silently gone
```

### Never use `:memory:` for a concurrency test

Each connection gets its **own** database, so a conflict can never occur — **the test passes while asserting nothing.** Every one of the 583 uses a temp-file database. Keep that convention.

---

## Key Decisions Made

- 2026-07-12: **Milestone M8 opened as a fresh start; WPF-era state files were NOT backfilled.** — `validate-state` returned FAIL (no `PROJECT.md`; `STATE.md` had none of the required sections; the three files contradicted each other). Repairing state for an architecture about to be replaced is wasted work. The old narrative is preserved verbatim in `.planning/STATE-ARCHIVE-wpf-p1-p20.md`.
- 2026-07-12: **Deployment — one designated workstation hosts the API.** — No server exists. Rejected: every user running their own API against a shared `.db` (destroys the single-writer premise; N processes on N hosts over SMB is exactly what sqlite.org warns against, and it kills WAL, SignalR *and* auth-as-a-boundary at once). Consequence: **all company data sits on one workstation's disk**, so hourly online backup to the network share is a `must_have`.
- 2026-07-12: **HTTP, not HTTPS — knowingly accepted risk.** — No certificate infrastructure. The session cookie crosses the LAN in plaintext, so anyone who can capture packets can assume any identity — including the admin who can permanently delete three months of data. Recorded, not hidden. Switching to HTTPS later is a one-line config change.
- 2026-07-12: **Keep SQLite (interim), WAL on the host's local disk.** — No managed DB available. The API is the only writer process, so N users ≠ N writers. Exit cost enumerated in spec §15: 5 constructs, ~15 call sites, **no SQLite date functions anywhere** (normally the most expensive thing to port).
- 2026-07-12: **Optimistic concurrency (`row_version` + 409) + SignalR.** — No table had a version column, and Task List commits every inline edit as a bare `UPDATE`, so concurrent editors overwrite each other **today**. No engine fixes this.
- 2026-07-12: **`row_version` DOES apply to `TimeLogs`** (reversal of the first draft). — The draft excluded it on the argument that a cross-user collision was impossible. That rested on *current behaviour* (team view is read-only), not an enforced invariant. Retrofitting after the endpoints and grid exist would cost far more.
- 2026-07-12: **The rule is: ALWAYS BUMP, CHECK SELECTIVELY.** — Two templates, not one. `SetOrderAsync` runs once per row during a drag, so a single check-and-bump template would **409-storm on an ordinary reorder**. Bumping without checking is safe; **checking without bumping is the bug the mechanism exists to prevent** — the spec's own Smart Fill carve-out reintroduced it in an earlier draft.
- 2026-07-12: **The checked repository methods are ADDITIVE; the originals stay unchecked.** — Making `expectedVersion` optional-defaulting-to-`null` is a trap: under the five-case table, every WPF re-edit of an already-filled cell becomes `null` + row-exists → **conflict on every second edit**. Originals stay bump-only; `*CheckedAsync` is new. Only the API path is protected, which is correct — WPF is deleted at M8.10.
- 2026-07-12: **Auth = username + password, cookie session, on the existing `Users` table.** — No AD. ASP.NET Identity rejected (drags in EF Core; creates a second user table). `PasswordHasher<T>` is usable standalone from `Microsoft.Extensions.Identity.Core` — 3 packages, no EF Core.
- 2026-07-12: **Authorization = one `is_admin` boolean**, gating exactly 3 destructive endpoints (run retention, restore backup, deactivate team). The user does not want edit-level permissions; those three are *destructive*, not merely privileged.
- 2026-07-12: **Shared contracts are the controller's job, never an agent's.** — Four agents needed `ConcurrencyConflictException`; left to invent it they would have produced four incompatible versions and the merge would be where we found out. Same lesson missed once and re-learned: `Models/Entities.cs` (W3.5/C-1).
- 2026-07-12: **A checked write RETURNS the new `row_version` (`Task<long>`); it never returns `void`.** — User-approved at the W3 merge. A `void` write forces the caller to re-read the version, and that read-back is **racy**: between the write committing and the re-read, another client can write; you then hold *their* version number with *your* data, and your next save passes the check and silently overwrites them — the exact lost update the mechanism exists to prevent. Returning the version from the same statement that performed the write (`RETURNING row_version`) closes it by construction. Also kills the CS0854 Moq breakage that the optional-parameter shape forced on three test files. `GetRowVersionAsync` is **deleted** — the version now arrives on the entity from the SELECT (W3.5/C-1).
- 2026-07-12: **A decision recorded in `STATE.md` is NOT a decision an agent knows.** — The "additive `*CheckedAsync`, originals stay bump-only" decision above was already written down *before* Wave 3 ran. W3-A followed it; **W3-B, W3-C and W3-D each independently re-derived a different shape** (optional parameter, `void` return), because their briefs did not restate it. Agents see only their brief. **Every cross-cutting decision must be re-stated verbatim in every brief that could touch it** — otherwise N agents produce N designs and the merge is where you find out.

## Approved Mode

**Mode A** — approved 2026-07-13, and it is the mode in force. The gate ran again for M8.5 and scored **1/5** Mode B signals (two domains: C# + Angular); no hard exclusion applies — no migration, no auth change, no release gate, no compliance impact.

*(Superseded: **Mode B**, approved 2026-07-12 for M8.1–M8.4, which scored 4/5 and tripped the "data migration impact" hard exclusion. M8.5 onward is materially smaller — the endpoints and the contracts already exist — so the gate legitimately re-scored. This entry stays only to explain why the earlier commits look like Mode B; **do not resume in Mode B.** M8.6 re-runs the gate for itself, as every milestone must.)*

## Config

`.planning/config.json` — Mode B · `parallelization: true` · `mode: interactive` · `model_profile: quality` · `commit_atomic: true` · Process 2.0 flags all on.

## Notes

- **The real database has been migrated to v10** and the WPF app runs on it. Backup before that: `~/Documents/TimesheetApp/timesheet.db.20260712-pre-v10.bak`. **Migrations are forward-only — `git revert` does not undo a changed `.db` file.**
- **`.planning/M8.1-UAT.md`** — five checks, deferred by the user. The one that matters if the WPF Reports screen ever misbehaves: `ReportsTab.xaml:4` now says `;assembly=TimesheetApp.Core`, and XAML binding failures are **silent at build time**.
- **Feature inventory:** `.planning/M8-FEATURE-INVENTORY.md` — the as-built record of the WPF app (7 screens, 8 dialogs, every business rule, 16 tables, ~40 design tokens). It is both the migration scope and the design brief. Anything not in it is not being migrated.
- **`.claude/memory/`** does not exist, so `workflow.memory_recall` is a no-op despite being enabled.
