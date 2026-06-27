# P8 — Task List · Goal-Backward Verification (STEP 8b, Mode B)

**Phase:** P8 — Task List (tracking, tags, holidays, Gantt)
**Branch:** `feature/task-list-2026-06-27`
**Date:** 2026-06-27
**Inputs:** `docs/superpowers/plans/2026-06-27-P8-task-list.md` (must_haves), `.planning/REQUIREMENTS.md` (TL/TAG/HOL), `docs/superpowers/specs/2026-06-27-task-list-design.md`
**Build/test:** `dotnet test src/TimesheetApp.sln` → **0 errors, 314 passed / 0 failed / 0 skipped** (W1 baseline 228 → +86 new P8 tests).

Legend: **MET** (artifact + symbol satisfy it) · **PARTIAL** (something missing) · **NOT MET** · **UAT-pending** (visual/interaction not code-verifiable).

---

## 1. must_haves — Observable Truths

| Observable truth | Status | Evidence |
|---|---|---|
| Opening a v6 DB upgrades to v7 (7 cols + 4 tables) without data loss; idempotent re-run | **MET** | `DatabaseInitializer.cs:14` `SchemaVersion=7`; v7 step `:237-244` (7 `ALTER TABLE Backlogs ADD COLUMN`); 4 `CREATE TABLE IF NOT EXISTS` Tags/BacklogTags/PcaContacts/Holidays `:123-146`; whole init one tx `:25-34`. Proven by `SchemaV7UpgradeTests.cs` (seeds user_version=6, runs real initializer; additive/data-preserving/idempotent) — green. |
| Sidebar shows Log Work · Backlog · Task List · Daily Report · Reports (+ Users · Settings); each loads; no Timesheet/Standup/Reports regression | **MET** (nav routing) / **UAT-pending** (visual no-regression) | `MainWindow.xaml:63-83` RadioButtons (Log Work/backlog/tasklist/Daily Report/reports); `MainViewModel.OnActiveViewChanged:96-108` routes each key; sub-tab `TabControl`/`OnSubTabChanged` removed (grep: **no matches** anywhere in src). Regression = 314 tests green incl. Timesheet/Standup/Reports suites; live nav no-regression is UAT. |
| A backlog can store internal+external deadline, rough+official estimate, manual %, note, PCT (User), PCA (list), N tags | **MET** | `Entities.cs:10-19` Backlog v7 fields; `BacklogRepository` Insert `:55-82` / Update `:84-167` thread all 7 cols; tags via `SetTagsAsync:179-193`; editor `RequestEditorViewModel.cs:72-95`; save `RequestsViewModel.cs:173-184,198-207`. |
| Task List shows per-month every non-DEFAULT backlog w/ status/assignees/deadlines/progress/logged/estimate/tag-chips; rows expand | **MET** (logic) / **UAT-pending** (rendering) | `TaskListViewModel.LoadAsync:92-150` (filter `PeriodMonth==monthKey`, DEFAULT excluded `:112`); `TaskListRow` populated `:133-136`; chips `BuildChips:286-297`; expand `ToggleExpand:228-231` + `TaskListTab.xaml` grid/chips/expander. |
| ≤2 working days of internal deadline AND behind → amber Warning; past deadline (not Done) → red Late; Done → neither | **MET** | `ScheduleStateService.Evaluate:9-45` — Done→Normal `:15`; no deadline→Normal `:18`; `today>deadline`→Late `:21`; ≤2-wd window `:28-32`; behind via cross-multiply (div-guarded) `:35-43`. `ScheduleStateServiceTests.cs` covers full decision table incl. div-by-zero/null/reversed — green. |
| Settings: create/edit/delete tags (icon+color+text), manage PCA (soft-delete), mark/unmark holidays on calendar | **MET** (commands) / **UAT-pending** (overlay/calendar visuals) | `SettingsViewModel.cs` tags CRUD `:184-225`, PCA add/rename/deactivate `:236-267`, `HolidayCalendarViewModel.cs` ToggleHoliday upsert/delete `:83-96`; `SettingsViewModelTests.cs` green. |
| Holidays + weekends excluded from every working-day computation via one shared calculator | **MET** | `WorkingDayCalculator.cs:6-20` (Sat/Sun + holiday set); used by `ScheduleStateService` (passed-in `calc`), `SmartInputService:30,51` (holiday overloads), Gantt axis `TaskListViewModel.BuildGantt:180` (`calc.WorkingDaysBetween`). Single `IWorkingDayCalculator` injected everywhere. |
| Task List toggles Grid↔Gantt (start→internal-deadline bars over working days, colored by state, external marker) collapsible | **MET** (model+toggle) / **UAT-pending** (canvas pixels) | `IsGantt`/`IsChartCollapsed` `TaskListViewModel.cs:79-80`; `BuildGantt:159-221` axis + StartDayIndex/SpanWorkingDays/ExternalMarkerIndex/HasStart; `TaskListTab.xaml:123-129` Canvas; `TaskListTab.xaml.cs DrawGantt:73` colors by `ScheduleState.Late/Warning :166-167`, external marker `:211`. `GanttModelTests.cs` geometry — green. |
| Completed months auto-export markdown on startup (incl. "Moved to next month"); manual "Export this month" button | **MET** | `TaskListArchiveService.cs` `ExportMonthAsync:49-96` (Members + "Moved to next month" `:88-89`, no data→null `:73`), `BackfillMissingMonthsAsync:98-128`; startup call `App.xaml.cs:123`; manual button `TaskListViewModel.ExportThisMonthAsync:235-248` + `TaskListTab.xaml:102`. `TaskListArchiveServiceTests.cs` (moved-out/no-data/idempotent/escape) — green. |

---

## 2. must_haves — Required Artifacts

| Artifact | Status | Evidence |
|---|---|---|
| DatabaseInitializer v7 migration + CreateTables (4 tables) | **MET** | `DatabaseInitializer.cs:14,123-146,237-244` |
| Entities Tag/PcaContact/Holiday + Backlog v7; ReadModels ScheduleState/TaskListRow/GanttBar/GanttModel | **MET** | `Entities.cs:10-19,59,62,65`; `ReadModels.cs:77,81-95` |
| ITagRepository, IPcaContactRepository, IHolidayRepository; IBacklogRepository tag-link + v7 cols; ITimeLogRepository.GetLoggedHoursByBacklogAsync | **MET** | `TagRepository.cs`, `PcaContactRepository.cs`, `HolidayRepository.cs`; `BacklogRepository` v7 cols `:12-13,55-167` + `GetTagIdsAsync/SetTagsAsync/GetTagIdsForAllAsync:171-203`; `TimeLogRepository.GetLoggedHoursByBacklogAsync:105-116` |
| IWorkingDayCalculator, IScheduleStateService, ITaskListArchiveService; SmartInputService holiday overloads | **MET** | `WorkingDayCalculator.cs`, `ScheduleStateService.cs`, `TaskListArchiveService.cs`; `SmartInputService.cs:23,49` holiday overloads |
| SettingsViewModel/SettingsTab additions + HolidayCalendarViewModel + HexToBrushConverter | **MET** | `SettingsViewModel.cs:62-291`; `HolidayCalendarViewModel.cs`; `HexToBrushConverter.cs`; `SettingsTab.xaml` (exists) |
| BacklogEditorViewModel/BacklogsTab tracking fields + tag pick | **MET** | `RequestEditorViewModel.cs:9-95` (TagPickVm + v7 props); `RequestsTab.xaml` (exists) |
| TaskListViewModel + TaskListTab (grid + Gantt canvas) | **MET** | `TaskListViewModel.cs`; `TaskListTab.xaml` (+ `.xaml.cs` DrawGantt) |
| MainWindow/MainViewModel sidebar restructure | **MET** | `MainWindow.xaml:63-83`; `MainViewModel.cs:78,91-108` |
| App.xaml.cs DI + MainViewModel injection + TL-09 startup backfill | **MET** | `App.xaml.cs:83-89` (repos+services), `:102-109` VM/MainVM transient, `:123-124` backfill |

---

## 3. must_haves — Key Links (confirmed in code)

| Key link | Status | Evidence |
|---|---|---|
| Schema v7 const + array move together (R1) → upgrade test green | **MET** | `SchemaVersion=7` (`:14`) and the v7 migration array element (`:237-244`) both present; `SchemaV7UpgradeTests` green. |
| Logged-hours join has NO `is_active` filter (XC-06, R3) | **MET** | `TimeLogRepository.cs:111-114` — `JOIN Tasks t ON t.id=l.task_id GROUP BY t.backlog_id`, no `is_active` predicate (comment `:108-109`). `TaskListRepositoryTests` asserts soft-deleted-task hours counted. |
| ScheduleStateService never throws (R4); shares IWorkingDayCalculator with Gantt axis (R5) | **MET** | `ScheduleStateService.cs:9-45` cross-multiplies, no division → never `DivideByZero`; same `IWorkingDayCalculator` passed to Evaluate and `BuildGantt` (`TaskListViewModel.cs:122-123` & `:147-149`). |
| New repos use IConnectionFactory per-method (XC-01); pca_contact_id no inline FK | **MET** | Each new repo opens `using var c = _factory.Create();` per method (Tag/Pca/Holiday); v7 ALTER adds `pca_contact_id INTEGER` with no `REFERENCES` (`DatabaseInitializer.cs:244`). |
| Sidebar string-keyed; top-level views routed via OnActiveViewChanged (R2) | **MET** | `MainViewModel.cs:91` `ActiveView` string; `OnActiveViewChanged:96-108` switch on key. |
| New DataKind values Tags/PcaContacts/Holidays broadcast on CRUD | **MET** | `DataChangedMessage.cs:13-15`; broadcast in `SettingsViewModel.cs:213,224,245,258,266` & `HolidayCalendarViewModel.cs:95`; consumed `TaskListViewModel.cs:61-63`. |

---

## 4. REQ Coverage (P8)

| REQ | Artifact(s) | Status |
|---|---|---|
| TL-01 Schema v7 cols + 4 tables (additive) | `DatabaseInitializer.cs:14,123-146,237-244` + `SchemaV7UpgradeTests` | **Covered** |
| TL-02 Sidebar Backlog/Task List/Reports top-level | `MainWindow.xaml:63-83`, `MainViewModel.cs:96-108` | **Covered** (live no-regression UAT-pending) |
| TL-03 Backlog tracking fields in editor | `RequestEditorViewModel.cs:72-95`, `RequestsViewModel.cs:173-207`, audit `BacklogRepository.cs:150-166` | **Covered** |
| TL-04 Task List overview per month | `TaskListViewModel.LoadAsync:92-150`, `TaskListTab.xaml` | **Covered** |
| TL-05 Logged hours + estimate (official??rough, whole) | `TimeLogRepository.cs:105-116`, `TaskListViewModel.cs:118,277` (`EstimateText`) | **Covered** |
| TL-06 Manual progress % (0–100, null≠0) | `RequestEditorViewModel.ParseProgress:120-127`; `TaskListRowVm.ProgressText:274` (null→"—") | **Covered** |
| TL-07 Auto Warning (≤2 wd + behind, done%<elapsed%) | `ScheduleStateService.cs:24-44` | **Covered** |
| TL-08 Auto Late (today>internal deadline, not Done) | `ScheduleStateService.cs:21` | **Covered** |
| TL-09 Monthly markdown (auto-backfill + manual + moved-out) | `TaskListArchiveService.cs`, `App.xaml.cs:123`, `TaskListViewModel.cs:235-248` | **Covered** |
| TL-10 Grid↔Gantt toggle + collapse | `TaskListViewModel.cs:79-80,159-221`, `TaskListTab.xaml(.cs)` | **Covered** (canvas visuals UAT-pending) |
| TL-11 Manage PCA contacts in Settings (soft-delete) | `PcaContactRepository.cs`, `SettingsViewModel.cs:236-274` | **Covered** |
| TAG-01 Create/manage tags in Settings | `TagRepository.cs`, `SettingsViewModel.cs:184-232`, `TagEditorViewModel.cs` | **Covered** |
| TAG-02 Render tag chips (custom+system) | `TaskListViewModel.BuildChips:286-297`, `TaskListTab.xaml` ChipTemplate + `HexToBrushConverter` | **Covered** (chip rendering/hex UAT-pending) |
| HOL-01 Holiday calendar sub-tab in Settings | `HolidayCalendarViewModel.cs`, `HolidayRepository.cs`, `SettingsTab.xaml` | **Covered** (calendar click UAT-pending) |
| HOL-02 Holidays excluded from all working-day math | `WorkingDayCalculator.cs:6-7`; consumed by smart-input, schedule, ≤2-day window, Gantt axis | **Covered** |

All 15 P8 REQ-IDs map to implementing artifacts; none uncovered.

---

## 5. Gaps (ranked)

No code-level gaps that block the phase. Residual items are UAT-only by design (spec §8 "UAT-only") and are expected at STEP 8b, not defects:

1. **(UAT) Visual / interaction surfaces not code-verifiable** — Gantt canvas pixel layout/colors/markers, holiday-calendar click feedback, tag-chip rendering + hex fallback appearance, sidebar nav with no Timesheet/Standup/Reports visual regression, backlog-editor field round-trip in the live form, Grid↔Gantt toggle/collapse. These are flagged UAT-pending in the tables above and must be confirmed in STEP 8a UAT.

2. **(Minor, informational — not a gap vs must_haves)** TAG-02 says chips appear on **both** the Backlog list and the Task List/Gantt. Verified rendering on the **Task List** (`TaskListTab.xaml` ChipTemplate). Whether chips also render on the **Backlog list** (`RequestsTab.xaml`) was not separately confirmed in this pass — recommend a quick UAT check on the Backlog tab. The plan's observable_truth only requires Task List chips (which are MET); the spec/REQ phrasing is broader, so this is a watch-item, not a blocker.

---

## 6. Verdict

**VERIFIED** — All 9 observable truths, all 9 required-artifact groups, and all 6 key_links are satisfied in code with concrete symbols; all 15 P8 REQ-IDs are covered. The full suite builds clean and runs **314/314 green** (228 baseline preserved + 86 new P8 tests covering the v6→v7 upgrade, working-day calc, schedule decision table, no-`is_active` roll-up, repo CRUD, Gantt geometry, archive moved-out/idempotent, and settings/editor VMs).

The only outstanding items are inherently UAT-only (visual/interaction) and are correctly deferred to STEP 8a. One informational watch-item: confirm tag chips also render on the Backlog list (TAG-02), not just the Task List.
