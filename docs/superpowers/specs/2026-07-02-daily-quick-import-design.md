# Daily Report — Quick Import (P18)

**Date:** 2026-07-02 · **Status:** Approved (design) — pending plan · **Mode:** A
**Topic:** In Daily Input, a "Quick Import" action clones a chosen past day's full standup data (the current user's entries + their issues) into the current day so the user can edit quickly instead of re-adding many items by hand.

## Context

Adding standup entries is one-at-a-time via `StandupEntryDialog` (`DailyInputTab` "+ Add entry"). When a day has many items that mostly repeat a previous day, this is slow. Quick Import copies a past day wholesale, then the user edits.

Data (per the code map): `StandupEntry` (`src/TimesheetApp/Models/StandupModels.cs:9`) + `StandupIssue` (`:16`). A "day" = `WorkDate` (DateOnly). Entries are scoped per **user** (`UserId`) and per **team** (`TeamId`, v8). Each entry has a `Section` ("yesterday"/"today"). Edit-lock (DR-06): only today + yesterday are editable (`StandupService.CanEditDay`).

## Locked decisions (from brainstorm, 2026-07-02)

| # | Decision | Value |
|---|----------|-------|
| D1 | What is copied | The **current user's** entries for the source day (**both** sections), **preserving `Section`**, plus **all their issues** (content + status **as-is**). |
| D2 | Duplicate handling | **Append all** — copy everything even if an entry with the same `backlog_code`/section already exists in the target day. User edits/deletes extras afterward. |
| D3 | Regenerate vs copy | Regenerate `Id=0`, `CreatedAt=clock.UtcNow`, `WorkDate=targetDate`, `OrderIndex` (recalc per section in target); issues regenerate `Id=0`, `EntryId=<new>`, `CreatedAt`. Copy everything else verbatim (BacklogId/Code, TaskText, Description, Deadline, Status, TeamId, IssueText, SolutionText, issue Status/OrderIndex). |
| D4 | Source picker | A **DatePicker** (any past day). |
| D5 | Target | The **currently-selected day** (`DailyReportViewModel.SelectedDate`), which must be editable — the button only shows when `CanEditSelectedDay`. |
| D6 | Scope | Current user + active team only (mirrors `GetMyStandupAsync`). |

## Approach

**Service** — add to `IStandupService` / `StandupService`:
```
Task<int> QuickImportDayAsync(DateOnly sourceDate, DateOnly targetDate);  // returns # entries cloned
```
- Guard: if `!CanEditDay(targetDate)` → return 0 (no-op). (Source day can be locked — we only read it.)
- Read the current user's source-day entries via `_repo.GetEntriesAsync(userId, sourceDate, activeTeamId)` + their issues via `GetIssuesForEntriesAsync(entryIds)` (uses the same user-id / active-team resolution as `GetMyStandupAsync`).
- For each source entry (grouped by section, in source order): compute the next `OrderIndex` per section in the target (start from `NextOrderAsync(userId, targetDate, section)` and increment), insert the cloned entry (`InsertEntryAsync`), then insert each cloned issue (`InsertIssueAsync`) with the new `EntryId`.
- Return the count. No broadcast here (the VM broadcasts after, consistent with `AddEntryAsync`).

**ViewModel** — `DailyReportViewModel`:
```
[RelayCommand] Task QuickImportAsync(DateOnly sourceDate)  // calls _service.QuickImportDayAsync(sourceDate, SelectedDate), then ReloadAndBroadcastAsync(); StatusMessage on 0.
```

**View** — a `QuickImportDialog` (DatePicker; mirrors `StandupEntryDialog` themed chrome) opened from a new **"⇩ Quick import"** `Button` next to "+ Add entry" in `DailyInputTab.xaml` (visible only when `CanEditSelectedDay`). Code-behind opens the dialog, and on OK calls `vm.QuickImportAsync(dialog.SelectedDate)`.

## Observable truths (must_haves)

1. A "Quick import" button appears in Daily Input when the selected day is editable.
2. It opens a dialog to pick a source day (DatePicker).
3. On confirm, the current user's entries for that day (both sections) + their issues are **appended** to the current day, preserving section, statuses as-is.
4. Cloned rows are new (regenerated ids/timestamps/order), editable (target is today/yesterday); the source day is unchanged.
5. Import into a locked day is a no-op with a status message; the board + input refresh after import.
6. Scope = current user + active team only.

## Risks & testing
- **Unit tests** (`StandupServiceTests` / real-DB harness): clone count, fields regenerated (new ids, WorkDate=target, CreatedAt bumped), issues copied with status, order recalculated per section, source unchanged, locked target = 0, empty source = 0, team/user scoping.
- No schema change. No render-crash risk (dialog mirrors existing themed dialog; DatePicker only).
- Edit-lock respected via `CanEditDay`.

## Out of scope (YAGNI)
- Importing other users' / other teams' entries. De-duplication. Section remap ("yesterday"→"today"). Selecting a subset of items (copy-all only). A "recent days with data" list (plain DatePicker).
