# ROADMAP — TimesheetApp

**Last updated:** 2026-07-19

## Active
- **M10 — Delete the WPF app.** *(was listed below as M8.11)* Approach: **verify-then-delete**. `PROJECT.md` §Success Criteria requires every feature in `.planning/M8-FEATURE-INVENTORY.md` be reachable in the web app, so a coverage audit runs **before** the deletion — its memo lands at `.planning/M10-COVERAGE-AUDIT.md`. Deletes `src/TimesheetApp/` (ViewModels, Views, Services, `App.xaml.cs`), the 13 WPF test files (179 `[Fact]`/`[Theory]`), the `ProjectReference` in `TimesheetApp.Tests.csproj`, and the `src/TimesheetApp.sln` entry. **.NET gate 689 → ~490.** `TimesheetApp.Core` is untouched. Currently at **STEP 2 Brainstorm**; the audit's first run was lost and is re-running (see STATE.md).
- **UAT is batched to one session after M10 + M11** (user decision 2026-07-19). `OT-13…OT-25` **plus** M9.1's `G3`/`G6`/`G10`, which were merged un-accepted. 🔴 `G3` says *"matches the old WPF app"* — after M10 that oracle is gone, which is exactly why the coverage audit must not be skipped.

## Planned
- **M11 — Settings → `IConfiguration`.** Sequenced strictly after M10 so each milestone moves the test gate for exactly one reason. `DbPath`/`ConfigPath`/`KeyRingPath` become required config; missing config = fail-fast, no fallback chain. 🔴 Blocking pre-req: prove empirically that an `appsettings.json` in `TimesheetApp.Api` does not outrank `WebApplicationFactory.UseSetting` — if it does, both API suites silently retarget the real company DB. Decisions locked in STATE.md; findings F1–F5 in `.planning/fast-lane-settings-appsettings.json`.
- **Remote hosting** — still unsolved and still blocking everyone but the user. See STATE.md §"STILL BLOCKING EVERYONE BUT THE USER".
- **M8.7–M8.10 — the four remaining screens, as ONE milestone with a shared Phase 1.** ✅ Shipped as **M9** (see above). *(Original plan notes retained below for history.)*
  **Task List · Daily Report · Reports · Users + Settings.**
  🔴 **They cannot be done as four independent milestones, and they cannot be fanned out naively.** Five files are needed by every one of them and owned by none — chief among them **`SettingsEndpoints.cs`: 47 routes, one file, zero annotated**, needed by all four *and* by Backlog. Plus the `includeTags` one-liner, `OpenApiContractTests.cs`, the single stub block in `worklog.service.ts`, and the generated `api/**` tree, where **a regeneration is a global event**.
  **Phase 1 (controller, sequential):** annotate every route for every screen → extend the contract tests once → widen `includeTags` once → **regenerate once** → write every `WorklogService` method once → build the two shared things nobody owns: a **team-filter component** (four screens need it; it does not exist in Angular) and **`GET /api/teams`** (Reports needs team *names*; `/api/me` returns ids only). Also fix `realtime.service.ts:63`, which throws away the `(kind, teamId)` the server sends, so no screen can filter what it re-fetches.
  **Phase 2 (four agents, true parallel, zero overlap):** one page directory each.
  **The hard one is Task List:** it has **no read model at all**. `GetLoggedHoursByBacklogAsync` is called by **no endpoint**, so logged-hours-per-backlog is **unobtainable by any route**; `IScheduleStateService` is DI-registered and called by nothing, so the Late / At-risk chips have **no wire representation**; and the Gantt's layout maths lives in the *WPF ViewModel*, not Core.
  **The easy one is Reports:** **zero routes to invent**, no charts (two tables, a five-level tree, four stat cards), and its team-scoping is already defused and proven by two passing tests.
  Recon: `.planning/research/M8.7-M8.10-recon.md` · `.planning/research/M8.8-daily-report-recon.md`

## Shipped
- **M11 — Configuration: locations from `IConfiguration`, policy stays writable** — **COMPLETE** (2026-07-20), **Mode A**. **.NET 475 · ApiTests 541 · Angular 775 · 0 warnings.**
  Delivers the user's go-live model: **`DbPath` comes from `appsettings.json`; an existing file at that path is opened, an absent one is created.** The create-half already worked; the from-configuration half did not work at all, in two independent ways.
  **F1** — `Program.cs:34` used `||`, so setting `DbPath` alone fell through to the desktop `%APPDATA%` defaults with **no error at all**. Now `ConfigPath`/`DbPath`/`KeyRingPath` are required and the process **refuses to start**, naming the missing key, a JSON snippet, the env-var form, and the legacy value to copy. **No fallback chain — the fallback chain was the bug.**
  **F2** — the persisted store outranked the passed argument, so `DbPath` in configuration was dead weight on any machine that had run before. The argument is now authoritative. ⚠️ Foot-gun stated: editing configuration now changes which database opens, so the banner prints the paths that **won** every start.
  **F5** — the writable store left its `appsettings.json` name collision (→ `local-settings.json`, copy-based migration, never move). **F3** stayed intact and is now guarded by a test. **F4** was checked and already true.
  Also: `IsDarkMode` left the server (unused there, per-browser by nature), and `UserDto.hasPassword` closed a screen that reported **every** account as able to log in when none could.
  Spec: `docs/superpowers/specs/2026-07-19-m11-configuration-design.md` · Template: `src/TimesheetApp.Api/appsettings.Example.json` · Summary: `.planning/M10-M11-SUMMARY.md`
- **M10 — Delete the WPF app** — **COMPLETE** (`daa4192`, 2026-07-19), **Mode A**. **.NET 692 → 465** (−227 test cases), **ApiTests 531**, **Angular 772**, **0 warnings**. `TimesheetApp.Core` untouched.
  The desktop app the project started as is gone: `src/TimesheetApp/` entirely (ViewModels, Views, `Services/CurrentTeamService` + `ThemeService`, `App.xaml.cs` and its whole startup lifecycle), 23 test files, the `ProjectReference`, the `InternalsVisibleTo`, and the solution entry.
  **The audit said DO NOT DELETE YET, and all three blockers were closed first** — that is why this was safe today and not this morning. Auth cutover **dissolved** when the user confirmed the database is disposable and go-live is a first run. The four scheduled jobs whose only caller was `App.xaml.cs` now run in the API (`a05b721`). Backup is configurable and inspectable from the web (`3866d63`, `b50b4d8`, `81beec4`), restore has an offline CLI + runbook (`4c075d8`) and refuses a non-database before deleting anything (`0c739f9`).
  **Gate shape was the user's call:** only silently-failing items blocked the deletion; the ~7 affordance items are deferred and listed in `.planning/M10-BLOCKERS.md`, not forgotten. Four permanent losses were accepted by name.
  **Two confident claims corrected by doing it:** the memo said `BackupServiceTests`/`WalBackupSafetyTests` were in the blast radius and needed relocating — they were not and did not. And the test loss is **227**, not the 205 this controller asserted nor the 190 the memo did: both counted `[Fact]`/`[Theory]` *attributes*, and a Theory with several `InlineData` rows is several cases.
  🔴 **`OT-13…OT-25` was never clicked. From here the audit memo IS the record of what the desktop did.** The source remains in git history.
  Audit: `.planning/M10-COVERAGE-AUDIT.md` · Blockers: `.planning/M10-BLOCKERS.md`
- **M9.2 — UI write honesty** — **MERGED to `main`** (`1d044ec`, 2026-07-19), **Mode A**. **753 Angular tests green** (baseline 742). QA **APPROVE WITH CONDITIONS**, 0 Critical, all 3 Important closed before merge.
  Two live data-loss defects, one class — *the UI asserting a write that did not happen*. **BUG-1:** typing unparseable text over a Log Work cell **silently deleted the hours already stored there**, and dropped the day/week totals to `0` on screen while the database still held them. **BUG-2:** "Backup now" reported success when nothing had been written.
  **No REQ-IDs added** — both violate requirements that already existed (**TS-04/XC-02/XC-04**, **BK-02**), written for WPF, satisfied by WPF, never implemented by the web port. Debt closure, not new scope.
  Found by the **M10 coverage audit**, not by a test and not by a user. 🔴 BUG-1 had a **green test sitting on top of it** (`grid-state.spec.ts:190`) — correct about the parse, blind to the fact that the same `null` meant DELETE two files downstream.
  🔴 **Merged un-UAT'd**, as M9.1 was, per the standing batching decision. **No test touches the DOM**, so the red border and status line are unproven by the suite — `G-A` is the only thing that proves a user sees them.
  Spec: `docs/superpowers/specs/2026-07-19-m9.2-ui-write-honesty-design.md` · Plan: `docs/superpowers/plans/2026-07-19-M9.2-ui-write-honesty.md` · Summary: `.planning/M9.2-SUMMARY.md`
- **M9.1 — Read-model / scope gaps** — **MERGED to `main`** (`2c2cb49`, 2026-07-19), **Mode A**. **1938 tests green** — 689 .NET + 507 API + 742 Angular (baseline 1924, +14). QA gate **APPROVE**, 0 Critical/Important.
  Closed the three fidelity gaps the 2026-07-16 code audit found still real after M9: **TL-12** Task List bands adaptively by team (>1 team checked) or project (exactly 1) — needed `teamId` on `TaskListRowDto`; **DR-11** Daily Report picker hard-scoped to the active team — needed `teamId` on `BacklogListItemDto`; **SET-05** default tasks deactivate → **reactivate** round-trip — needed `GetAllAsync` + an admin-only `GET /api/default-tasks/all`, closing a one-way hole **WPF shares** (a fix, not a port).
  All three projection-only + one read route. **No schema change.** Shape: Wave A (C#, sequential — both tasks edit `Dtos.cs`) → one client regen → Wave B (Angular, parallel, zero file overlap).
  🔴 **Merged WITHOUT UAT** by explicit user decision — `G3`/`G6`/`G10` were never clicked. Acceptable only because `main` is deployed nowhere; the cost is that trunk carries three unconfirmed behaviors. Batched into the end-of-M11 UAT session.
  Spec: `docs/superpowers/specs/2026-07-16-m9.1-read-model-scope-gaps-design.md` · Plan: `docs/superpowers/plans/2026-07-16-M9.1-read-model-scope-gaps.md` (Plan Checker 11/11 PASS) · UAT: `.planning/M9.1-UAT.md`
- **M8.6 — Backlog screen** — **COMPLETE** (`90151d4`, 2026-07-13), **Mode A**. **1142 tests green** — 868 .NET (658 + 210) + 274 Angular. 0 warnings.
  The screen the user clicked into and found dead. *"+ New backlog"* showed a toast and did nothing; the list was empty because the service returned `of([])`. **Both toasts are gone.** Now: a real list (server-side task counts via the batched `IN` query, no N+1), create, edit, a task sub-editor, and a change-history panel fed by a route that **did not exist** (`GET /api/backlogs/{id}/audit` — the repository method had been sitting unexposed since v2).
  **Six WPF bugs fixed rather than ported** (all user-approved), including one the XAML comment *denies exists*: the `DEFAULT` pseudo-backlog is visible on the WPF Backlog list **with a working Edit button**, and its project isn't in the dropdown — so open-and-save writes `project = ''`.
  **Two server bugs fixed.** A user in **zero teams** created a backlog with `team_id = NULL` — invisible to every read (admins included), no SignalR — and got **`200 OK` with the full DTO**. The same permanent-invisibility end state M8.3 paid for through `UPDATE`, reached this time through `INSERT`; the `INSERT` door was open only because no client could call the route.
  **Two Plan Checker BLOCKs (7 defects, then 4) before a line of code was written.** The worst: `/api/users/all` is **admin-only**, so the list would have **403'd for every ordinary user** — *a correctly-typed client that 403s*, which is a class of lie the `.Produces<T>()` discipline does not catch on its own.
  Spec: `docs/superpowers/specs/2026-07-13-backlog-screen-design.md` · Plan: `docs/superpowers/plans/2026-07-13-M8.6-backlog-screen.md` (rev. 3)
  **UAT open — the user tests at the end, by their choice:** OT-13 … OT-19.
- **M8.5 — Log Work task actions** — **COMPLETE** (`ebfea32`, 2026-07-13), **Mode A**. **995 tests green** — 830 .NET + 165 Angular. 0 warnings.
  Restored the three controls M8.4/W4 removed because the vendored design **faked** them: `+ Add task` called `toast.show('Task added')` and added nothing, `Move to next month` had no handler, and the delete dropzone had no drop handler. All three now work against the real API.
  All five endpoints already existed — but `BacklogEndpoints.cs` had **zero `.Produces<T>()`**, so OpenAPI described none of them and the generated TypeScript client could not contain them. **Annotating the C# was Wave A, not optional cleanup** — and `OpenApiContractTests.cs` (15 cases) now fails the build if that invariant is ever broken again. Three sequential waves, seven tasks: **A** annotate (metadata only, 815→830) → **B** regenerate → **C** the Angular (124→165).
  **UAT still open — the user must click OT-13 and OT-14**, the two interactions no single-feature test catches: *delete → reorder → reload* (the order must hold) and *delete → add → reload* (the new task must be last). Both are gap/tie hazards created by soft delete leaving `order_index` untouched.
  **Known UX gap, undecided:** a mis-drop onto the trash deletes instantly — no undo, no confirmation. It is a *soft* delete and the restore path is tested, but **no screen calls it.**
  Spec: `docs/superpowers/specs/2026-07-13-log-work-task-actions-design.md` · Plan: `docs/superpowers/plans/2026-07-13-M8.5-log-work-task-actions.md` (rev. 3)
- **M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular 17)** — **MERGED to `main`** (`feature/m8-web-migration-2026-07-13`, 2026-07-13). **939 tests green** — 658 Core/WPF + 157 API + 124 Angular. 0 warnings. From 548 at the start of M8.2.
  Why: the WPF UI is defect-prone, and the app is architected around a SQLite file on a synced shared folder, where two people editing at once **silently overwrite each other**.
  - [x] **M8.0** — Research (4 agents). Found 5 blockers, **2 of which would have destroyed production data**.
  - [x] **M8.1** — Extract `TimesheetApp.Core` (net8.0). 81 files moved, zero C# edited, **one** XAML line. 548 green.
  - [x] **M8.2** — Schema v10 · optimistic concurrency across 8 repositories · **the backup blocker** (every backup test faked the database with a *text file*, which cannot have a `-wal` — so `File.Copy` looked correct for four phases while a data-loss route sat wide open) · `ActiveTeamId` per-user. **628 green.**
  - [x] **M8.3** — ASP.NET Core API · cookie auth + Data Protection · **80 routes** · SignalR. **809 green.**
  - [x] **M8.4** — Angular: login, guarded shell, **Log Work working against the real API**. **939 green.**
  **Still open, and the user's to decide:** how the web app reaches anyone else's browser. The dev loop works same-origin through an `ng serve` proxy — the only transport where a `SameSite=Lax` cookie survives, since `SameSite=None` would require HTTPS and this project deliberately has none. **Production hosting collides with the deferred "the company has no server" blocker.**
  Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (rev. 3).

## ~~Planned (M8.x numbering)~~ — SUPERSEDED 2026-07-19, kept for traceability
The M8.6–M8.11 numbering was retired when the remaining screens were consolidated into **M9**. Mapping:
- ~~**M8.6–M8.9** — the six remaining Angular screens~~ → shipped as **M8.6** (Backlog) + **M9** (Task List · Daily Report · Reports · Users · Settings), with the residual read-model gaps closed in **M9.1**.
- **M8.10 — Export / Backup / Retention moved host-side** → 🟠 **STILL OPEN, and deliberately unresolved here.** These are inventory sections B7/B8/B9, and whether they are reachable in the web app is one of the questions the M10 coverage audit answers. Do not mark this shipped from memory — read the audit memo.
- ~~**M8.11** — Delete the WPF project~~ → renumbered **M10** (now Active, above).

## Shipped — earlier (WPF era, pre-M8)
- **P15 / P16 / P17 — Task List card layout + auto-provision user** — MERGED to `main` + pushed (`bc4c02f`, 2026-07-02), build clean, **536 tests green**. **P15** grouped section bands (adaptive Team/Project, collapsible, `Name (count)`). **P16** per-backlog **card** layout (`DataGrid`→grouped `ItemsControl`; tags full-width on top of each card, no horizontal scroll; Type/PCT/PCA → direct TwoWay) + tweaks (External in compact header, "Estimation" label, no-progress defaults to 0%). **P17** auto-provision current user on startup (unmapped Windows account → auto-create + map, no manual add / no picker). Specs+plans: `docs/superpowers/{specs,plans}/2026-07-02-*`. UAT: `.planning/P15-UAT.md`, `.planning/P16-UAT.md`. Summary: `.planning/P15-P16-P17-SUMMARY.md`. Follow-up UAT (non-blocking): live-check Type/PCT/PCA persist + auto-provision.
- **M1 — WPF Desktop Timesheet Tool v1** — build complete, 162 tests green, QA-passed. Awaiting user UAT.
  - [x] P1 Data + Schema
  - [x] P2 Services
  - [x] P3 Timesheet + Smart Input UI
  - [x] P4 Requests + Users UI
  - [x] P5 Reports
  - [x] P6 Settings + Export
  - [x] App shell (MainWindow/MainViewModel) + startup
  - See `.planning/M1-SUMMARY.md`.

## In review (awaiting UAT) — all on branch `feature/task-list-2026-06-27` (not merged), **493 tests green**, schema v8
- **M7 — 3-Month Retention/Prune [P12]** — DESTRUCTIVE, off by default; archive-then-prune business data >3 months (per-team markdown + retained .db snapshot); R5a time-axis delete (spanning backlog safe), settings never touched. QA APPROVE (no data-loss path), VERIFIED. UAT: `.planning/P12-Retention-UAT.md`. Spec: `docs/superpowers/specs/2026-06-28-retention-prune-design.md`.
- **M6 — Export Restructure [P11]** — two roots (SharePoint+local) mirrored, per-team `{root}/{Team}/{timesheet,daily,tasklist}` + `{root}/db` copy; supersedes flat archives; QA-approved (I-1 collision fixed). UAT: `.planning/P11-Export-Restructure-UAT.md`. Spec: `docs/superpowers/specs/2026-06-28-export-restructure-design.md`.
- **M5 — Local DB Backup [P9]** — manual + scheduled backup, restore, retention; QA-approved. UAT: `.planning/P9-*` (folder picker + restore).
- **M4 — Multi-Team [P10]** — schema v8, team scoping/membership/active-team/multi-team view, team-aware reports; QA-passed (BLOCK→fixed), goal-backward VERIFIED. UAT: `.planning/P10-Multi-Team-UAT.md`. Spec: `docs/superpowers/specs/2026-06-27-multi-team-design.md`. Summary: `.planning/P10-Multi-Team-SUMMARY.md`.
- **M3 — Task List [P8]** — schema v7→(v8), **build clean, QA-passed, goal-backward VERIFIED**.
  - [x] TL-01 schema v7 (Backlog tracking cols + Tags/BacklogTags/PcaContacts/Holidays, additive)
  - [x] TL-02 sidebar restructure (Backlog/Task List/Reports top-level)
  - [x] TL-03 backlog tracking fields (dual deadline/estimate, assignees, note, tags)
  - [x] TL-04/05/06 Task List month grid + logged hours + manual progress %
  - [x] TL-07/08 auto warning (done%<elapsed%, ≤2 working days) + late-deadline chips
  - [x] TL-09 monthly markdown export (auto-backfill + manual, with moved-out section)
  - [x] TL-10 Grid↔Gantt (native Canvas) + collapse · TL-11 PCA contacts in Settings
  - [x] TAG-01/02 custom tags (icon+color+text) + chips · HOL-01/02 holiday calendar + working-day math
  - UAT: `.planning/P8-Task-List-UAT.md`. Spec: `docs/superpowers/specs/2026-06-27-task-list-design.md`. Plan: `docs/superpowers/plans/2026-06-27-P8-task-list.md`. Summary: `.planning/P8-Task-List-SUMMARY.md`.
- **M2 — Daily Report (Standup) [P7]** — MERGED to `main` (2026-06-27). branch `feature/daily-report-2026-06-25`, 228 tests green, app launches clean.
  - [x] DR-01 schema v5 (StandupEntries + StandupIssues, additive migration)
  - [x] DR-02..04 entries (ad-hoc codes, nullable deadline) + multi-issue (status open/pending/resolved)
  - [x] DR-05 status set Todo/In-process/Done/Pending · DR-06 edit-lock (today+yesterday)
  - [x] DR-07 Input tab (request/task picker + ad-hoc) · DR-08 Team board (per active user)
  - [x] DR-09 weekly markdown archive (1 file/week, startup backfill) · DR-10 nav + live refresh
  - UAT: `.planning/P7-Daily-Report-UAT.md`. Spec: `docs/superpowers/specs/2026-06-25-daily-report-standup-design.md`. Plan: `docs/superpowers/plans/2026-06-25-P7-daily-report.md`.

## Backlog / deferred (non-blocking, from QA)
- Daily Report: inline edit of an existing entry's fields (today = delete + re-add); per-row reorder.
- XC-09 journal warning → surface to a UI banner (currently `Trace`).
- XC-10 backup retention prune + same-ms filename collision guard.
- Timesheet row labels show request_code (GetWeekAsync RequestCode).
- Advisory single-editor lock (deferred by design).

## Out of scope (v1, per REQUIREMENTS §Out of Scope)
Auth/login, multi-tenant/cloud, mobile/Mac, real-time multi-writer sync, email/Teams notifications, Request soft-delete.
