# P19 — Dark Mode — UAT

**Branch:** `feature/dark-mode-2026-07-02` · **Build:** clean · **Tests:** 538 green (ThemeService swap + palette parity).
**How to run:** kill any running app, then `dotnet run --project src/TimesheetApp`.

AI does **not** mark these done — run the app, compare to "Expected", tell me what matches / differs (esp. any color that reads poorly — I tune `Palette.Dark.xaml`).

| # | Maps | Check | Expected |
|---|------|-------|----------|
| 1 | DM-01 | Settings → **Appearance → Dark mode** checkbox, toggle it ON | The **whole app** turns dark **instantly** — no restart. Sidebar, tabs, cards, tables, badges, dialogs. |
| 2 | DM-01 | Toggle OFF | Back to the exact light theme (unchanged from before). |
| 3 | DM-03 | In dark mode, visit **every** tab: Log Work (Timesheet), Backlog, Task List (grid + a card), Daily (Input + Board), Reports, Users, Settings | **No light patches** — every surface/text/border is dark-themed. Open a few dialogs (Add entry, Quick import, Tag picker, deadline note) — also dark. |
| 4 | DM-03 | Readability in dark | Text is legible on its background; accent (teal) buttons + red danger buttons readable; amber "at risk" + green "resolved" badges legible. |
| 5 | DM-02 | Toggle dark ON, **close & reopen** the app | It **starts in dark mode** (persisted to appsettings.json). Toggle off + reopen → starts light. |
| 6 | DM-04 | Light mode overall | Looks identical to before P19 (no regressions). |
| 7 | — | Task List **Gantt** in dark | Bars/labels: the Gantt canvas reads theme brushes on redraw; if it looks light until you interact, tell me (I can force a redraw on theme change). |

## Results
_(fill in per row: OK / describe difference — especially any color that reads poorly)_

## Notes
- Dark palette (`Palette.Dark.xaml`) is a first tuned pass (slate + teal). **Tell me any color to adjust** — it's a one-line change per key, no rebuild-of-logic.
- Fixed by design (theme-independent): drop-shadows (`#0F172A`) and the green "online/active" dot (`#22C55E`).
- Mechanism: `DynamicResource` + palette-dictionary swap (`ThemeService`); no restart needed. Verified: ThemeService swap test + Light/Dark key-parity test.
