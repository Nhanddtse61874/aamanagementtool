# P7 — Daily Report (Standup) — Summary

**Milestone:** M2 · **Branch:** `feature/daily-report-2026-06-25` · **Date:** 2026-06-26
**Status:** Implemented + tested (225 green); awaiting user UAT before merge.

## What shipped
A Daily Standup feature replacing the "Daily Report" SOON nav placeholder:
- **Input tab** — each member self-fills their own Yesterday/Today entries for a selected date. Add rows by
  picking an existing request (its tasks become pickable; deadline/status typed) **or** typing an ad-hoc code
  (request need not exist yet). Multiple issues per entry, each with an optional solution (blank = pending)
  and a status (open/pending/resolved); issues are collaboratively editable.
- **Team board tab** — read-only, one card per active user for the day (dynamic team size).
- **Weekly markdown archive** — one file per week under `Documents/TimesheetApp/StandupArchives/{yyyyMMdd}_daily.md`
  (stamp = week's Monday), auto-backfilled for any completed week on every app startup; plus a manual button.
- **Edit-lock** — a member edits only their own rows, and only when the day is today or yesterday.

## Coverage
DR-01..10 all implemented; mapping in `docs/superpowers/plans/2026-06-25-P7-daily-report.md`. Spec:
`docs/superpowers/specs/2026-06-25-daily-report-standup-design.md`.

## Tests (+32, total 225)
- `StandupRepositoryTests` (7) — CRUD round-trip, ad-hoc null request, range, cascade delete.
- `StandupServiceTests` (11) — edit-lock + owner gate, validation, grouping, team board, collaborative issues.
- `StandupArchiveServiceTests` (6) — filename stamp, export content, no-data⇒no-file, idempotent, startup backfill.
- `DailyReportViewModelTests` (8) — load/lock, add (ad-hoc), delete, add-issue, archive messaging.

## Schema
v4 → **v5**: additive migration creates `StandupEntries` + `StandupIssues` (+ indexes), gated on
`PRAGMA user_version`. M1 tables/data untouched.

## Deliberate decisions (not re-litigated)
No auto-carry of Yesterday; no reverse-link of ad-hoc codes to later-created requests; deadline/status entered
manually at the task-row level; team size dynamic; per-day persistence + weekly markdown archive.

## Follow-ups (backlog, non-blocking)
- Inline edit of an existing entry's fields (today: delete + re-add).
- Per-row reorder; richer board styling pass during UAT.

## Next steps
1. User UAT via `.planning/P7-Daily-Report-UAT.md`.
2. On approval: merge `feature/daily-report-2026-06-25` → `main` (or open a PR), then update ROADMAP "Shipped".
