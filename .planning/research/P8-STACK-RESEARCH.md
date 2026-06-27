# P8 "Task List" — Stack Research (Mode B)

**Phase:** P8 / M3 — Task List (tracking, tags, holidays, Gantt)
**Stack:** .NET 8 / WPF MVVM / SQLite + Dapper / ClosedXML / CommunityToolkit.Mvvm. **No charting lib (D3).**
**Author:** Stack Research agent · 2026-06-27
**Tag legend:** `[VERIFIED]` = confirmed in this codebase (file:line) · `[CITED]` = documented framework behavior · `[ASSUMED]` = inferred, unconfirmed.

This report answers six "how do we do X in THIS stack" questions and gives a decisive recommendation per topic, grounded in existing patterns so the spec/plan can reuse them verbatim.

---

## 0. Codebase conventions to honor (cross-cutting)

These are the load-bearing patterns every P8 task must mirror:

- **Repos are stateless singletons; one short connection per method** via `IConnectionFactory.Create()`. `[VERIFIED]` `App.xaml.cs:60-67`, every repository (e.g. `BacklogRepository.cs:18-35`).
- **FK + journal pragmas already set per connection** (`ForeignKeys=true`, `journal_mode=DELETE`, `Pooling=false`). New tables with FKs are enforced automatically. `[VERIFIED]` `SqliteConnectionFactory.cs:31-47`.
- **SQLite-native "Raw" DTO + boundary mapping**: query into a `private sealed class XxxRaw { long id; string …; double …; }` then `.Select(MapXxx)` to the record. Dapper's positional-record path does **not** narrow `long→int` or `double→decimal`. `[VERIFIED]` `TimeLogRepository.cs:135-155`, `BacklogRepository.cs:162-173`.
- **Dates stored as TEXT ISO-8601** (`yyyy-MM-dd` for days, `yyyy-MM-ddTHH:mm:ssZ` for timestamps), culture-invariant. `[VERIFIED]` `BacklogRepository.cs:145-148`, `TimeLogRepository.cs:119`.
- **Cross-tab refresh via `IMessenger`** (`WeakReferenceMessenger.Default`, `DataChangedMessage`/`DataKind`). New Task-List edits should broadcast a kind so the Gantt/grid refresh live. `[VERIFIED]` `App.xaml.cs:50-51`.
- **Editor-overlay UI pattern**: list `DataGrid` + a modal-looking `Border` overlay (`#66000000` scrim → white `CornerRadius=12` card, header/footer strips) bound to an `Editor` sub-VM, toggled by `NullToCollapsedConverter`. `[VERIFIED]` `RequestsTab.xaml:83-120`, mirrored in `SettingsTab.xaml:84-90`.
- **The view file for Backlogs is `RequestsTab.xaml`** but the class is `x:Class="TimesheetApp.Views.Tabs.BacklogsTab"` and MainWindow references `<tabs:BacklogsTab>`. `[VERIFIED]` `RequestsTab.xaml:1`, `MainWindow.xaml:157`. (Naming gotcha for the planner.)

---

## 1. Gantt in native WPF (D3, TL-10)

**Recommendation: `ItemsControl` with a `Canvas` `ItemsPanel`, one bar per backlog, positioned via `Canvas.Left`/`Canvas.Top` + `Width` bound to VM-computed pixel values (NOT value converters).** Do the working-day→pixel math in the ViewModel, expose plain `double` X/Width/Y per bar. Draw the weekend/holiday background columns as a second `ItemsControl` layered behind, or as `Rectangle` children added in code-behind/VM.

Why this over the alternatives:

- **ItemsControl+Canvas (chosen)** — declarative, MVVM-friendly, virtualizes poorly but the dataset is tiny (a handful of backlogs/month). `Canvas.Left`/`Canvas.Top` are attached properties you bind directly on the item container via `ItemContainerStyle`. `[CITED]` This is the standard no-library WPF Gantt approach.
- **Manual Canvas children in code-behind** — most control, but bypasses MVVM/data-templating and is harder to test; reserve only for the static axis/gridlines layer. `[ASSUMED]`
- **DataGrid + adorner** — fights the DataGrid layout, adorners don't scroll/clip cleanly; reject. `[ASSUMED]`

**Pixel mapping over a working-day axis** (weekends + Holidays skipped, HOL-02): build an ordered list of working days `[start..maxDeadline]` once, then `dayIndex = workingDays.IndexOf(date)` and `x = dayIndex * COL_W`. A bar spans `xStart = index(barStart)*COL_W`, `width = (index(barEnd) - index(barStart) + 1) * COL_W`. This **collapses** non-working days (they take zero axis width) — simplest and matches "weekends/holidays visually skipped." If the design wants them shown-but-marked, instead keep a full calendar axis and paint weekend/holiday columns with a gray `Rectangle`; the bar then must subtract nothing (only color differs). **Recommend the collapsed working-day axis** as primary (cleaner, matches SI/working-day model), `[ASSUMED]` pending design confirmation.

Reuse the existing working-day enumeration logic — currently a private `WorkingDays(from,to)` in `SmartInputService.cs:45-52`. **Promote it to a shared `IWorkingDayCalculator`** (see §HOL below) that also consults Holidays, then the Gantt axis, TL-07 schedule math, and smart input all share one source. `[VERIFIED]` the helper exists privately at `SmartInputService.cs:45-52`.

**Bar binding skeleton** (item VM exposes `BarX`, `BarWidth`, `BarY`, `BarBrush`, `Label`):

```xml
<ItemsControl ItemsSource="{Binding GanttBars}">
  <ItemsControl.ItemsPanel>
    <ItemsPanelTemplate><Canvas/></ItemsPanelTemplate>
  </ItemsControl.ItemsPanel>
  <ItemsControl.ItemContainerStyle>
    <Style TargetType="ContentPresenter">
      <Setter Property="Canvas.Left" Value="{Binding BarX}"/>
      <Setter Property="Canvas.Top"  Value="{Binding BarY}"/>
    </Style>
  </ItemsControl.ItemContainerStyle>
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Border Width="{Binding BarWidth}" Height="22" CornerRadius="5"
              Background="{Binding BarBrush}" ToolTip="{Binding Label}">
        <TextBlock Text="{Binding Label}" Margin="6,0" Foreground="White"
                   FontSize="11" VerticalAlignment="Center"/>
      </Border>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

`Canvas.Left`/`Top` as setters on the container `Style` is the verified-correct way to bind attached layout from an item VM. `[CITED]`

**Expand/collapse of the chart area (TL-10):** simplest is binding the chart container's `Visibility` (or a `RowDefinition.Height`) to an `IsChartExpanded` bool via the existing `BoolToVisibilityConverter` (`Views/Converters/BoolToVisibilityConverter.cs` exists `[VERIFIED]` glob). For animated collapse use a `DoubleAnimation` on `Height`, but a plain Visibility toggle matches the app's current non-animated style. `[ASSUMED]` recommend the plain toggle.

**Grid↔Gantt view toggle:** reuse the **string-match `ActiveView` pattern** already used for the sidebar and sub-panels — a bool/string property + `StringMatchToVisibilityConverter` swapping a Grid panel and a Gantt panel, preserving the selected month (the month is a separate VM property, untouched by the toggle). `[VERIFIED]` `StringMatchConverter.cs:23-31`, `MainWindow.xaml:144-145`.

---

## 2. Tag chip rendering (TAG-01/02, D4)

**Recommendation: render each chip as `Border` (rounded, `CornerRadius≈9`, `Padding="8,2"`) → horizontal `StackPanel` of glyph `TextBlock` + text `TextBlock`. Convert the stored hex `color` string → `Brush` with a small dedicated `IValueConverter` (a "HexToBrush" converter), exactly mirroring `AvatarBrushConverter`.**

The codebase already renders pill/badge chips this way for the Type badge — copy it: `Border Background=… CornerRadius=9 Padding=8,2` + `TextBlock FontSize=11 FontWeight=SemiBold`. `[VERIFIED]` `RequestsTab.xaml:57-62`. Static badge brushes (`BadgeGreenBg/Fg`, `AmberBg/Fg`, `DangerSoft`) are already in the theme for the **system chips** (amber warning, red late). `[VERIFIED]` `Theme.xaml:511-518`.

**Hex string → Brush converter** — the project already does `ColorConverter.ConvertFromString(hex)` + `Freeze()`; reuse that idiom in a new converter:

```csharp
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var hex = v as string;
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
```

`ColorConverter.ConvertFromString` accepts `#RRGGBB`/`#AARRGGBB`/named colors. `[VERIFIED]` exact pattern at `AvatarBrushConverter.cs:23-25`; `[CITED]` for the hex-format support. Wrap the parse defensively (bad hex throws `FormatException`) and fall back to a neutral brush. `[ASSUMED]` defensive fallback.

For **readable text on arbitrary background**, either store a fixed dark/white foreground, or compute luminance in a second converter. **Recommend storing chips with light backgrounds + a fixed dark foreground** (matches the existing soft-badge style — `AccentSoft` bg + `Accent` fg) to dodge contrast math. `[ASSUMED]`

**Icon (glyph/emoji):** the app already uses emoji glyphs directly as `Text` (`Tag="&#128197;"` nav icons, `⚠` in markdown). `[VERIFIED]` `MainWindow.xaml:63`, `StandupArchiveService.cs:126`. So `icon` is just a string the `TextBlock` shows — **no font switching required for emoji.** Segoe MDL2 glyphs would need `FontFamily="Segoe MDL2 Assets"`; `[CITED]` but emoji is simpler and consistent with current usage — **recommend emoji**, store the literal glyph in `Tags.icon`. `[ASSUMED]`

**Simple icon/glyph picker:** an `ItemsControl`/`WrapPanel` of a small hard-coded emoji array as clickable `Button`s that set the selected glyph on the editor VM, plus a free-text box to paste any glyph. No image upload (TAG-01 `[ASSUMED]` confirms). `[ASSUMED]` This is the lightest build and matches the app's "no fancy controls" altitude.

**Chip-list rendering** on a backlog row / Task List: `ItemsControl` with a `WrapPanel` `ItemsPanel`, `ItemTemplate` = the chip Border above. System chips (`warning`/`late`) are produced by the VM into the **same** chip collection with fixed styling, so one template renders both (TAG-02). `[ASSUMED]` (clean unification).

---

## 3. Holiday calendar (HOL-01)

**Recommendation: use the WPF `Calendar` control with a custom `CalendarDayButton` style whose `DataTrigger`/`MultiBinding` marks holiday days, and toggle a holiday on `Calendar.SelectedDatesChanged` (or a click handler). Do NOT use `BlackoutDates` for holidays.**

Rationale:
- `BlackoutDates` makes days **unselectable** (you can't click a blacked-out day), which is the opposite of "click a day to mark/unmark it." `[CITED]` Reject for the toggle UX.
- `SelectionMode="MultipleRange"` + binding `SelectedDates` could represent the holiday set, but two-way binding `SelectedDates` (an `ObservableCollection<DateTime>` that the control mutates) is fiddly and the highlight is the OS "selected" look, not a holiday marker. `[CITED]`
- **Cleanest:** `SelectionMode="SingleDate"`, handle the selection-changed (code-behind → VM command, the app already uses click handlers like `OnBrowse`, `OnSubTabChanged` `[VERIFIED]` `SettingsTab.xaml:15`, `MainWindow.xaml:152`), and **re-style `CalendarDayButton`** so days present in a holiday set render with an amber background. Bind "is this day a holiday" via a converter/`MultiBinding` against the VM's holiday `HashSet<DateOnly>`. `[CITED]` `CalendarDayButton` is the documented restyle seam for per-day visuals.

A pragmatic, lower-risk alternative that fully matches the app's hand-rolled style: **build the month as a 7-col `UniformGrid`/`ItemsControl` of day `ToggleButton`s** (the app already templates everything else by hand), each bound `IsChecked` to "is holiday," `Command` toggles persistence. This avoids fighting `Calendar`'s template. **Recommend the `Calendar` restyle first; fall back to the `UniformGrid` if the template proves painful.** `[ASSUMED]`

**Persistence:** `Holidays(holiday_date TEXT PK, description TEXT)` (TL-01). Toggle = `INSERT OR IGNORE` / `DELETE` on the PK date, same one-connection-per-method repo style. Shared via the DB (D6/HOL-01). `[VERIFIED]` repo idiom `SettingsRepository.cs:19-25` (`INSERT OR REPLACE`). The sub-tab lives inside `SettingsTab` (D6) — add a section like the existing template/default-task sections (`SettingsTab.xaml:42-80`). `[VERIFIED]`

---

## 4. SQLite migration v6→v7 with Dapper (TL-01, D9, DATA-05)

**The exact existing pattern is in `DatabaseInitializer.cs`** `[VERIFIED]`:

1. `private const long SchemaVersion = 6;` — bump to **7**. `[VERIFIED]` line 14.
2. `InitializeAsync` opens **one connection + one transaction**, runs `CreateTables` → `RunMigrations` → `EnsureDefaultBacklog` → `SeedDefaultTasksIfEmpty`, then `tx.Commit()`. `[VERIFIED]` lines 23-35.
3. `CreateTables` runs `CREATE TABLE IF NOT EXISTS …` for stable tables (idempotent), executed with `transaction: tx`. New v7 tables (`Tags`, `BacklogTags`, `PcaContacts`, `Holidays`) **go here** with `IF NOT EXISTS` (they have no rename history, so unconditional creation is safe). `[VERIFIED]` lines 37-120.
4. `RunMigrations` holds an **array of `Action<IDbConnection, IDbTransaction>` indexed by version step.** Reads `PRAGMA user_version`; `for (step = current; step < migrations.Length; step++) migrations[step](conn, tx);` then `PRAGMA user_version = {SchemaVersion}`. Step index N runs when `user_version < N+1`. `[VERIFIED]` lines 152-219.
5. `ALTER TABLE … ADD COLUMN` is **not idempotent**, so it MUST live in a gated migration step (runs exactly once), never in `CreateTables`. `[VERIFIED]` the v2/v4/v6 steps demonstrate this exactly (lines 164-205).
6. PRAGMA can't be parameterized → version is interpolated from the compile-time constant. `[VERIFIED]` line 217.

**Pattern to follow for v7** — append **one** new array element (the 8th, index 7) and create the 4 tables in `CreateTables`:

In `CreateTables`, add to the `IF NOT EXISTS` DDL block:
```sql
CREATE TABLE IF NOT EXISTS Tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    text TEXT NOT NULL, icon TEXT, color TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS BacklogTags (
    backlog_id INTEGER NOT NULL, tag_id INTEGER NOT NULL,
    PRIMARY KEY (backlog_id, tag_id),
    FOREIGN KEY (backlog_id) REFERENCES Backlogs(id),
    FOREIGN KEY (tag_id) REFERENCES Tags(id) ON DELETE CASCADE);
CREATE TABLE IF NOT EXISTS PcaContacts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS Holidays (
    holiday_date TEXT PRIMARY KEY, description TEXT);
```

Add the v7 migration step (gated ADD COLUMNs on `Backlogs`):
```csharp
// v7 -> Task List tracking columns on Backlogs (Tags/BacklogTags/PcaContacts/Holidays
// are created idempotently in CreateTables). ADD COLUMN is not idempotent -> gated step.
static (c, t) => c.Execute(
    @"ALTER TABLE Backlogs ADD COLUMN deadline_internal      TEXT;
      ALTER TABLE Backlogs ADD COLUMN deadline_external      TEXT;
      ALTER TABLE Backlogs ADD COLUMN rough_estimate_hours   REAL;
      ALTER TABLE Backlogs ADD COLUMN official_estimate_hours REAL;
      ALTER TABLE Backlogs ADD COLUMN progress_percent       INTEGER;
      ALTER TABLE Backlogs ADD COLUMN note                   TEXT;
      ALTER TABLE Backlogs ADD COLUMN pca_contact_id         INTEGER;",
    transaction: t),
```
Then bump `SchemaVersion = 7`. **Reuse `assignee_user_id` (v4) as PCT — do NOT re-add it** (TL-01 explicit). `[VERIFIED]` it already exists (`DatabaseInitializer.cs:187-188`, `Backlog` record line 14).

**FK caveat `[CITED]`:** SQLite `ALTER TABLE ADD COLUMN` cannot add a column with an inline `REFERENCES` FK constraint that has a non-NULL/non-constant default; a plain nullable `pca_contact_id INTEGER` (no inline FK) is fine and matches how v4 added `assignee_user_id` as a plain INTEGER ("not an FK constraint so deactivating a user never blocks" — `DatabaseInitializer.cs:186-187`). **Recommend `pca_contact_id` as a plain nullable INTEGER, no inline FK** (TL-01's "FK→PcaContacts" is satisfied logically; soft-delete semantics in TL-11 require the same no-hard-constraint approach as the assignee). `[VERIFIED]` precedent + `[CITED]` SQLite limitation.

**Backward-compat note already designed-in:** old client opening a v7 DB still works because all steps are additive (DATA-05). `[VERIFIED]` comment lines 154-156.

---

## 5. Decimal / REAL handling for hours (TL-05, D8)

**Convention (follow exactly): store hours as SQLite `REAL`; bind a `double` on write; map `(decimal)` on read at the boundary.** `[VERIFIED]` `TimeLogRepository.cs`:
- Write: `Hours = (double)log.Hours` (param) → column `REAL`. Line 116.
- Read: `Raw.hours` is `double`, mapped `(decimal)r.hours`. Lines 124-125, 142, 154.
- Model: `TimeLog.Hours` is `decimal`. `[VERIFIED]` `Entities.cs:45`.

For **estimates** (`rough_estimate_hours`, `official_estimate_hours` as REAL, nullable, D8 = duration in hours): model them as `decimal?` on the `Backlog` record, bind `(double?)` on write, map `(decimal?)` on read — same boundary idiom, just nullable. **Logged-hours rollup** (TL-05) = `SUM(TimeLogs.hours)` over the backlog's tasks; SQLite returns the sum as `double` → map to `decimal` at the boundary. **No `is_active` filter on the Tasks join** (XC-06) — copy the join shape from `TimeLogRepository.GetReportRowsAsync` (`JOIN Tasks t ON t.id=l.task_id JOIN Backlogs r ON r.id=t.backlog_id`). `[VERIFIED]` lines 49-58.

**Display formatting**: reuse `ExportService.FormatHours(decimal)` — "4" when whole, "3.5" otherwise (EXP-04). It's `internal static`; **promote to a shared helper or duplicate the 3-line method** for Task-List display so whole estimates/logged-hours render without decimals (TL-05 "whole hours render without decimals"). `[VERIFIED]` `ExportService.cs:124-127`.

Estimate fallback for display: `official ?? rough` (TL-05). `[VERIFIED]` requirement; trivial VM coalesce.

---

## 6. Markdown export — monthly Task List archive (TL-09, D7)

**Recommendation: add a `TaskListArchiveService` that mirrors `StandupArchiveService` one-for-one** — it is the exact template for D7's "auto-backfill completed periods on startup + manual export."

Pattern to copy (`StandupArchiveService.cs` `[VERIFIED]`):
- Ctor injects the data repo(s) + `IAppConfig` + `IClock`. Lines 14-25.
- `FileNameFor(period)` → deterministic name. For Task List: `{yyyyMM}_tasklist.md` (TL-09), vs standup's `{yyyyMMdd}_daily.md` (line 27-28).
- `ExportMonthAsync(month)`: load rows for the month; `if (count==0) return null;` (no data → no file, TL-09); build markdown via `StringBuilder`; `Directory.CreateDirectory(dir)`; `File.WriteAllTextAsync(path, md, Encoding.UTF8)`. Lines 30-50.
- `BackfillMissingMonthsAsync()`: compute "current period" from `_clock.Today`; everything **strictly before** it is "completed"; for each completed month with data and no file, export. Lines 52-66. (Standup uses weeks; swap week-Monday logic for month `yyyy-MM`.)
- **Archive dir resolution:** `_config.ArchivePath` if set, else `Path.Combine(<db dir>, "TaskListArchives")`. Standup uses `"StandupArchives"`. Lines 68-75. (TL-09 says `…/Documents/TimesheetApp/TaskListArchives/` — `[ASSUMED]` mirrors standup; note standup actually defaults next to the **DB**, not Documents, so confirm whether to follow the literal Documents path or the existing "next to DB" behavior. **Recommend matching existing behavior: next to the DB / `ArchivePath`** for consistency.)
- **Wire startup backfill in `App.OnStartup`** next to the standup one (best-effort try/catch, never blocks startup). `[VERIFIED]` `App.xaml.cs:107-110`. Register the service as a singleton like `IStandupArchiveService`. `[VERIFIED]` line 81.
- **Markdown escaping:** reuse the `Esc`/`EscapePipe` (`s.Replace("|", "\\|")`) idiom and `FormatHours`. `[VERIFIED]` `StandupArchiveService.cs:132`, `ExportService.cs:124-130`.

**TL-09 wrinkle — "Moved to next month" section:** read `BacklogAudit` for `period_month` changes (the audit already records `period_month` field changes — `BacklogRepository.cs:117`). The archive for month M must also list backlogs whose `period_month` audit shows they moved **out of M to M+1**. `[VERIFIED]` audit captures `period_month` old→new; `[ASSUMED]` the exact "moved out" query (filter `BacklogAudit WHERE field='period_month' AND old_value=@M`).

Manual "Export this month" button → a VM command calling `ExportMonthAsync(selectedMonth)`, same as standup's `ArchiveWeekCommand`. `[VERIFIED]` `MainWindow.xaml:183-184`.

---

## Summary of decisions (drives spec/plan)

| Topic | Decision |
|---|---|
| Gantt | `ItemsControl`+`Canvas` panel; VM computes `BarX/BarWidth/BarY/BarBrush` over a **collapsed working-day axis**; `Canvas.Left/Top` via `ItemContainerStyle`. Promote `WorkingDays` to a shared holiday-aware `IWorkingDayCalculator`. Grid↔Gantt + collapse via existing string-match / bool-visibility converters. |
| Tag chips | `Border`(rounded)+`StackPanel`(glyph `TextBlock` + text). New `HexToBrushConverter` (copy `AvatarBrushConverter`+`ColorConverter.ConvertFromString`). Emoji glyph stored as literal string. System chips reuse theme `Amber*`/`Danger*` brushes, same chip template. Glyph picker = `WrapPanel` of emoji buttons. |
| Holidays | WPF `Calendar` (`SingleDate`) + restyled `CalendarDayButton` to mark holidays; selection-changed → VM toggle command; **not** `BlackoutDates`. Fallback: hand-rolled `UniformGrid` of `ToggleButton`s. Sub-tab inside `SettingsTab`. |
| Migration v6→v7 | Bump `SchemaVersion=7`; 4 new tables in `CreateTables` (`IF NOT EXISTS`); one gated migration step with 7 `ALTER … ADD COLUMN` on `Backlogs`; `pca_contact_id` = plain nullable INTEGER (no inline FK, like `assignee_user_id`); reuse `assignee_user_id` for PCT. |
| Decimal/REAL | Hours/estimates = `REAL` column, `(double)` write / `(decimal)` read at boundary via `Raw` DTO; estimates `decimal?`. Logged-hours = `SUM(hours)` join with **no** `is_active` filter. Display via `FormatHours`. |
| Markdown export | Clone `StandupArchiveService` → `TaskListArchiveService` (`{yyyyMM}_tasklist.md`, no-data→no-file, monthly backfill in `App.OnStartup`, `ArchivePath`-or-next-to-DB dir). "Moved to next month" from `BacklogAudit` `period_month` changes. |
