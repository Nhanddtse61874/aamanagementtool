# ROADMAP — TimesheetApp

**Last updated:** 2026-07-13

## Active
- **M9 UAT round-2 + close remaining code gaps.** M9 (= the four screens M8.7–M8.10) **SHIPPED & merged** (`8c12f20`, 2026-07-14); **UAT round-1 fixed 5 defects** (`e710717`, 2026-07-15 — see STATE.md). Now open: round-2 click-through (OT-13…OT-25, minus **OT-20 closed**) + three code gaps still real per the 2026-07-16 audit — **Task List group-by-team** (`TaskListRowDto` has no teamId/team name), **Daily Report picker active-team scope** (`BacklogListItemDto` has no teamId), **default-tasks round-trip** (no `GetAllAsync`).
- **M8.7–M8.10 — the four remaining screens, as ONE milestone with a shared Phase 1.** ✅ Shipped as **M9** (see above). *(Original plan notes retained below for history.)*
  **Task List · Daily Report · Reports · Users + Settings.**
  🔴 **They cannot be done as four independent milestones, and they cannot be fanned out naively.** Five files are needed by every one of them and owned by none — chief among them **`SettingsEndpoints.cs`: 47 routes, one file, zero annotated**, needed by all four *and* by Backlog. Plus the `includeTags` one-liner, `OpenApiContractTests.cs`, the single stub block in `worklog.service.ts`, and the generated `api/**` tree, where **a regeneration is a global event**.
  **Phase 1 (controller, sequential):** annotate every route for every screen → extend the contract tests once → widen `includeTags` once → **regenerate once** → write every `WorklogService` method once → build the two shared things nobody owns: a **team-filter component** (four screens need it; it does not exist in Angular) and **`GET /api/teams`** (Reports needs team *names*; `/api/me` returns ids only). Also fix `realtime.service.ts:63`, which throws away the `(kind, teamId)` the server sends, so no screen can filter what it re-fetches.
  **Phase 2 (four agents, true parallel, zero overlap):** one page directory each.
  **The hard one is Task List:** it has **no read model at all**. `GetLoggedHoursByBacklogAsync` is called by **no endpoint**, so logged-hours-per-backlog is **unobtainable by any route**; `IScheduleStateService` is DI-registered and called by nothing, so the Late / At-risk chips have **no wire representation**; and the Gantt's layout maths lives in the *WPF ViewModel*, not Core.
  **The easy one is Reports:** **zero routes to invent**, no charts (two tables, a five-level tree, four stat cards), and its team-scoping is already defused and proven by two passing tests.
  Recon: `.planning/research/M8.7-M8.10-recon.md` · `.planning/research/M8.8-daily-report-recon.md`

## Shipped
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

## Planned
- **M8.6–M8.9** — the six remaining Angular screens: Backlog · Task List · Daily Report · Reports · Users · Settings. The backend **already serves all of them** (80 routes); each screen now needs only: annotate its routes → regenerate → wire. *(M8.4/W2 deliberately left them as `of([])` stubs: wiring all 20 service methods at once breaks the build, because 7 of 9 components bind the old view models and `tsconfig` is `strict` + `strictTemplates`.)*
- **M8.10** — Export / Backup / Retention moved host-side.
- **M8.11** — Delete the WPF project.

## Shipped
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
