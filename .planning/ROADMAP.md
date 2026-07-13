# ROADMAP — TimesheetApp

**Last updated:** 2026-07-13

## Active
- **M8.6 — Backlog screen** — starting 2026-07-13. **STEP 2 (brainstorm).**
  The screen the user clicked into and found dead: *"+ New backlog"* shows a toast and does nothing, and the list is empty because the service returns `of([])`. It is still the vendored design's shell.
  **No architectural unknowns remain** — M8.5 established the pattern and already annotated five of the Backlog/Task routes, which M8.6 inherits: **annotate its routes (`.WithName` + `.WithTags` + `.Produces<T>()`) → regenerate the client → wire the Angular.**

## Shipped
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
