# ROADMAP — TimesheetApp

**Last updated:** 2026-07-12

## Active
- **M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular 17)** — started 2026-07-12, **Mode B**, in progress.
  Why: the WPF UI is defect-prone (nine re-entrancy guards, a hand-drawn Gantt, several binding workarounds), and the app is architected around a SQLite file on a synced shared folder, where two people editing at once silently overwrite each other.
  **Deployment:** no server exists, so **one designated workstation hosts the API** and the team reaches it over the LAN. That restores the single-writer property SQLite needs. It also puts all company data on one machine's disk, so **hourly online backup to the network share is a `must_have`, not an option**.
  Scope inventory: `.planning/M8-FEATURE-INVENTORY.md` (7 screens, 8 dialogs, every business rule, 16 tables, ~40 design tokens).
  - [x] **M8.0** — Research (4 agents). Found 5 blockers, 2 of which would have destroyed production data. `.planning/research/M8-*.md`
  - [ ] **M8.1** — Extract `TimesheetApp.Core` (net8.0). Pure `git mv`, zero C# changes. Gate: **548/548 green + WPF still launches**. Plan: `docs/superpowers/plans/2026-07-12-M8.1-core-extraction.md`
  - [ ] **M8.2** — API host + schema v10 + optimistic concurrency + SignalR
  - [ ] **M8.3** — Auth (username/password, cookie, `is_admin` on 3 destructive endpoints)
  - [ ] **M8.4–M8.8** — Angular screens (design bundle vendored at `src/timesheet-web/`; covers ~35–45% — the shell, not the behaviour)
  - [ ] **M8.9** — Export / Backup / Retention moved host-side
  - [ ] **M8.10** — Delete the WPF project
  Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (rev. 2, approved).

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
