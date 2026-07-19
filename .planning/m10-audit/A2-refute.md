# A2 — Log Work (Timesheet) — Adversarial Refutation

Section audited: **A2 — TimesheetTab + TimesheetViewModel**
Auditor claimed 13 behaviors COVERED. **7 refuted (PARTIAL), 6 upheld (COVERED).**

Scope reminder: `src/TimesheetApp/` DIES (ViewModels, Views, Services, App.xaml.cs).
`src/TimesheetApp.Core/` SURVIVES. Nothing in this section lives in Core except the Smart Fill
*math* (`SmartInputService.cs`), which survives but has no web caller for the parts it exposes.

---

## Verdict table

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| Week nav (Prev/Next, "Week of dd/MM/yyyy") | COVERED | **YES** | **PARTIAL** | Jump-to-any-week DatePicker `TimesheetTab.xaml:138-139` → `TimesheetViewModel.cs:95-106` (`OnJumpDateChanged` snaps to that date's Monday) has **no web equivalent**: `log-work.component.ts:278-280` is only prev/next/this-week. [VERIFIED] |
| Smart fill | COVERED | **YES** | **PARTIAL** | Web is drastically narrower — see detail §2. `SmartInputPanelVm.cs:83-116,60,66-67,118-149` vs `log-work.component.ts:460-525` + `smart-fill.ts:43-56`. [VERIFIED] |
| Week-total chip | COVERED | no | COVERED | `TimesheetTab.xaml:110-118` (`WeekTotal`, `N1`+"h") ≡ `log-work.component.html:9-12` + `log-work.component.ts:268-269`. [VERIFIED] |
| Grid columns TASK \| MON..FRI \| TOTAL | COVERED | no | COVERED | `TimesheetTab.xaml:226-255` + `TimesheetViewModel.cs:211-216` ("MON 13/07") ≡ `log-work.component.html:39-45` + `week.ts:62-72` (same `dow` + `dd/MM`). [VERIFIED] |
| Task row: handle, name, 5 day cells, row total | COVERED | **YES** | **PARTIAL** | Row *structure* matches, but **holiday day-cells are entirely absent web-side**: `TimesheetTab.xaml:342-376` (`HolidayCellBorder` + `Tag={MonIsHoliday}` + `IsReadOnly={MonReadOnly}`) and the "Holiday" watermark `TimesheetTab.xaml:46-65`, driven by `TimesheetViewModel.cs:137-147,228-239`. Grep over `pages/log-work` finds holiday only in a comment (`log-work.component.ts:362`). [VERIFIED] |
| Inline "Add task" dialog | COVERED | no | COVERED | `TaskInputDialog.xaml:1-38` ≡ `add-task-dialog.component.ts:21-39` (title/label/Enter/Esc/empty-refusal), wired `log-work.component.html:210-212` → `.ts:539-552` → `worklog.service.ts:347-350` (POST /api/tasks). Web is *stricter* (`nextOrderIndex`, `grid-state.ts:140-142`, fixes a real WPF append-at-`Count` bug). [VERIFIED] |
| "Move to next month" (hidden for DEFAULT) | COVERED | no | COVERED | Both guards present both sides: `RequestGroupVm.cs:40` + `TimesheetViewModel.cs:477` ≡ `move-month.ts:23-25` + `log-work.component.ts:603`, UI gate `TimesheetTab.xaml:393-399` ≡ `log-work.component.html:139-142`. Audit preserved server-side: `BacklogEndpoints.cs:162`. [VERIFIED] |
| Trash zone pinned bottom, visible only when editable | COVERED | **YES** | **PARTIAL** | WPF `TimesheetTab.xaml:258-265` is `DockPanel.Dock="Bottom"` — **outside** the `ScrollViewer` at `:270`, i.e. genuinely pinned. Web `log-work.component.html:179-182` is the last child of `.grid` *inside* `.grid-scroll`, with `log-work.component.scss:80,90` giving it plain `margin` and **no `position:sticky`** (only `.grid__head` is sticky, `scss:21`). It scrolls away. [VERIFIED] |
| Footer DAY TOTALS row + week total | COVERED | **YES** | **PARTIAL** | The auditor's own cited range contains the **>8h red/bold warning**: `TimesheetTab.xaml:205-217` (`OverEightTag` on all five day totals) + style `TimesheetTab.xaml:9-17`, converter `Views/Converters/OverEightTagConverter.cs:6`. Web footer has only `.grid__foot .c-day.zero` (`log-work.component.scss:72`) — no over-8 rule anywhere in `pages/log-work`. [VERIFIED] |
| Type → blur → auto-save (no Save button) | COVERED | **YES** | **PARTIAL** | Commit-on-blur is real (`log-work.component.html:118-119` ≡ `TimesheetTab.xaml:347` `UpdateSourceTrigger=LostFocus`). But cited `TimesheetViewModel.cs:369-383` is mostly the **save-status feedback** — "Saving…" / "✓ Saved" / "⚠ {error}" rendered at `TimesheetTab.xaml:141-159`, the element that literally "replaces the old Save button". Web has **no success indicator**; grep for Saving/Saved across `src/app` returns only the `SavedBody` DTO. [VERIFIED] |
| Clear cell = DELETE (not save-of-zero) | COVERED | **YES** | **PARTIAL** | Narrow claim holds (`log-work.component.ts:330-348` → `worklog.service.ts:284-288`, DELETE). **But** `parseHours` maps *any unparseable text* to `null` (`grid-state.ts:197-202`) and `commitCell` routes `null` → `clearCell` (`log-work.component.ts:301`). Typing `abc` over a cell holding 4h **deletes the 4h**. WPF cannot: `TimesheetRowVm.cs:31-59` (`INotifyDataErrorInfo`) flags the cell and `TimesheetViewModel.cs:372-376` refuses the write. Silent data loss on a typo. [VERIFIED] |
| Drag reorder, same backlog group only | COVERED | no | COVERED | Same-group is *structural* web-side: groups connect only to `'trash'` (`log-work.component.html:91-92`), asserted `log-work.component.ts:697`. `reorder.ts:24-32` rewrites every row exactly as `TimesheetViewModel.cs:421` does. Wired `worklog.service.ts:382-385`. [VERIFIED] |
| Drag to trash = soft-delete (is_active=0, logs kept) | COVERED | no | COVERED | `TimesheetViewModel.cs:426-431` (`SetActiveAsync(id,false)`) ≡ `log-work.component.ts:764-776` (arm) → `:789-815` (`setTaskActive(id,false)`) → `worklog.service.ts:406-409` (PUT /api/tasks/{id}/active, "NOTHING IS DESTROYED"). Web adds a confirm dialog. [VERIFIED] |

---

## Detail on the refutations

### §2 — Smart fill is the largest gap in this section

`SmartInputPanelVm.cs` **dies** (it is in `src/TimesheetApp/ViewModels/`). What dies with it:

| WPF capability | File:line | Web equivalent |
|---|---|---|
| Find tasks by **backlog code / project** partial search, team-scoped | `SmartInputPanelVm.cs:83-116` | **none** — web picks from `allTasks()`, only tasks already on *this week's* grid (`log-work.component.ts:483-484`) |
| **Multi-task** checkbox selection; total spread across all checked tasks | `SmartInputPanelVm.cs:26-27,126-127` | **none** — `sfTaskId` is a single number (`log-work.component.ts:185`) |
| **Two modes**: `DistributeEven` *and* `FillFull8h` | `SmartInputPanelVm.cs:60,151-152` | **none** — even split only (`smart-fill.ts:43-56`) |
| Arbitrary **From/To date range** (may span weeks/months) | `SmartInputPanelVm.cs:66-67` | **current week only** (`log-work.component.ts:463`, `log-work.component.html:250`) |
| **Preview + validate before apply**; `CanApply` gates the write | `SmartInputPanelVm.cs:118-149,154-157`, `SmartInputPreviewDialog.xaml` | **none** — Apply writes immediately (`log-work.component.ts:494-525`) |

The merge-not-replace semantics the auditor praised (`grid-state.ts:168-183`) are genuinely correct — but
that is the *response handling*, not the *feature*. `SmartInputService.cs` (Core) survives and still
implements `BuildPlan` with modes + holiday-awareness; **no web code calls it**, so the survival is inert.

### §5 — Holiday cells

HOL-02 gave WPF three visible behaviors per day column: grey `HolidayBg` fill, a
"Holiday — not a working day" tooltip, and a read-only box carrying a "Holiday" watermark
(`TimesheetTab.xaml:29-36,46-65,342-376`). The web grid has none. The server still rejects the write
(400, asserted `log-work.component.spec.ts:463-469`), so this is **not** a data-integrity gap — but the
user now discovers a holiday only by typing into it and being refused.

### Out-of-scope losses noticed while tracing (flag for whoever owns those rows)

These are not in A2's claim list, but they live in the same two files and die with them:

- **Entry user filter** ("Whole team (read-only)" / any other user) — `TimesheetTab.xaml:172-186`,
  `TimesheetViewModel.cs:110-131,176-189`. The whole `IsTeamView` / `IsReadOnly` / `CanEdit` axis. The web
  Log Work screen has no such control; `worklog.service.ts:239-242` exposes `allUsers` and **no component
  passes it**.
- **Month filter** (month + year combos, filters which tickets show) — `TimesheetTab.xaml:176-179`,
  `TimesheetViewModel.cs:115-124,290-293`. Confirmed absent by `move-month.ts:7-9`.
- **Collapse-all preference persisted across restarts** — `TimesheetViewModel.cs:242-261` writes settings key
  `entry.collapseAll`; web `collapseAll()` (`log-work.component.ts:854-862`) is in-memory only.
