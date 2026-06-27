# P10 "Multi-Team" — Research Synthesis (STEP 4)

**Date:** 2026-06-27 · Inputs: P10-ARCHITECTURE / P10-PITFALL / P10-FEATURE research. Resolves all open questions (autonomous, per recommendations). Drives spec + plan. **Execution PAUSES for user plan approval.**

## A. Locked facts (VERIFIED — from architecture research)
- Schema live at **v7** → target **v8**: append one migration step (`ALTER Backlogs/StandupEntries ADD team_id INTEGER` — **nullable**, no inline FK) + `Teams`/`UserTeams` DDL in `CreateTables` (idempotent). `Tasks`/`TimeLogs` inherit team via backlog; StandupEntries needs its own `team_id` (ad-hoc rows have null backlog_id).
- **Data migration = post-init bootstrap service**, NOT inside the init transaction (needs repos + must run before DefaultTaskSync). Guard "Teams empty?" for idempotency; **XC-10 backup first**; own transactions.
- `ICurrentTeamService` mirrors `ICurrentUserService`; active team persisted **app-local** (`JsonAppConfig.ActiveTeamId`), resolved after current-user, validated against the user's active memberships, broadcast via event + `DataKind.Teams`.
- **DEFAULT backlog becomes unique per team_id**; `DefaultTaskSyncService.SyncAsync` loops active teams, each `EnsureDefaultBacklogIdAsync(teamId)` + materialize global `DefaultTasks` per team. Drop the initializer's single-DEFAULT seeding (bootstrap + per-team sync own it).
- **Highest-impact change:** `ITaskRepository.GetActiveForTimesheetAsync()` → takes `teamId` (Log Work grid = active team only). Read APIs gain optional `IReadOnlyList<int>? teamIds=null` (null=all, preserves tests); create APIs take required `int teamId`.
- **DI collision (bug risk):** a bare second `Func<int>` for team-id would clobber the existing user-id `Func<int>` → **inject `ICurrentTeamService` directly** into TimeLogService/StandupService/DefaultTaskSync instead.

## B. OPEN QUESTIONS — RESOLVED (autonomous)
| # | Question | Decision |
|---|---|---|
| 1 | RPT-04 "chưa log" banner scope | **Active-team-scoped** (members of active team via UserTeams), matches TM-06. |
| 2 | Export/markdown team representation | **Team grouping level**: markdown `## {team}` above `## {user}`; Excel + tasklist-archive get a **Team column**; standup archive groups by team. |
| 3 | Report drill-down extra Team level | Add **Team** as the top node above Project in the RPT-03 tree (collapses to a single node when one team checked). |
| 4 | DEFAULT seeding ownership | **Drop** `EnsureDefaultBacklog` from the initializer; bootstrap (migration repoint) + per-team `DefaultTaskSync` own per-team DEFAULT creation. |
| 5 | First-run team name | Fresh DB → auto-create **"My Team"** (renamable in Settings), no blocking dialog (keep zero-config). Existing DB → silent **"Architect Improvement"** migration. |
| 6 | team-id provider | **Inject `ICurrentTeamService`** (no `Func<int>` for team — avoids the collision). |
| F-Q3 | Reset view filter on team switch | **Yes** — switching active team resets each screen's multi-team checkbox to {new active team}. |
| F-Q5 | Cross-team editing | **No** — editing is active-team-only; to edit another team's data, switch active team first. The switcher is the "working as Team X" indicator; create buttons name the target team; edit affordances on non-active-team rows disabled. |
| F-Q8 | User in zero teams | Empty state ("ask an admin to add you to a team"), working screens disabled. Shouldn't occur post-migration/first-run. |
| F-Q9 | "chưa log" per-team | Active-team members (same as #1). |
| F-Q11 | Daily Board membership | Board shows **members of the checked teams** (behavior change from "all active users"), per TM-07. |
| F-Q7/10 | 50-50 allocation | **No percentage/allocation** — plain dual membership; a 50-50 member simply appears in both teams. |

## C. Top risks → mitigations (from pitfall research)
- **R1 (Critical) export-query leak**: `GetExportRowsAsync` (+ whole-team grid, reports) has no team filter today → would leak all teams. Add team predicate (it already joins Backlogs). Unit-test the filter.
- **R2 (Critical) DEFAULT mis-assign**: `EnsureDefaultBacklogIdAsync` via `GetByCodeAsync("DEFAULT")` breaks with per-team DEFAULT → use `GetDefaultForTeamAsync(teamId)`. Test per-team isolation + no double-count.
- **R3 (Critical) migration corruption**: `team_id` must be **nullable**; bootstrap (create team + backfill) runs **before** per-team DefaultTaskSync; XC-10 backup before the bulk backfill UPDATE; idempotent "Teams empty?" guard. Add a v7→v8 upgrade + backfill test.
- **R4 (High) RPT-04 team scope** needs UserTeams membership (Users has no team_id) — use `GetUserIdsForTeamAsync`.
- **R5 (High) stale active team**: validate persisted ActiveTeamId against the user's active memberships on resolve; fall back to first available; never dereference a stale/deleted team.
- **R6 (High) test regression**: `TestDb.SeedRequestAsync` must set a team_id (default a test team); `DatabaseInitializerTests` table-count/version pins update to v8; keep `TimeLogReportRow` positional ctor stable (add team as a trailing optional field or a sibling field, not a reorder). Treat `teamId 0` as "empty" (not match-all) so a fresh DB pre-setup never leaks/crashes.

## D. Wave plan (adopted from architecture §7, 9 waves, zero same-wave file overlap)
W1 Data+schema+repos → W2 current-team service + bootstrap migration + config → W3 DEFAULT-per-team + working-scope in TimeLogService → W4 standup team scope → W5 Settings Teams UI → W6 active-team switcher (MainView) → W7 multi-team checkbox on 4 screens → W8 reports/export team-aware → W9 DI + startup order + first-run user→team join.
Cross-wave file hotspots flagged: `MainViewModel.cs` (W6,W9), `ReportsViewModel.cs` (W7,W8) — run sequentially (parallelization:false), commit per wave. `DatabaseInitializer.cs` (W1), `App.xaml.cs` (W9) single-owner.
