# P10 Multi-Team — UAT

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-28
**Status:** 9 waves done, schema v8, **430 tests green**, QA gate PASSED (BLOCK→fixed), goal-backward VERIFIED.
This doc = the visual/interaction things automated tests can't cover. Run the app and confirm.

## Run
`dotnet run --project src/TimesheetApp`. First launch on your existing DB **auto-migrates v7→v8** and runs a **post-init bootstrap**: creates team **"Architect Improvement"**, assigns ALL existing backlogs/standup/users to it, sets it active (a backup is taken first). Back up `…\Documents\TimesheetApp\timesheet.db` once before first run for peace of mind.

## Checklist
### A. Migration & startup
- [ ] App launches; existing data intact; sidebar shows a **team switcher** (TEAM) above your user chip showing "Architect Improvement".
- [ ] Log Work / Backlog / Task List / Daily Report / Reports / Settings all open without error.

### B. Teams in Settings
- [ ] Settings has a **Teams** section: create a team (e.g. "Team B"), rename it, deactivate it.
- [ ] Creating a team gives it its own DEFAULT (Annual Leave/Meeting appear when it's active).
- [ ] **Members**: open a team's membership editor, check/uncheck users, Save; reopen reflects it. Add yourself to "Team B".

### C. Active team switcher + live update (I3 fix)
- [ ] After adding yourself to "Team B", the **sidebar switcher updates without restart** (shows both teams).
- [ ] Switching active team reloads Log Work / Backlog / Task List to that team's data.
- [ ] Deactivating your active team in Settings falls back gracefully (no crash).
- [ ] Switcher is **hidden** if you belong to only one team.

### D. Working scope (active team only)
- [ ] Log Work grid shows only the **active team's** tasks (incl. its DEFAULT).
- [ ] Creating a backlog stamps the **active team** (it appears under that team, not others).
- [ ] A new Daily Report entry belongs to the active team.

### E. Multi-team view filter (C1 fix — the big one to eyeball)
- [ ] On Backlog list, Task List, Reports, Daily board there's a **"Teams ▾" dropdown** — **it must open without crashing** (this was the fixed bug).
- [ ] Default shows only the active team; checking a 2nd team aggregates both; a **team chip/column** appears when >1 team is shown.
- [ ] Switching active team resets the filter to the new active team.

### F. Reports / exports team-aware
- [ ] Reports drill-down has a **Team** level at the top (Team → Project → Backlog → Task → Date).
- [ ] Markdown export groups by `## {team}`; Excel has a Team column; only the selected team(s) appear (no other team's data leaks).

### G. Per-team DEFAULT
- [ ] Annual Leave/Meeting logged under different active teams attribute to that team in Reports (no cross-team mixing).

### H. Fresh-DB first run (optional, if you can test on a new DB)
- [ ] A brand-new DB creates one active team "My Team", you're auto-joined, and there's exactly one DEFAULT (no stray empty-team data — I1 fix).

## Report back
Anything off → describe expected vs actual (screenshot helps). Sign off → I do STEP 11 (merge the whole branch: M3 Task List + P9 Backup + P10 Multi-Team).
