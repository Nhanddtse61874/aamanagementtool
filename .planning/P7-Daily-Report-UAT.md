# P7 Daily Report — UAT (user acceptance test)

**Build:** branch `feature/daily-report-2026-06-25`, 225 tests green, app launches clean.
**How to run:** `dotnet run --project src/TimesheetApp` → click **Daily Report** in the sidebar (no longer a SOON placeholder).

Tick each item; if behavior differs, describe what you saw.

## Input tab (your own standup)
- [ ] **Nav** — "Daily Report" is enabled; clicking it opens the Daily Report view with **Input** + **Team board** sub-tabs and a date toolbar (◀ / date picker / ▶ / Archive week).
- [ ] **DR-07 add (existing request)** — In Today, open the Request combo, pick an existing request → its tasks become pickable; choose a task, type a description, optionally a deadline + status → **Add** → the row appears under Today.
- [ ] **DR-03 add (ad-hoc)** — Type a brand-new code that isn't in the list (e.g. `NEW-REQ-1`) + a task + description → **Add** → it saves (no error), even though the request doesn't exist yet.
- [ ] **DR-04 issues** — On a row, type an issue and **+ Issue** → an amber issue block appears; fill the **Solution** box + pick a status (open/pending/resolved) → **Save**. Leaving the solution blank shows it as pending.
- [ ] **Delete** — A row's **Delete** removes it (your rows, today/yesterday only).
- [ ] **DR-06 edit-lock** — Use ◀ to go back 2+ days → the add-row forms disappear and the status line says the day is locked (read-only). Today and yesterday remain editable.

## Team board tab
- [ ] **DR-08 board** — Switch to **Team board** → one card per active user (dynamic count) for the selected date, each with Yesterday/Today entries and their issues. A user with nothing shows empty sections.
- [ ] **DR-10 live refresh** — Add/delete an entry in Input, then look at Team board → it reflects the change without a manual reload.

## Weekly archive
- [ ] **DR-09 manual** — Click **Archive week** → a `…/Documents/TimesheetApp/StandupArchives/{yyyyMMdd}_daily.md` file is written for the selected week (filename stamped with that week's Monday). With no data for the week it reports "No standup data this week".
- [ ] **DR-09 auto** — After a week passes, the next app launch auto-generates that completed week's markdown if missing (one file per week).

## Notes / differences observed
> (write here)
