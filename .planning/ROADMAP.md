# ROADMAP — TimesheetApp

**Last updated:** 2026-06-22

## Shipped
- **M1 — WPF Desktop Timesheet Tool v1** — build complete, 162 tests green, QA-passed. Awaiting user UAT.
  - [x] P1 Data + Schema
  - [x] P2 Services
  - [x] P3 Timesheet + Smart Input UI
  - [x] P4 Requests + Users UI
  - [x] P5 Reports
  - [x] P6 Settings + Export
  - [x] App shell (MainWindow/MainViewModel) + startup
  - See `.planning/M1-SUMMARY.md`.

## In review (awaiting UAT) — all on branch `feature/task-list-2026-06-27` (not merged), **458 tests green**, schema v8
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
