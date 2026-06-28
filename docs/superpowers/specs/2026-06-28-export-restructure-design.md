# Export Restructure (M6 / P11) — Design Spec

**Status:** Authored STEP 5, 2026-06-28. Mode B. No DB schema change. Builds on P10 (team-aware exports). Autonomous run.
**Source:** `.planning/REQUIREMENTS.md` (EX-01..07) + brainstorm decisions. Current-state map in this session's P11 exploration.

## 1. Goal
Reorganize the three markdown exports (timesheet, daily/standup, tasklist) + a `.db` copy into a **per-team folder structure**, written (mirrored) to **two configured roots** (a SharePoint/shared folder + a local folder):
```
{root}/
  db/                                   ← one whole-.db copy (EX-05); P12 will also drop pruned-month md here
  {TeamName}/
    timesheet/{yyyyMM}_timesheet.md     ← that team's month timesheet
    daily/{yyyyMMdd}_daily.md           ← that team's week standup (Monday stamp)
    tasklist/{yyyyMM}_tasklist.md       ← that team's month tasklist
    db/                                 ← reserved for P12 (that team's pruned-month md)
```

## 2. Config (EX-01)
`IAppConfig` + `JsonAppConfig` gain `ExportRoot1Path` (shared/SharePoint) + `ExportRoot2Path` (local), app-local, default `""` (mirror ArchivePath/BackupFolder). Settings UI: two Browse+Apply rows (copy the ArchivePath picker pattern). Empty root = skipped. Both empty = legacy flat archive fallback (EX-06).

## 3. Per-team content (EX-04) — refactor, don't duplicate
The existing builders produce **all-teams** files grouped by team. Refactor each to expose a single-team markdown builder, keeping the legacy all-teams method working (so existing tests stay green):
- `StandupArchiveService`: extract `BuildWeekMarkdown(rows scoped to teamIds, weekMonday)` → string; `ExportWeekAsync` keeps writing the flat all-teams file via the same builder with `teamIds=null` (the repos already accept `teamIds`).
- `TaskListArchiveService`: same — `BuildMonthMarkdown(teamIds, year, month)`.
- `ExportService.ExportMarkdownAsync(filter)` already takes `ExportFilter.TeamIds` → call it with `[teamId]` per team.
Per-team content = scope the repo queries to `[teamId]` (P10 team filters); markdown then contains only that team (the `## {team}` grouping collapses to one). '|' escaped (existing `Esc`); no-data period → no file.

## 4. Export hub (EX-02/03/06) — `IExportHubService`
A new orchestrator:
- `Task ExportNowAsync()` — regenerate current + completed periods for every active team into every configured root.
- `Task BackfillAsync()` — startup: for each active team × each completed week/month with data but no file in the structure, generate it (idempotent).
- For each configured root (1 and 2, skip empty): for each active team (sanitized name): write the 3 per-team markdown kinds; once per root write the `.db` copy to `{root}/db/` (reuse `BackupService` copy + retention). Best-effort per root (one failing root doesn't abort the other) — surface a status string.
- Inject: `IAppConfig`, `ITeamRepository`, the 3 builders, `IBackupService` (or its copy logic), `IClock`, `IPathSanitizer`.
- **Supersede flat archives (EX-06):** when an export root is configured, the structured export is the mechanism; the legacy `StandupArchiveService`/`TaskListArchiveService` flat backfills run only when **no** root is configured (App.OnStartup chooses).

## 5. Path sanitizer (EX-07)
`IPathSanitizer.SanitizeSegment(string) → string` (or a static util): strip/replace `Path.GetInvalidFileNameChars()`, trim, collapse; empty → fallback `team-{id}`. Unit-tested. (Replaces the inline pattern at ReportsViewModel.)

## 6. `.db` copy (EX-05)
One whole-`.db` copy per root at `{root}/db/timesheet_{yyyyMMddHHmmssfff}.db` (reuse `BackupService`'s copy + stamp + prune to a keep-count). Not per team.

## 7. Triggers / UI (EX-06)
- Startup (`App.OnStartup`): if any export root configured → `IExportHubService.BackfillAsync()` (replaces the two flat backfills); else keep the legacy `BackfillMissingWeeksAsync`/`BackfillMissingMonthsAsync`.
- Manual: a Settings "Export now" button (+ status); optionally also keep the existing per-screen "Export this month"/"Archive week" buttons working (they can route to the hub per active team, or stay legacy — keep them functional).

## 8. Testing
**Unit:** path sanitizer (invalid chars, empty→fallback); per-team builders produce single-team content; ExportHub writes the structure to both roots (use temp dirs), mirrors, skips empty root, one-root-fail-other-succeeds, no-data→no-file, idempotent overwrite; `.db` copy lands in `{root}/db/`; backfill generates completed periods. Keep all 430 prior tests green (legacy all-teams builders unchanged behavior).
**UAT:** two Browse pickers, "Export now", real folder structure on disk (incl. an OneDrive-synced SharePoint folder path), mirror correctness.

## 9. Out of scope
The 3-month retention/prune (P12 — it will drop pruned-month md into `{root}/{team}/db/`); SharePoint API (a synced folder path is treated as a normal directory); per-user export prefs.
