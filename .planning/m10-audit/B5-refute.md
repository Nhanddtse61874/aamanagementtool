# B5 — Adversarial refutation pass

Scope: the single COVERED claim in `B5.md:17` — *"Progress %: manual entry 0-100, **Backlog editor: disabled**, **Task List: inline editable**, `null → 0%`"*.

## Verdict table

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| Progress %: manual 0-100 · Backlog editor **disabled** · Task List inline editable · `null → 0%` | COVERED | **YES (in part)** | **PARTIAL** | Refuting side: `src/TimesheetApp/Views/Tabs/RequestsTab.xaml:306` `IsEnabled="False"` ("disabled on the create form so it can't be filled") **vs** `src/timesheet-web/src/app/pages/backlog/backlog-editor.component.html:108-110` — a plain **enabled** `<input id="bed-progress">` with `(ngModelChange)="patch('progressText', $event)"`; value reaches the create body at `backlog-form.ts:124` `progressPercent: parseProgress(form.progressText).value`, pinned live by `backlog-form.spec.ts:130` `expect(req.progressPercent).toBe(99)`. Surviving side: inline edit + `null→0%` verified end to end (below). |

## Sub-clause breakdown

| Sub-clause | Final | Evidence (WPF) | Evidence (web) |
|---|---|---|---|
| Manual entry, whole number 0-100, invalid never commits | COVERED [VERIFIED] | `TaskListViewModel.cs:584-601` — empty→`null`, `int.TryParse` + `>=0 and <=100`→value, else `return; // invalid input … do not commit` | `task-list.model.ts:169-177` `parseProgress` (strict `^\d{1,3}$` then range) + `task-list.component.ts:405-416` `commitProgress` → `if (!parsed.ok) { toast; return; }` — **no write**. Web is a strict superset (WPF fails silently; web toasts). |
| Commit trigger = Enter / blur; Escape reverts without persisting | COVERED [VERIFIED] | `TaskListTab.xaml.cs:184-208` (Enter/LostFocus push `EditProgressText`; Escape → `ResetProgressEdit`), `TaskListViewModel.cs:605-610` `_suppressProgressCommit` | `task-list.component.html:166-168` `(keydown.enter)` / `(keydown.escape)` / `(blur)`; `task-list.component.ts:391-394` `cancelProgress` + the `editingProgress() !== id` blur-disarm guard at `:407` |
| Task List **inline editable** (click bar → number box) | COVERED [VERIFIED] | `TaskListTab.xaml:329-351` + `TaskListTab.xaml.cs:170-175` `OnProgressDisplayClick` → `IsEditingProgress = true` | `task-list.component.html:160-175` — `@if (editingProgress() === row.backlogId)` swaps the bar button for the input; `startProgress` at `task-list.component.ts:371-375` |
| Persist path (whole-record safety) | COVERED [VERIFIED] | `TaskListViewModel.cs:351-365` `CommitBacklogEditAsync` — load `GetByIdAsync`, `backlog with { ProgressPercent = row.EditProgress }`, `UpdateAsync` | `task-list.component.ts:419-426` — `getBacklog(id)` then `updateBacklog(id, toUpdateRequest(dto, { progressPercent }))`; same load-then-patch shape, plus an `expectedVersion` WPF has no analogue for |
| `null → 0%` display (P16: "0%", not em dash) | COVERED [VERIFIED] | `TaskListViewModel.cs:536-537` `Row.ProgressPercent is { } p ? $"{p}%" : "0%"`; bar `TargetNullValue=0` at `TaskListTab.xaml:339` | `task-list.model.ts:354-357` `` `${percent ?? 0}%` `` → `task-list.component.ts:335` `percent()` → `task-list.component.html:172`; bar fill `[style.width.%]="row.progressPercent ?? 0"` at `:171` |
| Progress field **hidden on EDIT** | COVERED [VERIFIED] | `RequestsTab.xaml:297-299` — `DataTrigger IsEditMode=True → Visibility=Collapsed` | `backlog-editor.component.html:91` `@if (!isEdit())` wraps the whole six-field block (`:86-90` comment: "CREATE ONLY, all six") |
| Progress field **disabled on CREATE** | **MISSING (inverted)** | `RequestsTab.xaml:304-307` — comment *"Progress is read-only data (computed elsewhere): disabled on the create form so it can't be filled"*, `IsEnabled="False"`; the VM parse (`RequestEditorViewModel.cs:124-131`) is therefore unreachable from the UI | `backlog-editor.component.html:108-110` — **enabled** input; `backlog-form.ts:88-96` `validate()` treats it as a first-class field that can **block the save**. The divergence is deliberate, not accidental: `backlog-form.ts:44-47` names `RequestsViewModelTests.SaveNew_does_not_persist_out_of_range_progress` and says *"We are fixing that bug"* |

## Why the COVERED verdict does not survive

The auditor self-flagged this half [ASSUMED] and it does not hold. It is not "uncovered by omission" — it is **inverted**. The WPF rule is *progress cannot be authored on the create form at all*; the web app makes it an authored, validated, save-gating field. `B5.md:17` states the rule as a conjunction and marks the conjunction COVERED, which is false for one conjunct.

This is a product decision the web side took on purpose (`backlog-form.ts:44-47`), so it may well be the desired end state — but the M10 audit must not record it as "already covered". After `src/TimesheetApp/` is deleted, `RequestsTab.xaml:306` is the last artifact stating the original rule; nothing in the web tree preserves it.

## Secondary divergence found while tracing (not in the original claim)

The two web `parseProgress` functions are **not the same function** and do not agree:

- `task-list.model.ts:169-177` (inline edit) — strict `^\d{1,3}$`, whole numbers only. Matches `TaskListViewModel.cs:591` exactly.
- `backlog-form.ts:73-75` → `parseNumber(text, 'Progress', 100)` at `:55-67` — `Number()` + `isFinite` + range only, **no integer check**. `50.5` parses clean and is sent as `progressPercent: 50.5`.

WPF's create-form parser required a whole number (`RequestEditorViewModel.cs:127` `int.TryParse`). The server contract is `int? ProgressPercent` (`Api/Contracts/Dtos.cs:72`), so a decimal fails JSON binding with a 400 rather than corrupting data — the failure mode is an unhelpful error, not silent loss. Low severity, but it is a real "narrower/looser web version" of a WPF validation and belongs on the backlog-editor section's list, not this one.

## Bottom line

`refuted = true`, `finalVerdict = PARTIAL`. The inline-edit and `null → 0%` halves are genuinely covered and are, if anything, better on the web (explicit toast, optimistic-concurrency version). The Backlog-editor-disabled half is not covered and is contradicted by working, test-pinned web code.
