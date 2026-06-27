# Multi-Team (M4 / P10) — Design Spec

**Status:** Research-backed (STEP 4). Authored STEP 5, 2026-06-27. **Execution PAUSES for user plan approval (STEP 6 gate).**
**Mode:** B · **Schema:** v7 → **v8** (additive). Builds on P8 (Task List) + P9 (backup), branch `feature/task-list-2026-06-27`.
**Source of truth:** this doc + `.planning/REQUIREMENTS.md` (TM-01..10) + `.planning/research/P10-*.md` (architecture map + resolved synthesis). The architecture research holds the exhaustive per-method query list — this spec is the behavior contract.

## 1. Concept
Add **Team** as a new top-level org entity (e.g. "Architect Improvement", "Team A"). The existing **Project** enum (ARCS/PlusArcs/ARMS/Other) is unchanged — it stays a ticket category. A backlog belongs to exactly one **Team** and has a Project. **Users ↔ Teams = many-to-many** (a 50-50 member is simply in two teams; no allocation %). Business data (backlogs → tasks/timelogs; standup) is team-scoped; **settings data is global** (Users, Tags, Holidays, PCA, Templates, DefaultTasks). One **active team** drives editing/creation; a **multi-team checkbox** drives viewing.

## 2. Data model (schema v8)
- `Teams(id, name, is_active DEFAULT 1, created_at)`; `UserTeams(user_id, team_id, PK(user_id,team_id))` (no inline FK).
- `Backlogs` + `StandupEntries` gain nullable `team_id INTEGER` (no inline FK, mirrors assignee_user_id/pca_contact_id).
- `Tasks`/`TimeLogs` carry NO team_id (inherit via `Tasks.backlog_id → Backlogs.team_id`). StandupEntries needs its own team_id (ad-hoc rows have null backlog_id).
- Entities: new `Team(Id,Name,IsActive,CreatedAt)`; `Backlog` + `StandupEntry` records gain `TeamId` (nullable, appended). `TimeLogReportRow` gains team id+name as **trailing** fields (no positional reorder — R6).
- Migration: const 7→8, append one step (the two ALTERs); Teams/UserTeams in `CreateTables` (idempotent).

## 3. Data migration & first-run (`ITeamBootstrapService.EnsureBootstrappedAsync`, post-init bootstrap)
Runs in `App.OnStartup` after `InitializeAsync()`, **before** archive backfills + DefaultTaskSync. Idempotent (guard: Teams non-empty → return). XC-10 backup first; own transactions.
- **Existing DB (has business data):** create team **"Architect Improvement"**; `UPDATE Backlogs SET team_id=T WHERE team_id IS NULL` (incl. the existing single DEFAULT → becomes T's DEFAULT); `UPDATE StandupEntries SET team_id=T`; `INSERT UserTeams SELECT id,T FROM Users`; set T active. Silent (no dialog).
- **Fresh DB:** create team **"My Team"** (renamable), set active; per-team DefaultTaskSync seeds its DEFAULT + tasks. The fresh-DB auto-created user (in `MainViewModel.ResolveCurrentUserAsync`) is then `AddMemberAsync`'d to the active team.
- Validated default settings (N-days=3, auto-backup off, retention=30) — already defaulted in code; bootstrap just ensures no null/invalid state (TM-09).

## 4. DEFAULT backlog per team (TM-04)
"DEFAULT" is unique **per team_id** (not global). Drop the initializer's single-DEFAULT seeding; bootstrap repoints the legacy DEFAULT, and `DefaultTaskSyncService.SyncAsync` loops `ITeamRepository.GetActiveAsync()` → `EnsureDefaultBacklogIdAsync(teamId)` (find/create `backlog_code='DEFAULT' AND team_id=T`) → materialize the **global** `DefaultTasks` as tasks under each team's DEFAULT. Creating a team triggers its DEFAULT + a sync pass. Annual Leave/Meeting log under the **active team's** DEFAULT (the grid only shows that team's DEFAULT tasks) → attribute per-team in reports.

## 5. Current-team context (`ICurrentTeamService`, mirrors `ICurrentUserService`)
`ActiveTeamId`/`ActiveTeam`/`AvailableTeams` (the current user's *active* memberships) + `ActiveTeamChanged` event + `InitializeAsync`/`SetActiveTeamAsync`. Resolution: persisted `JsonAppConfig.ActiveTeamId` **if still in AvailableTeams**, else first available (post-migration = "Architect Improvement"). Persisted app-local (per machine/user — it's a UI preference, DATA-07 locality). `InitializeAsync` runs after current-user resolution (needs user id). On change: persist → raise event + send `DataChangedMessage(DataKind.Teams)`. **Services that need the team id inject `ICurrentTeamService` directly** (NOT a `Func<int>` — that would collide with the user-id provider).

## 6. Working scope vs viewing
- **Working (active team only, TM-06):** Log Work grid shows only the active team's tasks (incl. its DEFAULT) via `GetActiveForTimesheetAsync(teamId)`; a new backlog is created with `team_id=active`; a new standup entry carries the active team. No cross-team editing — switch active team to edit elsewhere. Edit affordances on non-active-team rows are disabled; create buttons name the target team.
- **Viewing (multi-team checkbox, TM-07):** Backlog list, Task List, Reports, Daily Board each have a "Teams ▾" checkbox (source = user's active teams; **default = active team only**; session-persisted per screen). Checking ≥2 teams aggregates; a team chip/column appears only when >1 checked (incl. on per-team DEFAULT rows). **Switching active team resets each screen's filter to {new active team}.**

## 7. Query/team-dimension contract (see architecture §2 for the exhaustive list)
Read APIs gain optional `IReadOnlyList<int>? teamIds=null` (null=all, preserves existing tests); create APIs take required `int teamId`. **`teamId 0` = empty (not match-all)** so a fresh pre-setup DB never leaks/crashes. Critical ones: `GetActiveForTimesheetAsync(teamId)`, `BacklogRepository.SearchAsync(term, teamIds)`, `TimeLogRepository.GetReportRowsAsync/GetExportRowsAsync(..., teamIds)` (**R1 leak fix** — already join Backlogs), `StandupRepository.GetEntriesForDayAsync(date, teamIds)`, `GetByCodeAsync("DEFAULT")` → `GetDefaultForTeamAsync(teamId)`. New `ITeamRepository` (CRUD + UserTeams membership, soft-delete like Users, replace-all members like SetTags).

## 8. UI
- **Sidebar active-team switcher** (TM-05): a ComboBox styled like the user chip, docked above it; lists AvailableTeams; setter → `SetActiveTeamAsync`; hidden when the user has only one team.
- **Multi-team checkbox** (TM-07): a shared `TeamFilterViewModel` + `Views/Controls/TeamFilter.xaml` dropped into the 4 view toolbars.
- **Settings "Teams" section** (TM-03): team CRUD (rename/deactivate soft-delete, mirror Users/PCA) + a membership editor overlay (per-team user checklist, replace-all via `SetMembersAsync`). Broadcasts `DataKind.Teams`; team-create triggers DEFAULT + sync.
- New `DataKind.Teams`.

## 9. Reports/export team-aware (TM-08)
Markdown grouping `## {team}` above `## {user}`; Excel + Task List archive get a **Team column**; standup archive groups by team; report drill-down adds a **Team** top node above Project (collapses to one node when a single team is checked). RPT-04 "chưa log" banner = **active-team members** (via UserTeams). Exports filter by the checked teams.

## 10. Edge rules
- Stale active team (deleted/renamed elsewhere, or user removed) → validate on resolve, fall back to first available; never dereference (R5).
- User in zero teams → empty state, working screens disabled (shouldn't occur post-bootstrap).
- Pre-migration TimeLogs on the old global DEFAULT → attributed to "Architect Improvement" (its backlog repointed). Correct per TM-02.

## 11. Testing
**Unit (must):** v7→v8 migration + bootstrap backfill (idempotent, team_id nullable, backup-first); team-filtered queries incl. **export-leak** (R1) and **per-team DEFAULT isolation/no-double-count** (R2); `ICurrentTeamService` resolution incl. **stale-id fallback** (R5); per-team DefaultTaskSync; `GetActiveForTimesheetAsync(teamId)` scoping; RPT-04 team scope; `TestDb.SeedRequestAsync` + `DatabaseInitializerTests` updated to v8 (R6); keep all ~328 prior tests green.
**UAT-only:** sidebar switcher, multi-team checkbox UX, Settings membership editor, team chips/columns, board membership, first-run on a fresh DB.

## 12. Out of scope
Per-user team allocation %, team-level permissions/roles, cross-team task assignment, SharePoint/cloud team sync, deleting a team's data (deactivate only).
