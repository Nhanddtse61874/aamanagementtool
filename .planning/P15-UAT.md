# P15 — Task List Grouped Section Bands — UAT

**Branch:** `feature/tasklist-grouped-bands-2026-07-02` · **Build:** clean · **Tests:** 538 green (render test incl.)
**How to run:** kill any running app first, then `dotnet run --project src/TimesheetApp`. Go to the **Task List** tab, **Grid** mode.

AI does **not** mark these done — run the app, compare reality to "Expected", and tell me what matches / differs.

| # | Maps | Check | Expected |
|---|------|-------|----------|
| 1 | OT-1/OT-2 | Open Task List (grid) with a single team active | Backlogs sit under **section bands** — one band per **Project** (ARCS / PlusArcs / ARMS / Other). Band header shows e.g. `ARCS (3)`. The PROJECT column is gone (the band shows it). |
| 2 | OT-3 | Click a band header | That whole group **collapses**; click again → expands. All groups start **expanded**. |
| 3 | OT-4 | Turn on the multi-team filter (check ≥2 teams) | Grouping switches to **Team** — one band per team, header shows `TeamName (n)`. The PROJECT column reappears (per-row), the per-row TEAM chip column stays hidden (band shows the team). |
| 4 | OT-4 | Back to a single team | Grouping returns to **Project**. |
| 5 | OT-5 | Inside a band, edit a row inline: Type / PCT / PCA / Internal / External / Start / End / Progress; open a backlog's ▸ task sub-rows and edit those | **Everything commits & persists exactly as before** — grouping changed nothing about editing. |
| 6 | OT-5 | Edit ✎ Tags on a row / task | Tag dialog works, chips update as before. |
| 7 | OT-6 | Toggle **Gantt** | Gantt view renders as before (bands are grid-only); toggle back to Grid → bands still there. |
| 8 | — | Band order | Project bands in enum order (ARCS→PlusArcs→ARMS→Other); team bands alphabetical. Within a band, rows keep their usual order (by code). |

## Results
_(fill in per row: OK / describe difference)_

## Notes
- Known/accepted (design): grouping disables WPF UI-virtualization; fine at month scale (dozens of rows).
- If a band header looks too plain vs Log Work, that's a styling tweak (easy follow-up) — not a functional fail.
