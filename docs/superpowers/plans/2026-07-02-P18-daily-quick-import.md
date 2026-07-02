# P18 — Daily Quick Import — PLAN (wave-grouped)

**Goal:** A "Quick import" action in Daily Input clones a chosen past day's standup data (current user's entries + issues, both sections) into the currently-selected editable day.
**Mode A.** Spec: `docs/superpowers/specs/2026-07-02-daily-quick-import-design.md`. **Stack skill: `dotnet`.**
**Architecture:** New service method `QuickImportDayAsync(sourceDate, targetDate)` (read via existing repo queries, clone via existing inserts, recalc order per section); VM command + a themed `QuickImportDialog` (DatePicker) + a button in `DailyInputTab`. No schema change; reuses existing repo/service seams.
**Tech Stack:** .NET 8 / WPF MVVM / Dapper+SQLite / xUnit.

## must_haves (goal-backward)
**Observable Truths**
- OT-1 "Quick import" button shows in Daily Input only when the selected day is editable.
- OT-2 Dialog picks a source day (DatePicker).
- OT-3 On confirm, the current user's source-day entries (both sections) + issues are **appended** to the selected day, preserving section, statuses as-is.
- OT-4 Cloned rows are new (regenerated id/CreatedAt/order, WorkDate=target); source day unchanged; board+input refresh.
- OT-5 Locked target → no-op + status message. Scope = current user + active team.

**Required Artifacts:** `IStandupService.QuickImportDayAsync`; `StandupService` impl; `DailyReportViewModel.QuickImportCommand`; `Views/Dialogs/QuickImportDialog.xaml(.cs)`; button in `DailyInputTab.xaml(.cs)`; `StandupServiceTests` cases.
**Key Links:** button → dialog → `vm.QuickImportAsync(sourceDate)` → `service.QuickImportDayAsync(sourceDate, SelectedDate)` → repo `GetEntriesAsync`/`GetIssuesForEntriesAsync` + `InsertEntryAsync`/`InsertIssueAsync` → `ReloadAndBroadcastAsync`.

---

## Wave 1 — Service (clone logic)

- **W1-T1** `[sonnet]` **`QuickImportDayAsync` + tests.**
  · **read_first:** `src/TimesheetApp/Services/IStandupService.cs`; `src/TimesheetApp/Services/StandupService.cs` (esp. `CanEditDay` :34, `AddEntryAsync` :95, `NextOrderAsync` :215, how current user + active team are resolved for `GetMyStandupAsync`); `src/TimesheetApp/Data/Repositories/IStandupRepository.cs` (`GetEntriesAsync`, `GetIssuesForEntriesAsync`, `InsertEntryAsync`, `InsertIssueAsync`); `src/TimesheetApp/Models/StandupModels.cs`; `src/TimesheetApp.Tests/Services/StandupServiceTests.cs` + `src/TimesheetApp.Tests/Data/TestDb.cs` (harness).
  · **action:** Add `Task<int> QuickImportDayAsync(DateOnly sourceDate, DateOnly targetDate);` to `IStandupService`. Implement in `StandupService`: resolve `userId` (current user) + `teamId` (active team) the SAME way `GetMyStandupAsync` does. If `!CanEditDay(targetDate)` return 0. Read `var src = await _repo.GetEntriesAsync(userId, sourceDate, teamId);` if empty return 0. Load issues: `var issues = await _repo.GetIssuesForEntriesAsync(src.Select(e => e.Id).ToList());` group by `EntryId`. Track a per-section next order (seed each section from `NextOrderAsync(userId, targetDate, section)`; increment locally so a batch doesn't collide). For each source entry (iterate in the repo's returned order): build a cloned `StandupEntry` with `Id=0, WorkDate=targetDate, CreatedAt=_clock.UtcNow, OrderIndex=<next for its section>`, everything else copied; `newId = await _repo.InsertEntryAsync(clone);`. For each of that entry's issues (in `OrderIndex` order): `await _repo.InsertIssueAsync(issue with Id=0, EntryId=newId, CreatedAt=_clock.UtcNow, else copied);`. Return count of entries cloned. **AVOID:** reusing source `OrderIndex` (recalc per section); mutating the source; touching entries of other users/teams; a broadcast here (VM owns it).
  · **verify (auto <60s):** `dotnet test src/TimesheetApp.sln --filter "FullyQualifiedName~StandupService"` — new tests: (a) clone count = source entries; (b) target has new ids, `WorkDate==target`, `CreatedAt` bumped; (c) issues cloned with `Status`/`SolutionText`; (d) order recalculated per section (no collision with an existing target entry); (e) locked target → 0, source unchanged; (f) empty source → 0; (g) only current user's / active team's entries copied.
  · **done:** `grep QuickImportDayAsync` hits interface+impl; new `[Fact]`s green; build clean.

## Wave 2 — VM + View  *(depends on W1; no file overlap — W1 = services/tests, W2 = VM/views)*

- **W2-T1** `[sonnet]` **VM command + dialog + button.**
  · **read_first:** `src/TimesheetApp/ViewModels/DailyReportViewModel.cs` (`SelectedDate`, `CanEditSelectedDay`, `AddEntryAsync` :109, `ReloadAndBroadcastAsync` :178, `StatusMessage`); `src/TimesheetApp/Views/Tabs/DailyInputTab.xaml` (the "+ Add entry" button ~L120) + `DailyInputTab.xaml.cs` (`OnAddEntry` dialog pattern :18); `src/TimesheetApp/Views/Dialogs/StandupEntryDialog.xaml(.cs)` (themed chrome + DialogResult pattern to mirror).
  · **action:** VM: add `[RelayCommand] async Task QuickImportAsync(DateOnly sourceDate)` → `var n = await _service.QuickImportDayAsync(sourceDate, SelectedDate); if (n <= 0) { StatusMessage = "Nothing to import / day locked."; return; } await ReloadAndBroadcastAsync();` (optionally `StatusMessage = $"Imported {n}."`). New `Views/Dialogs/QuickImportDialog.xaml(.cs)`: mirror `StandupEntryDialog` chrome (WindowStyle=None themed card, Cancel/Import footer); a `DatePicker` bound to a `SelectedDate` (`DateTime`) property, default `DateTime.Today.AddDays(-1)`; expose `DateOnly SelectedDate` + set `DialogResult=true` on Import. `DailyInputTab.xaml`: add a **"⇩ Quick import"** `Button` (`ToolbarButton` style) next to "+ Add entry", `Visibility` bound to `CanEditSelectedDay` via `BoolToVisibleConverter`, `Click="OnQuickImport"`. `DailyInputTab.xaml.cs`: `OnQuickImport` opens `QuickImportDialog`; on `ShowDialog()==true` → `await vm.QuickImportAsync(dialog.SelectedDate)`. **AVOID (render-crash class):** no `TwoWay` onto a read-only prop; DatePicker `SelectedDate` bound to a settable `DateTime?`; no `Button` style on a `ToggleButton`.
  · **verify (auto <60s):** `dotnet build src/TimesheetApp.sln` + `dotnet test src/TimesheetApp.sln` — full suite green. (If a `DailyReportViewModel` test seam exists, add one asserting `QuickImportAsync` calls the service with `(sourceDate, SelectedDate)` and reloads.)
  · **done:** button + dialog present; `grep QuickImportAsync` hits VM; build clean; full suite green.

---

## REQ coverage (100%)
QI-01 (button when editable) → W2-T1 · QI-02 (dialog+DatePicker) → W2-T1 · QI-03 (append clone entries+issues, preserve section, status as-is) → W1-T1 · QI-04 (regenerate ids/order/date) → W1-T1 · QI-05 (reload+broadcast; locked no-op) → W1-T1 (guard) + W2-T1 (reload) · QI-06 (scope user+team) → W1-T1.

## Risks / gates
1. **Order collision** — recalc per section from `NextOrderAsync` + local increment (don't reuse source order). Gated by test (d).
2. **Edit-lock** — target must be editable (`CanEditDay`); source read-only. Test (e).
3. **Scope leak** — only current user + active team (mirror `GetMyStandupAsync`). Test (g).
4. **Build/run gotcha:** kill running `TimesheetApp` before build.

## Model note
Both `[sonnet]` (`claude-sonnet-5`). Inline execution (Mode A) — build+test after each wave.
