Master report written to `.planning/P13-QUALITY-AUDIT.md`. Summary of what I produced and the verification I did:

## Master report: `e:\Learning\AAM 2nd\aamanagementtool\.planning\P13-QUALITY-AUDIT.md`

**Verdict: the app is healthy.** 0 Critical, 9 Important, 15 Suggestion (after dedup). Zero `.Result`/`.Wait()`, zero confirmed render crashes, zero blocking-async defects. The known WPF crash class is already systematically mitigated.

### I did NOT take the slice audits at face value â€” 5 corrections from reading source:

1. **VM finding #3 cited a phantom file.** `BacklogsViewModel.cs` does not exist. The real per-row N+1 (`GetActiveByBacklogAsync` in a `foreach`) is in `RequestsViewModel.cs:107`. Citation corrected.
2. **Data finding #5 downgraded** Importantâ†’Suggestion. The "UPDATE before null-guard data-loss bug" is a benign 0-row no-op; `before` is read on a separate auto-commit connection.
3. **Views F-09 reframed.** The claim "app-level FontFamily covers emoji at the window level" is false â€” commit `89fce98` touched only 3 files for **astral** glyphs (ðŸ› ï¸). There is no global `Segoe UI Emoji`. The DailyBoard/DailyInput residue is 2 `âš ` + a `âœ“` setter â€” **BMP symbols, latent not confirmed-crash**.
4. **VM #1 scoped down.** The `ISmartInputService` ctor param is genuinely dead, but the DI registration must stay.
5. **Tests F10 dropped** â€” `GetByIdAsync` v9 coverage already exists at `TaskListRepositoryTests.cs:186-195`.

### Top issues called out explicitly:
- **B-2 (N+1 family)** â€” the highest-value item: same loop shape in **5 sites across 3 slices** (`RequestsViewModel:107`, `TaskListViewModel:144`, `TaskListArchiveService:170/130`, `TimeLogService:262`, `SmartInputPanelVm:98`), all collapsible to batch repo methods.
- **B-3 / B-4** â€” bare `catch` in `MainViewModel.SafeLoad:323` (masks fatal exceptions, same path as the multi-team SO crash); `async void` no-try/catch in `SettingsTab.xaml.cs:115,138`.
- **B-5 / B-6** â€” two real v9 data-loss-on-edge gaps: `TagRepository.DeleteAsync` skips `TaskTags`; the `note` column is written but never read back (`BacklogAuditEntry` has no `Note` â€” verified at `Entities.cs:40-42`).
- **B-1** â€” latent emoji residue.

### Refactor plan: 3 waves, safe-first
- **Wave A (mechanical, parallel-safe)** â€” 5 file-disjoint lanes: `DateHelpers`/`FormatHelpers` extraction (~250 LOC dup removed), `[RelayCommand]` cleanup, async hygiene, `TestAppConfig` dedup + the missing `UpdateStatusAsync`/`GetTagIdsForAllAsync` tests + schema-version pinning. Ends greener than baseline.
- **Wave B (targeted correctness)** â€” render/async/error-handling/data-loss, with explicit file-overlap sequencing (B-6 before B-2 on the repos; B-8+B-11 combined).
- **Wave C (structural â€” gated on USER APPROVAL)** â€” required-dep tightening, the `SettingsViewModel` god-object (recommend **declining** the full split), fake-async. Each flagged as opt-in.

Every refactor item lists files touched + a `dotnet test --filter`. Plan deliberately avoids rewriting working, tested code.
