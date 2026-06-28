# P11 Export Restructure — UAT

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-28
**Status:** 3 waves done, **458 tests green**, QA APPROVE-WITH-SUGGESTIONS (I-1 folder-collision fixed). No DB schema change.

## Run
`dotnet run --project src/TimesheetApp`.

## Checklist
### A. Configure (EX-01)
- [ ] Settings → "Export logs": pick a **Shared/SharePoint folder** (root 1) and a **Local folder** (root 2) via Browse → Apply. Paths persist after restart.

### B. Export now + structure (EX-02/03/04/05)
- [ ] Click **"Export now"**; status shows ok per root (or failed: …).
- [ ] In **each** chosen root, verify the structure:
  - `{root}/db/timesheet_<stamp>.db` (one whole-DB copy per root)
  - `{root}/{TeamName}/timesheet/{yyyyMM}_timesheet.md`
  - `{root}/{TeamName}/daily/{yyyyMMdd}_daily.md`
  - `{root}/{TeamName}/tasklist/{yyyyMM}_tasklist.md`
- [ ] Both roots contain the **same** structure (mirror).
- [ ] Each team's files contain **only that team's** data (open a couple to confirm no other team's rows).
- [ ] A team/period with no data produces **no file** (not an empty file).
- [ ] Team names with odd characters get a safe folder name; two similar names don't collide (the I-1 fix — one gets a `-{id}` suffix).

### C. Startup backfill + supersede (EX-06)
- [ ] With a root configured, restart → completed past weeks/months get generated into the structure automatically (no duplicates on a second restart — idempotent).
- [ ] With **no** root configured, the app still writes the **legacy flat** StandupArchives/TaskListArchives (backward-compat) and does not error.

### D. Robustness
- [ ] If one root is unreachable/invalid, the other still exports (status shows the failure for the bad one).
- [ ] A SharePoint-synced folder path works like a normal local path.

## Report back
Anything off → expected vs actual (a screenshot of the folder tree helps). This is the last feature before **P12 (3-month retention/prune)** — which I'll stop at the plan for your approval (it deletes data).
