# STATE — TimesheetApp (resume doc)

**Last updated:** 2026-07-01 (PM #2) — **P14 SharePoint Export CODE-COMPLETE** on branch `feature/sharepoint-export-2026-07-01`
(from `main`). SP-01/02/03 done; build clean (0 warnings); **536 tests green** (was 522; +13 SP-01 validator + 1 SP-03 guard).
**Awaiting user UAT** (Verify button colors + Export-now guard) before ship. Also added CLAUDE.md rule: `sonnet` tier → `claude-sonnet-5`.

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
