# P8 Task List — UAT (User Acceptance Test)

**Branch:** `feature/task-list-2026-06-27` · **Date prepared:** 2026-06-27
**Status:** Implementation complete (8 waves), build clean, **314 automated tests green**, QA gate APPROVE, goal-backward VERIFIED.
**This doc:** the things automated tests can't cover — visuals + mouse/keyboard interaction. Please run the app and confirm or describe differences. I do **not** claim these done until you confirm.

## How to run
`dotnet run --project src/TimesheetApp` (or launch `bin/Debug/net8.0-windows/TimesheetApp.exe`).
First launch on your existing DB will **auto-migrate v6→v7** (adds tracking columns + Tags/PcaContacts/Holidays tables). Your data is preserved; a backup is taken before bulk writes (XC-10). The startup will also backfill any completed past month's Task List markdown.

> ⚠️ Suggest backing up `%USERPROFILE%\Documents\TimesheetApp\timesheet.db` once before first run, just for peace of mind on the migration.

## Checklist

### A. Migration & startup (TL-01)
- [ ] App launches without error on your existing DB.
- [ ] No crash dialog; existing Timesheet/Backlog/Daily Report data intact.

### B. Sidebar restructure (TL-02) — high-regression, click each
- [ ] WORKSPACE shows: **Log Work · Backlog · Task List · Daily Report · Reports**; ADMIN: **Users · Settings**.
- [ ] **Log Work** = the weekly entry grid only (no more Entry/Backlog/Reports sub-tabs); logging hours still works.
- [ ] **Backlog** (top-level) opens the backlog list; create/edit works.
- [ ] **Task List** (top-level, SOON pill gone) opens the new screen.
- [ ] **Daily Report** Input/Board still work.
- [ ] **Reports** (top-level) populates month/week/banner.
- [ ] **Users / Settings** unchanged.

### C. Settings — new sections (TAG-01, TL-11, HOL-01)
- [ ] **Tags**: create a tag (type text, pick/paste an emoji icon, pick a color hex/swatch → live preview chip); edit it; delete it.
- [ ] Typing a garbage color does NOT crash the app (falls back to a neutral chip).
- [ ] **PCA contacts**: add a contact, rename it, deactivate it (deactivated drops from dropdowns but old backlogs still show the name).
- [ ] **Holiday calendar**: navigate months (prev/next); click a weekday to mark it a holiday (it styles differently); click again to unmark; weekends already look distinct.

### D. Backlog editor tracking fields (TL-03)
- [ ] Editing a backlog shows a **Tracking** section: internal deadline, external deadline, rough estimate (h), official estimate (h), progress %, note, PCT assignee (User), PCA contact, and tag checkboxes.
- [ ] Save then re-open the same backlog → all values round-trip (incl. checked tags + PCA).
- [ ] Entering progress > 100 or a non-number / negative estimate is rejected gracefully (message, not crash).
- [ ] DEFAULT backlog has no tracking UI.

### E. Task List grid (TL-04/05/06, TAG-02)
- [ ] Month selector filters backlogs to the selected month (DEFAULT excluded).
- [ ] Columns show: code, project, type, PCT, PCA, internal/external deadline, progress (bar + number; `—` when unset), logged hours, estimate.
- [ ] Logged hours match what you've logged for that backlog's tasks.
- [ ] Estimate shows official, or rough when official is blank.
- [ ] Tag chips render (custom: icon+color+text; ordered after any system chip).
- [ ] Expand a row → its tasks + statuses show.
- [ ] "Export this month" writes a markdown file and shows a status message.

### F. Schedule chips (TL-07/08) — set up a scenario
- [ ] A backlog **past its internal deadline** and not all-tasks-Done shows a **red "Late"** chip.
- [ ] A backlog within **2 working days** of its internal deadline, with an estimate, and **behind** (logged hours low vs time elapsed) shows an **amber warning** chip.
- [ ] A backlog where **all active tasks are Done** shows **neither** chip.
- [ ] Holidays you marked are skipped in the "2 working days" / behind calculation (no false late/warning across a holiday).

### G. Gantt (TL-10)
- [ ] Toggle **Grid ↔ Gantt**; the month stays selected.
- [ ] Bars span start → internal deadline over working days (weekends/holidays not shown on the axis — expected).
- [ ] Bar colors match chips: late=red, warning=amber, normal=teal.
- [ ] External (PCA) deadline shows as a marker.
- [ ] A backlog with no start date shows a faint placeholder row (still visible).
- [ ] Collapse/expand the chart area works; wide months scroll horizontally.

### H. Holiday effect on smart-fill (HOL-02)
- [ ] In Log Work smart-fill, a date range covering a marked holiday distributes **no** hours onto the holiday (like weekends).

## Reporting back
For anything that fails, tell me what you saw vs expected (a screenshot helps). I'll infer severity and loop fixes back through STEP 7. Once you sign off, I'll do STEP 10/11 (merge to `main` + finalize SUMMARY/ROADMAP).
