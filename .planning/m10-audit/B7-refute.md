# B7 (Export / ExportHub / SharePoint) — Adversarial Refutation

**Method:** every claim traced end to end — Angular template → component → service → HTTP route → API endpoint → DI registration → Core method. Read-only; no build, no run, no DB access.

**Headline:** 8 of 9 claims survive, and they survive for the *strongest possible* reason — the behavior is Core-owned and cannot be lost by deleting `src/TimesheetApp/`. 1 claim is refuted down to PARTIAL.

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| `ExportService.ExportExcelAsync` — sheet "Timesheet", Team\|User\|Backlog\|Project\|Task\|Date\|Hours | COVERED | No | **CORE-SURVIVES** | Logic is Core: `TimesheetApp.Core/Services/ExportService.cs:80-115` (headers :86-92, ordering :95-100). WPF caller `TimesheetApp/ViewModels/ReportsViewModel.cs:179-187` dies; web replacement traced in full — button `reports.component.html:51-58` → `reports.component.ts:338-357` → `worklog.service.ts:694-699` → `TimesheetApp.Api/Endpoints/TimesheetEndpoints.cs:305-323` → same Core method. DI: `Program.cs:105`. [VERIFIED] |
| `ExportMarkdownAsync` — heading hierarchy, escape `\|`, hours "4" not "4.0" | COVERED | No | **CORE-SURVIVES** | Core: `ExportService.cs:29-78`, `EscapePipe` :135, `FormatHelpers.FormatHours` :70, group keys :129-132. Auditor's note verified true: grep of all of `src/TimesheetApp/` finds **no** timesheet-markdown download button (only `TaskListTab.xaml:148`, a different service). Real consumer is Core→Core: `ExportHubService.cs:125`. [VERIFIED] |
| ExportHub folder structure `{Team}/tasklist\|timesheet\|daily` + `db/` | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:104-145` (`tasklist` :110, `timesheet` :122, `daily` :134, `db` :145). Web reaches it: `settings.component.html:360` → `settings.component.ts:557-568` → `SettingsEndpoints.cs:1041-1049` → `ExportNowAsync`. DI: `Program.cs:112`. [VERIFIED] |
| 2 export roots (Root1 Shared/SharePoint + Root2 Local), mirrored; empty root skipped | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:50-53` — `.Where(r => !string.IsNullOrWhiteSpace(r))` is the empty-skip; `roots.Count == 0` → "no export root configured". Values resolve on the API: `Program.cs:34-36` (`new JsonAppConfig()`) + `Program.cs:49` (`AddSingleton(appConfig)`). [VERIFIED] |
| `ExportNowAsync` = this month + previous month, this week + previous week | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:44` → `RunAsync(backfillOnly:false)`; months `:159-163`, weeks `:178-182`. Admin gate present on web: `SettingsEndpoints.cs:1043` (`ctx.IsAdmin` → 403) + `:1046` `RequireAuthorization(AdminPolicy)`. [VERIFIED] |
| No data for a period → no file written | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:114` (`md is null`), `:126` (`!FormatHelpers.HasTimesheetData(md)` — the timesheet case needs its own check because `ExportService` always emits a header), `:138`. `Directory.CreateDirectory` is deliberately *after* each guard, so no empty dirs either. [VERIFIED] |
| Per-root best-effort: one root failing does not block the other | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:72-82` — try/catch inside the `foreach (var root in roots)`, `continue`-on-Error at `:66-70`. Status string returned to web verbatim at `SettingsEndpoints.cs:1044` and rendered `settings.component.ts:563`. [VERIFIED] |
| Dedupe segment (I-1): colliding sanitized team names get `-{teamId}` | COVERED | No | **CORE-SURVIVES** | Core: `ExportHubService.cs:94-103` — `HashSet<string>(OrdinalIgnoreCase)`, `if (!usedSegments.Add(segment)) segment = $"{segment}-{team.Id}"`. `IPathSanitizer` registered on API: `Program.cs:102`. [VERIFIED] |
| `SharePointDestinationValidator` rule table (Error / Ok / Warning) | COVERED | **YES** | **PARTIAL** | Rule *code* is Core and does run on web — `SharePointDestinationValidator.cs:15-49`, registered `Program.cs:106` (so the optional `_spValidator` param at `ExportHubService.cs:31` resolves non-null; that attack failed). **But only one of the three levels is observable on web.** `ExportHubService.cs:66` inspects `is { Level: DestinationLevel.Error }` and nothing else — the `Ok` message (`:43-44`) and the `Warning` message (`:46-48`) are computed and discarded. WPF rendered all three, colour-coded: `SettingsTab.xaml:52-72` (DataTriggers `Ok`/`Warning`/`Error` at `:60,:63,:66`) driven by `SettingsViewModel.cs:220-231` — **both files die in M10**. No API route exposes `Verify`: grep of `TimesheetApp.Api` for `SharePointDestinationValidator` returns only the DI line. [VERIFIED] |

## The one refutation, stated precisely

The auditor wrote *"The rule logic and its enforcement run identically on web."* That is true for **Error** and vacuous for the other two.

- **Error** — genuinely covered. `ExportHubService.cs:66-70` skips the root and pushes `check.Message` into the status string, which reaches the browser via `SettingsEndpoints.cs:1044` → `settings.component.ts:563`.
- **Ok / Warning** — the validator produces them, and **nothing on the web ever reads them**. `Warning` in particular carried real operational meaning: *"Writable, but this looks like a local folder — files stay on this PC and will NOT reach SharePoint"* (`SharePointDestinationValidator.cs:46-48`). A misconfigured plain-local root exports successfully and reports `ok: {root}` on the web. The admin is never told the files are not reaching SharePoint.

This is not the same thing as the "interactive Verify UI" row the auditor carved out. That row is about *pre-emptive* checking. This is about the **post-hoc** result too: even after running Export now, the Warning verdict is unobservable. Marking the rule table COVERED would tell a future reader that all three branches survive as observable behavior. They do not.

**Severity: low-to-moderate, not data loss.** No export stops working and no file lands in the wrong place — the Error block still fires. What is lost is a diagnostic that the exports are silently going nowhere useful.

## Narrowing checks that came back clean

Attacks attempted that the code defeated — recorded so nobody re-runs them:

- **`_spValidator` null on the API?** No. `Program.cs:106` registers it; the `= null` default at `ExportHubService.cs:31` is only for older tests.
- **Web Excel export drops the project / user / team filter?** No. `worklog.service.ts:719-725` sends `userId`, `project`, and one appended `teamIds` entry per team — matching `ExportFilter` at `TimesheetEndpoints.cs:310`.
- **Web loses the team scoping?** No — it is *tighter*. `EffectiveTeamIds` (`TimesheetEndpoints.cs:427-439`) intersects client ids with `ctx.MemberTeamIds`; the component additionally disables the button on `teamEmpty()` (`reports.component.ts:339`, `reports.component.html:52`). WPF had no such guard.
- **Web loses the suggested filename?** No. `report-model.ts:124-130` reproduces `SuggestedExportFileName()` including the `team` fallback and invalid-char stripping.
- **Export-now unauthenticated on web?** No. Admin-gated twice (`SettingsEndpoints.cs:1043` + `:1046`).
- **Export roots unresolvable on the API?** No. `JsonAppConfig` default ctor reads the same app-local config the WPF app used (`Program.cs:34-36`).

## Adjacent fact worth recording (not a loss)

`worklog.service.ts:702-707` `exportMarkdown()` has **no component caller** — only two spec references (`worklog.service.spec.ts:890,916`). `GET /api/export/markdown` is therefore reachable only by typing the URL. This is *not* a regression: WPF exposed no equivalent button either. Flagging it so a future reader does not mistake the dead method for live coverage of something.

Note also: in WPF the pre-emptive **Verify button existed only on Root1** (`SettingsTab.xaml:48`); Root2 had Browse + Apply only (`SettingsTab.xaml:74-79`). The interactive-Verify loss is half as wide as it first appears.
