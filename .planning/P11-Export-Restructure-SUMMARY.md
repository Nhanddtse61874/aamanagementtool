# P11 — Export Restructure (M6) — Phase Summary

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-28 · **Mode:** B
**Status:** 3 waves + QA fix done. **Awaiting user UAT** (`.planning/P11-Export-Restructure-UAT.md`). No DB schema change.
**Tests:** 430 → **458+ green**. Build clean.

## What shipped (EX-01..07)
Reworks the 3 markdown exports + a `.db` copy into a per-team folder structure mirrored to two configured roots.
- **Two export roots** (Shared/SharePoint + Local) in Settings, app-local persist (EX-01).
- **Structure** `{root}/{TeamName}/{timesheet,daily,tasklist}/` + `{root}/db/` whole-`.db` copy, **mirrored to both roots** (EX-02/03/05).
- **Per-team content**: each team folder = that team's data only — via fetch-relocating builder refactor (`BuildWeekMarkdownAsync`/`BuildMonthMarkdownAsync` scoped to `[teamId]`); legacy all-teams `ExportWeekAsync`/`ExportMonthAsync` delegate with `teamIds=null`, byte-identical (EX-04).
- **Triggers**: startup backfill of completed periods when a root is configured (else legacy flat archive) + a manual "Export now" (EX-06).
- **Path sanitizer** for safe team-folder names, with collision-suffix (EX-07 + QA I-1).
- `BackupService.BackupToFolderAsync(folder, keep)` reused for the `{root}/db` copy.
- New `IExportHubService` orchestrates per-team × per-root, best-effort per root, idempotent, no-data→no-file.

## Commits
`869271e` W1 config+sanitizer+UI · `9eb9e31` W2 builders+hub+BackupToFolder · `8ba7371` W3 DI+startup+Export-now · (+ QA I-1 collision fix).

## QA / verification
- **Plan Checker**: APPROVE-WITH-FIXES → 4 fixes applied pre-execution (BackupToFolder overload, GetAllAsync incl. inactive, fetch-relocating builder + regression pinning, SettingsViewModel ctor sites).
- **QA Gate**: APPROVE-WITH-SUGGESTIONS — 0 Critical; builder byte-identical + no cross-team leak verified; **I-1 (team-folder collision)** fixed.

## UAT-pending
Two Browse pickers, "Export now", the real on-disk structure + mirror across a SharePoint-synced folder, startup backfill vs legacy fallback. See UAT doc.

## Out of scope (→ P12)
3-month retention/prune drops pruned-month markdown into `{root}/{team}/db/` and deletes old business data from the DB — separate phase, pauses at plan.
