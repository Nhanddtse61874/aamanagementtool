# P18 — Daily Quick Import — UAT

**Branch:** `feature/daily-quick-import-2026-07-02` · **Build:** clean · **Tests:** 541 green (5 new QuickImport).
**How to run:** kill any running app, then `dotnet run --project src/TimesheetApp`. Go to **Daily Report → Input** tab.

AI does **not** mark these done — run the app, compare to "Expected", tell me what matches / differs.

**Setup for a good test:** on a past day (e.g. yesterday) add a few entries (both Yesterday + Today sections) with some issues. Then go to **today** and Quick Import from that day.

| # | Maps | Check | Expected |
|---|------|-------|----------|
| 1 | QI-01 | Input tab, on an editable day (today/yesterday) | A **"⬇ Quick import"** button sits next to "+ Add entry". On a locked day (2+ days ago) both are hidden. |
| 2 | QI-02 | Click "Quick import" | A themed dialog opens with a **Source day** DatePicker (defaults to yesterday). |
| 3 | QI-03/04 | Pick a past day with entries → Import | That day's items (your entries, **both** Yesterday + Today sections) are **appended** to the current day, with their issues (text + status), statuses as-is. Cloned rows are editable. |
| 4 | QI-03 | Import a second time (or into a day that already has items) | Items are **appended** again (duplicates allowed) — order continues after existing ones, no overwrite. |
| 5 | QI-04 | Look at the source day after importing | The **source day is unchanged** (only the current day gained copies). |
| 6 | QI-05 | Import from a day with no entries / status line | Status message like "Nothing to import from that day…"; nothing added. |
| 7 | QI-06 | (multi-team) with a different active team | Only **your** entries for the **active team** are imported. |
| 8 | — | After import | The Input list + the Board refresh immediately (broadcast). |

## Results
_(fill in per row: OK / describe difference)_

## Notes
- Copies the **current user's** entries only (not teammates'). Both sections preserved (yesterday→yesterday, today→today).
- Issue statuses/solutions copied as-is (you edit after). Deadlines copied verbatim.
