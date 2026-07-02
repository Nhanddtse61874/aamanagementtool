# P15 — Task List Grouped Section Bands — PLAN (wave-grouped)

> **For agentic workers:** implement task-by-task; each task is self-contained. Steps use `[model]` dispatch tags.

**Goal:** Group the Task List grid rows under collapsible section bands, adaptively by Team (multi-team view) or Project, without touching the inline-edit machinery.
**Mode A.** Spec: `docs/superpowers/specs/2026-07-02-tasklist-grouped-section-bands-design.md`. Plan-check: self-review below.
**Architecture:** View-layer only — `CollectionViewSource` groups `Rows` by a computed `GroupKey`; `DataGrid.GroupStyle` renders one collapsible `Expander` band per group. Row VMs, cell templates, `RowDetailsTemplate`, and every code-behind inline-edit handler are unchanged.
**Tech Stack:** .NET 8 / WPF MVVM / CommunityToolkit.Mvvm / xUnit (STA render guard). **Stack skill: `dotnet`.**

## must_haves (goal-backward)

**Observable Truths**
- OT-1 Grid mode shows backlogs under section-band headers (one band per group), not one flat list.
- OT-2 Band header shows `GroupName (count)`, e.g. `ARCS (3)`.
- OT-3 Clicking a band header collapses/expands that whole group; groups start **expanded**.
- OT-4 Group key is **adaptive**: multi-team view active → group by **Team**; otherwise → group by **Project** (enum order ARCS→PlusArcs→ARMS→Other).
- OT-5 Every inline editor (Type/PCT/PCA/Internal/External/Start/End/Progress/Tags) and the per-backlog task sub-row expand behave **exactly as before**.
- OT-6 Grid↔Gantt toggle and Gantt view are unaffected (VM `Rows` order NOT changed — Gantt code-behind reads it).

**Required Artifacts**
- `TaskListRow.GroupKey` (string), `TaskListRow.GroupOrder` (int).
- `TaskListViewModel`: set `GroupKey`/`GroupOrder` per row on build; expose `GroupByTeam`/`GroupByProject` bools (mutually exclusive, from the multi-team flag).
- `TaskListTab.xaml`: `CollectionViewSource` (group by `GroupKey`, sort by `GroupOrder`) + `DataGrid.GroupStyle` Expander band + hide the column that became the group key.
- Tests: VM unit test for `GroupKey`/`GroupOrder`/flags (both modes); `TaskListTabRenderTests` renders the grouped grid without throwing.

**Key Links:** `GroupKey`/`GroupOrder`/flags (W1) → `CollectionViewSource` GroupDescription + SortDescription (W2) → `GroupStyle` band render (W2). Column-hide (W2) depends on the `GroupByTeam`/`GroupByProject` flags (W1).

---

## Wave 1 — VM group keys

- **W1-T1** `[sonnet]` **Row group key + VM flags.**
  · **read_first:** `src/TimesheetApp/ViewModels/TaskListViewModel.cs` (find `TaskListRow` type + the row-build loop in `LoadAsync`/reload + the multi-team flag driving `ShowTeam`/`TeamFilter.ShowTeamColumn`); `src/TimesheetApp.Tests` existing `TaskList*Tests.cs` for the test harness pattern (mock repo + how a VM is constructed).
  · **action:** On `TaskListRow` add `GroupKey` (string) and `GroupOrder` (int) — plain get/set props (NOT `[ObservableProperty]` bound TwoWay to anything; they are set once at build, read-only to the view). In the VM, compute a local `bool multiTeam` from the **existing** multi-team condition (the same one that sets `ShowTeam`/`ShowTeamColumn` — reuse it, do NOT invent a new source of truth). Set VM bools `GroupByTeam = multiTeam`, `GroupByProject = !multiTeam` (observable, for column-hide binding). For each row in the build loop set `GroupKey = multiTeam ? TeamName : Project` and `GroupOrder = multiTeam ? <stable team index by name> : <project enum index: ARCS=0,PlusArcs=1,ARMS=2,Other=3>`. **AVOID:** reordering the `Rows` collection itself (Gantt code-behind reads `Rows`; group ordering is done in the view via SortDescription in W2). **AVOID:** a `NullReferenceException` when `TeamName`/`Project` is null — fall back to a stable `"—"`/max-order bucket.
  · **verify (auto <60s):** `dotnet test src/TimesheetApp.sln --filter "FullyQualifiedName~TaskList"` — new test asserts: single-team → every row `GroupKey==Project` & `GroupByProject==true`; multi-team → `GroupKey==TeamName` & `GroupByTeam==true`; `GroupOrder` for a known Project set matches enum order.
  · **done:** `grep GroupKey`, `grep GroupOrder`, `grep GroupByTeam` all hit `TaskListViewModel.cs`; the new `[Fact]`(s) are green; build clean.

## Wave 2 — XAML grouping band  *(depends on W1; no file overlap — W1 = .cs, W2 = .xaml + render test)*

- **W2-T1** `[sonnet]` **Section-band grouping in XAML.**
  · **read_first:** `src/TimesheetApp/Views/Tabs/TaskListTab.xaml` (the `DataGrid` at ~L188 + its columns, esp. the existing `VmProxy`-bound TEAM column visibility at ~L247, and the `TableContainer`/`RoundedClip` wrapper); `src/TimesheetApp.Tests/…TaskListTabRenderTests.cs` (STA render-guard pattern).
  · **action:** Add a `CollectionViewSource x:Key="GroupedRows"` in `UserControl.Resources`: `Source={Binding Rows}`, one `PropertyGroupDescription PropertyName="GroupKey"`, and `SortDescription`s: primary `GroupOrder` Ascending, secondary = the grid's **current** display-order field (confirm from the VM/query — e.g. `BacklogCode`) so within-group order is deterministic. Point `DataGrid.ItemsSource="{Binding Source={StaticResource GroupedRows}}"`. Add `DataGrid.GroupStyle` → `GroupStyle.ContainerStyle` (TargetType `GroupItem`) whose template hosts an **`Expander` `IsExpanded="True"`**: header = a full-width band `TextBlock` `"{Binding Name}"` + `"({Binding ItemCount})"` (use `HeaderBg`/`Accent` theme brushes, matches the rounded-container look); Expander content = `ItemsPresenter`. **Hide the group-key column:** bind the `PROJECT` column `Visibility` (via the existing `VmProxy`) to `GroupByProject` inverted-ish (visible only when `GroupByProject==false`, i.e. multi-team); extend the TEAM column's existing visibility to also hide when `GroupByTeam==true` (so the band, not a column, shows the team). **AVOID:** the project's WPF render-crash class — no `Button` style on a `ToggleButton`, no `TwoWay` binding onto a read-only prop; `Expander` header text is display-only. **AVOID:** removing `BasedOn={StaticResource {x:Type DataGrid}}` (keeps RowHeight/alternating rows) and the `IsGantt` hide trigger. Do NOT touch cell templates, `RowDetailsTemplate`, `RowStyle`, or any code-behind handler.
  · **verify (auto <60s):** `dotnet build src/TimesheetApp.sln` then `dotnet test src/TimesheetApp.sln --filter "FullyQualifiedName~TaskListTabRender"` — the STA render test instantiates the tab (grouped XAML) and asserts no render throw.
  · **done:** `grep -c "CollectionViewSource"` and `grep "GroupStyle"` hit `TaskListTab.xaml`; build clean; render test green; **full suite still 536 green** (`dotnet test src/TimesheetApp.sln`).

---

## REQ coverage (100%)
GSB-01 (grouped bands) → W1-T1 + W2-T1 · GSB-02 (name+count) → W2-T1 · GSB-03 (collapsible/default-open) → W2-T1 · GSB-04 (adaptive key) → W1-T1 · GSB-05 (inline edits unchanged) → W2-T1 (by non-modification; verified by full suite) · GSB-06 (Gantt unaffected) → W1-T1 (Rows order preserved).

## Risks / gates
1. **Within-group order:** WPF sort is not guaranteed stable → add the secondary `SortDescription` (W2). Confirm the current order field from the VM before wiring.
2. **Gantt:** must read the unsorted VM `Rows` (view sort is CVS-only). If Gantt instead reads the grouped view, keep a separate unsorted source. Verify Gantt still draws in `Grid↔Gantt`.
3. **Virtualization:** grouping disables UI virtualization by default; Task List is month-scoped (dozens of rows) → acceptable, no mitigation added (YAGNI).
4. **Render-crash class:** grouped/Expander XAML must render — gated by `TaskListTabRenderTests`.
5. **Build/run gotcha:** kill running `TimesheetApp` before build (locks exe).

## Model note
Both tasks `[sonnet]` → `claude-sonnet-5` per CLAUDE.md. Escalate W2-T1 to `opus` if the render test throws on the grouped XAML (render-crash class).
