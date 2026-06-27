# P8 "Task List" — PITFALL RESEARCH (Mode B)

**Phase:** P8 / M3 (Task List: tracking, tags, holidays, Gantt)
**Stack:** WPF .NET 8 / SQLite+Dapper / MVVM, OneDrive-synced DB (`journal_mode=DELETE`, short connections)
**Agent:** Pitfall Research
**Date:** 2026-06-27
**Schema today:** `user_version = 6`; target `v7`.

> Every claim is tagged `[VERIFIED]` (confirmed in code — file:line), `[CITED]` (from REQUIREMENTS / STATE / spec text), or `[ASSUMED]` (inferred). Each pitfall has a concrete MITIGATION; regression-sensitive items name the existing test/behavior that must stay green.

---

## TOP 5 RISKS (ranked by severity)

| # | Risk | Sev | Why |
|---|------|-----|-----|
| **R1** | **v7 migration breaks the v6 rename chain / leaves stray legacy tables** — the `RunMigrations` step array is index-positional and the `CreateTables` legacy-`Requests` block is gated on `user_version<6`. A v7 step appended wrong, or a FK column added against a still-named-`Requests` table, corrupts or fails init for ALL existing M1/M2 DBs. | **Critical** | Single transaction wraps the whole init; a throw rolls back but the app can't open. Hits every existing user. |
| **R2** | **TL-02 sidebar restructure desyncs the index-based reload map** — `ActivateTabAsync` (0/1/2/3/4) and `OnSubTabChanged` (`0→0,1→1,2→3`) hard-code indices; moving Backlog/Reports out of the Timesheet sub-TabControl silently loads the wrong VM or stops live-refresh. | **High** | No compile error; manifests as stale/blank tabs at runtime. High blast radius across all M1/M2 tabs. |
| **R3** | **TL-05 logged-hours rollup re-introduces the `is_active` join bug** — `GetActiveForTimesheetAsync` filters `t.is_active=1`; if the rollup reuses it, soft-deleted tasks' TimeLogs vanish from the backlog total, violating XC-06 (the exact class of bug already fixed once in `TimeLogRepository`). | **High** | Silent under-counting; same bug family STATE.md says was already fixed live. |
| **R4** | **Schedule math (TL-07/08) divide-by-zero & null/DateOnly edge cases** — `loggedHours/estimate` and `elapsed/total` divide; null deadline/start/estimate, today<start, deadline≤start, zero working days all blow up or render nonsense chips. | **High** | A single crash on one backlog kills the whole Task List render (and `DispatcherUnhandledException` only shows a dialog). |
| **R5** | **Gantt WPF Canvas non-virtualization + DPI pixel binding** — `Canvas` has no UI virtualization; many backlogs × working-day columns = layout thrash and blurry/misaligned bars at non-100% DPI; collapse/expand re-lays the whole tree. | **Medium** | Perf/visual only (not data), but the most-likely UAT "feels broken" complaint and entirely manual-test-only. |

---

## 1. Migration v6 → v7 risks

### 1.1 Index-positional migration array is fragile
[VERIFIED] `RunMigrations` runs `for (step = current; step < migrations.Length; step++)` against a positional `Action[]`, and `if (current < SchemaVersion) PRAGMA user_version = {SchemaVersion}` (`DatabaseInitializer.cs:157-218`). `SchemaVersion` is a `const long = 6` (`:14`).
- **Pitfall:** v7 requires BOTH (a) appending exactly one element to `migrations[]` AND (b) bumping `SchemaVersion` to 7. Bumping the const without adding the array element means `step < migrations.Length` never reaches the v7 work yet `user_version` is set to 7 → the v7 columns/tables are never created but the DB claims to be v7 → every later read of `deadline_internal` etc. throws "no such column". The reverse (add element, forget const bump) sets `user_version=6` and re-runs the v7 step on every launch — `ADD COLUMN` is **not** idempotent → "duplicate column name" on 2nd launch.
- **MITIGATION:** Add the v7 step at index 6 AND set `const SchemaVersion = 7` in the same commit; add a `DatabaseInitializerTests` assertion that `user_version == 7` after init (extend the existing `InitializeAsync_Sets_User_Version_To_Target` test at `DatabaseInitializerTests.cs:96`, which currently only asserts `>= 1`).

### 1.2 Whole init is one transaction → a bad v7 step bricks startup
[VERIFIED] `InitializeAsync` opens one `tx`, runs `CreateTables + RunMigrations + EnsureDefaultBacklog + SeedDefaultTasksIfEmpty`, then `tx.Commit()` (`DatabaseInitializer.cs:23-35`). [VERIFIED] `App.OnStartup` awaits `InitializeAsync()` before any window (`App.xaml.cs:105`); an unhandled throw there happens **before** `DispatcherUnhandledException` can show a friendly dialog for a window that doesn't exist yet.
- **Pitfall:** any v7 SQL error (bad FK, duplicate column on a half-migrated DB) rolls back AND prevents the app from opening at all — for every existing user, since the DB is OneDrive-shared.
- **MITIGATION:** keep v7 strictly additive (`ADD COLUMN` / `CREATE TABLE IF NOT EXISTS`); test the v6→v7 upgrade path explicitly (see 1.7). Per XC-10, the DB is NOT auto-backed-up on init today — `DbBackupHelper.BackupAsync` is only called from bulk writes (`TimeLogService.cs:75,128`). Consider a one-shot `File.Copy` backup immediately before running migrations when `current < target` (cheap insurance; honors the XC-10 "backup before bulk write" spirit). [ASSUMED — extends XC-10 to the migration path]

### 1.3 `pca_contact_id` FK on `ADD COLUMN` vs SQLite + the legacy-table gate
[VERIFIED] v6 already does `ALTER TABLE Requests RENAME TO Backlogs` etc. inside the array (`:195-205`), and the legacy `Requests`/`RequestAudit` `CREATE` is gated on `version < 6` in `CreateTables` (`:127-149`). [VERIFIED] `Tasks` was created with an inline `FOREIGN KEY (request_id) REFERENCES Requests(id)` (`:53`) that survives the rename (SQLite keeps the FK pointing at the renamed table).
- **Pitfall A (ordering):** the v7 step runs on a DB where the table is **already named `Backlogs`**. The v7 `ALTER` must target `Backlogs`, never `Requests`. Copying the v6 step's `Requests` name is an easy mistake → "no such table: Requests".
- **Pitfall B (FK on ADD COLUMN):** SQLite **does** allow `ALTER TABLE Backlogs ADD COLUMN pca_contact_id INTEGER REFERENCES PcaContacts(id)` ONLY if the new column's default is NULL (it is — nullable). But the referenced table **must already exist at the time of the ALTER**. So `CREATE TABLE PcaContacts` MUST run earlier in the same v7 step than the `ADD COLUMN ... REFERENCES PcaContacts`. [VERIFIED nullable-default rule is the SQLite constraint; ordering is the actionable point]
- **Pitfall C (FK enforcement is ON):** [VERIFIED] every connection sets `PRAGMA foreign_keys=ON` (`SqliteConnectionFactory.cs:36,45`) and there's a test that a bad-FK insert is rejected (`DatabaseInitializerTests.cs:124`). The brainstorm note in the prompt asks whether `pca_contact_id` should follow `assignee_user_id`'s **no-inline-FK** precedent. [VERIFIED] `assignee_user_id` was deliberately added with **no FK** so "deactivating a user never blocks" (`DatabaseInitializer.cs:185-188` comment). [CITED] TL-01 acceptance says `pca_contact_id ... FK→PcaContacts`.
  - **Conflict:** PcaContacts use **soft-delete** (`is_active`, TL-11) not hard-delete, so a real FK won't be violated by deactivation — a FK is safe here, unlike users. BUT a real FK means you can never hard-delete a PcaContact that's referenced, and a stale/conflicting OneDrive copy that drops a PcaContacts row would make the whole DB fail FK check on open if `foreign_key_check` were run.
  - **MITIGATION (recommend):** follow the `assignee_user_id` precedent — add `pca_contact_id INTEGER` with **no inline FK**, resolve the name by join with no `is_active` filter (mirrors `assignee` rendering in `TimeLogService.BuildGroupsAsync:202-217`). This is consistent with the codebase's "soft-delete + name-resolve, never block on FK" pattern and avoids OneDrive-divergence FK failures. Flag the deviation from TL-01's literal "FK→PcaContacts" wording for the architect to ratify. [ASSUMED recommendation]

### 1.4 Backward-compat: old client opening a v7 DB
[CITED] DATA-05 requires an old client opening a newer DB to still work. [VERIFIED] additive `ADD COLUMN`/`CREATE TABLE` satisfies this — a v6 client never SELECTs the v7 columns and `for (step=current...)` simply doesn't run v7 on its side; it reads `user_version=7` but its own `SchemaVersion=6` so it skips the bump. [VERIFIED] the v6 client's `CreateTables` legacy gate `version < 6` is **false** on a v7 DB, so it won't recreate stray `Requests` tables either (`:127-128`).
- **Residual pitfall:** if a v6 client WRITES to a v7 DB (e.g. inserts a Backlog), it omits the v7 columns → they stay NULL, which is fine (all v7 cols are nullable). But a v6 client cannot satisfy a future NOT NULL/CHECK; keep every v7 column nullable with no CHECK. [ASSUMED]

### 1.5 The documented legacy `Requests`-on-relaunch quirk
[VERIFIED] STATE.md §Schema and the `CreateTables` comment (`:122-126`) document that re-creating `Requests`/`RequestAudit` with `IF NOT EXISTS` would leave **stray empty tables on every relaunch** on a v6+ DB — already guarded by `version < 6`. [VERIFIED] regression test `InitializeAsync_DoesNotRecreate_Legacy_Request_Tables_On_Relaunch` (`DatabaseInitializerTests.cs:59-72`).
- **Pitfall:** v7 must NOT touch this gate. If the v7 work is (wrongly) placed inside `CreateTables` instead of `RunMigrations`, it could re-evaluate the version mid-stream. **Keep all v7 DDL inside the `RunMigrations` array element**, where the version read is taken once at the top (`:208`).
- **MITIGATION / must-not-regress:** that legacy-tables test plus `InitializeAsync_Is_Idempotent_No_Duplicate_Default_Request` (`:48`) and `InitializeAsync_Creates_All_Seven_Tables` (`:34`, `ExpectedTables` array `:17` — extend with the 4 new tables) must stay green.

### 1.6 Idempotency of new tables/seed
[VERIFIED] `EnsureDefaultBacklog` and `SeedDefaultTasksIfEmpty` are idempotent guards run every launch (`:221-249`). New `Tags`/`PcaContacts`/`Holidays` need **no seed** (user-created); use `CREATE TABLE IF NOT EXISTS` so re-running v7 on a partially-migrated DB doesn't throw. But remember `ADD COLUMN` is the non-idempotent part — that's why it must be version-gated (covered by 1.1).

### 1.7 Missing test: there is no v6→v7 (or any cross-version) upgrade test today
[VERIFIED] all `DatabaseInitializerTests` start from a **fresh** DB (`:22-30` new temp dir each run) — there is **no test that seeds a v6 DB and upgrades it to v7**. So the most dangerous path (existing-user upgrade) is currently untested.
- **MITIGATION (required new test):** create a DB, force `PRAGMA user_version=6` with the v6 shape (or run init at v6 by checking out the gate), insert representative M1/M2 rows, then run the v7 initializer and assert: new columns exist, new tables exist, `user_version=7`, and the seeded M1/M2 rows are **unchanged** (count + values). This is the single highest-value test for P8.

### 1.8 XC-01 / XC-09 journal cleanliness on the migration write
[VERIFIED] migrations run inside one transaction on a connection from `SqliteConnectionFactory` (`journal_mode=DELETE`, `Pooling=False`), so on commit + dispose the `-journal` sidecar is removed and no `-wal/-shm` is ever created (`SqliteConnectionFactory.cs:31-50`). [VERIFIED] `IsJournalGone` check exists (`SqliteMaintenance.cs:31-32`) but is only invoked after bulk writes, **not** after init.
- **Pitfall:** a v7 migration is exactly the kind of "bulk write" XC-09/XC-10 care about, yet init doesn't check `IsJournalGone` afterward. If the migration is interrupted (laptop sleep / OneDrive lock), a lingering `-journal` could be synced.
- **MITIGATION:** after `InitializeAsync` commits, call `SqliteMaintenance.IsJournalGone(_config.DbPath)` and route a `IJournalWarningSink.Warn` (the sink + UI banner already exist, `App.xaml.cs:127-129`). Cheap, consistent with the existing pattern. [ASSUMED extension]

---

## 2. Sidebar restructure (TL-02) regressions

### 2.1 The index map is hard-coded in TWO places that must move together
[VERIFIED] `MainViewModel.ActivateTabAsync(int index)` switches on `0 Timesheet · 1 Backlog · 2 Users · 3 Reports · 4 Settings` (`MainViewModel.cs:129-146`, comment `:126`).
[VERIFIED] `OnActiveViewChanged` maps the sidebar string → that index: `"timesheet"=>0, "users"=>2, "settings"=>4, "dailyreport"` special-cased, else `-1` (`MainViewModel.cs:90-95`).
[VERIFIED] `MainWindow.OnSubTabChanged` maps the **sub-TabControl** index → the shell index: `tc.SelectedIndex switch {0=>0, 1=>1, 2=>3}` (Entry/Backlog/Reports) then calls `ActivateTabAsync` (`MainWindow.xaml.cs:15-23`).
[VERIFIED] the sidebar today has Timesheet, Daily Report, Task List (disabled SOON pill `MainWindow.xaml:71-79`), Users, Settings; the Timesheet content is a `DockPanel` hosting `TabControl x:Name="SubTabs"` with Entry/Backlog/Reports `TabItem`s (`MainWindow.xaml:144-163`).

- **Pitfall (the core regression):** pulling Backlog and Reports OUT of the `SubTabs` group and making them top-level sidebar items breaks **three** coupled mechanisms:
  1. **`OnActiveViewChanged` string→index** has no entries for `"backlog"`, `"tasks"`, `"reports"` today → they fall to `-1` → **the new top-level items never reload their VM** (Backlog/Reports show stale or empty data on click). Must add `"backlog"=>1, "reports"=>3, "tasks"=>?` and a `"reports"` multi-load (Reports needs 4 loads, see `ActivateTabAsync:136-143`).
  2. **`OnSubTabChanged`** becomes dead/wrong once Entry is the only sub-tab — but if the Timesheet `DockPanel` keeps the `TabControl` with one item, `SelectionChanged` still fires `0=>0` (harmless). If the `TabControl` is removed entirely (Entry promoted to the panel root), the `SelectionChanged="OnSubTabChanged"` handler reference must be removed or it dangles. [VERIFIED handler is XAML-wired `MainWindow.xaml:152`]
  3. **Reports' 4-step load** (`LoadUsersAsync + LoadBannerAsync + LoadMonthly + LoadWeekly`, `:136-143`) only happens via index 3. If Reports moves to a string-keyed top-level item, that whole block must be reachable from `OnActiveViewChanged`, or Reports renders with no users/banner/data. **This is the most likely concrete regression.**

- **MITIGATION:**
  - Make `OnActiveViewChanged` the **single source of truth** for reload (string-keyed), and either delete `OnSubTabChanged` (if Entry has no siblings) or shrink its map. Don't leave two parallel index maps.
  - Add explicit cases for every new top-level view; Reports keeps its 4-call sequence verbatim.
  - **Must-not-regress tests:** `CrossTabSyncTests.cs` and `MainViewModelTests.cs` exercise activation/reload — run them and add cases asserting that activating `"backlog"` calls `Backlogs.LoadAsync` and `"reports"` runs the 4 loads. [VERIFIED these test files exist]
  - **Live-refresh:** the cross-tab messenger bus (`IMessenger`, `App.xaml.cs:50-51`, `CrossTabSyncTests`) must still deliver `DataChanged` to Backlog/Reports VMs now that they're not nested — verify subscriptions aren't tied to sub-tab visibility. [ASSUMED — verify VM subscribes in ctor, not on tab-show]

### 2.2 `ActiveView` default + initial visibility
[VERIFIED] `ActiveView` defaults to `"timesheet"` (`MainViewModel.cs:86`) and content panels bind visibility via `StringMatchToVisibility` (`MainWindow.xaml:144,168,200,212`). New top-level items need their own `DockPanel` with a `StringMatchToVisibility` ConverterParameter and a `RadioButton` in the same nav group (so exactly one is checked). Missing a `RadioButton` group membership → two highlighted items or none.

### 2.3 Label rename "Timesheet" → "Log Work"
[CITED/ASSUMED] TL-02 marks the "Log Work" label `[ASSUMED]`. [VERIFIED] `ActiveView` string `"timesheet"` is used as the converter key in 4+ XAML bindings and the VM switch — **changing the display Content text is safe; changing the `ConverterParameter`/`ActiveView` token requires updating every binding + the VM switch together.** Recommend keeping the internal token `"timesheet"` and only changing the visible `Content="Log Work"`.

---

## 3. Working-day helper change (HOL-02)

### 3.1 Three independent WorkingDays implementations exist — consolidation risk
[VERIFIED] `SmartInputService.WorkingDays` (Mon–Fri only, `SmartInputService.cs:45-52`).
[VERIFIED] `TimeLogService.LastNWorkingDays` (separate Mon–Fri loop, `TimeLogService.cs:238-248`) and `IsWeekend` (`:233`).
[VERIFIED] `StandupArchiveService.MondayOf` (week math, `StandupArchiveService.cs:135`).
- **Pitfall:** HOL-02 says ONE shared helper must consult Holidays and be used by smart-input, schedule math, the ≤2-day window, and the Gantt axis. Refactoring `SmartInputService.WorkingDays` to take a holiday set changes a **pure, DB-free** service into one that needs a holiday source. [VERIFIED] `SmartInputService` is explicitly documented "No DB, no IClock" (`:5`) and constructed param-less in tests (`SmartInputServiceTests.cs:9`). Injecting `IHolidayRepository` into it would break that purity and all its tests' construction.
- **MITIGATION:** keep `SmartInputService` pure — pass the holiday set **in** as a parameter (e.g. `DistributeEven(from, to, total, IReadOnlySet<DateOnly> holidays)`), or have the **caller** pre-filter the day list. The new shared helper (`WorkingDayCalculator`) is pure and takes `(from, to, holidays)`. The VM/service layer owns fetching holidays from the repo and passing them down. This preserves SmartInputService's "pure math" contract.

### 3.2 Tests that must stay green
[VERIFIED] `SmartInputServiceTests` (12 cases): the canonical `10h/3 → 3.3,3.3,3.4` (`:13`), the sum-exact property test (`:34`), weekend-skip (`:71`), all-weekend no-op (`:83`), from>to no-op (`:93`), bad-total reject (`:100`), Full-8h (`:114`).
- **MITIGATION:** if the signature gains a `holidays` param, give it a default of "empty set" OR update all 12 tests to pass `[]`. With an **empty holiday set every existing assertion must produce identical output** — make that a regression invariant. The integer-tenths remainder-on-last-day logic (`SmartInputService.cs:20-29`) and `HasMoreThanOneDecimal` reject (`:54-55`) must not change.

### 3.3 New edge cases (holiday inside a smart-input range)
- A holiday landing mid-range reduces the working-day count → the **remainder lands on a different last day** and the per-day share changes. [VERIFIED] remainder goes to `days[^1]` (`:27`); if a holiday is the last calendar day in range, the last *working* day shifts earlier — fine, but add a test: `10h` over a range whose last day is a holiday must distribute over the working days only and still sum to exactly `10h`.
- A range that is **all holidays** (or all-weekend-or-holiday) must hit the existing `days.Count == 0 → Fail("No working days...")` path (`:17-18,38-39`) — extend, don't bypass, that guard. [VERIFIED guard exists]
- [VERIFIED] `TimeLogService.ValidateDayTotalsAsync` rejects weekends per cell (`:59`); HOL-02 does **not** ask to reject logging on a holiday (XC-05 is weekday-only; holidays are about *estimation/schedule* math). **Do NOT add holiday rejection to time-log validation** — that would regress XC-05's intent and is out of TL scope. [ASSUMED scope boundary — flag for spec]

---

## 4. Schedule math edge cases (TL-07/08)

[CITED] TL-07 formula: `behind = loggedHours/estimateHours < workingDaysElapsed(start,today) / workingDaysTotal(start,internalDeadline)`; chip suppressed when no start/deadline/estimate, when done, or when already past deadline (TL-08 takes over). TL-08: `today > deadline_internal AND not Done`.

### 4.1 Divide-by-zero
- **`estimateHours == 0` or null** → `loggedHours/estimate` is divide-by-zero (decimal throws `DivideByZeroException`; `0m/0m` throws). [VERIFIED] estimates are nullable REAL (TL-01 schema). **MITIGATION:** guard `estimate is null or <= 0 → no warning chip` (TL-07 already says "not shown when no estimate").
- **`workingDaysTotal(start, deadline) == 0`** (start==deadline on a weekend/holiday, or deadline<start) → divide-by-zero on the time fraction. **MITIGATION:** `if (workingDaysTotal <= 0) → no chip`. [VERIFIED the working-day helper can return 0 — same `Count==0` path as SmartInput `:17`]

### 4.2 Null / ordering
- **null `start` or `deadline_internal`** → TL-07 not computable → no chip (per acceptance). [VERIFIED both nullable, `BacklogRepository` maps them via `ParseDay` returning `DateOnly?` `:157-159`].
- **today < start** → `workingDaysElapsed` would be 0 or negative; done% (≥0) is not < 0 → not flagged, which is correct (work hasn't started). **MITIGATION:** clamp `elapsed = max(0, elapsed)`; if `elapsed == 0`, time-fraction is 0, so `done% < 0%` is false → no chip. Verify the helper counts inclusively/consistently with `workingDaysTotal`.
- **deadline < start** (data entry error) → negative total → guard via 4.1's `<= 0` check.
- **TL-08 precedence:** [CITED] late-deadline chip takes precedence over warning, and warning is suppressed once past deadline. **MITIGATION:** compute late first; if late → red chip and return (don't also evaluate warning). A Done backlog shows neither.

### 4.3 DateOnly vs DateTimeOffset / timezone
[VERIFIED] deadlines/start stored as `yyyy-MM-dd` TEXT and parsed to `DateOnly` (`BacklogRepository.cs:148,157-159`). [VERIFIED] "today" must be `IClock.Today` = `DateOnly.FromDateTime(DateTime.Now)` (**local** time, `IClock.cs:14`) — NOT `UtcNow` (which `DbBackupHelper` uses for stamps, `:31`). [VERIFIED] `created_at` is stored UTC (`Iso` uses `UtcDateTime`, `:145-146`).
- **Pitfall:** comparing a `DateOnly` deadline against a UTC-derived "today" off-by-one near midnight. **MITIGATION:** all schedule comparisons use `IClock.Today` (local DateOnly) exclusively; never mix in `UtcNow`. Inject `IClock` into the schedule service for deterministic tests (mirrors `TimeLogService`/`StandupArchiveService` which already take `IClock`).
- **"≤2 working days" window:** count working days between `today` and `deadline` using the shared holiday-aware helper, not calendar days. [CITED TL-07 "counts working days (excludes weekends + Holidays)"]

### 4.4 Testability
All of §4 is **pure function** territory → must have unit tests (see §8). Build the schedule evaluator as a pure service taking `(loggedHours, estimate, start, deadline, today, holidays, isDone)` → enum `{None, Warning, Late}`. Property: never throws for ANY combination of null/zero/reversed inputs.

---

## 5. Gantt WPF Canvas pitfalls

[CITED] D3 / TL-10: native WPF `Canvas`, one horizontal bar per backlog spanning start→deadline across working days; weekends+holidays visually skipped; colored by schedule state; collapsible chart area.

- **5.1 No virtualization** [VERIFIED conceptually — `Canvas` is not a virtualizing panel]. Every bar + every day gridline is a live visual. For a 2–5 person team with a month of backlogs this is small, but if the day axis draws one element per working day × per row it multiplies. **MITIGATION:** cap the drawn range to the selected month; draw the day-axis grid once (shared background), not per row; consider `DrawingVisual`/`StreamGeometry` for gridlines instead of N `Line` elements.
- **5.2 Pixel-position binding & DPI** [ASSUMED]. Binding `Canvas.Left`/`Width` to computed pixels assumes 96 DPI; at 125%/150% Windows scaling WPF scales automatically (device-independent units) so this is usually fine — BUT manual `ActualWidth`-based math (dividing available width by day count) must read `ActualWidth` after layout, not at construction (it's 0 before measure). **MITIGATION:** compute bar geometry in a `SizeChanged`/`Loaded` handler or a custom `Panel.ArrangeOverride`, not in the VM at load time; use DIUs throughout, never hard-coded device pixels.
- **5.3 Collapse/expand layout thrash** [ASSUMED]. Toggling the chart area visibility re-measures the whole tree; binding the toggle to `Visibility.Collapsed` (not `Hidden`) avoids reserving space but forces a re-layout on each toggle. **MITIGATION:** collapse a container, keep the Canvas content cached; debounce month-switch + toggle so they don't both re-render.
- **5.4 Weekend/holiday "skipped" columns** must use the SAME shared working-day helper (§3) as the math, or bars and chips disagree. [CITED HOL-02 lists "the Gantt day axis" as a consumer of the shared helper.]
- **Testability:** the Canvas rendering itself is **not auto-testable** (visual). The day→x-coordinate mapping IS pure → unit-test the coordinate function separately from the Canvas. Flag the visual/drag aspects for UAT (§8).

---

## 6. Tag chips / hex color

- **6.1 Invalid hex → brush crash** [ASSUMED — no existing color converter found]. [VERIFIED] no hex/color converter exists in `Views/Converters` (only Active/BoolVis/DateOnly/Initial/OverEightTag/StringMatch/AvatarBrush). `ColorConverter.ConvertFromString("#zzz")` throws `FormatException`; binding a bad `Tags.color` to a `Brush` will throw on the UI thread → caught only by `DispatcherUnhandledException` (dialog spam). **MITIGATION:** write a `HexToBrushConverter` that try/catches and falls back to a neutral default brush; validate/normalize hex on save in the Tag editor (require `#RRGGBB`); never bind raw text straight to a `SolidColorBrush`.
- **6.2 Emoji/glyph font fallback** [ASSUMED]. [VERIFIED] the app uses 'Segoe UI' 13px (STATE.md design tokens); sidebar already uses emoji glyphs via `Tag="&#128197;"` (`MainWindow.xaml:63`). Custom-tag icons are `[ASSUMED]` emoji/Segoe glyphs (TAG-01). Some emoji render as tofu if the chip uses a non-emoji `FontFamily`. **MITIGATION:** render icon TextBlocks with `FontFamily="Segoe UI Emoji"` fallback; restrict the picker to a known glyph set.
- **6.3 Deleting a tag in use (cascade BacklogTags)** [CITED TAG-01: "deleting a tag removes its BacklogTags links"]. [VERIFIED] the existing cascade precedent is `StandupIssues.entry_id ... ON DELETE CASCADE` (`DatabaseInitializer.cs:116`). **Pitfall:** `BacklogTags(backlog_id, tag_id)` needs the FK to `Tags(id)` declared `ON DELETE CASCADE` AND `foreign_keys=ON` (which it is, `SqliteConnectionFactory.cs`) for the delete to cascade automatically; otherwise a tag delete leaves orphan `BacklogTags` rows that break the chip join. **MITIGATION:** declare `FOREIGN KEY(tag_id) REFERENCES Tags(id) ON DELETE CASCADE` in the v7 `CREATE TABLE BacklogTags`, and add a repo test that deleting a referenced tag removes its links. Mirror for `BacklogTags.backlog_id` (though backlogs aren't deletable today — decision 4).

---

## 7. Monthly export (TL-09)

[CITED] mirror the standup pattern: `…/Documents/TimesheetApp/TaskListArchives/{yyyyMM}_tasklist.md`; backfill every completed month (strictly before current) with data but no file; manual "Export this month"; includes a "Moved to next month" section derived from `BacklogAudit` period_month changes; idempotent overwrite; no-data month → no file.

Reference implementation to copy: `StandupArchiveService` (`StandupArchiveService.cs`).

- **7.1 Month-boundary "completed vs current" must use LOCAL time** [VERIFIED] standup backfill uses `_clock.Today` (local) for `currentMonday` (`StandupArchiveService.cs:54`). **MITIGATION:** TL-09 must use `_clock.Today` to derive the current month (`yyyy-MM`) and treat strictly-earlier months as completed. Using `UtcNow` would mis-classify the boundary day. [VERIFIED period_month format is `"yyyy-MM"`, `RequestEditorViewModel.cs:49`].
- **7.2 Idempotent overwrite vs backfill skip** [VERIFIED] standup `ExportWeekAsync` always overwrites (`File.WriteAllTextAsync`, `:48`) while `BackfillMissingWeeksAsync` only writes if `!File.Exists` (`:64`). **MITIGATION:** copy this exactly — manual export overwrites; backfill skips existing files. Don't let backfill overwrite a hand-edited archive.
- **7.3 No-data month → no file** [VERIFIED] standup returns `null` when `entries.Count==0` (`:36`). Mirror: a month with no backlogs in `period_month` AND no "moved out" audit rows → no file.
- **7.4 "Moved to next month" double-counting** [CITED] derive from `BacklogAudit` where `field='period_month'`. [VERIFIED] the audit logs `period_month` changes with old/new values (`BacklogRepository.cs:117`) and `"Move ▶"` bumps period_month by one (`TimesheetViewModel.cs:395-411`).
  - **Pitfall:** a backlog moved out of month M appears BOTH in M's "Moved to next month" section (because its audit shows `old=M`) AND in month M+1's main list (because its current `period_month=M+1`). That's intended — but if a backlog is moved **multiple times** (M→M+1→M+2), naive audit reading lists it under several months' "moved" sections and could double-count in any aggregate. **MITIGATION:** for month M's "moved out" list, select audit rows where `old_value == M` (the move happened FROM M), and de-dup by backlog id (take the latest move per backlog per source month). Don't sum hours into both the "moved" section and the main list — the moved section is informational (lists the backlog), not an hours roll-up. [ASSUMED — confirm with spec whether moved backlogs show hours]
- **7.5 File path / encoding / OneDrive churn** [VERIFIED] standup writes UTF-8 (`Encoding.UTF8`, `:48`) and `Directory.CreateDirectory` first (`:46`). Mirror. **OneDrive churn:** backfill writes once (guarded by `File.Exists`); the **manual** button overwrites every click — acceptable (user-initiated). Don't auto-export on every Task List load (would churn OneDrive). [ASSUMED]
- **7.6 Escaping** [VERIFIED] standup escapes `|` in cells (`Esc`, `:132`). Backlog code/project/note/tag text can contain `|` → escape every interpolated cell in the markdown table the same way.
- **7.7 Startup ordering** [VERIFIED] standup backfill runs in `App.OnStartup` after init, wrapped in try/catch so it never blocks startup (`App.xaml.cs:109-110`). Add the Task List backfill the same way, AFTER `InitializeAsync` and AFTER `DefaultTaskSyncService.SyncAsync` (`:117`), in its own try/catch.

---

## 8. Testing strategy

### Needs UNIT tests (pure / service / data — must be auto-tested)
| Area | What to test | Mirror existing |
|---|---|---|
| v6→v7 migration (§1.7) | upgrade a seeded v6 DB → columns/tables exist, `user_version=7`, M1/M2 rows intact | `DatabaseInitializerTests` |
| Working-day helper (§3) | weekends + holidays excluded; empty-set == today's behavior; holiday-in-range | `SmartInputServiceTests` |
| Schedule evaluator (§4) | divide-by-zero, null/zero/reversed dates, today<start, late-precedence, done-suppression; **never throws** | new pure-service test |
| Logged-hours rollup (§Top R3) | sum across tasks **including soft-deleted** (no `is_active` on the TimeLogs join, XC-06) | `TimeLogServiceTests`, `ReportAggregatorTests` |
| Tag delete cascade (§6.3) | deleting a tag removes `BacklogTags`, leaves backlog | `RepositoryCrudTests` |
| Hex→brush converter (§6.1) | valid hex → brush; invalid → fallback, no throw | new converter test |
| Monthly export (§7) | idempotent overwrite, no-data→no-file, "moved out" de-dup, `\|` escaping, local-time month boundary | `StandupArchiveServiceTests` |
| PcaContacts soft-delete (TL-11) | deactivated contact hidden from dropdown but name still resolves on existing backlogs | `UsersViewModelTests` pattern |
| TL-02 activation (§2.1) | activating `"backlog"`/`"reports"`/`"tasks"` reloads the right VM (Reports runs its 4 loads) | `MainViewModelTests`, `CrossTabSyncTests` |

### CANNOT be auto-tested — flag for UAT (mouse/visual)
- [CITED STATE.md:60-62] **Drag & drop is already untested** ("mouse interaction — NOT auto-tested"); any new Gantt drag inherits this.
- **Gantt bar rendering / DPI / collapse-expand** (§5) — visual layout only.
- **Gantt drag to change start/deadline** (if added) — mouse interaction; UAT only.
- **Holiday calendar click-to-mark** (HOL-01) — mouse interaction; the **insert/delete repo** behind it IS unit-testable, the calendar click is not.
- **Tag chip visual rendering** (emoji/glyph fallback, color) — eyeball only.
- **Sidebar nav highlight** (one-and-only-one RadioButton checked) — visual.
> Extract every pure mapping (day→x coordinate, schedule→color, hex→brush) into a testable function so only the irreducibly-visual/mouse parts go to UAT.

---

## Cross-cutting reminders for the architect (STEP 5)
- Keep `SmartInputService` pure (pass holidays in) — don't inject a repo (§3.1).
- Resolve the `pca_contact_id` FK-vs-no-FK question against the `assignee_user_id` precedent (§1.3) — recommend **no inline FK**.
- v7 = strictly additive, all-nullable, no CHECK, inside the `RunMigrations` array, const bumped to 7, with a v6→v7 upgrade test (§1.1, §1.7).
- One reload source of truth for the sidebar (§2.1); preserve Reports' 4-call load.
- All "today" comparisons via `IClock.Today` (local), never `UtcNow` (§4.3, §7.1).
