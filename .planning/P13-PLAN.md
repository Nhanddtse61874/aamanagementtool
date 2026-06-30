# P13 — Task List Operations & History — PLAN (wave-grouped)

**Mode B.** Schema **v8→v9** (additive). Plan-check: **APPROVE-WITH-FIXES** (all fixes folded in below).
Full design detail per task: **`.planning/P13-DESIGN-NOTES.md`**. Check detail: `.planning/P13-PLANCHECK.md`.

**Verified facts (corrected from brief):** `SchemaVersion = 8` (DatabaseInitializer.cs:14), migrations[] indices 0-7, new = index 8 (v8→v9). Tables are created idempotently in **CreateTables (37-191)**; migration steps only `ALTER`/data-migrate. `ICurrentUserService` is a registered singleton (App.xaml.cs:156); `TaskListViewModel` is transient → adding a ctor param needs **no DI change**. `MissingBanner` (ReportsViewModel:122) backs D1 (zero VM change). `CompactComboBox` absent → added. Test precedents: `SchemaV7/V8UpgradeTests.cs`.

## must_haves (goal-backward)
**Observable Truths**
- OT-1 `PRAGMA user_version`=9; Tasks has `type`,`assignee_user_id`; `TaskTags`+`TaskAudit` exist; `BacklogAudit` has `note`; no existing row mutated.
- OT-2 Create form: only Progress disabled/grayed. OT-3 Edit form: Progress/Internal/External/PCA removed.
- OT-4 Task List grid: Type/PCT/PCA/Internal/External/Tags/Progress inline-editable → persisted + audited.
- OT-5 Internal/External edit → Note(reason) popup → stored in `BacklogAudit.note`.
- OT-6 Task sub-rows edit PCT/TYPE/TAG/Status (dropdowns) → `TaskAudit` per change.
- OT-7 Holiday Log Work cells darker-gray + "Holiday" placeholder. OT-8 Reports NOT-LOGGED wraps per-user + max-height scroll.

**Key Links:** schema(W1-T1) → repos(W1-T2/T3) → VM(W3-T1)/editor(W2) → grid XAML(W3-T3)/dialog(W3-T2). Theme keys(W1-T4) → W4.

---

## Wave 1 — Schema foundation  🚩 SCHEMA/DESTRUCTIVE GATE
*(additive only; app auto-creates a `timesheet.db.pre-v9-*.bak` before migrating — verify the backup hook runs)*

- **W1-T1** `[opus]` **Migration v9.** DatabaseInitializer.cs: bump `SchemaVersion` 8→9; add `CreateTables` entries `CREATE TABLE IF NOT EXISTS TaskTags(task_id,tag_id PK)` + `TaskAudit(...)` **(FIX-I2: in CreateTables, not migration)**; migration index 8 = **3× ALTER** (`Tasks.type`, `Tasks.assignee_user_id`, `BacklogAudit.note`). **NEW** `Tests/Data/SchemaV9UpgradeTests.cs` mirroring V8 **(FIX-C13)**.
  · files: `Data/DatabaseInitializer.cs`, `Tests/Data/SchemaV9UpgradeTests.cs` · verify: `dotnet test … ~SchemaV9`
- **W1-T2** `[opus]` **Tasks model + repo.** Entities.cs: `TaskItem` +`Type`,`AssigneeUserId` (trailing defaults) + new `TaskAuditEntry`. TaskRepository.cs: add the 2 cols to all 4 SELECTs + `MapTask`; new `UpdateExtendedAsync`/`UpdateStatusAsync`/`SetTaskTagsAsync`/`GetTagIdsAsync`/`GetAuditAsync` (write `TaskAudit`). ITaskRepository.cs. Tests `Tests/Data/TaskListRepositoryTests.cs` (audit-row asserts) **(FIX-I14)**.
  · verify: `dotnet test … ~TaskList`
- **W1-T3** `[sonnet]` **Backlog repo audit.** BacklogRepository.cs: `UpdateAsync` +`auditNote` param → `BacklogAudit.note` on deadline rows; `SetTagsAsync` writes a `tags` audit row. IBacklogRepository.cs. Tests `Tests/Data/RepositoryCrudTests.cs`. Drop speculative read-side note **(FIX-I10)**.
  · verify: `dotnet test … ~RepositoryCrud` **(FIX-C5: not ~Backlog)**
- **W1-T4** `[sonnet]` **Theme keys.** Theme.xaml: `+HolidayBg` brush (#D0D5DB-ish, distinct from HeaderBg) + `CompactComboBox` style (for grid/sub-row inline combos).
  · verify: `dotnet build src/TimesheetApp.sln`

## Wave 2 — Backlog editor (Group A)
- **W2-T1** `[opus]` RequestEditorViewModel.cs + RequestsTab.xaml: Create disables ONLY Progress; Edit removes Progress/Internal/External/PCA; fix Progress field layout. · verify: `dotnet test … ~Editor` + build
- **W2-T2** `[opus]` **Tags dropdown.** New `Views/Controls/TagPicker.*` (multi-select, mirrors TeamFilter "Tags (N) ▾" + type-to-filter); wire into editor Create+Edit (binds `TagPicks`/`CheckedTagIds`). · verify: build

## Wave 3 — Task List inline + sub-rows (B1/B2/B3) — split by file ownership
- **W3-T1** `[opus]` TaskListViewModel.cs: inline-edit commands (Type/PCT/PCA/Internal/External/Tags/Progress → `UpdateAsync`+audit, deadlines pass note); task sub-row commands → TaskRepository; +`ICurrentUserService` ctor param. Progress stays OneWay on ProgressBar; edits via separate field.
- **W3-T2** `[sonnet]` New `Views/Controls/DeadlineNoteDialog.*` (modal reason popup, app overlay convention).
- **W3-T3** `[opus]` TaskListTab.xaml: editable cells (per-column editing template; non-edited stay read-only) + sub-row editing UI (PCT/TYPE/TAG/Status) + wire dialog.
  · verify: build + `dotnet test … ~TaskList`

## Wave 4 — Holiday + Reports (C/D)
- **W4-T1** `[sonnet]` TimesheetTab.xaml: `HolidayCellBorder` → `HolidayBg` + "Holiday" placeholder overlay (visible when cell Tag=True). · verify: build
- **W4-T2** `[sonnet]` ReportsTab.xaml: NOT-LOGGED card → bind `MissingBanner` per-user list inside `ScrollViewer MaxHeight=…` + `TextWrapping=Wrap` (zero VM change). · verify: build

---
## REQ coverage (100%)
A1→W2-T1 · A2→W2-T1 · A3→W2-T1 · A4→W2-T2 · B1→W3-T1/T3 · B2→W3-T1/T2/T3 · B3→W1-T2+W3-T1/T3 · C1→W4-T1 · D1→W4-T2

## Risks / gates
1. **Wave 1 = schema gate** (user pre-approved). Confirm pre-migration DB backup runs.
2. Inferred **Progress→Task List inline** — user Approved.
3. `SetTagsAsync` audit lives in the repo (not a separate VM call) to avoid double-audit.
4. ProgressBar stays OneWay (WPF TwoWay-on-readonly render-crash class) — numeric edit via separate control.
