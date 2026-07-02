# P15 / P16 / P17 — SUMMARY (Task List card layout + auto-provision user)

**Shipped:** 2026-07-02 · **Merge:** `bc4c02f` "Merge feature/tasklist-grouped-bands-2026-07-02 into main" → `origin/main`.
**Mode:** A (all three). **Build:** clean (0 warnings). **Tests:** 536 green.
**Branch:** `feature/tasklist-grouped-bands-2026-07-02` (pre-merge tip `9a0fdf1`, deletable).

## What shipped

### P15 — Grouped section bands (view-layer)
Group the Task List grid rows under collapsible **section bands**, adaptively by **Team** (multi-team view) or **Project** (else), band header = `Name (count)`, default expanded.
- `TaskListRowVm.GroupKey` (adaptive) + `GroupOrder` (project enum rank / team alpha rank); VM `GroupByProject`/`GroupByTeam` flags. `Rows` order unchanged (Gantt reads it).
- Spec `docs/superpowers/specs/2026-07-02-tasklist-grouped-section-bands-design.md`; plan `…/plans/2026-07-02-P15-tasklist-grouped-bands.md`.

### P16 — Per-backlog card layout (definitive rewrite)
`DataGrid` → grouped `ItemsControl` of **cards** so **tags sit in a full-width strip on top of each card** (always visible, no horizontal scroll).
- Card = tag strip (top) + compact header (caret · CODE · Project[team-mode] · PCT · Internal · External · Progress) + expandable detail (Type/PCA/Start/End + Logged/Estimation + task sub-rows).
- **Type/PCT/PCA → direct `TwoWay`** (removed the `OnRowType/Pct/PcaChanged` code-behind — the DataGrid CellTemplate write-back bug does not apply in an `ItemsControl`). Deadlines (reason-note popup), Start/End, Progress click-to-edit, Tags dialog, expand — kept.
- Reuses P15's `GroupedRows` CVS + `GroupStyle` band (now on the `ItemsControl`).
- Tweaks (Fast Lane, same day): External surfaced in the compact header; expanded "Est" → "Estimation"; **no-progress defaults to 0%** (bar always shown).
- Spec `…/specs/2026-07-02-tasklist-card-layout-design.md`; plan `…/plans/2026-07-02-P16-tasklist-card-layout.md`.

### P17 — Auto-provision current user (Fast Lane)
On startup, an **unmapped Windows account auto-creates** a user named after `Environment.UserName` + maps `windows_username` + joins the active team — no manual "add user" step, no picker (previously only on a fresh/empty DB; otherwise it prompted `SelectUserDialog`).
- `MainViewModel.ResolveCurrentUserAsync` — removed the `active.Count==0` gate + the dialog branch. `selectUser` delegate + `App.ShowSelectUserDialog` retained as an unused fallback seam.

## Decisions
- P15: view-layer DataGrid grouping (not a rewrite) — chosen for zero inline-edit risk. Superseded for *layout* by P16, but its grouping model (GroupKey/GroupOrder/CVS/GroupStyle) is retained by P16.
- P16: full `DataGrid`→`ItemsControl` rewrite chosen deliberately ("làm trọn vẹn") to get tags on top + to drop the fragile CellTemplate combo commit workaround.
- P17: always auto-create (Option A) — DBs live per Windows profile, so duplicate-user risk is negligible.

## Residual risk / follow-up (non-blocking — user chose to merge before formal UAT)
- **P16 Type/PCT/PCA `TwoWay` persist** is not unit-testable (render test covers render-safety only) — needs a live-DB confirm. If a combo fails to save (or writes null on load), revert those three to the code-behind commit path.
- **P17 auto-provision** — verify on an unmapped Windows account / fresh DB that the app lands in a usable session with no picker.

## Tests
- +`TaskListViewModelTests`: `Grouping_by_project_when_single_team`, `Grouping_by_team_when_multi_team`; `Progress_formats_null_as_zero` (was `_as_dash`).
- `TaskListTabRenderTests`: now guards the card layout (STA render, expanded card).
- `MainViewModelTests`: `NeedsSelection_withExistingUsers_autoCreatesNewUser` (replaced the picker test); removed 2 obsolete cancel tests.

## Not done / out of scope
- UI virtualization under grouped `ItemsControl` (month-scale, YAGNI). Aligned columns / alternating rows (traded away for cards). Gantt unchanged. No schema change.
