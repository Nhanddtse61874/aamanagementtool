# P16 — Task List Per-Backlog Card Layout — PLAN (wave-grouped)

> **For agentic workers:** implement wave-by-wave; each task self-contained. `[model]` = dispatch tier.

**Goal:** Replace the Task List `DataGrid` with a grouped `ItemsControl` of per-backlog **cards** so tags sit in a full-width strip on top of each card (always visible, no horizontal scroll), keeping P15 section bands and all edit/persist behavior.
**Mode A.** Spec: `docs/superpowers/specs/2026-07-02-tasklist-card-layout-design.md`. Plan-check: self-review below.
**Architecture:** View rewrite only. `ItemsControl.ItemsSource` = the P15 `GroupedRows` CVS; `ItemsControl.GroupStyle` = the P15 Expander band; `ItemTemplate` = a card. Type/PCT/PCA combos move to direct `TwoWay` (the write-back bug is `DataGrid`-only); deadlines/start-end/progress/tags/expand keep their existing code-behind commit paths. **Stack skill: `dotnet`.**
**Tech Stack:** .NET 8 / WPF MVVM / CommunityToolkit.Mvvm / xUnit (STA render guard).

## must_haves (goal-backward)

**Observable Truths**
- OT-1 Each backlog is a **card** under its section band; **no horizontal scroll** to see tags.
- OT-2 A **full-width tag strip** (chips + `✎ Tags`) sits on top of each card, **always visible**; editing tags works (dialog as today).
- OT-3 Compact header shows CODE + PCT assignee + Internal deadline + Progress + an expand caret.
- OT-4 Expanding shows Type/PCA/External/Start/End (labeled, wrapping) + Logged/Estimate + the editable task sub-rows.
- OT-5 Type/PCT/PCA edits **persist** (now direct `TwoWay`); deadlines (reason note), Start/End, Progress, Tags, task sub-rows persist **as before**.
- OT-6 Section-band grouping (adaptive Team/Project, collapsible, `Name (count)`) still works.
- OT-7 Grid↔Gantt toggle, Gantt view, month selector, Export, `TeamFilter` unaffected.

**Required Artifacts**
- `Views/Tabs/TaskListTab.xaml`: DataGrid block → `ScrollViewer` > `ItemsControl` (grouped) with card `ItemTemplate`; `GroupStyle` moved onto the `ItemsControl`.
- `Views/Tabs/TaskListTab.xaml.cs`: remove `OnRowTypeChanged`/`OnRowPctChanged`/`OnRowPcaChanged`; keep the rest.
- `TimesheetApp.Tests/Views/TaskListTabRenderTests.cs`: render the card layout (a card expanded) without crash.

**Key Links:** `GroupedRows` CVS + `GroupByProject` (P15, via `VmProxy`) → card template (band + column-hide reuse). `EditType`/`EditPctUserId`/`EditPcaId` (`TwoWay`) → existing `OnXxxChanged → Commit()`. `Tag="{Binding}"` on deadline/date/progress editors → existing code-behind handlers. `ToggleExpandCommand` via `RelativeSource AncestorType=ItemsControl`.

---

## Wave 1 — Card layout (the rewrite)

- **W1-T1** `[opus]` **DataGrid → grouped ItemsControl of cards.**
  · **read_first:** `src/TimesheetApp/Views/Tabs/TaskListTab.xaml` (whole grid block ~L178-484: `TableContainer` Border, `DataGrid`, columns, `RowDetailsTemplate`, `RowStyle`, the P15 `CollectionViewSource`/`GroupStyle`, `ChipTemplate`, `VmProxy`); `src/TimesheetApp/Views/Tabs/TaskListTab.xaml.cs` (the `On*` handlers + how each reads `sender`/`Tag`).
  · **action:** Keep the outer rounded `TableContainer` `Border` (+ its `IsGantt` hide trigger). Replace its `DataGrid` child with `ScrollViewer` (`VerticalScrollBarVisibility="Auto"`, **`HorizontalScrollBarVisibility="Disabled"`** → no h-scroll) > `ItemsControl ItemsSource="{Binding Source={StaticResource GroupedRows}}"`. **Move** the P15 `GroupStyle` (Expander band, `Name (ItemCount)`, `IsExpanded=True`) from `DataGrid.GroupStyle` to `ItemsControl.GroupStyle` **verbatim**. Add `ItemsControl.ItemTemplate` = a card `DataTemplate` (DataType `vm:TaskListRowVm`):
    - Root `Border` — card chrome (`CornerRadius=8`, subtle `BorderBrush`/`Background`, `Margin="0,0,0,8"`, `Padding="10"`) + `Style.Triggers` `IsMouseOver` → `#F1F5F9` (reuse today's row-hover color).
    - **Tag strip (top, full-width):** the existing `Chips` `ItemsControl` (`WrapPanel` + `{StaticResource ChipTemplate}`) + the `✎ Tags` `Button` (`{StaticResource MiniGhostButton}`, `Click="OnEditBacklogTagsClick"`) — copied from the old TAGS column cell.
    - **Compact header** (`Grid`/`StackPanel`): `BacklogCode` (bold); muted `Project` `TextBlock` with `Visibility="{Binding Data.GroupByProject, Source={StaticResource VmProxy}, Converter={StaticResource BoolToVisibleConverter}, ConverterParameter=Invert}"`; **PCT** `ComboBox` (`{StaticResource CompactComboBox}`, `ItemsSource="{Binding AssigneeOptions}"`, `SelectedValuePath="Id"`, `DisplayMemberPath="Name"`, `SelectedValue="{Binding EditPctUserId, Mode=TwoWay}"`); **Internal** `DatePicker` (`{StaticResource CompactDatePicker}`, `Tag="{Binding}"`, `SelectedDate="{Binding DeadlineInternal, Mode=OneWay, Converter={StaticResource DateOnly}}"`, `SelectedDateChanged="OnInternalDeadlineChanged"`); the **Progress** `Grid` (bar-display + click-to-edit `TextBox`) copied verbatim; expand caret `Button` (`Command="{Binding DataContext.ToggleExpandCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"`, `CommandParameter="{Binding}"`, ▸/▾ via `IsExpanded` trigger).
    - **Expanded detail** — a panel `Visibility="{Binding IsExpanded, Converter={StaticResource BoolToVisibleConverter}}"`: a `WrapPanel` of labeled editors — **Type** `ComboBox` (`SelectedItem="{Binding EditType, Mode=TwoWay}"`), **PCA** `ComboBox` (`SelectedValue="{Binding EditPcaId, Mode=TwoWay}"`), **External/Start/End** `DatePicker`s (`OneWay` + `Tag="{Binding}"` + `SelectedDateChanged="OnExternalDeadlineChanged"`/`OnStartDateChanged"`/`OnEndDateChanged"`), display `LoggedHoursText`/`EstimateText`; then the **task sub-rows** block (empty-state `TextBlock` + `ItemsControl ItemsSource="{Binding TaskRows}"` with its template) copied **verbatim** from the old `RowDetailsTemplate`.
    - In `TaskListTab.xaml.cs`, **delete** `OnRowTypeChanged`, `OnRowPctChanged`, `OnRowPcaChanged` (Type/PCT/PCA now commit via `TwoWay`). Do **not** touch any other handler.
    - **AVOID (render-crash class):** Progress bar stays `Mode=OneWay` (never `TwoWay` on `RangeBase.Value`); no `TwoWay` onto any read-only pass-through (`BacklogCode`/`Project`/`DeadlineInternal`/…); no `Button` style on a `ToggleButton`; expand uses the reliable `ToggleExpandCommand` (not an `IsChecked` write). **AVOID** deleting the `ChipTemplate`/`VmProxy`/`GroupedRows` resources — they're reused. **AVOID** changing any VM method or the surrounding controls (month/Gantt/export/TeamFilter).
  · **verify (auto <60s):** `dotnet build src/TimesheetApp.sln` then `dotnet test src/TimesheetApp.sln --filter "FullyQualifiedName~TaskListTabRender"` — STA render test instantiates the tab (card layout, a card expanded) and asserts no render throw.
  · **done:** `grep -c "<DataGrid" TaskListTab.xaml` = 0; `grep "<ItemsControl"` + `grep "ItemTemplate"` hit it; `grep "OnRowTypeChanged" TaskListTab.xaml.cs` = 0; build clean; render test green.

## Wave 2 — Verify + regression  *(depends on W1)*

- **W2-T1** `[sonnet]` **Render-test refresh + full regression.**
  · **read_first:** `src/TimesheetApp.Tests/Views/TaskListTabRenderTests.cs` (it seeds a backlog + task + tag, sets `Rows[0].IsExpanded=true`, renders, asserts no crash — still valid for cards).
  · **action:** Confirm the render test still exercises the card path; if any `StaticResource`/handler reference moved, adjust the test's `EnsureAppResources` or assertions minimally so it renders the **expanded** card (tag strip + compact header + expanded fields + a task sub-row). Add a one-line comment noting it now guards the card layout. Do **not** weaken it. Then run the **full** suite.
  · **verify (auto):** `dotnet test src/TimesheetApp.sln` — full suite **538 green**, 0 failures.
  · **done:** full suite green; render test asserts the expanded card renders; build clean.

---

## REQ coverage (100%)
CARD-01 (card, no h-scroll) → W1-T1 · CARD-02 (tag strip top, editable) → W1-T1 · CARD-03 (compact header) → W1-T1 · CARD-04 (expanded fields + tasks) → W1-T1 · CARD-05 (Type/PCT/PCA persist + others as before) → W1-T1 (+W2-T1 regression) · CARD-06 (section bands) → W1-T1 (reuse GroupStyle) · CARD-07 (Gantt/month/export/filter unaffected) → W1-T1 (unchanged).

## Risks / gates
1. **Render-crash class** — the card template must render; gated by `TaskListTabRenderTests` (W1 verify + W2 full suite).
2. **TwoWay combos actually commit** — not unit-testable via render; **UAT must confirm** Type/PCT/PCA changes reach the DB (live-DB check). Flag in P16-UAT.
3. **Lost DataGrid features** (alternating rows, aligned columns, fixed row height, built-in scroll) — accepted; scroll replaced by explicit `ScrollViewer`.
4. **No UI virtualization** under grouped `ItemsControl` — month-scale (dozens) → acceptable, no mitigation (YAGNI).
5. **Unreferenced handlers** — the 3 removed in W1; build stays clean (unused private methods don't warn). If any other handler becomes unreferenced, remove it too.
6. **Build/run gotcha:** kill running `TimesheetApp` before build (locks exe).

## Model note
W1-T1 `[opus]` (broad rewrite + render judgment). W2-T1 `[sonnet]` (`claude-sonnet-5`). Inline execution (Mode A) — main agent runs build+tests after each wave.
