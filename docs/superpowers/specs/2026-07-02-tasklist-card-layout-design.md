# Task List — Per-Backlog Card Layout (P16)

**Date:** 2026-07-02
**Status:** Approved (design) — pending Mode Gate + plan
**Topic:** Rewrite the Task List grid from a `DataGrid` to a grouped `ItemsControl` of per-backlog **cards**, so each backlog's **tags sit in a full-width strip on top** (always visible, no horizontal scroll).
**Builds on:** P16 keeps P15's section bands (`docs/superpowers/specs/2026-07-02-tasklist-grouped-section-bands-design.md`) — the `GroupKey`/`GroupOrder` VM work and the `GroupedRows` `CollectionViewSource` + `GroupStyle` band are reused on the `ItemsControl`. P16 supersedes P15's **DataGrid** row layout (same branch `feature/tasklist-grouped-bands-2026-07-02`).

## Context / Problem

After P15 the Task List is a grouped `DataGrid` (one row per backlog under Project/Team section bands). Tags live in the **rightmost column** (`Width="1.5*" MinWidth="200"`), so on a normal window the user must **scroll right** to see/edit them. The user wants tags **above each backlog, always visible**.

A `DataGrid` cannot place per-row, full-width content *above* the cells (row-details render *below*, only on expand). Delivering "tags on top, always visible" therefore requires replacing the `DataGrid` with an `ItemsControl` whose item template is a **card**. This is the DataGrid→ItemsControl rewrite deferred twice before as "high-risk to inline edits"; the user explicitly chose to do it definitively now ("làm trọn vẹn, dứt điểm").

**Silver lining:** the Task List inline edits use `OneWay` bindings + code-behind commit handlers *only because* a `ComboBox`/`DatePicker` inside a `DataGridTemplateColumn.CellTemplate` does not push its `TwoWay` write back to the row VM (the root of the P13 "parent-row Type/PCT/PCA combo didn't save" bug). Inside a plain `ItemsControl` `DataTemplate`, `TwoWay` works normally — so the Type/PCT/PCA combos move to clean `TwoWay` and their write-back workaround handlers are deleted.

## Locked decisions (from brainstorm, 2026-07-02)

| # | Decision | Value |
|---|----------|-------|
| D1 | Layout | `DataGrid` → `ItemsControl`; **each backlog = a card**. Column-header row is dropped (cards use inline labels). |
| D2 | Grouping | **Keep** P15 section bands — reuse `GroupedRows` CVS + `GroupStyle` Expander band, now on the `ItemsControl`. |
| D3 | Card compact (always visible) | Full-width **tag strip** (chips + `✎ Tags`) on top; header line = **CODE** + **PCT (assignee, editable)** + **Internal deadline (editable)** + **Progress** (bar+%, click-to-edit) + **expand caret**. |
| D4 | Card expanded (on caret) | Flow-wrapped labeled fields: **Type · PCA · External · Start · End**; display **Logged · Estimate**; then the **task sub-rows** (as today). |
| D5 | Inline-edit wiring | **Type/PCT/PCA → direct `TwoWay`** (delete `OnRowType/Pct/PcaChanged` code-behind). **Keep** existing paths: Internal/External deadline (`SelectedDateChanged` → reason-note popup → `CommitDeadlineAsync`), Start/End (`CommitStartEndAsync`), Progress click-to-edit, Tags modal dialog, expand via `ToggleExpandCommand`. |
| D6 | Field layout | Flow labeled, wrapping by width. No aligned columns (accepted trade-off for no horizontal scroll). |
| D7 | Project/Team in card | Show **Project** (muted) in the card header only when grouped by Team (reuse `GroupByProject`); **Team** never shown in the card (its band shows it). |

Unchanged: month selector, Grid↔Gantt toggle, chart-collapse, Export-this-month, `TeamFilter`, the Gantt canvas/model, and every VM commit/persist/audit method.

## Chosen approach

Replace the `<DataGrid>…</DataGrid>` block (inside the existing rounded `TableContainer` border, hidden in Gantt) with:

- A `ScrollViewer` (the `DataGrid` gave scrolling for free; `ItemsControl` needs one) wrapping
- An `ItemsControl` with `ItemsSource="{Binding Source={StaticResource GroupedRows}}"`, the **same** `GroupStyle` Expander band from P15 (moved from `DataGrid.GroupStyle` to `ItemsControl.GroupStyle`), and an `ItemTemplate` = the **card** `DataTemplate`.

### Card `DataTemplate` (bound to `TaskListRowVm`)

- Root `Border` (card chrome: subtle border/background, `CornerRadius`, `Margin` between cards; hover highlight like today's row hover).
- **Tag strip** (top, full width): the existing `Chips` `ItemsControl` (`WrapPanel` + `ChipTemplate`) + the `✎ Tags` `Button` (`Click="OnEditBacklogTagsClick"`). Reuses `ChipTemplate` verbatim.
- **Compact header** (`StackPanel`/`Grid`): `BacklogCode` (bold); muted `Project` (visible only when `GroupByProject == false`); `PCT` `ComboBox` (`AssigneeOptions`, `SelectedValue="{Binding EditPctUserId, Mode=TwoWay}"`); `Internal` `DatePicker` (OneWay + `SelectedDateChanged="OnInternalDeadlineChanged"`, `Tag="{Binding}"`); Progress (the existing bar-display / click-to-edit `Grid`); expand caret `Button` → `ToggleExpandCommand`.
- **Expanded detail** (`Visibility` bound to `IsExpanded`): a `WrapPanel` of labeled editors — `Type` `ComboBox` (`SelectedItem="{Binding EditType, Mode=TwoWay}"`), `PCA` `ComboBox` (`SelectedValue="{Binding EditPcaId, Mode=TwoWay}"`), `External`/`Start`/`End` `DatePicker`s (OneWay + their `SelectedDateChanged` handlers, `Tag="{Binding}"`), display `LoggedHoursText`/`EstimateText`; then the existing task sub-rows `ItemsControl` (`TaskRows`, unchanged template) + the empty-state hint.

### Code-behind (`TaskListTab.xaml.cs`)

- **Delete** `OnRowTypeChanged`, `OnRowPctChanged`, `OnRowPcaChanged` (Type/PCT/PCA now commit via `TwoWay` → `OnEditTypeChanged`/`OnEditPctUserIdChanged`/`OnEditPcaIdChanged` → `Commit()`, which already exist in the VM).
- **Keep + rewire** to the card's named elements: `OnInternalDeadlineChanged`, `OnExternalDeadlineChanged`, `OnStartDateChanged`, `OnEndDateChanged`, `OnProgressDisplayClick`, `OnProgressEditVisibleChanged`, `OnProgressEditKeyDown`, `OnProgressEditLostFocus`, `OnEditBacklogTagsClick`, `OnEditTaskTagsClick`, `OnSelectGrid`/`OnSelectGantt`, Gantt canvas handlers. (These use `Tag="{Binding}"` to find the row, so they work unchanged from a card.)

### VM

No new members required — `EditType`/`EditPctUserId`/`EditPcaId` are already `TwoWay`-settable with `OnXxxChanged → Commit()`, and `GroupByProject`/`GroupByTeam`/`GroupKey`/`GroupOrder` exist from P15. (If a compact-header display needs a formatted string not already present, add a trivial pass-through.)

## Observable truths (goal-backward — feeds `must_haves` at STEP 6)

1. Each backlog renders as a **card** under its section band; there is **no horizontal scroll** to see tags.
2. Tags appear as a **full-width strip on top of each card, always visible**; `✎ Tags` edits the set (dialog as today).
3. Compact header shows CODE, PCT assignee, Internal deadline, and Progress; a caret expands the card.
4. Expanding shows Type/PCA/External/Start/End (labeled, wrapping) + Logged/Estimate + the editable task sub-rows.
5. **Type/PCT/PCA edits persist** (now via direct `TwoWay`); deadlines (with reason note), Start/End, Progress, Tags, and task sub-row edits persist exactly as before.
6. Section-band grouping (adaptive Team/Project, collapsible, `Name (count)`) still works.
7. Grid↔Gantt toggle, Gantt view, month selector, Export, and `TeamFilter` are unaffected.

## Risks & testing

- **Render-crash class** (recurring in this project): the card template must render without throwing — Progress bar stays `OneWay`; no `TwoWay` onto a read-only prop; no `Button` style on a `ToggleButton`. Gate: extend `TaskListTabRenderTests` to render the card layout with a card expanded.
- **The `TwoWay` combos must actually commit** — verify Type/PCT/PCA changes reach the DB (a live-DB/UAT check; the reason they were code-behind was DataGrid-specific and does not apply to `ItemsControl`).
- **Lost `DataGrid` features** — alternating rows, fixed `RowHeight`, aligned columns, built-in scrolling. Accepted trade-off; scrolling replaced by an explicit `ScrollViewer`.
- **No UI virtualization** — `ItemsControl` (StackPanel) + grouping are unvirtualized; Task List is month-scoped (dozens of cards) → acceptable (YAGNI; note only).
- **Large diff** — most of `TaskListTab.xaml` rewritten + code-behind trimmed → split into waves; keep **538 tests green** and build clean after each.

## Out of scope (YAGNI)

- Any change to VM/repository commit, persist, or audit logic.
- Re-enabling UI virtualization; a custom virtualizing grouped panel.
- Gantt redesign; changes to month selector / export / team filter.
- Making Start/End/deadline DatePickers `TwoWay` (they keep the code-behind + note-popup path).
- Restyling chips or the section band beyond hosting them in the card.
