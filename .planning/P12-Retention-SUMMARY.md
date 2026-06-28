# P12 — 3-Month Retention / Prune (M7) — Phase Summary (DESTRUCTIVE)

**Branch:** `feature/task-list-2026-06-27` · **Date:** 2026-06-28 · **Mode:** B
**Status:** 3 waves + QA done. **Awaiting user UAT** (`.planning/P12-Retention-UAT.md`). **OFF by default.** Schema v8 (+1 Settings marker key, no new table).
**Tests:** 459 → **493 green** (+34). Build clean, 0 warnings.

## What shipped (RT-01..07)
Opt-in retention that archives then deletes business data older than N (default 3) months.
- **Config** (RT-01): `RetentionEnabled` (default false) + `RetentionMonths` (default 3), app-local.
- **R5a deletion** (RT-04/05): time-axis prune — TimeLogs/StandupEntries by `work_date` month; Tasks/Backlogs only when no remaining logs/tasks (spanning backlog survives), `period_month<=cutoff`, never DEFAULT. Children-first, one transaction, FK-safe (RESTRICT). BacklogAudit deleted only for pruned backlogs (NOT-NULL FK), kept for survivors. Settings/reference tables never touched.
- **Archive-before-prune** (RT-03): per-team `{root}/{Team}/db/{yyyyMM}_pruned.md` (both roots) + a full `.db` snapshot to a dedicated **never-auto-pruned** `{root}/db/prune-snapshots/` folder, verified non-zero BEFORE any delete; a month isn't pruned unless its snapshot verified.
- **Triggers** (RT-02): startup (gated on enabled + export root + no conflict-copy) + Settings manual "Run retention now" (confirm dialog) + a write-free **"Preview"** dry-run.
- **Safety** (RT-07): conflict-copy abort first; XC-10 backup; single transaction; `Settings` marker `retention.pruned_through`; post-commit XC-09 journal check; months re-derived each run (stale marker can't cause delete-without-archive). 34 tests incl. spanning-backlog, settings-untouched, archive-fail→no-delete, conflict-abort, idempotency, window edges.

## Commits
`95fb3fc` W1 core+SQL · `c852ab0` W2 archiver+wiring · `e5df032` W3 Settings UI.

## QA / verification
- **Plan Checker**: APPROVE-WITH-FIXES → 5 fixes applied pre-execution (snapshot-retention BLOCKER-1, cutoff pin, conflict gate, AddMonths dup, Settings view name).
- **QA Gate**: APPROVE-WITH-SUGGESTIONS — **NO irreversible-data-loss path found**; 0 Critical/Important. 3 cosmetic/UAT suggestions (backup-null log, optional grace-month buffer, preview-count edge).
- **Goal-Backward** (`.planning/P12-Retention-VERIFICATION.md`): VERIFIED-WITH-GAPS (all LOW; RT-06 empty-render = UAT).
- **Recovery design correction**: markdown is a human summary, NOT byte-exact — the retained `.db` snapshot is the true recovery artifact (now never auto-pruned).

## Deviations (accepted)
- BacklogAudit for pruned backlogs is deleted (FK forces it) — captured in the snapshot.
- All-months-archived-then-single-delete (contiguous archived prefix) instead of strict per-month tx — equivalent/safer.
- No grace-month buffer (prunes ≤ M-N exactly) — **user confirmed 2026-06-28: keep 3 months, no buffer** (do not re-raise). Multi-machine risk mitigated by conflict-copy abort + off-by-default.

## UAT-pending
Preview/Run on a real >3-month DB; on-disk snapshot+markdown; settings survival; pruned months render empty. See UAT doc.
