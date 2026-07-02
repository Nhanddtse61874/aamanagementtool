# P16 — Task List Per-Backlog Card Layout — UAT

**Branch:** `feature/tasklist-grouped-bands-2026-07-02` · **Build:** clean (0 warn) · **Tests:** 538 green (render test covers the card layout)
**How to run:** kill any running app, then `dotnet run --project src/TimesheetApp`. Task List tab, **Grid** mode.

AI does **not** mark these done — run the app, compare to "Expected", tell me what matches / differs.

| # | Maps | Check | Expected |
|---|------|-------|----------|
| 1 | CARD-01 | Open Task List (grid) | Each backlog is a **card** (not a table row). **No horizontal scrollbar** — everything fits width, fields wrap. |
| 2 | CARD-02 | Look at the top of each card | A **full-width tag strip** (chips) sits on top, **always visible** (no scrolling). `✎ Tags` opens the tag dialog; toggling updates the chips. |
| 3 | CARD-03 | Card compact header | Shows caret + **CODE** + **PCT** (assignee dropdown) + **Internal** deadline + **Progress** bar/%. |
| 4 | CARD-03 | Click Progress | Swaps to the 0-100 input; Enter / click-away commits; Escape cancels (as before). |
| 5 | CARD-04 | Click the caret ▸ | Card expands: **Type · PCA · External · Start · End** (labeled, wrapping) + **Logged / Est** + the **task sub-rows**. Click ▾ collapses. |
| 6 | **CARD-05** ⚠️ | **Change Type, PCT, PCA on a card, then reopen the app (or check the DB)** | **The change PERSISTS.** (These moved from code-behind to direct TwoWay — this is the #1 thing to verify; tests can't confirm a real DB write.) |
| 7 | CARD-05 | Change Internal/External deadline | Reason-note popup appears → OK saves (with note), Cancel reverts — as before. |
| 8 | CARD-05 | Change Start / End | Saves directly (no note) — as before. |
| 9 | CARD-05 | Expand a card → edit a task sub-row (Type/PCT/Status/✎Tags) | Persists as before. |
| 10 | CARD-06 | Single team vs multi-team | Cards grouped under **section bands** — by Project (single team) / by Team (multi-team); band `Name (count)`; click band to collapse the group. |
| 11 | CARD-07 | Toggle **Gantt** / change month / Export / team filter | All work exactly as before (cards are grid-only). |

## Results
_(fill in per row: OK / describe difference)_

## Notes / known
- ⚠️ **#6 is the key regression check** — Type/PCT/PCA now commit via TwoWay. If any of them does NOT save (or saves a wrong/blank value on load), tell me — I'll add a guard or revert those to the code-behind commit path.
- Accepted (design): no aligned columns / no alternating rows (cards use inline labels); grouped `ItemsControl` isn't UI-virtualized (fine at month scale).
- Card look (border/spacing/background) is easy to tweak if you want it tighter/looser — not a functional fail.
