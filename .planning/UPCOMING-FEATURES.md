# Upcoming Features — locked brainstorm decisions (2026-06-27)

Captured so context compaction doesn't lose them. Implementation order + phase numbers:
**P9 Local Backup → P10 Multi-Team → P11 Export Restructure → P12 3-Month Retention.**
All built on branch `feature/task-list-2026-06-27` (M3 Task List, awaiting UAT, not merged).
Run style: autonomous (quality profile); **PAUSE for user plan-approval before executing P10 and P12** (schema-wide / destructive). P9 + P11 may run autonomously like M3.

---

## P9 — Local DB Backup (user-facing) [a.k.a. "M5"] — DOING FIRST
Distinct from the existing XC-10 `DbBackupHelper` (which copies `{db}.{stamp}.bak` next to the DB before bulk writes — stays in OneDrive). P9 = deliberate, user-controlled backup to a **local folder OUTSIDE OneDrive**.
- **Trigger:** manual "Backup now" button **+** scheduled auto (e.g. once/day on startup if no backup today; toggle on/off).
- **Restore:** YES — list backups, pick one → confirm → safety-copy current DB first → replace → prompt restart.
- **Location:** user **chooses the folder from the start** (no silent default; Browse in Settings; persisted app-local). 
- **Content:** full **.db file only** (timestamped copy).
- **Retention:** keep last N (configurable). No schema change.

## P10 — Multi-Team [a.k.a. "M4"] — schema-wide; PAUSE at plan
- **Team = official company team** (e.g. "Architect Improvement", "Team A"), NEW top-level entity. **Project enum (ARCS/PlusArcs/ARMS/Other) stays** as ticket category. Backlog gets `team_id`.
- **User ↔ Team = many-to-many** (supports a 50-50 member in 2 teams).
- **Scope:** backlog-scoped (Task/TimeLog/Standup inherit via backlog; Standup also gets `team_id`). Users global + team membership. **Tags / Holidays / PCA / Templates / DefaultTasks = global** (shared).
- **DEFAULT backlog PER team** (each team's own Annual Leave/Meeting → correct per-team reporting). DefaultTasks sync materializes per team.
- **Working vs viewing:** ONE **active team** for logging/creating (sidebar team switcher); **multi-team checkbox filter** for VIEWING on: **Backlog list, Task List, Reports, Daily Report board** (Timesheet entry stays active-team only).
- **Migration:** create team **"Architect Improvement"**, set active, assign ALL existing backlogs + users + standup + the existing DEFAULT backlog to it. Schema v7→v8 additive.
- **First-run (fresh DB):** create a default team, set active, seed its DEFAULT + default tasks; **define + validate default values for every setting** (user explicitly asked to design first-time setup).

## P11 — Export Markdown Restructure [a.k.a. "M6"] — after P10
Reworks the existing markdown exports (timesheet, daily, tasklist).
- **Two destinations, MIRROR both every export:** a SharePoint/shared folder (in OneDrive) **and** a local folder. Both configurable.
- **Folder structure** under each chosen root: **a folder per team first**, then the 4 subfolders inside each team folder — `{root}/{TeamName}/timesheet/`, `{root}/{TeamName}/daily/`, `{root}/{TeamName}/tasklist/`, `{root}/{TeamName}/db/` (added 2026-06-27). Put each kind's markdown into its team's subfolder. (Team-aware → depends on P10.)
- **`db/` subfolder contains BOTH:** a full **.db copy** AND the **markdown of pruned (old) months** — written to **both** destinations.
- Team-aware (built after P10) — exports reflect team scoping.

## P12 — 3-Month Data Retention / Prune [a.k.a. "M7"] — after P11; PAUSE at plan (DESTRUCTIVE)
- DB keeps only the **3 most recent months** of **business data**.
- On entering the 4th month: the **1st month's business data** is exported to markdown (into the export structure / `db/` folder) **then DELETED from the live DB**.
- **DELETE ONLY business/history data**: backlogs, tasks, timelogs, standup history of that month. **NEVER delete settings data**: Users, Teams, Tags, PCA contacts, Holidays, Templates, DefaultTasks.
- Data is preserved as markdown (in `db/` folder, both destinations) → "không lo bị mất".
- High risk → mandatory backup before prune (reuse P9/XC-10), idempotency, dry-run/verify, unit tests, user plan-approval before execution.

---

### Cross-feature notes
- P11/P12 depend on P10 (team scoping). P12 depends on P11 (markdown export targets) + P9 (safety backup before prune).
- "SharePoint folder" = treated as a normal local/UNC path the user points at (an OneDrive-synced SharePoint library folder); no SharePoint API.
- Retention prune interacts with Reports (older months only visible via markdown after prune) — confirm reports gracefully handle the 3-month live window.
