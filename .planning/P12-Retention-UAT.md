# P12 Retention/Prune — UAT (DESTRUCTIVE — test on a COPY first)

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-28
**Status:** 3 waves done, **493 tests green**, QA APPROVE (no data-loss path found), goal-backward VERIFIED. **OFF by default.**

## ⚠️ Before you test
Retention **deletes** business data older than N months. **Test on a COPY of your DB first** (point the DB path at a copy in Settings, or back up `timesheet.db`). It only runs when you enable it AND an export root is configured.

## Run
`dotnet run --project src/TimesheetApp`. You need an **export root configured** (P11 "Export logs") so pruned months are archived first.

## Checklist
### A. Config (RT-01)
- [ ] Settings → "Data retention": toggle **Enable** + set **Months** (default 3) + Apply. Persists after restart. Default is OFF.

### B. Preview / dry-run (RT-07) — safe, writes nothing
- [ ] Click **Preview** → shows the cutoff month + per-month counts (StandupEntries/TimeLogs/Tasks/Backlogs) that WOULD be pruned. Nothing is deleted (verify your data is intact after Preview).
- [ ] With no data older than N months → Preview says nothing to prune.

### C. Run retention (RT-02..05) — destructive
- [ ] Click **"Run retention now"** → a confirm dialog warns it deletes data older than N months (archived first). Confirm.
- [ ] After it runs: for each configured root, verify on disk:
  - `{root}/{Team}/db/{yyyyMM}_pruned.md` (per-team markdown of the pruned month)
  - `{root}/db/prune-snapshots/timesheet_{yyyyMM}_pre-prune_<stamp>.db` (the **full DB snapshot** — your real recovery artifact; this folder is never auto-pruned)
- [ ] The pruned month's data is gone from the live DB; the **recent 3 months remain**.
- [ ] **Spanning backlog check:** a backlog from an old month that still has recent (in-window) time logs **survives** with its recent logs intact (only its old logs are gone).
- [ ] **Settings survive:** Users, Teams, Tags, PCA contacts, Holidays, Templates, default tasks are all intact. Each team's DEFAULT (Annual Leave/Meeting) survives.

### D. After-prune behavior (RT-06)
- [ ] Selecting a pruned month in Task List / Reports / Daily shows **empty** (no crash).

### E. Safety gates
- [ ] With retention OFF (or no export root) → "Run retention now"/startup does nothing (status says so).
- [ ] Re-running retention is a no-op (nothing left to prune).

## Decision to confirm (QA S2)
Currently prunes months **≤ M-3** exactly. A teammate's *unsynced* edits to a just-aged-out month could be at risk on multi-machine OneDrive (mitigated by the conflict-copy abort + off-by-default). If you want an extra safety margin, I can add a **1-month grace buffer** (prune only ≤ M-4). Tell me if you want that.

## Report back
Anything off → expected vs actual. Recovery note: if a prune ever deletes something wrong, restore from the `prune-snapshots/` `.db` (or P9 backup) — that's the byte-exact copy; the markdown is a human-readable summary only.
