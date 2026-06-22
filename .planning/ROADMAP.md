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

## Backlog / deferred (non-blocking, from QA)
- XC-09 journal warning → surface to a UI banner (currently `Trace`).
- XC-10 backup retention prune + same-ms filename collision guard.
- Timesheet row labels show request_code (GetWeekAsync RequestCode).
- Advisory single-editor lock (deferred by design).

## Out of scope (v1, per REQUIREMENTS §Out of Scope)
Auth/login, multi-tenant/cloud, mobile/Mac, real-time multi-writer sync, email/Teams notifications, Request soft-delete.
