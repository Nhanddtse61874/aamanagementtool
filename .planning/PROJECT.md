# TimesheetApp (Worklog)

## Vision

An internal tool for a small engineering organisation to log work hours, track backlogs and tasks against deadlines, run daily standups, and produce timesheet reports — with as little ceremony as possible. It exists to replace spreadsheets, not to become project-management software. It is used daily by the people whose work it records, so friction in the UI is a direct tax on every one of them.

## Problem Statement

Engineers must log hours and report progress, but the tools available are either too heavy (enterprise PM suites) or too loose (spreadsheets, chat threads). Hours go unlogged, deadlines slip unnoticed, and standup notes evaporate.

The first answer was a WPF desktop app (M1–M7, phases P1–P20, shipped). It works, but it has two structural problems that cannot be fixed in place:

1. **The UI is buggy and hard to use.** It carries nine re-entrancy guard flags, a hand-drawn Gantt canvas, and several workarounds for WPF binding defects (documented in `.planning/M8-FEATURE-INVENTORY.md` §D9).
2. **It is architected around a SQLite file on a shared OneDrive folder.** Pooling and WAL are disabled, and there is a scaffold of defences against OneDrive conflict copies. Two people editing at once silently overwrite each other — a limitation the original requirements deliberately deferred ("advisory single-editor lock — deferred by design").

**M8 migrates it to the web** (ASP.NET Core 8 + Angular), which fixes both: a real UI, and a single writer process behind an API.

## Success Criteria

- Every feature in `.planning/M8-FEATURE-INVENTORY.md` is reachable in the web app — 7 screens, 8 dialogs, all business rules.
- Two users editing the same record never silently overwrite each other; a conflict is surfaced, not resolved by luck.
- The 548 existing tests remain green throughout the migration and continue to cover the business logic in its new home.
- Users log in with a username and password and stay logged in across browser restarts.
- No user can trigger a destructive operation (permanent data retention, database restore, team deactivation) unless they are an admin.
- The WPF project is deleted, and nothing depends on it.

## Constraints

- **On-prem only.** No Azure or AWS subscription is available. The app runs on an internal server (IIS), reachable only inside the company network.
- **No Active Directory.** Windows Authentication is therefore not an option; auth is username + password.
- **10–50 users across multiple teams.**
- **SQLite is an interim database**, chosen because a managed database is not available. Its replacement is anticipated, and its exit cost is enumerated in the M8 backend-foundation spec (§13).
- **Migrations are forward-only and additive.** There is real production data; a bad migration cannot be rolled back by reverting a commit.
- **Do not rewrite the business layer.** 49 of 50 service files and all of `Data/` and `Models/` are already free of WPF and are covered by tests. The migration wraps them; it does not replace them.

## Stack

- **.NET 8** — C#, Dapper, `Microsoft.Data.Sqlite`, ClosedXML
- **ASP.NET Core 8** — Web API, cookie authentication, SignalR
- **Angular** — TypeScript SPA
- **SQLite** — interim; WAL + pooling once server-hosted
- **xUnit** — 548 tests
- *(being retired)* WPF + CommunityToolkit.Mvvm

## Created

2026-06-21
