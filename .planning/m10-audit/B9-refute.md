# B9 — Retention / Prune (DESTRUCTIVE, default OFF) — Adversarial Refutation

Read-only audit. No build, no test run, no DB access.

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| Manual Preview action — dry-run, itemized per-month counts, non-mutating | COVERED | No | COVERED | **WPF (dies):** `src/TimesheetApp/ViewModels/SettingsViewModel.cs:322-357`, button `src/TimesheetApp/Views/Tabs/SettingsTab.xaml:153-154`. **Algorithm (survives):** `src/TimesheetApp.Core/Services/IRetentionService.cs:6-20,30` + `RetentionService.cs:83`. **Web, full trace:** route `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:1002-1013` → TS models `src/timesheet-web/src/app/api/models/retention-preview.ts:5-7` + `retention-month-preview.ts:4` → generated fn `src/timesheet-web/src/app/api/fn/ops/ops-retention-preview.ts:30` → service `src/timesheet-web/src/app/services/worklog.service.ts:1209-1210` → component `src/timesheet-web/src/app/pages/settings/settings.component.ts:571-581` → template `settings.component.html:397-421` renders cutoff + all 5 counts + empty state. [VERIFIED] |
| Manual Run retention now — destructive, gated behind a prior preview + confirmation | COVERED | **Yes** | **PARTIAL** | Gating is covered and in fact **stricter** on web. But the **run-outcome status string is discarded with no replacement anywhere.** `src/TimesheetApp.Api/Endpoints/SettingsEndpoints.cs:1029` does `await retention.EnsureRetentionAsync();` and throws the return value away; only *exceptions* are logged (`:1030`). Four operator-facing outcomes are **non-exception returns**: `src/TimesheetApp.Core/Services/RetentionService.cs:91` (aborted — OneDrive conflict copies), `:104` (nothing to prune), `:129` (no month archived; nothing pruned), `:166` (`Compose(status, warnings)` — the skipped-month warnings). WPF displayed all of them: `src/TimesheetApp/ViewModels/SettingsViewModel.cs:373` → bound at `src/TimesheetApp/Views/Tabs/SettingsTab.xaml:158`. Web instead shows a fixed "Retention has STARTED" (`settings.component.ts:615-617`). [VERIFIED] |

## Claim 2 — why PARTIAL, precisely

What **is** covered (do not re-do this work):

- Route exists, admin-gated, returns 202 — `SettingsEndpoints.cs:1022-1039`.
- Same Core method invoked either way — `EnsureRetentionAsync()` at `SettingsEndpoints.cs:1029` vs `SettingsViewModel.cs:373`.
- Destructive button **does not render** until a preview has been taken — `settings.component.html:390-394`.
- Confirmation dialog is real and wired — `settings.component.ts:602-623`, template `settings.component.html:428-431`, handler `settings.component.ts:650-655` (`pending.run()`).
- Web is **stricter than WPF**: WPF's "Run retention now" button was always enabled with no preview prerequisite (`SettingsTab.xaml:155-156`); its only guard was a `MessageBox` confirm (`SettingsTab.xaml.cs:148-171`).

What is **lost**:

`IRetentionService.EnsureRetentionAsync` documents a partial-failure contract at `IRetentionService.cs:32-35` — *"Aborts the whole run if an OneDrive conflict copy is present; skips any month whose archive failed or whose snapshot is missing/zero. Returns a human-readable status."* Those guards do not throw; they **return a string**. On the web that string reaches no one — not the UI, not the log. An operator who runs retention and hits `RetentionService.cs:91` (aborted, nothing deleted) sees exactly the same "Retention has STARTED" as a fully successful run. The skipped-month warnings composed at `:166` vanish identically.

This is the classic narrower-web-version pattern: the happy path is covered, the documented edge-case reporting on a **destructive** operation is silently dropped. The auditor saw this and waved it through as "reporting differs but the destructive guarantee is unchanged". The destructive guarantee is indeed unchanged — but for a destructive op whose safety guards communicate *by return value*, discarding the return value is a behavior gap, not a formatting one.

Cheapest closure: log the returned status at `SettingsEndpoints.cs:1029` instead of discarding it. That alone downgrades the risk to cosmetic.

## Out-of-scope findings the section owner must not lose

Neither is one of the two claims I was assigned, but both sit inside B9 and both look like they may have been waved through elsewhere:

1. **Automatic opt-in retention run at startup — no web equivalent found.** WPF ran retention on launch when enabled: `src/TimesheetApp/App.xaml.cs:88-93` (`if (config.RetentionEnabled) … EnsureRetentionAsync()`). The API registers the service (`src/TimesheetApp.Api/Program.cs:114`) but a grep of `Program.cs` + `Infrastructure/*.cs` for `HostedService|BackgroundService|retention` returns **only that one registration line** — there is no scheduled or startup invoker. If B9 has a row for the automatic run, it is **MISSING**, not covered. [VERIFIED]
2. **Retention config (enable + months) has no web UI.** WPF: `SettingsTab.xaml:140-151` → `SettingsViewModel.cs:310-318` → `IAppConfig.SetRetentionEnabled/SetRetentionMonths` (`src/TimesheetApp.Core/Config/IAppConfig.cs:46-50`). Web explicitly disclaims it: *"The window itself is configured on the server"* (`settings.component.html:379-380`). Note `RetentionService` reads **only** `RetentionMonths` (`RetentionService.cs:60`) and never `RetentionEnabled` — so the enabled flag's sole consumer was `App.xaml.cs:91`, i.e. finding 1. The cutoff a web preview computes is therefore not adjustable from the web at all. [VERIFIED]
