# Task List — Grouped Section Bands (Log Work-inspired)

**Date:** 2026-07-02
**Status:** Approved (design) — pending Mode Gate + plan
**Topic:** Group the Task List grid rows under collapsible section bands, adaptively by Team or Project.
**Supersedes:** the deferred "structural rewrite to Log Work's grouped section-band ItemsControl layout" noted in the 2026-07-01 visual pass (`6f2c9fe`). This delivers the grouped-band look **without** the DataGrid→ItemsControl rewrite that was deemed high-risk to the just-fixed inline edits.

## Context / Problem

The Task List grid ([`TaskListTab.xaml`](../../../src/TimesheetApp/Views/Tabs/TaskListTab.xaml)) is a flat `DataGrid`: one row per non-DEFAULT backlog in the selected month, expandable to its tasks via `RowDetailsTemplate`. It already got a visual pass (rounded `TableContainer`, flat progress bar, vertical gridlines, row hover). What is still missing from the Log Work reference is **grouped section bands** — a header band that groups related backlogs.

Inline editing in this grid is fragile and hard-won: `ComboBox`/`DatePicker` in a `DataGridTemplateColumn.CellTemplate` do **not** push their `TwoWay` writes back to the row VM, so every editable cell (Type/PCT/PCA/Internal/External/Start/End/Progress) is driven `OneWay` for display + a code-behind handler (`OnRowTypeChanged`, `OnInternalDeadlineChanged`, …) for the commit. Any layout change that touches these must not disturb them.

## Goal

Add collapsible section bands to the grid, grouping backlogs adaptively, with **zero change** to the inline-edit machinery.

## Locked decisions (from brainstorm, 2026-07-02)

| # | Decision | Value |
|---|----------|-------|
| D1 | Interpretation of "section band" | **Band groups backlogs** (keep the table + inline edits) — NOT a per-backlog card, NOT a DataGrid→ItemsControl rewrite |
| D2 | Grouping key | **Adaptive**: multi-team view active → group by **Team**; otherwise → group by **Project** (enum ARCS / PlusArcs / ARMS / Other) |
| D3 | Band collapse | **Collapsible** — click the band header to collapse/expand the whole group; **default expanded** |
| D4 | Band content | Group name + backlog count, e.g. `ARCS (3)` — no aggregate totals |
| D5 | Implementation approach | **DataGrid grouping** via `CollectionViewSource` + `DataGrid.GroupStyle` (Approach ①). Rejected: interleaved header-rows (②), N nested DataGrids (③) |

## Chosen approach — DataGrid grouping (view-layer only)

Grouping is expressed entirely in the view + one computed VM property. The DataGrid's columns, cell templates, `RowDetailsTemplate`, `RowStyle` (hover + details visibility), and **all code-behind inline-edit handlers stay exactly as-is** — grouping does not touch the row VMs.

### VM changes — `TaskListViewModel` / `TaskListRow`

1. `TaskListRow.GroupKey` (string) — computed when rows are built: `= <multi-team active> ? TeamName : Project`. Uses the same multi-team flag that already drives the per-row `ShowTeam` / `TeamFilter.ShowTeamColumn`.
2. `TaskListRow.GroupOrder` (int) — deterministic group ordering: Project mode → enum order (ARCS=0, PlusArcs=1, ARMS=2, Other=3); Team mode → stable by team name. Used as the primary `SortDescription` so bands appear in a sensible order (not hash order).

Both are set inside the existing row-build loop in `LoadAsync`/reload — recomputed on every reload (including when the team filter changes), so adaptive grouping "just works" without dynamically swapping the group description.

### XAML changes — `TaskListTab.xaml`

1. A `CollectionViewSource` resource: `Source={Binding Rows}`, one `PropertyGroupDescription` on `GroupKey`, `SortDescription` on `GroupOrder` (then the existing row order as secondary).
2. `DataGrid.ItemsSource` → the CVS view instead of `{Binding Rows}` directly.
3. `DataGrid.GroupStyle` with a `ContainerStyle` targeting `GroupItem` whose template is an **`Expander`**:
   - Header = `Name (ItemCount)` band (styled dải, full width, uses existing theme brushes — e.g. `HeaderBg`/`Accent`).
   - `IsExpanded="True"` (D3 default open).
   - Content = the group's `ItemsPresenter` (the normal backlog rows).
4. **Hide the column that became the group key**: when grouping by Project, hide the `PROJECT` column; when by Team, the `TEAM` column is already conditionally shown (`ShowTeamColumn`) and can be hidden while grouped-by-team to avoid redundancy.

Nothing else in the DataGrid definition changes.

### Data flow

`LoadAsync` builds `Rows` (each with `GroupKey`/`GroupOrder`) → `CollectionViewSource` groups + sorts → `DataGrid.GroupStyle` renders one `Expander` band per group → band content = existing rows with existing inline editors. Team-filter change → reload → `GroupKey` recomputed → CVS re-groups. Gantt mode → the whole grid `Border` still hides as today (grouping only affects grid mode).

## Risks & testing

- **Inline edits:** untouched by construction (grouping is a view concern; row VMs and code-behind handlers unchanged). This is the entire reason Approach ① was chosen.
- **WPF virtualization:** enabling `DataGrid` grouping disables UI virtualization by default. Task List is scoped to one month (typically a few dozen backlogs), so this is acceptable; note it but do not add complexity to re-enable virtualization unless a perf problem is observed.
- **Render-crash class:** this project has a recurring WPF render-crash class (TwoWay-on-readonly DPs, Button-style-on-ToggleButton). The `Expander`/`GroupStyle` XAML must render without throwing — covered by the existing STA render guard `TaskListTabRenderTests`; extend/verify it renders the grouped grid.
- **Regression gate:** build clean, existing **536 tests stay green**; grid still switches Grid↔Gantt; expand/collapse of a backlog's task sub-rows still works inside a band.

## Observable truths (goal-backward — feeds `must_haves` at STEP 6)

1. In grid mode, backlogs appear under section bands (a band header per group), not as one flat list.
2. Each band header shows the group name + backlog count (e.g. `ARCS (3)`).
3. Clicking a band header collapses/expands that whole group; groups start expanded.
4. Grouping is by Team when the multi-team view is active, otherwise by Project.
5. Every inline editor (Type/PCT/PCA/Internal/External/Start/End/Progress/Tags) and the task sub-row expand still behave exactly as before.
6. Grid↔Gantt toggle and Gantt view are unaffected.

## Out of scope (YAGNI)

- DataGrid → ItemsControl rewrite; per-backlog cards; always-visible task lists (rejected interpretations ② / A).
- Borderless combo/DatePicker template rewrites.
- Aggregate totals (logged / avg progress) on the band.
- User-configurable grouping toggle or manual group ordering.
- Re-enabling UI virtualization under grouping.
