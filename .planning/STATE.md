# STATE — TimesheetApp (resume doc)

**Task List CARD layout + grouped bands + auto-provision user — MERGED to `main` + PUSHED (2026-07-02).**
`main` = `bc4c02f` "Merge feature/tasklist-grouped-bands-2026-07-02 into main" = `[origin/main]` (in sync). Build clean, **536 tests green**.
Shipped together: **P15** (grouped section bands — adaptive Team/Project, collapsible), **P16** (per-backlog **card** layout — tags
full-width on top, no h-scroll; Type/PCT/PCA → direct TwoWay; + External-in-header / "Estimation" / progress-default-0 tweaks),
**P17** (auto-provision current user on startup — unmapped Windows account auto-creates + maps, no manual add). Summary:
`.planning/P15-P16-P17-SUMMARY.md`. Feature branch `feature/tasklist-grouped-bands-2026-07-02` still at `9a0fdf1` (pre-merge tip; deletable).
**⏳ Follow-up UAT (did NOT block merge, per user):** live-check P16 Type/PCT/PCA persist (TwoWay) + P17 auto-provision on an unmapped account; fix-forward if either misfires.

<!-- Detailed step-by-step records for P15/P16/P17 are below (marked "⏳ ACTIVE" historically) — all shipped in bc4c02f. -->
_Prior redesign (visual pass `6f2c9fe`) was merged earlier in `45b6285`; superseded by the P16 card layout above._

---

**⏳ ACTIVE — P19: Dark mode.** Branch `feature/dark-mode-2026-07-02` (from `main` @ `d16fa3a`). Mode A. (P18 Quick Import is on a SEPARATE branch `feature/daily-quick-import-2026-07-02`, awaiting its own UAT/merge.)
User chose the **robust/high-impact** approach (verify thoroughly) — see memory `startup-phase-prefers-robust-over-minimal`.
- **Approach (locked):** split palette → `Palette.Light.xaml`/`Palette.Dark.xaml`; `Theme.xaml` styles + all views reference palette keys via **`DynamicResource`**; `ThemeService` swaps the palette dict at runtime = **live, no-restart** toggle. Promote hardcoded hex literals → theme keys. Config `IsDarkMode` + Settings toggle. Palette = ~36 keys (Theme.xaml L13-35 + L509-525).
- **STEP 5/6 done:** spec `docs/superpowers/specs/2026-07-02-dark-mode-design.md`; plan `docs/superpowers/plans/2026-07-02-P19-dark-mode.md` (3 waves: W1 palette infra+config+service+toggle `[opus]`; W2 convert all view refs + promote literals `[sonnet]`; W3 dark-palette tuning + both-theme render matrix `[sonnet]`). Plan-check APPROVE. **W1/W2 are large.**
- **Status: waiting_for_user** — approve plan → execute W1 (inline). Verify-heavy per user: grep reconciliation (no over-conversion), hex-literal audit, both-theme STA render matrix, full UAT sweep.

**⏳ ACTIVE — Task List grouped section bands (Log Work).** Branch `feature/tasklist-grouped-bands-2026-07-02` (from `main` @ `8aa1aef`).
- **STEP 2 Brainstorm + STEP 3 Mode Gate: DONE (2026-07-02).** **Mode A** approved (0/5 Mode B signals — 1 domain, low risk, no formal QA gate).
- **Design approved & committed** `a77aa91`: `docs/superpowers/specs/2026-07-02-tasklist-grouped-section-bands-design.md`.
- **Decision:** deliver the grouped "section band" look via **view-layer DataGrid grouping** (Approach ① — `CollectionViewSource` + `DataGrid.GroupStyle` Expander), **NOT** the DataGrid→ItemsControl rewrite (rejected as high-risk to inline edits). Adaptive group key: multi-team→Team else Project; collapsible bands (default open); band shows `Name (count)`. Inline-edit machinery untouched by construction.
- **STEP 6 Plan: DONE (2026-07-02).** Plan `docs/superpowers/plans/2026-07-02-P15-tasklist-grouped-bands.md` — 2 tasks / 2 waves (W1 VM `GroupKey`/`GroupOrder`/flags; W2 XAML `CollectionViewSource`+`GroupStyle` Expander band + hide group-key column). Plan-check **APPROVE** (11/11). Both tasks `[sonnet]`.
- **STEP 7 Execute: DONE (2026-07-02, inline, Mode A).** W1 `357d92b` (VM `GroupKey`/`GroupOrder`/`GroupByProject`/`GroupByTeam` + 2 tests), W2 `ec7d439` (XAML `CollectionViewSource`+`GroupStyle` Expander band; PROJECT hidden single-team, TEAM column Collapsed). Build clean, **538 tests green** (was 536; +2), render test passes (grouped XAML render-safe).
- **Status: waiting_for_user → STEP 8 UAT.** `.planning/P15-UAT.md` — user runs `dotnet run --project src/TimesheetApp`, Task List grid: verify bands (Project single-team / Team multi-team), collapse, inline edits unchanged, Gantt unaffected. **Not merged/pushed** — awaits UAT pass.
- **NEXT (P15):** superseded by P16 for the layout — P15's grouping logic (GroupKey/bands) stays; UAT folded into P16.

**⏳ ACTIVE — P16: Task List per-backlog CARD layout (same branch).** User wants tags full-width **on top of each backlog, always visible** (no scroll-right) → definitive rewrite `DataGrid` → `ItemsControl` cards.
- **STEP 2 Brainstorm + STEP 3 Mode Gate: DONE (2026-07-02).** **Mode A** (0/5 B signals). Design approved & committed `e1c3244`: `docs/superpowers/specs/2026-07-02-tasklist-card-layout-design.md`. Card = compact header (CODE·PCT·Internal·Progress·caret) + full-width tag strip on top; expand → Type/PCA/Ext/Start/End + tasks. **Type/PCT/PCA → direct TwoWay** (kills the DataGrid CellTemplate write-back bug class); deadlines/start-end/progress/tags/expand keep existing paths. Keeps P15 section bands (reuse `GroupedRows` CVS + `GroupStyle` on the ItemsControl).
- **STEP 6 Plan: DONE.** `docs/superpowers/plans/2026-07-02-P16-tasklist-card-layout.md` — W1 `[opus]` XAML card rewrite + drop 3 combo handlers; W2 `[sonnet]` render-test refresh + full suite. Plan-check **APPROVE**.
- **STEP 7 Execute: DONE (2026-07-02, inline).** W1 `62648ac` (TaskListTab.xaml DataGrid→ItemsControl cards + drop OnRowType/Pct/PcaChanged), W2 `9ce94d3` (render-test note). Build clean (0 warn), **538 tests green**, render test covers the card layout. `GridTextCell` resource removed (orphaned by the rewrite).
- **Status: waiting_for_user → STEP 8 UAT.** `.planning/P16-UAT.md`. **⚠️ #1 UAT check = Type/PCT/PCA now commit via TwoWay** (not test-coverable — must confirm real DB write; revert to code-behind if it misfires). **Not merged/pushed** beyond the branch.
- **UAT tweaks (Fast Lane, 2026-07-02):** External deadline surfaced in the compact header (next to Internal); expanded "Est" label → "Estimation"; **no-progress now defaults to 0%** (ProgressText "—"→"0%", bar always shown). Build clean, 538 green.
- **NEXT:** on UAT pass → STEP 9 QA (light, `requesting-code-review`) → merge to `main` + push (push on user OK). Branch also carries P15 (grouping) + P16 (cards) + P17 (auto-provision user).

**⏳ P17 — Auto-provision current user (Fast Lane, 2026-07-02, same branch).** User: khi run app mà tài khoản Windows chưa map thì tự tạo user, khỏi add tay.
- **Change:** `MainViewModel.ResolveCurrentUserAsync` — removed the `active.Count==0` gate + the SelectUserDialog branch; now **always auto-creates** a user named after `Environment.UserName` + maps windows_username when the account is unmapped (whether or not other users exist). `InitializeActiveTeamAsync` already joins them to the active team. `selectUser` delegate + `App.ShowSelectUserDialog` retained as an unused fallback seam (startup no longer prompts). Decision: **always auto-create** (Option A — DBs are per-Windows-profile so no dup risk in practice).
- **Tests:** removed 2 obsolete cancel tests, rewrote the picker test → `NeedsSelection_withExistingUsers_autoCreatesNewUser`. Build clean, **536 green**.
- **UAT note:** hard to hit on your own machine (your account is already mapped from prior runs); a fresh Windows account / unmapped DB lands straight in a usable session with no picker.

---


**Last updated:** 2026-07-01 (PM #4) — **Task List inline-edit UX overhaul + operational-field relocation**, merged to `main` + pushed.
Branch `feature/sharepoint-export-2026-07-01` (P14 export + dropdown-persist fix + this UX pass) → `main`. Build clean, **536 tests green**.

### Task List UX pass (2026-07-01 PM #4) — user-driven, verified via live DB + screenshots
- **Inline-edit reliability (DataGrid CellTemplate class of bug — TwoWay writes don't reach the row VM):**
  parent Type/PCT/PCA combos already fixed (prior commit); this pass fixed **Progress** (click-away now commits via
  code-behind, not the unreliable LostFocus binding; input widened + not clipped) and confirmed the DatePicker/combo
  code-behind pattern is the house rule for grid-cell editors.
- **Expand no longer collapses on sub-row edit:** `LoadAsync` preserves expanded BacklogIds across the reload that
  every inline commit triggers.
- **Operational fields moved to the Task List (business rule: Backlog editor = default fields only):**
  - **Start/End date** — new inline START/END DatePicker columns (`TaskListRow` gained `EndDate`; `CommitStartEndAsync`);
    hidden in the Backlog editor on EDIT.
  - **Tags** — hidden in the Backlog editor on EDIT; edited in the Task List via a **modal `TagSelectDialog`**
    (an in-grid Popup — cell OR row-details — closes before a checkbox can be ticked; a Window + an in-cell "✎ Tags"
    Button is reliable). Chips are display-only in the grid; the dialog's checkboxes mutate the same `TagPickVm` the row
    VM is subscribed to (commits per toggle), and the grid refreshes on dialog close.
- **Visual (referenced Log Work's clean look, no risky DataGrid→ItemsControl rewrite):** subtle vertical gridlines
  (`VerticalGridLinesBrush`), flat rounded `FlatProgressBar`, cleaner ▸/▾ expand caret, text columns inset+centered to
  line up with the boxed editors.
- **Not done (noted):** full grouped-section rewrite like Log Work (would risk the just-fixed inline edits; offered as a
  separate pass if wanted).

### Prior (2026-07-01 PM #3) — BUGFIX: Task List parent-row inline combos persist (Type/PCT/PCA)
Plus P14 SharePoint Export CODE-COMPLETE (SP-01/02/03). Also added CLAUDE.md rule: `sonnet` tier → `claude-sonnet-5`.

### BUGFIX (2026-07-01 PM #3) — Task List parent-row Type/PCT/PCA dropdowns didn't save
- **Symptom (user):** changing the Type/PCT/PCA dropdown on a Task List **backlog row** didn't reach the DB.
- **Diagnosis (DB + file-log evidence):** the row VM commit path, repo SQL, and audit were all correct AND unit-tested;
  **sub-row (task) combos in the expand panel persisted fine** (`TaskAudit` grew), but **parent-row combos never fired their
  setter**. Root cause: a `ComboBox` in a **`DataGridTemplateColumn.CellTemplate`** does NOT push its `SelectedItem`/`SelectedValue`
  TwoWay write back to the row VM — the **same** reason the deadline `DatePicker`s in this grid are driven from code-behind
  (and the old expand `ToggleButton`'s `IsChecked` "never reached the row"). The 3 combos were left as TwoWay bindings → silent no-op.
  Prior P13 UAT "#1 cell format" only checked visuals (28px), never the persist round-trip; VM unit tests pass with a mock repo,
  so nothing caught it.
- **Fix (3 files, mirrors the DatePicker pattern):** `TaskListTab.xaml` — Type/PCT/PCA combos now bind **OneWay** (display) + carry
  `Tag="{Binding}"` + `SelectionChanged` handlers. `TaskListTab.xaml.cs` — `OnRowTypeChanged/OnRowPctChanged/OnRowPcaChanged`
  set the VM edit prop on a genuine user pick (guards: `picked == current` skips seeds/reloads; `!IsKeyboardFocusWithin` skips
  non-user changes) → the VM `OnXxxChanged` → Commit → persist + audit. `TaskListViewModel.cs` — comment only.
- **Verified:** PLUS-2004 Type Estimate→Implement, PCT→Chi, PCA→Hino all landed in `Backlogs` + `BacklogAudit` (live-DB check).
  Sub-rows still fine. 536 tests green.
- **Not yet checked (follow-up):** the parent-row **Progress** click-to-edit textbox uses a similar in-cell TwoWay LostFocus
  binding — TextBox-in-cell usually commits on LostFocus, but should be UAT-spot-checked. Reported bug was dropdowns only.
- **No automated test:** this is a WPF view-binding defect (CellTemplate write-back); the VM/repo layers were already correct and
  tested. Consistent with the DatePicker fix, no unit test added — relies on UAT.

### P14 — SharePoint Export (file-sync) — what was built (this session)

### P14 — SharePoint Export (file-sync) — what was built (this session)
- **Approach (locked at brainstorm):** file-based — write into a OneDrive-synced / mapped SharePoint folder (WebDAV UNC or mapped
  drive). NOT Graph/MSAL (user has no Azure AD app). Config = a folder/drive path, never a web URL. See `.planning/P14-SharePoint-Export-REQUIREMENTS.md`.
- **SP-01 (Verify destination):** new `ISharePointDestinationValidator` / `SharePointDestinationValidator` — `Classify` (pure: WebUrl /
  SharePointOrNetwork / PlainLocal) + authoritative write-probe. Levels: Error(red, web-URL/unwritable) / Warning(amber, plain-local) /
  Ok(green, UNC / `sharepoint`/`DavWWWRoot` / `OneDrive` segment / mapped Network drive). DI singleton (`App.xaml.cs`). Settings VM
  `VerifyExportRoot1Command` + `ExportRoot1VerifyStatus`/`Level`; `SettingsTab.xaml` "Verify" button + colored status line + hint.
  13 tests (`SharePointDestinationValidatorTests`).
- **SP-02 (Export delivers Logs+Daily+DB):** satisfied by existing `ExportHubService.ExportRootAsync` (tasklist+timesheet+daily+db per
  root). No new code; existing hub tests stay green.
- **SP-03 (Guard bad destination):** `ExportHubService` gained optional `ISharePointDestinationValidator` (null in older tests); `RunAsync`
  pre-verifies each root — `Level==Error` → `failed: {root} — {reason}` + skip, other root still runs (best-effort per-root preserved).
  1 test (web-URL root fails with reason, writable root still `ok:`).
- **Files:** `Services/ISharePointDestinationValidator.cs`, `Services/SharePointDestinationValidator.cs`, `Services/ExportHubService.cs`,
  `App.xaml.cs`, `ViewModels/SettingsViewModel.cs`, `Views/Tabs/SettingsTab.xaml`; tests `SharePointDestinationValidatorTests.cs`,
  `ExportHubServiceTests.cs`.

### Prior (2026-07-01 PM #1) — QA-hardening pass MERGED to `main` + PUSHED to origin (`--no-ff`, HEAD `927da96`).
Branch `feature/qa-fixes-2026-07-01` (9 commits, from `main` @ `af9f683`) landed: agent-team audit (5 dims × verify),
**522 tests green** (was 514; +8), build clean (0 warnings), app boots + DI resolves.
✅ **UAT #1/#2/#3 CONFIRMED by user (passed)** — Task List cell format, Settings tag creation, and TagPicker all verified OK on `main`. QA branch fully closed. Awaiting next task assignment.

### Follow-up feature (same branch, after the audit batches)
- **Task List Progress cell — click-to-edit:** display mode shows only the % bar; click → swaps in a 0-100
  number input (auto-focused); Enter / click-away commits (via the existing `EditProgressText` LostFocus
  binding → reload → bar reflects new %); Escape cancels. `TaskListRowVm.IsEditingProgress` + `ResetProgressEdit`,
  `BoolToVisibilityConverter` gained a `ConverterParameter=Invert`. (commit `7b59c4a`)

### This QA pass — what was fixed (branch `feature/qa-fixes-2026-07-01`, 6 commits)
- **Baseline fix:** the "514 green" was actually 513/1 — `TeamFilterLoadTests` flaked on a cross-collection
  `new Application()` race. Serialized the 3 STA-render test classes into one `[Collection("WpfSta")]`.
- **Batch A (validation/data-integrity):** holiday guard was missing on the BULK log paths
  (`ValidateSmartFillAsync`/`ValidateDayTotalsAsync`) — Smart Fill could write hours onto a holiday; added it.
  `SmartInputPanelVm` searched backlogs with NO team scope (cross-team leak) → threaded active-team id.
  Auto-save + Smart Fill never broadcast `DataKind.Logs` → Reports/Task List went stale; now they do.
- **Batch B (Settings):** tag icon/color quick-picks were bound to `DataContext.PickIconCommand` (wrong prefix →
  silent no-op); template Save with no tasks failed silently → surfaced `ErrorMessage`; blank tag icon left a
  phantom chip gap → converter now collapses empty strings.
- **Batch C (UI consistency):** hard-coded `Red`→`{StaticResource Danger}`; Users "Deactivate" teal→`DangerGhostButton`;
  5 dialog titles 16→17; drag-grip 14→15; DailyBoard name 14→15.
- **Batch D (drag-drop):** grip hidden on locked standup days; honest cross-group drag cursor (`AreInSameGroup`).
- **Batch E:** backup/restore `File.Copy` moved off the UI thread (`Task.Run`); `SelectUserDialog` restyled to the
  themed chrome (+ STA render-guard).
- **#4 answer (SharePoint auth):** **NOT implemented.** "Export/backup to SharePoint" is only `File.Copy`/
  `File.WriteAllTextAsync` into a local/mapped folder (`ExportRoot1/2Path`). No MSAL/Graph, no account window, no
  certificate window. Making it real API upload is an architecture change → needs its own plan, not a bugfix.
- **Deferred (noted, not done):** drop-target row highlight (WPF DragEnter/Leave flicker handling); concurrent
  `ReloadAsync` race guard (0.88, hard-to-hit, hot path); calendar-cell vs tag-picker button heights (different
  contexts); `DefaultTaskSyncService` order_index reset (0.78, low confidence).

### Prior state (P13 merge — still true)
**MERGED to `main` + pushed to origin** (HEAD `af9f683`). **P13 "Task List Operations & History" CODE-COMPLETE**
across 4 waves + quality refactor. Schema **v9**. Prior stacked features (M3 Task List, M5 Backup, M4 Multi-Team,
M6 Export, M7 Retention) landed on `main`. **P13 UAT was still open** at merge time.

## How to resume (READ FIRST)
Open a session in `E:\Learning\AAM 2nd\aamanagementtool`, say *"đọc .planning/STATE.md để tiếp tục"*.
- On **`main`** (P13 merged + **pushed** to origin; fast-forward, no conflicts). `dotnet test src/TimesheetApp.sln` → expect **514 green**.
- Run: `dotnet run --project src/TimesheetApp`. First launch migrated the real DB to **v9** (additive).
- **On main/origin:** `68e168d` (Wave 1 schema v9 + audit-driven refactor) + `1c2669e` (Wave 2-4 + UAT fixes + state). `feature/task-list-2026-06-27` still points at the same commit.
- **Planning docs:** `.planning/P13-REQUIREMENTS.md` (resolved decisions + field mapping), `P13-PLAN.md` (4-wave plan), `P13-QUALITY-AUDIT.md` + `P13-AUDIT-SLICES.md` (audit), `P13-DESIGN-NOTES.md`, `P13-PLANCHECK.md`.
- Config: model_profile `quality`; Mode **B** (team) for P13; autonomous, PAUSE at plan for schema.

## ✅ UAT CLOSED (2026-07-01) — all P13/QA acceptance confirmed by user
- **#1 Task List cell format** — unified editable controls to 28px (`CompactComboBox` 26→28 + new `CompactDatePicker`). **PASSED.**
- **#2 Settings "can't create tags"** — was a side-effect of #3 (created tags invisible in picker); no code bug in SaveTagAsync/TagEditorViewModel. **PASSED** (create → appears in Settings list + backlog TagPicker).
- **#3 TagPicker empty (FIXED)** — `TagPicker.xaml.cs` ctor captured `_cvs.View` while `_cvs.Source` was null; `RebuildView` now re-points the view. **PASSED** (tags show in backlog Create/Edit).
- **Wave 2/3/4 behavior** — editor gating, Task List inline edits + audit, deadline note popup, sub-row edits, holiday cells, Reports per-user list. **PASSED.**

## ⏳ NEXT: P14 UAT (user tests app), then ship
On branch `feature/sharepoint-export-2026-07-01` (from `main`). P14 code-complete + committed, 536 tests green, build clean, schema v9 (unchanged — P14 adds no schema). **Awaiting user UAT** in Settings → "Shared/SharePoint folder": (1) web URL → Verify red; (2) plain local folder → amber; (3) mapped SharePoint/`\\…` drive → green; (4) Export now with a bad (web-URL) root → `failed: … — …web URL…` while the local root still `ok:`. On UAT pass → STEP 9 QA (skip/light) → merge to `main` + push (push only on user OK).

## P13 — what was built (this session)
**Schema v9** (additive; `DatabaseInitializer.cs` SchemaVersion 8→9; `SchemaV9UpgradeTests`): Tasks `+type,+assignee_user_id`; new `TaskTags`/`TaskAudit` (in CreateTables); `BacklogAudit +note`. `SchemaV7/V8UpgradeTests` version-asserts bumped to 9 (V8 seed gained BacklogAudit).
**Wave 1** — repos: `TaskRepository` `UpdateExtendedAsync/UpdateStatusAsync/SetTaskTagsAsync/GetTagIdsAsync/GetAuditAsync` (TaskAudit); `BacklogRepository.UpdateAsync(+auditNote→BacklogAudit.note)` + `SetTagsAsync` tag-audit; `BacklogAuditEntry +Note` read-back. `TaskItem +Type,+AssigneeUserId`; `TaskAuditEntry`. Theme `HolidayBg`(#D5DAE1)+`CompactComboBox`.
**Quality refactor** (audit verdict 0 Critical): bug fixes B-5 (`TagRepository.DeleteAsync` cleans TaskTags), B-6 (note read-back), B-3 (`MainViewModel.SafeLoad` logs not swallows), B-4 (`SettingsTab` async-void try/catch), async hygiene; dedup `DateHelpers.MondayOf`+`FormatHelpers`; **N+1 fixes** via `GetActiveByBacklogsAsync` (VM task counts), `IHolidayRepository.IsHolidayAsync` (hot path), `GetAuditForBacklogsAsync` (archive). +`BatchQueryTests`.
**Wave 2** (Backlog editor, `RequestEditorViewModel`/`RequestsTab.xaml`): CREATE disables only Progress; EDIT hides operational (Progress/Internal/External/PCA) via `IsEditMode` DataTriggers; Progress layout fixed; Tags → new **`TagPicker`** control (multi-select dropdown, mirrors TeamFilter).
**Wave 3** (Task List inline, `TaskListViewModel`+`TaskListTab.xaml`+`DeadlineNoteDialog`): grid cells Type/PCT/PCA (combos), Internal/External (DatePicker→note popup), Progress (bar OneWay + edit box), Tags (TagPicker) inline-edit → persist+audit. Expand sub-rows edit PCT/TYPE/TAG/Status → TaskAudit. `EditOption` sentinel, `_suppressCommit` guard, `ICurrentUserService` ctor param. **`TaskListTabRenderTests`** STA render-guard.
**Wave 4** (`TimesheetTab`/`ReportsTab`): holiday cells `HolidayBg` (darker) + "Holiday" placeholder in `GridDayBox` template; Reports NOT-LOGGED banner → per-user list (`MissingBanner`) with MaxHeight+scroll, stat card → count (fixed the "2 Not Logged" dup).
**UAT fixes**: theme `TextBox` Padding 8,6→**8,4** (fixed descender-clipping in fixed-Height inputs app-wide); the #1/#2/#3 above.

## What this is
WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite+Dapper / ClosedXML / CommunityToolkit.Mvvm).
Brand in-app = **"Worklog"**. App: `src/TimesheetApp`; tests `src/TimesheetApp.Tests`.
GitHub: **Nhanddtse61874/aamanagementtool** (private). **Stack skill: load `dotnet` skill for .NET work** (was missed early-session; now standard).

## Commands
- Build: `dotnet build src/TimesheetApp.sln`  ·  Test: `dotnet test src/TimesheetApp.sln` (514)
- Run: `dotnet run --project src/TimesheetApp`. DB: `%USERPROFILE%\Documents\TimesheetApp\timesheet.db` (v9).
- **Build/run gotcha:** kill the running app (`Get-Process TimesheetApp | Stop-Process -Force`) before building — it locks the exe. Agents that edit code must NOT build (orchestrator builds once after).

## Schema — user_version **9**
v9 (P13): Tasks `+type,+assignee_user_id`; `TaskTags(task_id,tag_id)`+`TaskAudit(...)` in CreateTables; BacklogAudit `+note`. v8=Multi-Team; v7=Task List tracking; v6=Request→Backlog rename. Migrations are additive, gated on user_version, with a pre-vN .bak backup.

## Decisions locked (don't re-litigate)
- P13 field split: CREATE grays only Progress; EDIT = non-operational fields; operational (Progress/Internal/External/PCA) managed inline in Task List with history; Tags editable both places. Task-level history = YES (TaskAudit). Reports "note logged" = the NOT-LOGGED warning (display-only).
- Mode B team-orchestration via the Workflow tool: design/audit/plan + implementation done by parallel agent waves, **but the main agent build+tests+verifies every wave** (agents don't build — caught real compile/mock breakages each time).
- DESIGN SOURCE OF TRUTH = `E:\Learning\AAM\Design Old\Designfromclaude\Timesheet Tool.dc.html` (teal #0F766E, 'Segoe UI' 13px). Read before UI changes.
- Brand "Worklog" display-only. Projects = fixed enum ARCS/PlusArcs/ARMS/Other.

## Working style (this user)
- Iterative UAT: focused change → run the app → user tests → next. Mirror the user's language (VN↔EN).
- When a feature "doesn't work", get DB/runtime evidence first (several were UX traps / null-view bugs, not logic).
- Commit + push each accepted change (push only when the user OKs). Surgical; don't "improve" working code unasked.
- WPF render-crash class (recurring): TwoWay-by-default DPs (Run.Text, RangeBase.Value, ToggleButton.IsChecked) on read-only props throw at render; Button-style on ToggleButton throws. STA render harness (`TaskListTabRenderTests`/`SettingsMembershipOverlayLoadTests`) catches these in CI.
