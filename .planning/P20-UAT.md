# P20 — Task List "Continue on next month" — UAT

**Branch:** `feature/tasklist-continue-2026-07-02` · **Build:** clean · **Tests:** 541 green (5 new continuation tests).
**How to run:** kill app, then `dotnet run --project src/TimesheetApp`. Task List tab, a concrete month selected.

| # | Maps | Check | Expected |
|---|------|-------|----------|
| 1 | OT-1 | Task List on a concrete month (e.g. this month) | Each backlog card has a **"↻ Continue"** button (in the tag strip, next to ✎ Tags). Switch Month to **"All months"** → the button disappears. |
| 2 | OT-2 | Click "↻ Continue" on a backlog | A copy is created for **next month** with **Type = "Continue"**, keeping progress + fields + backlog tags. Status line shows "Đã continue '…' → {yyyy-MM}". The **original stays** in the current month unchanged. |
| 3 | OT-2 | Switch Month to next month | The continued backlog appears there (Type = Continue). |
| 4 | OT-3 | Give the backlog some tasks (Todo/In-process/Done), then Continue → check next month's copy | Only the **not-Done** tasks were copied (Todo + In-process); the Done task was not. Task type/assignee + task tags carried over. |
| 5 | OT-4 | Continue the same backlog **again** to the same next month | **Blocked** — status line "'…' đã có ở {yyyy-MM} — không tạo lại." No duplicate created. |
| 6 | OT-5 | (optional) Backlog history | A "continued" audit entry exists on the copy (from the source month). |

## Results
_(fill in per row: OK / describe difference)_

## Notes
- "Next month" = the selected month + 1 (rolls Dec→Jan). Copy is per **current user's active team** scope for the duplicate guard.
- Progress % is **kept** on the copy (per your choice). Only **not-Done** tasks copy.
- Status feedback reuses the Task List toolbar status line (same one as Export).
