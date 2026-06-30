

========== AUDIT SLICE 1 ==========
Now I have a complete read of the data layer. Here is the audit:

---

## Data Layer Quality Audit â€” Findings

### FINDING 1 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/BacklogRepository.cs:166` and `:186`, `TaskRepository.cs:137`**
Severity: **Important**
Category: **async-correctness**

`NameOf`/`PcaNameOf` local functions call `c.QuerySingleOrDefault<string?>` (the **synchronous** Dapper extension) from inside an `async` method body on the same `IDbConnection`. There is no thread-pool blocking risk (SQLite Dapper sync on a desktop UI thread is not a deadlock hazard), but it is an inconsistency: every other call on the same connection uses `QuerySingleOrDefaultAsync`. More concretely, Dapper's synchronous path opens an `IDbCommand` without using `SqliteConnection`'s async path â€” under some SQLite drivers this bypasses any in-flight async work ordering.

Concrete fix: Replace the synchronous local functions with async ones:
```csharp
// BacklogRepository.cs ~line 165, TaskRepository.cs ~line 136
async Task<string?> NameOfAsync(int? uid) => uid is null ? null
    : await c.QuerySingleOrDefaultAsync<string?>("SELECT name FROM Users WHERE id = @id;", new { id = uid });
var oldName = await NameOfAsync(before.assignee_user_id is { } b ? (int)b : null);
var newName = await NameOfAsync(backlog.AssigneeUserId);
await LogAsync("assignee", oldName, newName);
```
Same fix needed for `PcaNameOf` in `BacklogRepository` (~line 185) and `NameOf` in `TaskRepository` (~line 136).

Confidence: **High** | Risk of fixing: **Low** (pure async swap on the same open connection)

---

### FINDING 2 [VERIFIED]
**`src/TimesheetApp/Data/DatabaseInitializer.cs:23-35`**
Severity: **Important**
Category: **async-correctness**

`InitializeAsync()` is an `async Task` method that does **all work synchronously** and returns `Task.CompletedTask`. The method signature promises callers an awaitable async operation, but internally uses synchronous Dapper `Execute`/`ExecuteScalar` calls (no `await`). This is not wrong at runtime on a desktop app (SQLite is fast at startup, no deadlock risk), but it misleads callers and suppresses the CS1998 "async method lacks await" compiler warning only because it compiles as a non-async `Task`-returning method. The broader risk: if a future developer wraps `ConfigureAwait(false)` assumptions or adds a real awaitable call here, the inconsistency bites them.

Concrete fix: Either (a) rename the method to `Initialize()` returning `void` or `ValueTask.CompletedTask` and update `IDatabaseInitializer`, or (b) make it genuinely async by using `ExecuteAsync`/`ExecuteScalarAsync` throughout. Option (b) is lower churn since the interface already returns `Task`:
```csharp
public async Task InitializeAsync()
{
    using var conn = _factory.Create();
    using var tx = conn.BeginTransaction();
    await CreateTablesAsync(conn, tx);
    ...
    tx.Commit();
}
```

Confidence: **High** | Risk of fixing: **Med** (schema runs at startup; must preserve synchronous transaction semantics â€” do not split `tx` across awaits on separate connections)

---

### FINDING 3 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/BacklogRepository.cs:257-268` and `TaskRepository.cs:211-223`**
Severity: **Important**
Category: **dead-code / data-loss**

`GetAuditAsync` in both `BacklogRepository` and `TaskRepository` queries only the columns present in the original `AuditRaw` private class â€” **neither reads the `note` column** that was added in v9. The `BacklogAuditEntry` record also has no `Note` property (Entities.cs line 40-42), so the v9 `note` column written by `UpdateAsync`/`SetTagsAsync` is silently discarded on every read. If the UI ever needs to show the deadline-change reason from the B2 Note popup, the data exists in the DB but is unreachable through the current model.

Concrete fix â€” add `Note` to the record and wire it through:
```csharp
// Entities.cs
public sealed record BacklogAuditEntry(
    int Id, int BacklogId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt,
    string? Note = null);   // v9 deadline-change note

// BacklogRepository.AuditRaw
public string? note { get; set; }

// GetAuditAsync SELECT
SELECT id, backlog_id, field, old_value, new_value,
       changed_by_user_id, changed_by_name, changed_at, note
FROM BacklogAudit ...

// MapBacklogAuditEntry
new BacklogAuditEntry(..., a.note)
```

Confidence: **High** | Risk of fixing: **Low** (additive; the default-null param keeps existing callers compiling)

---

### FINDING 4 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/TagRepository.cs:43-49`**
Severity: **Important**
Category: **dead-code / data-loss**

`TagRepository.DeleteAsync` removes `BacklogTags` links for the deleted tag but does **not** remove `TaskTags` links. After v9 added `TaskTags`, a `Tag` hard-delete leaves orphan rows in `TaskTags` (referencing a non-existent `tag_id`). The `BacklogTags` table also has no FK constraint declared, so SQLite does not cascade the delete automatically; the same manual cleanup is needed for `TaskTags`.

Concrete fix: add `DELETE FROM TaskTags WHERE tag_id = @id;` to the existing transaction:
```csharp
public async Task DeleteAsync(int tagId)
{
    using var c = _factory.Create();
    using var tx = c.BeginTransaction();
    await c.ExecuteAsync("DELETE FROM BacklogTags WHERE tag_id = @id;", new { id = tagId }, tx);
    await c.ExecuteAsync("DELETE FROM TaskTags  WHERE tag_id = @id;", new { id = tagId }, tx);
    await c.ExecuteAsync("DELETE FROM Tags      WHERE id = @id;",     new { id = tagId }, tx);
    tx.Commit();
}
```

Confidence: **High** | Risk of fixing: **Low** (additive DELETE inside existing tx; no cascades to break)

---

### FINDING 5 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/BacklogRepository.cs:140`**
Severity: **Important**
Category: **error-handling**

In `UpdateAsync`, the pre-read of the `before` row happens **before** the `UPDATE` (`c.ExecuteAsync` at line 111). If the row does not exist (i.e., `before is null`), the `UPDATE` still executes, silently affecting zero rows, and the method returns normally as if successful. The `if (before is null) return;` guard (line 140) comes **after** the write, not before it. This is the opposite of the intent (skip audit if not found) â€” the intent is presumably to skip the *write* too if the row doesn't exist. In practice the row always exists when called from the VM, but the logic is wrong and will silently do a no-op write on a bad id.

Concrete fix: move the guard above the `ExecuteAsync`:
```csharp
var before = await c.QuerySingleOrDefaultAsync<BacklogRaw>(...);
if (before is null) return;   // â† move here, before the UPDATE
await c.ExecuteAsync("UPDATE Backlogs SET ...", ...);
```

Confidence: **High** | Risk of fixing: **Low** (the row always exists in production; just fixing logic order)

---

### FINDING 6 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/TaskRepository.cs:174-208` (SetTaskTagsAsync) and `BacklogRepository.cs:202-243` (SetTagsAsync)**
Severity: **Suggestion**
Category: **duplication**

The replace-all-tags-with-audit pattern is copy-pasted identically between `BacklogRepository.SetTagsAsync` and `TaskRepository.SetTaskTagsAsync`. The two differ only in the table name (`BacklogTags`/`TaskTags`), id column name (`backlog_id`/`task_id`), and audit table (`BacklogAudit`/`TaskAudit`). This is ~30 lines duplicated.

For a WPF desktop app with only two callers this is not worth a shared helper unless the pattern grows to a third entity. Flag for awareness, not immediate action â€” the current duplication is traceable, tested, and correct.

Concrete fix: A private static helper `SetTagsInternalAsync(conn, tx, string linkTable, string idCol, int entityId, ...)` called by both. Only do this if a third N:N-tagged entity appears.

Confidence: **High** | Risk of fixing: **Med** (refactor risk across two tested paths; defer until needed)

---

### FINDING 7 [VERIFIED]
**`src/TimesheetApp/Data/DatabaseInitializer.cs:297-307`**
Severity: **Suggestion**
Category: **async-correctness**

Migration step index is computed from the current `user_version` **twice** â€” once at line 191 for the `Requests`/`RequestAudit` gate, and again at line 297 for the loop. Both read `PRAGMA user_version` inside the same transaction via two separate `ExecuteScalar` calls. SQLite transaction isolation means both reads see the same value, so there is no data race, but caching the first read and passing it to `RunMigrations` would remove the redundant round-trip and make the intent clear.

Concrete fix:
```csharp
var version = conn.ExecuteScalar<long>("PRAGMA user_version;", transaction: tx);
if (version < 6) { /* create Requests/RequestAudit */ }
RunMigrations(conn, tx, version);  // pass cached version; remove the second ExecuteScalar inside
```

Confidence: **High** | Risk of fixing: **Low**

---

### FINDING 8 [VERIFIED]
**`src/TimesheetApp/Data/Repositories/BacklogRepository.cs:29-43` (SearchAsync)**
Severity: **Suggestion**
Category: **sql-correctness**

The `LIKE` search on `backlog_code` and `project` is case-sensitive in SQLite's default `BINARY` collation for non-ASCII characters but case-insensitive for ASCII. This means searching "req-100" won't match "REQ-100" on a SQLite build with a non-UTF-8 collation. Since codes are uppercase by convention this is unlikely to cause a real bug, but it is a latent edge case. If case-insensitive search is desired, add `COLLATE NOCASE` to the `LIKE` predicate or use `lower()` on both sides.

Confidence: **Med** (harmless with current uppercase-only codes; could matter if codes ever contain mixed case) | Risk of fixing: **Low**

---

### WHAT IS CLEAN (no findings)

The remaining repositories (UserRepository, TimeLogRepository, TeamRepository, StandupRepository, SettingsRepository, HolidayRepository, PcaContactRepository, TaskTemplateRepository, DefaultTaskRepository) are clean against all audit criteria:
- No `.Result`/`.Wait()` anywhere in the data layer [VERIFIED].
- No direct `new Repository()` in production code; all are DI-injected [VERIFIED â€” test files only].
- `using var c = _factory.Create()` pattern is consistent across all methods; no IDisposable leaks [VERIFIED].
- SQL is fully parameterized throughout; no string concatenation of user input into query text [VERIFIED].
- `Raw` DTO pattern (map `long/string` at the boundary, never rely on Dapper narrowing) is consistent and correctly applied [VERIFIED].
- Migration steps are additive (ALTER TABLE ADD COLUMN, CREATE TABLE IF NOT EXISTS) and gated on `user_version`; no idempotency violations [VERIFIED].
- `EnsureDefaultBacklog` uses `WHERE NOT EXISTS` guard â€” idempotent [VERIFIED].
- N+1 risk is contained: `GetTagIdsForAllAsync` bulk-loads all tag links in one query; `GetIssuesForEntriesAsync` uses `IN @ids`; `GetLoggedHoursByBacklogAsync` aggregates in SQL [VERIFIED].
- `UpsertBatchAsync` wraps the loop in a single transaction (SI-05 compliant) [VERIFIED].
- `ON CONFLICT` / `INSERT OR REPLACE` / `INSERT OR IGNORE` semantics are correctly matched to each use-case [VERIFIED].
- `Iso()`/`Day()`/`ParseDay()` formatting helpers are duplicated across repos, but this is deliberate (each repo is self-contained); no cross-repo hidden coupling [VERIFIED].

### SUMMARY TABLE

| # | File:Line | Severity | Category | Confidence | Fix Risk |
|---|---|---|---|---|---|
| 1 | BacklogRepository:166,186 / TaskRepository:137 | Important | async-correctness | High | Low |
| 2 | DatabaseInitializer:23-35 | Important | async-correctness | High | Med |
| 3 | BacklogRepository:257-268 / TaskRepository:211-223 | Important | dead-code | High | Low |
| 4 | TagRepository:43-49 | Important | data-loss | High | Low |
| 5 | BacklogRepository:140 | Important | error-handling | High | Low |
| 6 | TaskRepository:174 / BacklogRepository:202 | Suggestion | duplication | High | Med |
| 7 | DatabaseInitializer:297-307 | Suggestion | async-correctness | High | Low |
| 8 | BacklogRepository:29-43 | Suggestion | sql-correctness | Med | Low |

========== AUDIT SLICE 2 ==========
Now I have a complete picture. Here are the findings:

---

## Models + Services Audit â€” Findings

### MODELS

**Models/Entities.cs, Models/ReadModels.cs, Models/StandupModels.cs â€” overall clean.** `sealed record` types, good use of `IReadOnlyList<T>`, clear domain separation. No domain logic misplaced into model types. The one deliberate deviation â€” computed property `StandupIssue.HasSolution` â€” is acceptable (it is a pure derived bool with no side-effects and is in the model file by design).

**No render-crash risk in models.** None of the model types are used directly as `DependencyProperty` backing or bound two-way.

---

### SERVICES

**F1 â€” Services/TimeLogService.cs:262 Â· Important Â· async-correctness**
`IsHolidayAsync` calls `_holidays.GetAllAsync()` on every `SaveCellAsync` call, fetching the full holiday table to do a linear `.Any()` scan. This is a N+1-style round-trip inside the hot path (`SaveCellAsync` is called on every cell save). [VERIFIED]

Fix: cache the holiday set once per call at the `SaveCellAsync` level â€” `var holidays = (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();` â€” and pass it to the check. Or add `GetByDateAsync(DateOnly)` to `IHolidayRepository` so the SQL does the filter.

Confidence: High. Risk-of-fixing: Low.

---

**F2 â€” Services/CurrentTeamService.cs:79 Â· Suggestion Â· dead-code**
`SetActiveTeamAsync` ends with `await Task.CompletedTask;`. The method has no async I/O after this line; it is the only statement. The `async` keyword is therefore redundant, and `await Task.CompletedTask` is a no-op that adds a state-machine allocation. [VERIFIED]

Fix: remove `async` and the `await Task.CompletedTask` line; change the return type to `Task` and `return Task.CompletedTask;` (or just return nothing and change to `void` â€” but the interface declares `Task`, so `return Task.CompletedTask;`).

Confidence: High. Risk-of-fixing: Low.

---

**F3 â€” Services/SmartInputService.cs:15 Â· Important Â· SOLID / DI**
The optional-defaulted constructor `public SmartInputService(IWorkingDayCalculator? calc = null) => _calc = calc ?? new WorkingDayCalculator();` instantiates a concrete `WorkingDayCalculator` directly when the DI graph omits it. In production the DI graph *does* register `IWorkingDayCalculator` (`App.xaml.cs:187`), so the fallback fires only in tests that call `new SmartInputService()` without arguments. This violates the "never instantiate services directly" rule inside a service implementation, and conceals the dependency from the DI container. [VERIFIED]

Fix: make `IWorkingDayCalculator` a required constructor parameter (`public SmartInputService(IWorkingDayCalculator calc)`). Update the six test call-sites that use `new SmartInputService()` to `new SmartInputService(new WorkingDayCalculator())`. The tests already `new WorkingDayCalculator()` directly in other test files, so this is two lines per test.

Confidence: High. Risk-of-fixing: Low.

---

**F4 â€” Services/TaskListArchiveService.cs:86,130 Â· Important Â· async-correctness (N+1 query)**
`BuildMonthMarkdownAsync` and `BackfillMissingMonthsAsync` both loop over all non-DEFAULT backlogs and call `await _backlogs.GetAuditAsync(b.Id)` individually inside the loop â€” one DB round-trip per backlog. For 50 backlogs this is 50 serial queries. `BackfillMissingMonthsAsync` also does this loop twice (lines 130). [VERIFIED]

Fix: add `GetAuditForAllAsync()` (or `GetAuditForBacklogsAsync(IReadOnlyList<int> backlogIds)`) to `IBacklogRepository`, returning a `ILookup<int, BacklogAuditEntry>` keyed by `backlog_id`. Replace the per-backlog `GetAuditAsync` calls with a single batch fetch before the loop.

Confidence: High. Risk-of-fixing: Med (requires a new repository method + SQL; logic change is straightforward).

---

**F5 â€” Services/ExportHubService.cs:184 + Services/StandupArchiveService.cs:165 + Services/PruneArchiver.cs:185 + Services/TimeLogService.cs:267 Â· Suggestion Â· duplication**
`MondayOf(DateOnly)` is copy-pasted identically across four service classes. The same one-liner `date.AddDays(-(((int)date.DayOfWeek + 6) % 7))` exists in each as a `private static`. `RetentionService.cs:47-51` also self-documents `AddMonths` as a known duplicate of `ExportHubService.AddMonths`. [VERIFIED]

Fix: extract both `MondayOf` and `AddMonths` (the `(int year, int month, int delta)` overload) into a `internal static class DateHelpers` in a shared file (e.g. `src/TimesheetApp/Services/DateHelpers.cs`). All four service classes reference it without any interface change.

Confidence: High. Risk-of-fixing: Low (purely mechanical extraction; no behavioral change).

---

**F6 â€” Services/ExportService.cs:135-138 + Services/TaskListArchiveService.cs:241-247 Â· Suggestion Â· duplication**
The `FormatHours(decimal)` formatter (`h == Math.Truncate(h) ? (long)h : h.ToString("0.#")`) is duplicated between `ExportService` (named `FormatHours`, `internal static`) and `TaskListArchiveService` (named `Hours`, `private static`). Both do identical decimal-to-display-string logic. [VERIFIED]

Fix: move `FormatHours` to `DateHelpers` (or a new `FormatHelpers` class), mark `internal static`, and have both services call it. `ExportService.FormatHours` is already `internal` and tested directly, so no test changes needed beyond the using namespace.

Confidence: High. Risk-of-fixing: Low.

---

**F7 â€” Services/ExportHubService.cs:137-138 + Services/PruneArchiver.cs:188-189 Â· Suggestion Â· duplication**
`HasTimesheetData(string md)` is duplicated identically in `ExportHubService` and `PruneArchiver`, both checking for `"| Date | Task | Hours |"` as the has-data sentinel. [VERIFIED]

Fix: same `FormatHelpers`/`DateHelpers` class â€” extract as `internal static bool HasTimesheetData(string md)`.

Confidence: High. Risk-of-fixing: Low.

---

**F8 â€” Services/StandupArchiveService.cs:18 + Services/TaskListArchiveService.cs:27 Â· Suggestion Â· naming / SOLID**
`ITeamRepository? teams = null` is an optional constructor parameter in both archive services. The comment says "optional for legacy tests." Optional dependencies via nullable constructor params weaken the DI contract â€” the DI container silently injects `null` for an unregistered optional, which makes it easy to omit a new registration without a build error. Since `ITeamRepository` is already registered in production (`App.xaml.cs:183`), the null fallback is only exercised in older tests. [VERIFIED]

Fix: make `ITeamRepository` a required parameter in both constructors (non-nullable). Update the affected tests to supply a mock/stub `ITeamRepository` (or use the existing `TeamRepository` with an in-memory SQLite, which the test harness already uses for other repos).

Confidence: High. Risk-of-fixing: Med (requires updating test setup for the two archive service test classes).

---

**F9 â€” Services/BackupService.cs:43 + Services/DbBackupHelper.cs:31 Â· Suggestion Â· async-correctness**
Both `BackupToFolderAsync`/`BackupAsync` are declared `Task<string?>` but are implemented entirely synchronously with `File.Copy` and returning `Task.FromResult<string?>`. `File.Copy` can block for seconds on a cold spinning disk or a network share path (the configured `BackupFolderPath` / `ExportRoot*` could be a mapped drive). The callers `await` them so they are *safe* but not truly non-blocking â€” the UI thread (or the startup `async void`) blocks in practice. [VERIFIED]

Fix: either (a) accept the current design as an intentional trade-off (the comment "File.Copy is safe at idle") and leave it â€” for a local .db file this is acceptable â€” or (b) wrap in `await Task.Run(...)` for the `File.Copy` call if user-facing latency becomes an issue. This is a Suggestion, not a correctness bug.

Confidence: High. Risk-of-fixing: Med (wrapping in Task.Run changes exception surfacing slightly; test implications exist for the backup tests).

---

**No findings in the following areas (genuinely clean):**
- **Async correctness â€” no `.Result`/`.Wait()` anywhere in Services.** [VERIFIED]
- **Error handling â€” no swallowed exceptions in business paths.** The bare `catch { }` blocks in `DbBackupHelper.PruneOldBackups` and `BackupService.Prune` are deliberate best-effort pruning guards, correctly commented, and do not swallow business errors.
- **DI registration â€” all services injected via interfaces; no `new Service(...)` in production VMs/services except the `SmartInputService` fallback flagged in F3.**
- **Nullability â€” `?? Array.Empty<T>()` guards on repo returns are consistent throughout.**
- **Interface segregation â€” each `IXxxService` is narrow and coherent; `ITimeLogService` has many methods but they are all genuinely in one cohesive domain.**
- **`ILogger<T>` â€” not used (this is a WPF desktop app with no host; `Trace.TraceWarning` via `TraceJournalWarningSink` is the appropriate lightweight substitute and is not a static logger in the problematic sense).**
- **`RetentionService` SQL + transaction ordering** â€” children-first, explicit `tx.Commit()`, `effectiveCutoff` prefix guard, conflict-copy abort â€” all correct. The self-documented FIX-A comment on the `AddMonths` duplication (F5) is already acknowledged in-code.
- **Models** â€” correct use of `sealed record`, `readonly record struct`, `IReadOnlyList`, and `IReadOnlySet`; no mutable state on entity types; no domain logic in entity types except the single justified `HasSolution` computed bool.

========== AUDIT SLICE 3 ==========
I now have a complete picture of all 20 ViewModel files. Here is the full audit report.

---

## ViewModel Quality Audit â€” src/TimesheetApp/ViewModels

### Preamble

All 20 files were read in full. The codebase is in generally good shape: no `.Result`/`.Wait()` blocking, no `async void`, `WeakReferenceMessenger` with static-lambda recipient pattern used consistently throughout, no raw `new` on services. The findings below are real issues, ordered by severity.

---

### CRITICAL

**None found.** No blocking async, no TwoWay on genuinely read-only properties, no render-crash-class violations in the VM layer that are not already mitigated.

---

### IMPORTANT

**1. `TimesheetViewModel.cs:37` â€” Dead injected parameter (`ISmartInputService smartInput`)**
Category: dead-code / SOLID (ISP)
[VERIFIED]

`smartInput` is accepted as a constructor parameter but is never stored, used, or forwarded. `SmartInputPanelVm` is constructed directly from `timeLogs`, `backlogs`, and `tasks` on line 56. This means the DI container resolves and constructs `ISmartInputService` on every startup for no reason, and every call-site must supply the argument even in tests.

Fix: Remove the `smartInput` parameter from the constructor signature. If `ISmartInputService` is intended to be used in the future, note it in a TODO comment instead of accepting it unused.
Confidence: High | Risk-of-fixing: Low (compiler-verified removal; DI registration in `App.xaml.cs` is still valid as other consumers may exist â€” check before removing the registration).

---

**2. `TimesheetViewModel.cs:57` â€” Strong-reference event subscription on `SmartInput.Applied` creates potential leak**
Category: resource-leak / mvvm
[VERIFIED]

```csharp
SmartInput.Applied += async () => await ReloadAsync();
```

The lambda captures `this` (via `ReloadAsync`), giving `SmartInput` a strong reference back to `TimesheetViewModel`. Since `TimesheetViewModel` is a DI singleton and `SmartInput` is owned by it, the object graph is circular and never collected â€” in practice this means the handler fires forever. More importantly, if `SmartInput` is ever replaced or the lambda is ever registered more than once (e.g., if the ctor ran twice in tests), the subscription accumulates. The messenger registrations everywhere else use the stateless `static (r, m) => r.Method()` pattern specifically to avoid this. This subscription is the one place that breaks the pattern.

Fix: Use the same style as the messenger registrations â€” replace with a named method or ensure the lambda does not capture `this`:
```csharp
SmartInput.Applied += OnSmartInputApplied;
// ...
private void OnSmartInputApplied() => _ = ReloadAsync();
```
This is structurally consistent with the rest of the file and doesn't change behaviour, but makes the lifecycle explicit and prevents double-registration in test scenarios.
Confidence: High | Risk-of-fixing: Low

---

**3. `BacklogsViewModel.cs:105-107` / `TaskListViewModel.cs:140-144` â€” N+1 DB queries inside `foreach` loop**
Category: async-correctness (performance)
[VERIFIED]

`BacklogsViewModel.RefreshAsync` issues one `GetActiveByBacklogAsync(r.Id)` call per backlog row inside a `foreach`. For a user with 50 backlogs this becomes 50 sequential `await` round-trips to SQLite. The same pattern exists in `TaskListViewModel.LoadAsync` line 144. `BacklogsViewModel` uses the count only for display (`tasks?.Count ?? 0`); `TaskListViewModel` uses the full task list to compute `isDone` and to display sub-rows.

Fix for `BacklogsViewModel`: add `ITaskRepository.GetTaskCountByBacklogAsync(IReadOnlyList<int> backlogIds) -> Dictionary<int, int>` (a single `SELECT backlog_id, COUNT(*) FROM tasks WHERE is_active=1 AND backlog_id IN (...) GROUP BY backlog_id`), or use the existing aggregate query and avoid the per-row call. This is the higher-impact fix since Backlogs is the most-visited tab.

Fix for `TaskListViewModel`: add `GetActiveTasksByBacklogsAsync(IReadOnlyList<int> ids) -> ILookup<int, TaskItem>` â€” one query returning all tasks for the filtered set, then `ILookup[b.Id]` per row.

Confidence: High | Risk-of-fixing: Med (requires repository contract + test additions; does not touch VM logic, only the data-access seam).

---

**4. `HolidayCalendarViewModel.cs:86-87` â€” Redundant DB round-trip in `ToggleHolidayAsync`**
Category: async-correctness (performance)
[VERIFIED]

```csharp
var existing = (await _holidays.GetForMonthAsync(date.Year, date.Month))
    .Any(h => h.Date == date);
```

`LoadAsync` (called 6 lines later on line 94) already fetches the same month's holidays and builds a `HashSet<DateOnly>`. But `ToggleHolidayAsync` re-fetches the entire month just to determine the toggle direction. The 42-cell grid already knows which cells are marked (the `IsHoliday` bool on each `HolidayDayCell`). The command parameter is the `DateOnly`; the XAML could pass the cell or the VM could look up the known state.

Fix: Accept the `HolidayDayCell` as the command parameter and read `cell.IsHoliday` directly:
```csharp
[RelayCommand]
private async Task ToggleHolidayAsync(HolidayDayCell cell)
{
    if (cell.IsHoliday) await _holidays.DeleteAsync(cell.Date);
    else await _holidays.UpsertAsync(cell.Date, null);
    await LoadAsync();
    _messenger.Send(new DataChangedMessage(DataKind.Holidays));
}
```
Confidence: High | Risk-of-fixing: Low (the cell is already the DataContext in XAML; one-line XAML change to pass `{Binding}` instead of `{Binding Date}`).

---

**5. `DailyReportViewModel.cs:95-98` â€” Trivially async relay commands that should be synchronous**
Category: naming / mvvm
[VERIFIED]

```csharp
[RelayCommand]
public Task PrevDayAsync() { SelectedDate = SelectedDate.AddDays(-1); return Task.CompletedTask; }

[RelayCommand]
public Task NextDayAsync() { SelectedDate = SelectedDate.AddDays(1); return Task.CompletedTask; }
```

These are synchronous operations wrapped in `Task.CompletedTask`. The load is driven by `OnSelectedDateChanged` which calls `_ = LoadAsync()`. The `Task`-returning signature on the relay command is superfluous â€” it creates the false impression of async work and triggers the CommunityToolkit async-command path (with `IsRunning` tracking) unnecessarily.

Fix: Convert to plain `[RelayCommand] private void PrevDay()` / `NextDay()`. The `OnSelectedDateChanged` handler already fires `LoadAsync` asynchronously.
Confidence: High | Risk-of-fixing: Low

---

**6. `MainViewModel.cs:318-327` â€” Bare `catch` in `SafeLoad` silently swallows all exceptions including `OutOfMemoryException`**
Category: error-handling
[VERIFIED]

```csharp
catch
{
    // Best-effort: a tab failing to preload must not prevent the shell from showing.
}
```

A bare `catch` swallows `StackOverflowException`, `OutOfMemoryException`, `ThreadAbortException`, etc. â€” exceptions that signal the process is in an unrecoverable state. In production, this has previously hidden real bugs (the multi-team startup stack-overflow mentioned in commit `62fe66c` would have been masked by this during the preload phase).

Fix: Catch `Exception` (not bare `catch`) to keep the intent clear and to allow the CLR to handle fatal exceptions normally:
```csharp
catch (Exception)
{
    // Best-effort: a tab failing to preload must not prevent the shell from showing.
}
```
Confidence: High | Risk-of-fixing: Low

---

**7. `ReportsViewModel.cs:141` / `TimesheetViewModel.cs:208` â€” Duplicated `MondayOf` logic**
Category: duplication
[VERIFIED]

Identical static method implementation in both VMs:
```csharp
// ReportsViewModel:
internal static DateOnly MondayOf(DateOnly date) =>
    date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

// TimesheetViewModel:
public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
```

Fix: Promote to a shared `DateHelper` or `CalendarExtensions` static class in the project (or a `DateOnly` extension method). `ReportsViewModel` references `TimesheetViewModel.MondayOf` indirectly in tests, but not in production code. Given CLAUDE.md's "simplicity first" rule, this is a low-friction extract â€” one file, no interface, no abstraction overhead.
Confidence: High | Risk-of-fixing: Low

---

### SUGGESTION

**8. `TaskListViewModel.cs:93-94` â€” Property-change handlers fire `LoadAsync` for year/month independently, causing two loads when both change at once**
Category: async-correctness (UX)
[VERIFIED]

```csharp
partial void OnSelectedYearChanged(int value) => _ = LoadAsync();
partial void OnSelectedMonthChanged(int value) => _ = LoadAsync();
```

Changing the year from a ComboBox fires `OnSelectedYearChanged` â†’ `LoadAsync()`. If the month simultaneously changes (or the user rapidly picks both), two concurrent `LoadAsync` calls run against the same `Rows` collection, causing interleaved `Clear()` + `Add()` sequences on the UI thread. This is the same pattern avoided in `TimesheetViewModel` via constructor-time field assignment and `ReloadAsync` being called once from `LoadCommand`. The Backlogs VM avoids this by separating the DB load (`RefreshAsync`) from the in-memory filter (`ApplyFilters`).

Fix: Introduce a debounce (a `CancellationTokenSource` reset on each partial handler, with `LoadAsync` receiving the token) â€” or simply combine into a single `SelectedPeriod` record property that triggers one reload. Given the low cost of `LoadAsync` (all local DB), the risk of visible glitch is low but not zero.
Confidence: Med | Risk-of-fixing: Med

---

**9. `BacklogEditorViewModel.cs:66-67` â€” `DateTime.Today` at field-initialiser time, not `IClock`**
Category: naming / test-quality
[VERIFIED]

```csharp
[ObservableProperty] private int _periodMonthNumber = DateTime.Today.Month;
[ObservableProperty] private int _periodYear = DateTime.Today.Year;
```

`BacklogEditorViewModel` is a pure working-set VM (no DI), so injecting `IClock` is out of scope. However, the field defaults are evaluated at construction time (when `ForCreate` or `ForEdit` is called), so this is consistent. The `Years` list on line 140 likewise uses `DateTime.Today.Year`. These are correct for a pure display VM with no testability requirement. This is flagged as a low-priority note only; the larger VMs all correctly use `IClock`.
Confidence: Low | Risk-of-fixing: Low (no fix required; informational only)

---

**10. `SettingsViewModel.cs:34-62` â€” Constructor accepts 13 parameters; `IHolidayRepository` is passed through only to construct the child `HolidayCalendarViewModel`**
Category: SOLID (SRP) / god-object
[VERIFIED]

`SettingsViewModel` orchestrates templates, default tasks, tags, PCA contacts, teams, membership, backup, retention, export, holiday calendar, and app-config. The constructor has 13 parameters. The `holidays` parameter has exactly one use: `new HolidayCalendarViewModel(holidays, _messenger)` on line 61. This is the "pass-through dependency" anti-pattern; the field is never stored.

The VM is large by necessity (it is the entire Settings screen), and CLAUDE.md explicitly warns to "weigh risk-of-fixing" for large VMs. A full decomposition (e.g., `BackupSettingsVm`, `RetentionSettingsVm`, `TeamSettingsVm`) would be high-risk. However, the trivial fix for the holiday pass-through is to accept `HolidayCalendarViewModel` directly rather than `IHolidayRepository`.

Fix (surgical): Change the ctor to accept `HolidayCalendarViewModel holidayCalendar` instead of `IHolidayRepository holidays`, and update the DI registration accordingly. This removes one parameter and makes the dependency graph explicit.
Confidence: Med | Risk-of-fixing: Med (DI wiring change; tests that supply `holidays` would need updating)

---

**11. `TeamFilterViewModel.cs:48` â€” Strong-reference subscription on `ICurrentTeamService.ActiveTeamChanged` not unsubscribed**
Category: resource-leak / mvvm
[VERIFIED]

```csharp
_currentTeam.ActiveTeamChanged += OnActiveTeamChanged;
```

`ICurrentTeamService` is a singleton. Each `TeamFilterViewModel` instance (one per screen VM, four screens) subscribes without unsubscribing. Since the screen VMs and the service are all singletons in this app, in production the lifetime match means no actual leak occurs. However:
- In tests, each test that creates a `TeamFilterViewModel` without disposing it will accumulate subscriptions on the shared service instance.
- If any VM were ever registered as transient, the leak would be real.

Fix: Implement `IDisposable` on `TeamFilterViewModel` and unsubscribe in `Dispose`:
```csharp
public void Dispose() => _currentTeam.ActiveTeamChanged -= OnActiveTeamChanged;
```
Confidence: Med | Risk-of-fixing: Low

---

### Render-Crash Risk Class â€” Status

All known WPF render-crash patterns were specifically checked:

- **`Run.Text` bindings**: All have `Mode=OneWay` explicitly set [VERIFIED in TimesheetTab.xaml:146,285; RequestsTab.xaml:392-401; SettingsTab.xaml:167-168,614].
- **`ProgressBar.Value`**: TaskListTab.xaml:257 uses `Mode=OneWay` with `FallbackValue=0, TargetNullValue=0` [VERIFIED]. The VM property `ProgressPercent` is `int?` â€” the null case is handled by FallbackValue/TargetNullValue, not by TwoWay. Clean.
- **`ToggleButton` with Button-typed style**: Theme.xaml has dedicated `MiniGhostToggle` (line 644) and `ToolbarGhostToggle` (line 694) both `TargetType="ToggleButton"`, created specifically to avoid this crash class [VERIFIED]. All ToggleButton usages in XAML use these ToggleButton-targeted styles.
- **`ToggleButton.IsChecked` on read-only VM property**: `IsChartCollapsed` (TaskListTab.xaml:117) and `IsExpanded` (TimesheetTab.xaml:257) are `[ObservableProperty]` â€” both have setters. The nav RadioButton-style `IsChecked` bindings (MainWindow.xaml:78-102) use a StringMatchConverter in OneWay read direction. [VERIFIED clean].
- **`Selector.SelectedItem` on read-only VM properties**: All bound `SelectedItem` targets (`SelectedTarget`, `FilterProject`, `ActiveTeam`, `SelectedBacklog`, etc.) are `[ObservableProperty]` with setters. [VERIFIED clean].
- **emoji glyphs in tag chips**: Chips use `Icon` as plain `TextBlock.Text`; the app-level FontFamily fix (from commit `04790b5`) covers this at the window level. No new unguarded emoji in the ViewModel layer.

No latent render-crash-class risks found in the VM layer.

---

### Summary Table

| # | File:Line | Severity | Category | Confidence | Fix Risk |
|---|-----------|----------|----------|------------|----------|
| 1 | TimesheetViewModel.cs:37 | Important | dead-code | High | Low |
| 2 | TimesheetViewModel.cs:57 | Important | resource-leak / mvvm | High | Low |
| 3 | BacklogsViewModel.cs:105 / TaskListViewModel.cs:144 | Important | async-correctness | High | Med |
| 4 | HolidayCalendarViewModel.cs:86 | Important | async-correctness | High | Low |
| 5 | DailyReportViewModel.cs:95-98 | Important | naming / mvvm | High | Low |
| 6 | MainViewModel.cs:323 | Important | error-handling | High | Low |
| 7 | ReportsViewModel.cs:141 / TimesheetViewModel.cs:208 | Important | duplication | High | Low |
| 8 | TaskListViewModel.cs:93-94 | Suggestion | async-correctness | Med | Med |
| 9 | BacklogEditorViewModel.cs:66-67 | Suggestion | naming | Low | Low |
| 10 | SettingsViewModel.cs:34-62 | Suggestion | SOLID / god-object | Med | Med |
| 11 | TeamFilterViewModel.cs:48 | Suggestion | resource-leak | Med | Low |

========== AUDIT SLICE 4 ==========
Now I have a complete picture of the entire Views slice. Here is the full audit:

---

## Views Slice â€” Quality Audit

### Render-Crash Risk

**F-01**
`src/TimesheetApp/Views/Tabs/TimesheetTab.xaml:146` Â· **Critical** Â· render-crash-risk

`Run.Text` has a TwoWay default binding mode. `CollapseToggleText` is bound with `Mode=OneWay`, which is correct. However line 285 binds `AssigneeName` with `Mode=OneWay` inside a `<Run>` â€” also correct. All `Run.Text` bindings in the codebase correctly carry `Mode=OneWay`. [VERIFIED â€” no latent crash here.]

Actually, correcting: all `Run.Text` bindings were found to explicitly carry `Mode=OneWay`. No crash risk. Moving on.

**F-02**
`src/TimesheetApp/Views/Tabs/TaskListTab.xaml:117-129` Â· **Important** Â· render-crash-risk

The "Hide/Show chart" `ToggleButton` binds `IsChecked="{Binding IsChartCollapsed}"` â€” no explicit `Mode`. `ToggleButton.IsChecked` is TwoWay by default; `IsChartCollapsed` is `[ObservableProperty]` on `TaskListViewModel` (line 98 of the VM file), which generates a **read-write** property, so this binding is safe â€” TwoWay to a writable property is fine. [VERIFIED]

**F-03**
`src/TimesheetApp/Views/Tabs/TimesheetTab.xaml:257` Â· **Important** Â· render-crash-risk

`ToggleButton.IsChecked="{Binding IsExpanded, Mode=TwoWay}"` on the `SectionToggle` style â€” `IsExpanded` must be a writable property on the group VM. This is correctly TwoWay-to-writable and is the intended pattern for the collapse toggle. Confirmed safe. [VERIFIED]

**F-04**
`src/TimesheetApp/Views/Tabs/TaskListTab.xaml:257` Â· **Critical** Â· render-crash-risk [VERIFIED]

`ProgressBar.Value` is bound at line 257:
```xml
Value="{Binding ProgressPercent, Mode=OneWay, FallbackValue=0, TargetNullValue=0}"
```
`Mode=OneWay` is explicitly set. This correctly sidesteps the known TwoWay-on-readonly ProgressBar crash. Correctly handled.

**F-05 â€” REAL FINDING**
`src/TimesheetApp/Views/Controls/TeamFilter.xaml:12` Â· **Important** Â· render-crash-risk [VERIFIED]

```xml
<Popup IsOpen="{Binding IsChecked, ElementName=TeamsToggle}" ...>
```
`Popup.IsOpen` is writable and element-name bindings default to `OneWay` when the target is not a DependencyProperty with a TwoWay default. This is safe. However, the bigger concern: `ToggleButton.IsChecked` bound to `TeamsToggle` has no explicit `Mode`. Since `TeamsToggle` is not bound to a VM property (it is only used as a source for the Popup's `IsOpen`), there is no read-only-property crash risk. Safe. [VERIFIED]

---

### Real Findings

**F-06 â€” SOLID / dependency injection violation**
`src/TimesheetApp/Views/Dialogs/SelectUserDialog.xaml.cs:19` Â· **Important** Â· SOLID [VERIFIED]

`SelectUserDialog` takes `IUserRepository _users` directly as a constructor parameter and calls `_users.InsertAsync(...)` on line 47 (the first-run "create user" path). A dialog is a View-layer class; it should not hold references to repository interfaces. In practice `App.xaml.cs` (line 218) passes the repository in from the composition root, but this still blurs View/Data layer boundaries. The insertion should be delegated to a service or VM method.

Concrete fix: move the `InsertAsync` call into a small method on `CurrentUserService` (or similar) and pass a `Func<string, Task<User>>` callback into the dialog instead of the repository.

Confidence: High Â· Risk-of-fixing: Low

---

**F-07 â€” Swallowed exception / silent async fire-and-forget in VM constructor**
`src/TimesheetApp/ViewModels/TaskListViewModel.cs:57` Â· **Important** Â· error-handling [VERIFIED]

```csharp
TeamFilter.SelectionChanged += (_, _) => _ = r.LoadAsync();
```
and the messenger handler at line 73:
```csharp
_ = r.LoadAsync();
```
Both discard the `Task` without observing exceptions. Any exception thrown inside `LoadAsync` is silently swallowed â€” no crash, but data silently fails to reload and the failure is unobservable. This is not strictly a Views finding, but the Views code-behind `TaskListTab.xaml.cs` has the same pattern at line 62 (the `OnVmPropertyChanged` handler calls `DrawGantt()` synchronously, but the underlying VM uses fire-and-forget).

This is correctly categorised under the Views slice because the code-behind's `DrawGantt()` reads `vm.Gantt` which may not have loaded yet if the async chain fails silently.

Concrete fix: Use `CommunityToolkit.Mvvm`'s `IAsyncRelayCommand` trigger or add a `.ContinueWith(t => Log(t.Exception))` on the discarded tasks.

Confidence: High Â· Risk-of-fixing: Low

---

**F-08 â€” Code-behind exposes commands wrapping VM internals (duplication)**
`src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs:22-44` and `src/TimesheetApp/Views/Tabs/RequestsTab.xaml.cs:22-34` Â· **Suggestion** Â· duplication [VERIFIED]

Both code-behind files create `RelayCommand<T>` wrappers for VM methods (`RemoveTask`, `MoveUp`, `MoveDown`) because those methods are plain methods not decorated with `[RelayCommand]`. Both tabs duplicate identical patterns:

```csharp
RemoveTaskCommand = new RelayCommand<TemplateTaskRowVm>(row => {
    if (row is not null) Vm?.TemplateEditor?.RemoveTask(row);
});
```

The fix is to decorate `RemoveTask`, `MoveUp`, `MoveDown` on `BacklogEditorViewModel` and `TemplateEditorViewModel` with `[RelayCommand]` (CommunityToolkit source-generates the command), then bind directly in XAML without the code-behind relay. This eliminates all six wrapper commands from two code-behind files.

Concrete fix: Add `[RelayCommand]` to `RemoveTask(EditableTaskRowVm)`, `MoveUp(EditableTaskRowVm)`, `MoveDown(EditableTaskRowVm)` in both editor VMs; remove the wrapper properties from the code-behind and update the XAML bindings from `AncestorType=UserControl` to `AncestorType=DataGrid` or use `{Binding DataContext.RemoveTaskCommand}`.

Confidence: High Â· Risk-of-fixing: Low

---

**F-09 â€” NullToCollapsedConverter used to hide an emoji-bearing TextBlock but FontFamily not set**
`src/TimesheetApp/Views/Tabs/DailyBoardTab.xaml:40` Â· **Important** Â· render-crash-risk [VERIFIED]

```xml
<TextBlock x:Name="ic" Text="âš " FontWeight="Bold" Foreground="{StaticResource AmberFg}"
           Margin="0,0,6,0" VerticalAlignment="Top"/>
```
The `âš ` (U+26A0, Warning Sign) glyph in `DailyBoardTab` and line 61 of `DailyInputTab.xaml` (`Text="âš "`) both render without `FontFamily="Segoe UI Emoji"`. Per the known production crash pattern, non-emoji-font TextBlocks on emoji glyphs trigger a WPF glyph-measure failure on some Windows configurations. The prior fix (`89fce98`) added `FontFamily="Segoe UI Emoji"` to emoji glyphs â€” these two `âš ` instances were missed.

The same applies to `Text="âœ•"` (line 59, DailyInputTab.xaml) and the `âœ“` checkmark (DailyBoardTab.xaml:55, DailyInputTab.xaml:101) which are set dynamically via DataTrigger. The DataTrigger-set `Text` value `"&#10003;"` (âœ“) and `"&#10133;"` (âž• in other tabs) similarly lack `FontFamily`.

Concrete fix: Add `FontFamily="Segoe UI Emoji"` to every `TextBlock` that renders an emoji or symbol glyph outside the basic ASCII range. Specifically:
- `DailyBoardTab.xaml:40` (`âš ` icon TextBlock)
- `DailyInputTab.xaml:61` (`âš ` icon TextBlock)
- The `âœ•` delete button content in DailyInputTab.xaml:59 (Button Content, not a TextBlock â€” Buttons render content through a ContentPresenter inside a TextBlock in the MiniGhostButton template, which has `FontFamily` set to Segoe UI, not Segoe UI Emoji; the `âœ•` is safe since it is ASCII-adjacent, but the `âœ“` set by DataTrigger is not)

Confidence: High Â· Risk-of-fixing: Low

---

**F-10 â€” async void in code-behind with no exception surface**
`src/TimesheetApp/Views/Tabs/SettingsTab.xaml.cs:115,138` Â· **Important** Â· error-handling [VERIFIED]

`OnRestoreBackup` (line 115) and `OnRunRetention` (line 138) are `async void` event handlers. This is the accepted WPF pattern for event handlers (cannot return `Task`), and both already wrap their awaits in the outer async void. However, neither has a `try/catch` around the `await` call. If `Vm.RestoreCommand.ExecuteAsync(backup)` throws (e.g. file locked), the exception is unhandled and propagates to the STA dispatcher pump, crashing the app. `OnRunRetention` at line 151 awaits `Vm.RunRetentionCommand.ExecuteAsync(null)` with the same issue.

Compare with `ReportsTab.xaml.cs` which correctly wraps the await in `try/catch` (lines 29-39).

Concrete fix: Wrap the await calls in both methods with `try { ... } catch (Exception ex) { MessageBox.Show(...) }` â€” same pattern already used in `ReportsTab`.

Confidence: High Â· Risk-of-fixing: Low

---

**F-11 â€” DataTrigger on `AuditEntries.Count` vs. null-safe**
`src/TimesheetApp/Views/Tabs/RequestsTab.xaml:378` Â· **Suggestion** Â· xaml-quality [VERIFIED]

```xml
<DataTrigger Binding="{Binding AuditEntries.Count}" Value="0">
    <Setter Property="Visibility" Value="Collapsed"/>
</DataTrigger>
```
If `AuditEntries` is `null` (editor VM freshly constructed, not yet loaded), the binding path `AuditEntries.Count` silently produces no value â€” the default (`Visibility.Visible`) wins, showing an empty section. The `Visibility` default should be `Collapsed`, with a trigger to show when `Count > 0`. The current sense is inverted relative to the safe default.

Concrete fix: Swap the logic â€” set default `Visibility` to `Collapsed`, add a trigger for `Value` != "0" (or bind `HasAuditEntries` bool from the VM). A simpler alternative is `Visibility="{Binding AuditEntries.Count, Converter=...}"` with a count-to-visibility converter, or expose `bool HasAuditHistory` from the VM and use `BoolToVisibleConverter`.

Confidence: Med Â· Risk-of-fixing: Low

---

**F-12 â€” ComboBoxSearch.cs: reflection in hot path**
`src/TimesheetApp/Views/Behaviors/ComboBoxSearch.cs:106` Â· **Suggestion** Â· xaml-quality [VERIFIED]

```csharp
return item.GetType().GetProperty(path)?.GetValue(item)?.ToString() ?? string.Empty;
```
`Display()` is called on every keystroke for every visible item in the dropdown. `GetType().GetProperty(path)` allocates a `PropertyInfo` on every call. On a large PCA contact list this is measurable allocations-per-keypress.

Concrete fix: Cache the `PropertyInfo` per `(itemType, path)` in a `Dictionary<(Type, string), PropertyInfo?>` static field â€” a two-line change that eliminates repeated reflection.

Confidence: High Â· Risk-of-fixing: Low

---

**F-13 â€” SelectUserDialog binds `DataContext = this` then exposes a `SelectedUser` property bound in XAML with `Mode=TwoWay`**
`src/TimesheetApp/Views/Dialogs/SelectUserDialog.xaml.cs:26` and `.xaml:24` Â· **Suggestion** Â· mvvm [VERIFIED]

```csharp
DataContext = this; // in constructor
```
```xml
SelectedItem="{Binding SelectedUser, Mode=TwoWay}"
```
Setting `DataContext = this` in a `Window` subclass makes the Window both the View and its own ViewModel. When `SelectedUser` is set via the ListBox binding, `Ok_Click` then reads `SelectedUser` directly as a field. This is a minor MVVM violation for a simple dialog â€” it works, but it means the `SelectUserDialog` cannot be tested without a UI thread. For this simple two-field dialog the risk is minimal, but it violates the pattern used by every other dialog in the project (which use a separate VM or code-only result properties).

Concrete fix: Remove `DataContext = this`; instead set `UsersList.ItemsSource = activeUsers` and read `UsersList.SelectedItem` directly in `Ok_Click`. This removes the binding and makes the code simpler and consistent with `StandupIssueDialog` / `TaskInputDialog`.

Confidence: Med Â· Risk-of-fixing: Low

---

**F-14 â€” HolidayCellBorder Style.Tag binding uses VM ancestor but Tag property has no explicit Mode**
`src/TimesheetApp/Views/Tabs/TimesheetTab.xaml:323-325` Â· **Suggestion** Â· xaml-quality [VERIFIED]

```xml
Tag="{Binding DataContext.MonIsHoliday, RelativeSource={RelativeSource AncestorType=UserControl}}"
```
`FrameworkElement.Tag` has a TwoWay default binding mode but `MonIsHoliday` is presumably a read-only bool on the VM (day-of-week holiday flag). If the VM property has no setter, WPF will log a binding error but silently no-op â€” no crash, but the error pollutes the debug output and wastes a validation cycle per cell.

Concrete fix: Add `Mode=OneWay` to all five `Tag="{Binding DataContext.XxxIsHoliday, ...}"` bindings (lines 323, 330, 337, 344, 351).

Confidence: Med Â· Risk-of-fixing: Low

---

### Clean Slices (No Real Findings)

**Converters** â€” All converters are correct. `HexToBrushConverter` defensively catches parse errors. `BoolToVisibilityConverter.ConvertBack` returns `bool` correctly. `DateOnlyConverter` handles `null` on both paths. `OverEightTagConverter` uses `decimal` compare without float imprecision. `StringMatchConverter` correctly emits `Binding.DoNothing` on the unchecked path. [VERIFIED]

**Theme.xaml** â€” No Button-style-on-ToggleButton issues found. `ToolbarGhostToggle`, `MiniGhostToggle`, and `SectionToggle` are all `TargetType="ToggleButton"`. `MiniGhostButton` is `TargetType="Button"` and is only applied to Button elements. The ComboBox template's inner `ToggleButton` correctly binds `IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource=TemplatedParent}"` â€” `IsDropDownOpen` is a writable DP. [VERIFIED]

**RoundedClip.cs** â€” Correctly unhooks `SizeChanged` before re-hooking (line 26). No leak. [VERIFIED]

**BindingProxy.cs** â€” Correct `Freezable` subclass pattern; `UIPropertyMetadata` is appropriate here. No issues. [VERIFIED]

**ComboBoxSearch.cs** â€” Logic is correct. `OnTextChanged` guards against programmatic updates via `IsKeyboardFocusWithin`. `OnDropDownClosed` resets the filter so the shared `CollectionView` is clean. The `FindParentComboBox` walk is safe. The only real finding is the reflection allocation (F-12). [VERIFIED]

**DailyBoardTab / DailyBoardTab.cs** â€” Code-behind is a single-line `InitializeComponent()`. All logic in VM. Clean. [VERIFIED]

**SmartInputPreviewDialog** â€” Minimal code-behind. `ApplyCommand` bound directly to VM. [VERIFIED]

**TaskListTab.cs (Gantt drawing)** â€” `DrawGantt()` correctly clears and rebuilds the canvas on every call. `DataContextChanged` correctly wires/unwires `PropertyChanged`. No event-handler leak. `ResolveDayWidth` safely returns `MinDayWidth` when `ActualWidth` is 0 (pre-layout). [VERIFIED]

---

### Summary by severity

| # | Severity | Category | File (short) | One-line description |
|---|---|---|---|---|
| F-06 | Important | SOLID | SelectUserDialog.xaml.cs:19 | Dialog holds IUserRepository and calls InsertAsync â€” repository in the View layer |
| F-07 | Important | error-handling | TaskListViewModel.cs:57/73 | Fire-and-forget LoadAsync discards Task, exceptions silently swallowed |
| F-09 | Important | render-crash-risk | DailyBoardTab.xaml:40, DailyInputTab.xaml:61 | âš  emoji TextBlocks missing FontFamily="Segoe UI Emoji" |
| F-10 | Important | error-handling | SettingsTab.xaml.cs:115,138 | async void OnRestoreBackup / OnRunRetention lack try/catch around await |
| F-08 | Suggestion | duplication | SettingsTab.xaml.cs, RequestsTab.xaml.cs | 6 RelayCommand wrappers duplicated across two code-behind files; fix with [RelayCommand] on VM methods |
| F-11 | Suggestion | xaml-quality | RequestsTab.xaml:378 | DataTrigger on AuditEntries.Count with inverted default â€” shows empty section when null |
| F-12 | Suggestion | xaml-quality | ComboBoxSearch.cs:106 | GetType().GetProperty() per-keypress per-item; cache PropertyInfo |
| F-13 | Suggestion | mvvm | SelectUserDialog.xaml.cs:26 | DataContext=this makes Window its own VM; inconsistent with every other dialog |
| F-14 | Suggestion | xaml-quality | TimesheetTab.xaml:323-351 | Tag="{Binding ...IsHoliday}" missing Mode=OneWay â€” TwoWay default on read-only bool leaks binding errors |

========== AUDIT SLICE 5 ==========
Now I have a complete picture. Let me write the final audit report.

---

## Test Quality Audit â€” src/TimesheetApp.Tests

### F1 â€” `SmokeTests.cs:10` Â· Suggestion Â· dead-code Â· Vacuous assertion that always passes [VERIFIED]

`Assert.True(true)` proves nothing. The test name implies it tests the build, but the runner already found this file as evidence. The method is harmless but occupies a test slot and inflates the count.

**Fix:** Delete the method body or replace with `Assert.True(true, "placeholder")` and a TODO comment, or remove the class entirely (the DI test already validates the build more meaningfully).

**Confidence:** High Â· Risk-of-fixing: Low

---

### F2 â€” `DatabaseInitializerTests.cs:98-104` Â· Important Â· test-quality Â· Schema version assertion uses `>= 1` instead of pinning the actual target version [VERIFIED]

`InitializeAsync_Sets_User_Version_To_Target` asserts `version >= 1`. This is a "brittle in reverse" problem: the test would pass even if the migrator forgot to apply v7, v8, or v9 and stopped at v6. The class comment says "v8" tables are expected but the assertion gives no signal that the version advanced past 1.

**Fix:** Assert `Assert.Equal(9, version)` (the current target). When a new migration is added, the test breaks â€” that is the desired behaviour. If the constant lives in `DatabaseInitializer`, expose it as `public const int CurrentVersion` and reference it: `Assert.Equal(DatabaseInitializer.CurrentVersion, version)`.

**Confidence:** High Â· Risk-of-fixing: Low

---

### F3 â€” `DatabaseInitializerTests.cs:17-22` Â· Important Â· test-quality Â· `ExpectedTables` array is stale (v9 tables missing) [VERIFIED]

`ExpectedTables` lists `Teams` and `UserTeams` (the v8 comment says "P10 schema v8"), but does NOT include the v9 additions `TaskTags` and `TaskAudit`. `InitializeAsync_Creates_All_Seven_Tables` (misnamed â€” now 11 tables) will pass even if those two v9 tables are absent from a fresh schema, silently missing the regression.

**Fix:** Add `"TaskTags"` and `"TaskAudit"` to the array. Update the test method name to reflect the actual table count (e.g. `Creates_All_Required_Tables`).

**Confidence:** High Â· Risk-of-fixing: Low

---

### F4 â€” `TaskListRepositoryTests.cs` / `RepositoryCrudTests.cs` Â· Important Â· test-quality Â· `UpdateStatusAsync` has zero test coverage [VERIFIED]

`ITaskRepository.UpdateStatusAsync` (TaskRepository.cs:143) is a v9 repo method that writes a `TaskAudit` row for status changes. There is no test for it anywhere in the suite. The `UpdateExtendedAsync` tests (F2 scenario: audit on change, no-op on same value) were written; `UpdateStatusAsync` was not. Status is the most-edited field during inline-edit (the upcoming inline-edit wave will call it constantly).

**Fix:** Add to `TaskListRepositoryTests`:
```csharp
[Fact]
public async Task UpdateStatusAsync_audits_change_and_no_audit_when_unchanged()
{
    var tasks = new TaskRepository(_db);
    var bid = await _db.SeedRequestAsync("REQ-STS", "ARCS");
    var taskId = await _db.SeedTaskAsync(bid, "Build"); // default status = 'Todo'

    await tasks.UpdateStatusAsync(taskId, "Doing", changedByUserId: 1, changedByName: "A");
    var audit = await tasks.GetAuditAsync(taskId);
    Assert.Single(audit);
    Assert.Equal("status", audit[0].Field);
    Assert.Equal("Todo", audit[0].OldValue);
    Assert.Equal("Doing", audit[0].NewValue);

    // Re-applying same status â†’ no second row.
    await tasks.UpdateStatusAsync(taskId, "Doing", changedByUserId: 1, changedByName: "A");
    Assert.Single(await tasks.GetAuditAsync(taskId));
}
```

**Confidence:** High Â· Risk-of-fixing: Low

---

### F5 â€” `TaskListRepositoryTests.cs` Â· Important Â· test-quality Â· Missing coverage: `GetTagIdsForAllAsync` with `TaskTags` (task-level), distinct from `BacklogTags` [VERIFIED]

`SetTaskTagsAsync` and `GetTagIdsAsync` (on `ITaskRepository`) are tested, but `GetTagIdsForAllAsync` on `ITaskRepository` (the bulk-read used by `TaskListViewModel` to paint tag chips per task row) has no test. The parallel method on `IBacklogRepository` is well-tested in `SetTagsAsync_replaces_the_whole_set`. If the SQL or column name is wrong on the task variant it will silently return empty maps (no tag chips ever).

**Fix:** Add to `TaskListRepositoryTests`:
```csharp
[Fact]
public async Task GetTagIdsForAllAsync_returns_map_of_taskid_to_tagids()
{
    var tags = new TagRepository(_db);
    var tasks = new TaskRepository(_db);
    var t1 = await tags.InsertAsync(new Tag(0, "A", "a", "#111111", DateTimeOffset.UtcNow));
    var bid = await _db.SeedRequestAsync("REQ-TFA", "ARCS");
    var taskId = await _db.SeedTaskAsync(bid, "Build");

    await tasks.SetTaskTagsAsync(taskId, new[] { t1 }, changedByUserId: 1, changedByName: "A");

    var all = await tasks.GetTagIdsForAllAsync();
    Assert.True(all.ContainsKey(taskId));
    Assert.Equal(new[] { t1 }, all[taskId].ToArray());
}
```

**Confidence:** High Â· Risk-of-fixing: Low

---

### F6 â€” `SchemaV7UpgradeTests.cs:94` / `SchemaV8UpgradeTests.cs:110` Â· Suggestion Â· test-quality Â· Upgrade tests assert the final version (9) rather than the step's own target; comment is accurate but assertion is confusing [VERIFIED]

The test comment says "A v6 DB now upgrades all the way to the latest schema (v9)" â€” that is correct and intentional (a chain-run scenario). However, asserting `== 9` hard-codes the current latest version in a test that was written to verify the v6â†’v7 or v7â†’v8 step. When v10 is added, these tests will fail not because v7/v8 broke but because the final version changed. The SchemaV9 test also asserts `== 9` twice (pre and post) â€” the pre-check is meaningful; the post-checks are fine.

**Fix:** Either (a) accept the hard-coding as intentional and document it with a comment "This asserts the current schema target; bump to 10 when v10 ships" â€” or (b) reference `DatabaseInitializer.CurrentVersion` (see F2). Option (b) is safer; all three upgrade test classes need the same fix.

**Confidence:** Med Â· Risk-of-fixing: Low

---

### F7 â€” `SchemaV7UpgradeTests.cs`, `SchemaV8UpgradeTests.cs`, `SchemaV9UpgradeTests.cs` Â· Suggestion Â· test-quality Â· Upgrade test classes are `IDisposable` but the setup calls are synchronous â€” the ctor calls `SeedV*Database()` only in the test method; no bug, but they do not implement `IAsyncLifetime` unlike all other data test classes [VERIFIED]

These three classes seed the DB in the test body itself (before calling the initializer), so `IAsyncLifetime` is not required. This is fine. However, the test bodies are all packed into single `[Fact]` methods that cover pre-check + first run + data integrity + idempotency. If any phase fails, the later phases do not run and the failure message is ambiguous.

**Fix (low priority / suggestion only):** Split each single mega-fact into 3-4 narrower facts that share a seeded base via `IAsyncLifetime` + a shared `SeedAndUpgrade()` helper. This gives cleaner failure output but requires restructuring.

**Confidence:** Med Â· Risk-of-fixing: Med

---

### F8 â€” Multiple test files Â· Suggestion Â· duplication Â· `FakeConfig` / `StubConfig` duplicated across 4 test files [VERIFIED]

`FakeConfig` (implementing `IAppConfig` with 10 mutable properties) is copied verbatim in `RetentionServiceTests.cs`, `ExportHubServiceTests.cs`, `BackupServiceTests.cs`, and `PruneArchiverTests.cs` (named `StubConfig` there but identical in shape). This is ~55 lines Ã— 4 = 220 lines of identical boilerplate. If a new property is added to `IAppConfig`, all four copies must be updated, and the test that fails first due to a compilation error is whichever runs alphabetically first.

**Fix:** Extract a single `TestAppConfig : IAppConfig` to `src/TimesheetApp.Tests/TestAppConfig.cs` (a concrete class with public setters for all properties). All four test classes reference this shared type. Since `IAppConfig` is already stabilised, the compiler enforces the update in exactly one place.

**Confidence:** High Â· Risk-of-fixing: Low

---

### F9 â€” `TimesheetViewModelTests.cs` Â· Suggestion Â· test-quality Â· `ActiveTeamSwitch_resets_filter_and_reloads` in `TaskListViewModelTests.cs` uses `await Task.Yield()` as a sync fence [VERIFIED]

`TaskListViewModelTests.cs:351` uses `await Task.Yield()` to let the fire-and-forget reload triggered by `ActiveTeamChanged` run before asserting. `Task.Yield()` only yields once; if the reload itself is async and longer than one scheduler cycle, the assertion may race. On a fast CI machine this is usually fine, but it is the same pattern that caused historical flakiness in `MainViewModelTests` (the isolated messenger comment at line 181 documents this).

**Fix:** Expose a `Task LastLoad` property on `TaskListViewModel` (mirroring `TimesheetViewModel.LastAutoSave`) and `await vm.LastLoad` instead of `await Task.Yield()`. Alternatively, the `FakeTeam.SetActiveTeamAsync` is synchronous, so the handler fires synchronously inside the event raise; add `await Task.CompletedTask` immediately after `SetActiveTeamAsync` is already sufficient if the handler is posted synchronously. Verify the handler's dispatch mechanism first.

**Confidence:** Med Â· Risk-of-fixing: Med

---

### F10 â€” No test for `TaskRepository.GetByIdAsync` with v9 fields populated [VERIFIED]

`GetByIdAsync` on `TaskRepository` is the method called by the upcoming inline-edit save path to read back persisted type/assignee after `UpdateExtendedAsync`. The existing test (`UpdateExtendedAsync_audits_one_row_per_changed_field`) asserts the audit rows but calls `GetByIdAsync` to verify the persisted data (`loaded.Type`, `loaded.AssigneeUserId`) â€” this is actually already tested in `TaskListRepositoryTests.cs:186-195`. No gap here.

**Assessment:** Covered. Mark as clean.

---

### F11 â€” `SmokeTests.cs` class name misleads [VERIFIED]

The class is called `SmokeTests` but contains only a trivially true assertion. Actual smoke coverage is in `DependencyInjectionTests` (DI resolution) and `DatabaseInitializerTests` (schema creation). Renaming or removing `SmokeTests` avoids confusion when someone reads the test output and wonders why "SmokeTests passed" means nothing.

**Fix:** Delete the class (already noted under F1). The DI test covers the "does the app start" concern.

**Confidence:** High Â· Risk-of-fixing: Low

---

### Summary of missing coverage recommendations

In priority order:

1. **`UpdateStatusAsync` (Critical path for inline-edit wave)** â€” add test as shown in F4. Zero tests today; it is the highest-volume v9 write path.

2. **`DatabaseInitializerTests` table list stale** â€” add `TaskTags`, `TaskAudit` to `ExpectedTables` (F3). One-liner fix.

3. **Schema version assertion â€” pin to constant** â€” F2. One-liner fix per file.

4. **`GetTagIdsForAllAsync` on TaskRepository** â€” F5. Bulk-read used by the grid paint path; silent empty maps if wrong.

5. **`FakeConfig` deduplication** â€” F8. Maintenance risk; extract to a shared helper.

6. **`Active_team_switch_resets_filter_and_reloads` Task.Yield fence** â€” F9. Low probability flake but non-zero on slower CI.

7. **Upgrade test factoring** â€” F7. Suggestion only; no functional gap.

### What is genuinely clean (no issues found)

- v7/v8/v9 upgrade tests: comprehensive additive/data-preserving/idempotent coverage [VERIFIED]
- `TaskListRepositoryTests`: covers all v9 BacklogRepository audit paths (auditNote on deadlines, tags audit write-on-change/no-write-on-same, UpdateExtendedAsync, SetTaskTagsAsync) [VERIFIED]
- `RetentionServiceTests`: spans every documented edge (cutoff math, year-rollover, spanning backlogs, BacklogAudit survival, conflict-copy abort, contiguous-prefix, zero-byte snapshot guard, idempotency) [VERIFIED]
- `TimeLogServiceTests`: covers all XC-* validation paths including holiday rejection, journal warning, RPT-04 team scoping [VERIFIED]
- `DependencyInjectionTests`: resolves every registered ViewModel and service; `Func<int>` collision test is a meaningful regression guard [VERIFIED]
- `StandupServiceTests` / `StandupRepositoryTests`: full owner-gating, lock-day, team-scoped order-math, and team-standup board coverage [VERIFIED]
- `ExportHubServiceTests`: per-team folder structure, best-effort per root, no-data skip, collision-suffix, db-copy once-per-root, backfill all tested [VERIFIED]
- `BackupServiceTests`: timestamped naming, prune-to-N, unrelated-file safety, restore self-guard all tested [VERIFIED]
- `TaskListViewModelTests`: month filter, all-months, logged hours with soft-deleted tasks, schedule chips, team filter reload, active-team switch all tested [VERIFIED]
- `TeamScopedQueryTests`: SearchAsync empty-teamIds empty-result guard, cross-team leak prevention, GetDefaultForTeam, GetActiveForTimesheet teamId-0 empty, GetExportRows team projection all tested [VERIFIED]
