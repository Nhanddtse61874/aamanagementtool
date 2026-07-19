# M10 — WPF Coverage Audit

**Verdict: DO NOT DELETE YET**

This document is the consolidated result of a 22-section, two-pass audit of every behavior in `src/TimesheetApp/` (the WPF app) against the web stack (`TimesheetApp.Api` + `timesheet-web`). Each section was inventoried by one agent and then attacked by a second agent whose only job was to break the COVERED claims; where the two disagree, the refuter's verdict is recorded, because the refuter read the code a second time adversarially. **After M10 there is no running WPF app left to compare against.** Every behavior that dies is only recoverable from git history, and nobody will get a bug report for a feature that silently stopped existing — this document becomes the only written record of what the desktop app did. Read the 🔴 MISSING table before deciding; the verdict rests on it.

---

## The answer in one table

| Section | Features | COVERED | PARTIAL | MISSING | CORE-SURVIVES |
|---|---:|---:|---:|---:|---:|
| A1 — Shell (`MainWindow.xaml`) | 12 | 4 | 1 | **7** | 0 |
| A2 — Log Work (Timesheet) | 27 | 10 | 7 | **7** | 3 |
| A3 — Backlog (`RequestsTab`) | 27 | 21 | 3 | **2** | 1 |
| A4 — Task List | 37 | 29 | 4 | **3** | 1 |
| A5 — Daily Report | 25 | 14 | 5 | **2** | 4 |
| A6 — Reports | 11 | 7 | 1 | 0 | 3 |
| A7 — Users | 10 | 3 | 2 | 0 | 5 |
| A8 — Settings | 35 | 19 | 7 | **3** | 6 |
| A9 — Dialogs (8) | 14 | 4 | 4 | **5** | 1 |
| A10 — Controls | 10 | 8 | 1 | **1** | 0 |
| B1 — Smart Input / Smart Fill | 10 | 1 | 2 | **4** | 3 |
| B2 — TimeLog core rules (DayCap 8h) | 9 | 0 | 0 | 0 | 9 |
| B3 — Multi-team + R6 anti-leak | 15 | 7 | 2 | **1** | 5 |
| B4 — Standup business rules | 10 | 0 | 0 | 0 | 10 |
| B5 — Task List business rules | 19 | 0 | 2 | 0 | 17 |
| B7 — Export / ExportHub / SharePoint | 12 | 0 | 1 | **2** | 9 |
| B8 — Backup (two services) | 12 | 0 | 5 | **3** | 4 |
| B9 — Retention / Prune | 13 | 1 | 2 | **1** | 9 |
| B10 — Theme (light/dark) | 5 | 2 | 3 | 0 | 0 |
| B11 — CurrentUserService / identity | 5 | 1 | 1 | **2** | 1 |
| C — Schema / migrations / connection | 18 | 0 | 0 | 0 | 18 |
| D — WPF/Windows dependencies | 33 | 17 | 4 | 0 | 12 |
| **TOTAL** | **369** | **148** | **57** | **43** | **121** |

The 43 MISSING rows collapse to **29 distinct behaviors** (the active-team switcher alone accounts for 4 rows across A1/A10/B3; Smart Fill's gaps are counted in both A9 and B1). The deduplicated list is below.

All 22 section files were present and read. `B6` does not exist — `.planning/M8-FEATURE-INVENTORY.md` jumps B5 → B7, so nothing is absent. Three sections (`B2`, `B4`, `C`) have no `-refute.md`; correctly so — every row in them is CORE-SURVIVES, so there were no COVERED claims to attack.

---

## 🔴 MISSING — behavior that dies with the deletion and has no web equivalent

**This table is not empty. It has 29 rows.** Nothing here has been dispositioned by a human yet, which is why the verdict is not "safe to delete".

| Feature | Section | Lives in (WPF file:line) | Why it matters | Suggested disposition |
|---|---|---|---|---|
| **Active-team switcher** — pick your working team | A1, A10, B3 | `MainWindow.xaml:59-69` + `ViewModels/MainViewModel.cs:118,127-133` | A multi-team user has **no way to change their active team on the web at all**. The web `<select>` at `sidebar.component.html:45-52` is hard-coded mockup markup — two literal `<option>`s, no binding, no `(change)`. `WorklogService.setActiveTeam()` (`worklog.service.ts:1239`) has zero production callers (verified: only `worklog.service.spec.ts:736`). The web code says so itself at `team-filter.component.ts:142`. | **Port before deleting** |
| Active-team persistence + resolution-on-load | A1, B3 | `MainViewModel.cs:131-132` → `ICurrentTeamService.SetActiveTeamAsync` → `Users.active_team_id` | Server plumbing exists (`PUT /api/me/active-team`, `ApiCurrentTeamService.cs:75-84`) but is unreachable. Also: `ApiCurrentTeamService.InitializeAsync:66-69` computes the fallback but never writes it back, so a stale `active_team_id` snaps the user back when a deactivated team is reactivated. | Port with the switcher |
| Team switch resets every screen's TeamFilter | A1, B3 | `MainViewModel.cs:90-93`; `TeamFilterViewModel.cs:96-102` | The consuming half exists and is unit-tested (`team-filter.component.ts:146-148 reload()`, reserved for "whoever wires that switcher"); the producing half does not. | Port with the switcher |
| Team-switcher visibility rule (hidden at 0 teams, read-only at 1, switcher at 2+) | A1 | `MainViewModel.cs:120-123` | Distinct conditional-visibility rule that must be re-implemented even once the switcher is wired. | Port with the switcher |
| **TeamFilter toolbar on the Backlog screen** | A3, A10, B3 | `ViewModels/RequestsViewModel.cs:41-49,91-92`; `Views/Tabs/RequestsTab.xaml:50-52` | `backlog.component.html` has four dropdowns and no team control; `getBacklogList()` (`worklog.service.ts:463-465`) sends no `teamIds`, so `BacklogEndpoints.cs:571-574` defaults to **every** team the user belongs to — *broader* than WPF's default. Cannot narrow to one team. Not a leak (server still scopes to membership), but a real affordance loss. | Port before deleting |
| TEAM pill column on the Backlog grid | A3, A10 | `RequestsViewModel.cs:53-55,99-102`; `RequestsTab.xaml:80-93` | With >1 team in view, no web user can tell which team owns a backlog row. `teamId` is already on the DTO (`backlog-list-item-dto.ts:11`), just never rendered. | Port before deleting |
| Banner: OneDrive conflict-copy warning (XC-08) | A1 | `MainWindow.xaml:120-131` ← `MainViewModel.cs:289-296` ← `SqliteMaintenance.FindConflictCopies` | Detection survives in Core and still aborts a retention run (`RetentionService.cs:88-91`), but **nothing surfaces it to any client** — no endpoint, no component. A silent data-risk condition becomes invisible. | Accept the loss, with a reason — a single server-hosted DB reduces (does not eliminate) the scenario. Record it. |
| Banner: journal-integrity warning + Dismiss (XC-09) | A1, B1, B8 | `MainWindow.xaml:138-151` + `MainViewModel.cs:187-193`; wired `App.xaml.cs:115-117` | API registers `TraceJournalWarningSink` (`Program.cs:79`) — trace log only, no response field, no banner. The warning now reaches a server log and nobody else. | Accept the loss, with a reason — under `SqliteProfile.Server`'s WAL mode the `<db>-journal` file is never created (`SqliteConnectionFactory.cs:58-65`), so the guard is structurally inert on the web anyway. Record it. |
| Window chrome (1180×760, min 980×620, CenterScreen) | A1 | `MainWindow.xaml:6,8` | No `min-width` layout guard exists on the web shell. | Already obsolete — browsers own window chrome. |
| **Log Work: jump-to-any-week DatePicker** | A2 | `TimesheetViewModel.cs:95-106`; `TimesheetTab.xaml:138-139` | Web has Prev/Next/This-week only (`log-work.component.ts:278-280`). Reaching a week N away costs N clicks. | Port before deleting — small |
| **Log Work: entry-target user picker** ("Whole team (read-only)" + any individual user's grid) | A2 | `TimesheetViewModel.cs:110-131,176-189`; `TimesheetTab.xaml:173-175` | Genuine capability loss, and the API **deliberately refuses to support it**: `TimesheetEndpoints.cs:56-62` — "Deliberately does NOT accept a userId query param: unlike WPF's EntryTarget picker, the web API never lets one user view another named user's individual grid." | Accept the loss, with a reason — this is an intentional privacy narrowing. Get it signed off, do not let it pass silently. |
| Log Work: read-only team-view mode + amber "Read-only (whole team)" badge | A2 | `TimesheetViewModel.cs:130-132,371,446,452`; `TimesheetTab.xaml:180-185` | Direct consequence of the row above. | Accept with the row above |
| **Log Work: Month/Year filter on the grid** | A2 | `TimesheetViewModel.cs:115-124,168-172,290-293`; `TimesheetTab.xaml:176-179` | Web shows every backlog group regardless of period — no server-side or client-side `PeriodMonth` filter exists on this screen. On a long backlog list this is a real usability loss. | Port before deleting |
| **Log Work: holiday day-column rendering** (grey fill, "Holiday" watermark, read-only cell, tooltip) | A2, B5 | `TimesheetTab.xaml:29-36,46-65,342-376`; `TimesheetViewModel.cs:134-157` | Zero matches for "holiday" in `log-work.component.{ts,html,scss}`. The **rule** still holds server-side (400 + toast), so this is not a data-integrity gap — but the user now discovers a holiday only by typing into it and being refused. | Port before deleting — low cost, high visibility |
| Log Work: footer day-total >8h turns red + bold | A2 | `TimesheetTab.xaml:8-17,204-222` + `Views/Converters/OverEightTagConverter.cs:6` | The only over-cap visual warning on the grid. Web footer has `[class.zero]` and nothing else. | Port before deleting — trivial |
| **Task List: PROJECT label on the card when grouped by Team** | A4 | `TaskListTab.xaml:298-302` (bound to `Data.GroupByProject` inverted) | With ≥2 teams checked, **no web user can see which project any backlog belongs to**. The comment at `task-list.component.html:126` claims "the band above already shows it" — false in team mode. Data is on the wire (`Dtos.cs:146`) and unused. | Port before deleting — one template line |
| Task List: Gantt "No dated backlogs to chart for this month." caption | A4 | `TaskListTab.xaml.cs:221-225,377-388` | When every backlog is undated the web renders a silent empty card. The existing "Nothing here" branch (`task-list.component.html:68-72`) fires on a *different* condition (zero rows). | Port before deleting — trivial |
| **Task List: on-disk monthly markdown archive + startup backfill** | A4 | `TaskListViewModel.cs:333` → `TaskListArchiveService.ExportMonthAsync`; `App.xaml.cs:104` → `BackfillMissingMonthsAsync` | **Verified: zero API callers for either method.** The web route deliberately calls `BuildMonthMarkdownAsync` and returns content only (`TaskListEndpoints.cs:87-96`). Deleting WPF ends the accumulating on-disk archive and its backfill of completed months. Scope also differs: WPF exported all teams, web is caller-scoped. (The A4 refuter labelled the parent claim PARTIAL because the download button *is* covered; the archive sub-behavior itself has no web equivalent, so it is recorded here.) | Port as a hosted service, or accept with a reason |
| **Daily Report: startup backfill of missing weekly archives (DR-09)** | A5 | `App.xaml.cs:100` → `IStandupArchiveService.BackfillMissingWeeksAsync` | **Verified: the WPF startup is the only caller in the repo.** Already self-documented at `AdminEndpoints.cs:16-21`: "`Program.cs` never calls `BackfillMissingWeeksAsync`… so on a web-only deployment nothing ever wrote it." Any week nobody manually archives never gets a snapshot, silently, forever. | Port as a hosted service before deleting |
| **Daily Board: issue SOLUTION TEXT** | A5 | `Views/Tabs/DailyBoardTab.xaml:45-48` (`Text="{Binding SolutionText}"` when `HasSolution`) | On the team board you can now see *that* an issue was resolved (green ✓) but never *how*. The data is already on the wire (`Dtos.cs:106` `StandupIssueDto.SolutionText`) and the Input tab consumes it — pure template omission. | Port before deleting — one template line, highest value-per-effort in the audit |
| **Smart Fill: backlog-code search → multi-select task checklist** | A9, B1 | `ViewModels/SmartInputPanelVm.cs:83-116` | Web Smart Fill can only target tasks **already on the visible week's grid** (`log-work.component.ts:483-484`). A user cannot search-then-fill a task that isn't on their timesheet yet. | Port before deleting, or accept as a deliberate narrowing |
| **Smart Fill: multi-task one-shot fill** (one total split across N checked tasks) | A9, B1 | `SmartInputPanelVm.cs:26-27,126-127` | `sfTaskId` is a single `number` (`log-work.component.ts:185`). The wire format *does* accept an array (`TimesheetEndpoints.cs:461`) and Core still implements the tier-1/tier-2 split (`SmartInputService.cs:75-127`) — no UI reaches it. | Port or accept |
| **Smart Fill: "Full 8h" mode** | A9, B1 | `SmartInputPanelVm.cs:60,151-152` → `SmartInputService.cs:117-122` | Zero matches for `Full8h` anywhere in `timesheet-web`. The Core branch survives as **unreachable code**. | Port or accept |
| **Smart Fill: arbitrary From/To date range** | A9, B1 | `SmartInputPanelVm.cs:66-67` | Web is hard-capped to the currently displayed Mon–Fri week (`log-work.component.ts:463`). Multi-week backfill via Smart Fill is gone. | Port or accept |
| **Smart Fill: explicit Preview-then-Confirm gate** | A9, B1 | `SmartInputPanelVm.cs:118-149` + `Views/Dialogs/SmartInputPreviewDialog.xaml:42-46,114-128` | Web applies straight from the form (`log-work.component.ts:494-525`); a 400 toast after the fact is the only validation. `smartFillValidate` is wired in the service layer (`worklog.service.ts:304-307`) but **no component calls it** — dead client code. | Port or accept |
| **🔴 Settings: backup RESTORE (BK-05) + self-restore guard** | A8, B8, D6 | `ViewModels/SettingsViewModel.cs:292-305`; `Views/Tabs/SettingsTab.xaml:121-124`; `SettingsTab.xaml.cs:115-145` | **Verified: no route, no DTO, no UI anywhere on the web calls `RestoreAsync`.** Deliberately excluded — `SettingsEndpoints.cs:43-44`: "`IBackupService.RestoreAsync` is NOT exposed in M8.3: it overwrites the live .db in place while the API holds open connections, which corrupts live readers." The exclusion is correct engineering, but **no replacement mechanism (admin CLI, maintenance script, runbook) exists anywhere in the repo.** Deleting WPF deletes the only working restore path in the product. | **Port a replacement (offline CLI or documented runbook) before deleting** |
| Settings: backup list — "Refresh" + display of existing backups | A8, B8 | `SettingsViewModel.cs:283-288`; `SettingsTab.xaml:110-134` | **Verified: `ListBackups()` has exactly one caller and it is the WPF VM.** From the browser you can trigger a backup but never see what backups exist, when, or how big. Compounds the "Backup now lies on failure" PARTIAL below. | Port before deleting — read-only route + list |
| **Backup: once-per-day auto-backup trigger (BK-03)** | B8 | `App.xaml.cs:63` → `IBackupService.AutoBackupIfDueAsync()` | **Verified: only caller in the repo. No `IHostedService`/`BackgroundService` is registered anywhere in `TimesheetApp.Api` (grep returns nothing).** The automatic daily safety net silently stops the day WPF is deleted. The `AutoBackupEnabled` flag becomes both unreachable and inert. | Port as a hosted service before deleting |
| Settings: SharePoint destination Verify button (SP-01) | A8, B7 | `SettingsViewModel.cs:217-232`; `SettingsTab.xaml:52-72` | **Verified: `ISharePointDestinationValidator` appears in `TimesheetApp.Api` only as a DI line (`Program.cs:106`)** — no route exposes `Verify`. Confirming a destination is reachable *before* relying on it has no surviving path. Partly moot (root paths are host config now), and Root2 never had a Verify button. | Accept the loss, with a reason — but fix the related PARTIAL (Ok/Warning levels discarded) |
| **ExportHub: 12-month / 12-week catch-up backfill** | B7 | `App.xaml.cs:83` → `ExportHubService.BackfillAsync()` | **Verified: registered in DI (`Program.cs:112`), zero call sites in the API.** Only "Export now" (this + previous month/week) remains; it does not reach back 12 periods and has no skip-if-exists catch-up. A server with a newly configured export root, or one that had a downtime gap, never self-heals. | Port as a hosted service before deleting |
| **Retention: automatic unattended run at startup** when `RetentionEnabled` | B9 | `App.xaml.cs:88-93` → `IRetentionService.EnsureRetentionAsync()` | **Verified: no scheduler exists.** Retention now fires only when an admin remembers to click. Note `RetentionService` never reads `RetentionEnabled` (`RetentionService.cs:60` reads only `RetentionMonths`) — `App.xaml.cs:91` was the flag's **sole consumer**, so the toggle becomes meaningless as well as unreachable. | Accept the loss, with a reason (retention is default-OFF and destructive; manual-only may be *safer*) — but record it deliberately |
| **Identity: auto-provision an unmapped user on first run** | B11 | `MainViewModel.cs:267-287` | Self-service "walk up to any PC and be a usable account" is gone. Web creation is a manual 3-step admin flow (`pages/users/user-create.ts`). `AuthSetup.cs:162-181` returns `Unauthorized()` with no auto-create path. | Accept the loss, with a reason — this is the intended security-model change. Record it. |
| **Identity: first-run auto-join to the active team (TM-09)** | B11 | `MainViewModel.cs:200-224` (`InitializeActiveTeamAsync` → `Teams.AddMemberAsync`) | A freshly created web user is a member of **zero** teams until an admin adds them. `ClientContextFilter.cs:10-15` documents that these calls have no other caller in the API. | Accept the loss, with a reason — record the admin onboarding step |

---

## 🟠 PARTIAL — web covers some of it; state exactly what is dropped

57 rows; the ones that change a decision are below. Two of these are more dangerous than most of the MISSING list.

| Feature | Section | Lives in (WPF file:line) | Exactly what is dropped |
|---|---|---|---|
| **🔴 Log Work: clearing a cell = DELETE** | A2 | `ViewModels/TimesheetRowVm.cs:31-59` (`INotifyDataErrorInfo`); `TimesheetViewModel.cs:372-376` refuses the write | **Silent data loss on a typo.** Web `parseHours` maps *any unparseable text* to `null` (`grid-state.ts:197-202`) and `commitCell` routes `null` → `clearCell` (`log-work.component.ts:301`). Typing `abc` over a cell holding 4h **deletes the 4h**. WPF flags the cell and refuses. |
| **🔴 Settings: "Backup now" reports a backup that did not happen as success** | A8, B8 | `SettingsViewModel.cs:275-279` branches on the `null` and says *"No backup made — choose a folder and make sure the database file exists."* | `BackupService.BackupNowAsync()` returns `null` (not an exception) when the folder or DB path is blank (`BackupService.cs:35-44`). The null survives the wire (`SettingsOpsResult(string? Value)`, `SettingsEndpoints.cs:1159`) and the client coalesces it away: `` `Backup written to ${r.value ?? 'the configured folder'}` `` + toast `'Backup complete'` (`settings.component.ts:550-551`). `JsonAppConfig.cs:59` defaults `BackupFolderPath` to `""`, so a server omitting it says "Backup complete" **every time**. ~1 line of Angular to fix. |
| **Settings: "Export now" reports failure as success** | A8 | `SettingsViewModel.cs:248,252` shows the verbatim per-root report | `ExportHubService.RunAsync` returns `"no export root configured"` / `"ok: {root}"` / `"failed: {root} — {msg}"`. Client renders `` `Export written to ${r.value}` `` + toast `'Export complete'` (`settings.component.ts:563-564`), in a div with no `pre-wrap` so a two-root report collapses to one line. |
| **Settings: "Run retention now" has no outcome channel** | A8, B9 | `SettingsViewModel.cs:373` binds the result string; `SettingsTab.xaml:158` renders it | `SettingsEndpoints.cs:1029` awaits `EnsureRetentionAsync()` and **discards the return value**; only exceptions are logged. Four operator-facing outcomes are non-exception returns: aborted-on-conflict-copies (`RetentionService.cs:91`), nothing-to-prune (`:104`), no-month-archived (`:129`), skipped-month warnings (`:166`). On a destructive op, an aborted run and a successful run look identical. Cheapest fix: log the string at `:1029`. |
| **Settings: template editor validation is a silent no-op** | A8 | `SettingsViewModel.cs:409-419` + `ViewModels/TemplateEditorViewModel.cs:23-25`, whose comment says this behavior **"was a silent no-op"** and was fixed | `settings.component.ts:341` and `:345` both `return` silently; Save is `[disabled]="busy()"` only. A blank name or empty task list gives no message and no effect — **the web reintroduces the exact defect the WPF VM was changed to fix.** Also lost: structured task rows with Move up/down/Remove + Add-task dialog (`SettingsTab.xaml:501-529`) → one free-text `<textarea>`. |
| **Auth cutover: existing non-admin users are locked out** | B11 | WPF grants access with **no credential at all** — `CurrentUserService.cs:14`, `ResolveAsync():25-37` | `DatabaseInitializer.cs:331` adds `password_hash TEXT` with **no default and no backfill**; `:337` promotes only `MIN(id)` to admin. `AuthSetup.cs:176-177` hard-401s a NULL hash. `AuthEndpoints.cs:91-93` blocks self-recovery ("Ask an administrator"). No bulk provisioning path exists — `SetPasswordHashAsync` has exactly two callers, both one-user-at-a-time. **This is an operational blocker, not a code gap.** |
| Admin claim staleness | B11 | n/a (WPF had no roles) | DB-fresh `ctx.IsAdmin` re-checking exists on **7 routes only** (`SettingsEndpoints.cs:1004,1025,1043,1053`; `AdminEndpoints.cs:60,104,138`). Every other admin route — including team create/rename/deactivate and **admin password-reset** (`AuthEndpoints.cs:116-131`, a full account-takeover primitive) — trusts a claim frozen into a 30-day cookie. `AdminEndpoints.cs:23` documents this as a known open risk. |
| Task List has **no realtime subscription** | D | `ViewModels/TaskListViewModel.cs:79-87` subscribes to `Backlogs/Tasks/Logs/Tags/Holidays/PcaContacts` | `task-list.component.ts:68-69` injects only `WorklogService` + `ToastService`; `RealtimeService` is absent. Four other screens subscribe; the Task List does not refresh until manually reloaded. Transport is fine — this is one missing injection. |
| Task List: task-row assignee has no deactivated-user fallback | A4 | `TaskListViewModel.cs:189,200-201,680` (built from `GetAllAsync()`) | `task-list.component.html:260-264` iterates `getUsersActive()`. A task assigned to a departed person renders an **empty dropdown** — the owner is invisible. The helper written for this (`assigneeName()`, `component.ts:352-356`) is never called from the template. The *backlog* row does it correctly (`model.ts:305-315`), so the fix pattern already exists. |
| Task List: section bands no longer collapsible | A4 | `TaskListTab.xaml:207-219` (`<Expander IsExpanded="True">`) | Web band is a static `<div class="tl-band">` with no toggle and no per-band state anywhere in the component. |
| Task List: Gantt "unknown start" ghost bar | A4 | `TaskListTab.xaml.cs:323-335` draws it **full-width**, dashed, opacity .55 | Web draws it at the server's own position/span (`task-list.component.html:94-99`), which for a null start date is a **1-day sliver at the deadline** — reads as "runs exactly one day", not "we don't know when it starts". |
| Task List: export-this-month scope + feedback | A4 | `TaskListViewModel.cs:333` (all teams, `teamIds: null`) | Web export is caller-team-scoped, and the persistent status label became a transient toast. (The on-disk archive half is in the MISSING table.) |
| Backlog: template picker hidden on EDIT | A3 | `RequestsTab.xaml:356-362` — the template `ComboBox` has **no** `IsEditMode` trigger and is fully functional on edit (`RequestEditorViewModel.cs:234-248` + `RequestsViewModel.cs:244-245`) | `backlog-editor.component.html:166` gates it `@if (!isEdit() …)`. **Lost capability: applying a task template to an existing backlog.** The audit's "(create only)" premise was false on the WPF side. |
| Backlog: Progress% rule is inverted, not ported | A3, B5 | `RequestsTab.xaml:303-307` — `IsEnabled="False"`, *"Progress is read-only data (computed elsewhere): disabled on the create form so it can't be filled"* | `backlog-editor.component.html:108-110` is a plain **enabled** input whose value reaches the create body (`backlog-form.ts:124`, pinned by `backlog-form.spec.ts:130`). Deliberate (`backlog-form.ts:44-47`), but after deletion `RequestsTab.xaml:306` is the last artifact stating the original rule. Separately, `backlog-form.ts:73-75` has **no integer check** where `RequestEditorViewModel.cs:127` used `int.TryParse` — `50.5` passes client validation and 400s on JSON binding. |
| Log Work: auto-save "Saving… / ✓ Saved" indicator | A2 | `TimesheetViewModel.cs:354-383`; `TimesheetTab.xaml:141-159` — the element that replaced the Save button | Errors ARE surfaced (toast). There is **no affirmative success indicator** anywhere; grep for Saving/Saved across `src/app` returns only the `SavedBody` DTO. |
| Log Work: collapse-all not persisted | A2 | `TimesheetViewModel.cs:242-261` writes settings key `entry.collapseAll` | Web `collapseAll()` (`log-work.component.ts:854-862`) is in-memory only — every browser refresh starts fully expanded. No `localStorage` or settings call in `pages/log-work/`. |
| Log Work: trash zone is not pinned | A2 | `TimesheetTab.xaml:258-265` — `DockPanel.Dock="Bottom"`, **outside** the ScrollViewer | Web trash is the last child *inside* `.grid-scroll` with plain margin and no `position:sticky` (`log-work.component.scss:80,90`). It scrolls away. |
| Log Work: client-side pre-validation + inline red cell | A2 | `TimesheetRowVm.cs:46-59`; `TimesheetTab.xaml:347` | Rules survive in Core, but the web always attempts the PUT and reverts + toasts on 400. No invalid-cell styling exists in `log-work.component.scss`. |
| Log Work: section band `PeriodMonth` text | A2 | `TimesheetTab.xaml:299` | Not rendered on the web grid — the ticket's assigned month is invisible. |
| Daily Report: date-jump picker | A5 | `MainWindow.xaml:222` — a `<DatePicker>` sitting **between** the two cited Prev/Next buttons | Web toolbar has a read-only label (`daily-report.component.html:10`). Viewing a board two months back costs ~60 clicks. |
| Daily Report: Archive week — who and where | A5 | `MainWindow.xaml:225-226` — **no admin gate**, any user could click; writes to the **user's own disk** (`StandupArchiveService.cs:62-66`) | Web is admin-only (`AdminEndpoints.cs:150`) and writes on the **API host**, returning a path the browser cannot open. Both narrowings may be intended — decide deliberately. |
| Daily Report: quick-import source-day picker | A5, A9 | `Views/Dialogs/QuickImportDialog.xaml:29-30` + `.xaml.cs:13,20-27` | Web hard-codes `const source = addDays(target, -1)` (`daily-report.component.ts:345-346`). "Import last Friday into Monday" — the Monday-standup case — is unreachable. The endpoint already takes `sourceDate`; only UI is missing. |
| Daily Report: issue create can't set solution/status | A5, A9 | `Views/Dialogs/StandupIssueDialog.xaml:26-35` + `.xaml.cs:29-31` — three fields, all written | Web renders one input and hard-codes `{ solutionText: null, status: 'open' }` (`daily-report.component.ts:429-431`). Recoverable in a second edit step. |
| Daily Report: multi-line description | A5 | `StandupEntryDialog.xaml:63-64` (`AcceptsReturn`, `MinHeight=56`) | Web uses a single-line `<input>` (`daily-report.component.html:264`). |
| Reports: missing-logs banner has no height cap | A6 | `ReportsTab.xaml:32` — `ScrollViewer MaxHeight="96"` | `.missing__list` has no `max-height`/`overflow`. With many missing users the banner pushes the filter bar and stat cards below the fold. One-line SCSS fix. |
| Users: name-only user creation is unreachable | A7 | `UsersViewModel.cs:34-43` — adds on **name alone** | `users.component.ts:167-172` requires name **and** username **and** password; Create is disabled otherwise. The API still accepts name-only (`SettingsEndpoints.cs:461-476`) but has no caller. Confirm nobody needs a roster entry for a person who never logs in. |
| Avatar colour is not a hash | A1, A7 | `Views/Converters/AvatarBrushConverter.cs:13-38` — char-sum over a 5-colour palette, stable for **any** name | Sidebar hardcodes one colour for everyone (`sidebar.component.html:55`). The Users page uses `worklog.service.ts:192-200`, a **12-name hardcoded lookup** with a single `#0E7C66` fallback — every user created from now on renders the same green. |
| Settings: dark mode is admin-only on the web | A8 | `SettingsTab.xaml:9-11`, reachable by **every** WPF user | `/settings` is `adminGuard`-gated (`app.routes.ts:57-60`) and `theme.service.ts:37` states the service is injected only there. A non-admin cannot change theme at all. Almost certainly unintended — this gate protects a per-user cosmetic preference. |
| Theme: the dark palette is incomplete | B10 | `Views/Theme/Palette.Dark.xaml:30-58` — 39 keys incl. `DangerSoft`, `BadgeGreenBg`, `AmberBg`, `HolidayBg` | `.dark` overrides only 13 of 22 tokens (`styles.scss:30-43`) and 23 hardcoded light hexes sit in 5 component stylesheets (`reports.component.scss:5-26`, `settings.component.scss:69,73`, `task-list.component.scss:89,130`, `log-work.component.scss:80-81`, `sidebar.component.scss:36,38`). **`Palette.Dark.xaml` is the only artifact recording the intended dark values — port them out before deleting it.** |
| SharePoint validator: only `Error` is observable | B7 | `SettingsTab.xaml:52-72` renders all three levels colour-coded, driven by `SettingsViewModel.cs:220-231` | `ExportHubService.cs:66` inspects `Level: Error` and nothing else. The `Warning` message — *"Writable, but this looks like a local folder — files stay on this PC and will NOT reach SharePoint"* (`SharePointDestinationValidator.cs:46-48`) — is computed and discarded. A misconfigured local root exports and reports `ok:`. |
| Host config is no longer editable in-app | A8, B8, B9, D | `SettingsViewModel.cs:188-215,259-269,310-318` (DB path, archive path, both export roots, backup folder, auto-backup, keep count, retention enable/months) | Deliberate and documented (`settings.component.ts:40-67`, `SettingsEndpoints.cs:46`) — these are process-wide server settings now. **But the pointer is wrong**: the UI says "appsettings.json on the host" while `TimesheetApp.Api` has **no** `appsettings.json` (`Program.cs:53`); paths resolve via `JsonAppConfig` defaults at `%APPDATA%\TimesheetApp\appsettings.json`. Fix the copy before an operator edits the wrong file. |
| Dialog chrome + keyboard contract | A9 | Every WPF dialog has a tinted header strip and footer bar, and `IsCancel`/`IsDefault` on Cancel/OK — so Esc cancels and Enter confirms **everywhere** | No web dialog has header/footer strips. Only `add-task-dialog` and `confirm-dialog` bind Escape; `daily-report.component.html` has zero keydown bindings, and the deadline modal has neither Esc nor backdrop-click-to-close. |
| Multi-team: fallback active team is not persisted | B3 | `CurrentTeamService.cs:101-102` writes `SetActiveTeamIdAsync(userId, newId)` | `ApiCurrentTeamService.InitializeAsync:66-69` computes the same fallback and never writes it back. Masked by per-request resolution — until a deactivated team is reactivated, when the API snaps the user back to it. |
| No server-side paging | D | Architectural, pre-existing | Confirmed no `Skip`/`Take`/`page` in any `TimesheetApp.Api/Endpoints/*.cs`. **Not a regression** — equally true before and after. N+1 *was* fixed on the web path (`TaskListService.cs:9-14`). Recorded because the inventory named it. |

---

## ✅ COVERED — survived adversarial refutation

148 rows. These are claims a second agent actively tried to break and could not; each was traced route → DTO → service → component → template on the web side.

| Area | What holds | Notable |
|---|---|---|
| Shell navigation (A1) | All 5 workspace destinations + 2 admin destinations, 1:1, with real routes and a real `adminGuard`; per-nav data reload via Angular's default component recreation; "Active" status chip | Web is *stricter* than WPF: admin screens were ungated in WPF |
| Log Work grid (A2) | Week nav, week/row/group totals, grid columns and day labels, add-task dialog, move-to-next-month (incl. the DEFAULT guard), drag reorder, drag-to-trash soft delete, auto-save on blur | Web adds a delete confirm and a `nextOrderIndex` fix for a real WPF append bug |
| Backlog (A3) | Search, all 4 filter dropdowns incl. stale-selection reset, all grid columns, editor field sets on both create and edit paths, create-only tracking fields with a verified round-trip, change-history panel, every validation rule | Web is stricter on 4 of them (orphan-create 400, ≥1 task on edit too, blocking parse errors, no `teamId` on the wire) |
| Task List (A4) | Month/year/team filters, grid↔Gantt toggle, adaptive grouping, sort order, all inline commit paths (PCT/Type/PCA, deadline-with-note, start/end, progress, tags replace-all, task status/extended/tags), Continue, all three chip rules incl. Late-excludes-Warning, Gantt bar colours and the external-deadline marker | The deadline-note flow was traced end to end incl. cancel-reverts-the-picker |
| Daily Report (A5) | Prev/next day, entry card fields, delete gating, drag reorder + cross-section move + trash, issue add/solution/status/delete, the solution-presence colour rule, board team filter, the empty-teams anti-leak guard | Web adds optimistic concurrency on issue edits |
| Reports (A6) | All 6 filter controls, auto-load routing per filter (verified per-filter, not just "reloads"), all 4 stat cards incl. the zero-divide guard, both grids, the 5-level drill-down tree with correct `every`-based expand-all, Excel export incl. a character-for-character filename match | Web `switchMap` cancels stale responses; WPF did not |
| Users (A7) | Status pill, deactivate (web adds reactivate), list-includes-inactive from the same Core query and ordering | |
| Settings (A8) | Retention preview, warning-days key, template list/delete, default-task sync, tag create/list/delete, all PCA contact ops, all team ops incl. the DEFAULT-backlog bootstrap **verified in the endpoint body**, holiday grid + colour priority **verified at the stylesheet level**, membership replace-all | Web adds confirms and optimistic concurrency in several places |
| Dialogs (A9) | TaskInputDialog (all 3 call sites), StandupEntryDialog, DeadlineNoteDialog, TagSelectDialog | |
| Controls (A10) | TagPicker (header/filter/chips), TeamFilter control, `ShowFilter`, default-active-team seeding, the lazy-seed init-order guarantee, BindingProxy's four downstream effects | The refuter found the `TagsCommitted` "batching" premise was **dead WPF code** — the real WPF path was per-toggle, same as the web |
| Multi-team (B3) | `AvailableTeams` intersection, `ActiveTeamId` three-way resolution, TeamFilter on Task List / Daily Board / Reports incl. the empty-selection contract on all three, DEFAULT-backlog-per-team | Export is gated on `teamEmpty()` — it does not silently widen |
| WPF/Windows deps (D) | SaveFileDialog → browser download, Windows identity → real auth, drag-drop payload semantics **incl. both trash zones and the same-group-only rule**, theming, DatePicker write-back flow, N+1 fixed server-side, mixed VN/EN strings resolved (4 WPF files → 0 Angular files) | The "9 re-entrancy guard flags" were spot-checked: the two carrying business semantics are both explicitly ported |

---

## Claims that did NOT survive refutation

This is the audit's own error log. 19 refutation passes were run; **45 COVERED claims were downgraded** — the table below has 45 rows.

*(Corrected 2026-07-19. This line previously read "32" and STATE.md quoted "194 claims / 44 downgraded"; both were wrong. The arithmetic that settles it: 148 COVERED survive in the section table above, the downgrade table below has 45 rows, and 148 + 45 = **193 COVERED claims originally asserted**. Only 193/45/148 is consistent with the totals — do not re-quote 32 or 44.)*

Recorded in full because the pattern matters more than any single row: auditors consistently verified the happy path and stopped.

| Section | Claim | Auditor | Refuter | What decided it |
|---|---|---|---|---|
| A1 | Current-user chip (name + initial avatar) | COVERED | PARTIAL | Name/initial match exactly; the per-user hash colour (`AvatarBrushConverter.cs:16-19,34-37`) has no web equivalent |
| A2 | Week nav | COVERED | PARTIAL | The auditor cited `MainWindow`-style Prev/Next and skipped the jump DatePicker |
| A2 | Smart fill | COVERED | PARTIAL | Web is drastically narrower — 5 sub-capabilities absent |
| A2 | Task row | COVERED | PARTIAL | Holiday day-cells entirely absent web-side |
| A2 | Trash zone "pinned bottom" | COVERED | PARTIAL | WPF's is outside the ScrollViewer; the web's is inside it with no `sticky` |
| A2 | Footer DAY TOTALS | COVERED | PARTIAL | The auditor's own cited line range contained the >8h red/bold rule |
| A2 | Type → blur → auto-save | COVERED | PARTIAL | The cited lines were mostly the save-status feedback, which has no web equivalent |
| A2 | Clear cell = DELETE | COVERED | PARTIAL | Narrow claim held; unparseable text also routes to delete → silent data loss |
| A3 | "Add a template (create only)" | COVERED | PARTIAL | **The "(create only)" premise was false on the WPF side** — the picker has no `IsEditMode` trigger and works on edit. Web is narrower, not equal |
| A3 | Progress int 0-100 | COVERED | PARTIAL | The `0-100` half holds; the integer check does not exist on the web |
| A3 | No-delete-backlog invariant | COVERED | CORE-SURVIVES | Enforced structurally by `IBacklogRepository.cs:50` having no delete member |
| A4 | Export this month | COVERED | PARTIAL | The persistent on-disk archive + startup backfill have zero surviving callers |
| A4 | Section band header | COVERED | PARTIAL | The auditor cited the header `StackPanel` and skipped its `<Expander>` container |
| A4 | Adaptive grouping | COVERED | PARTIAL | It is a **two-part swap** — band shows Team ⇒ card shows Project. The web card never renders `row.project` |
| A4 | Task sub-rows | COVERED | PARTIAL | Task assignee `<select>` has no deactivated-user fallback |
| A4 | Gantt working-day axis | COVERED | CORE-SURVIVES | `GanttBuilder.cs:44` is Core; both hosts call it |
| A5 | Prev/Next day nav | COVERED | PARTIAL | The auditor cited `MainWindow.xaml:221` and `:224` and **skipped the `<DatePicker>` at `:222`** |
| A5 | Archive week | COVERED | PARTIAL | Admin-gated on web, and the file lands on the API host |
| A5 | Add entry modal | COVERED | PARTIAL | Description box: multi-line → single-line |
| A5 | Quick Import | COVERED | PARTIAL | Source-day picker hard-coded to yesterday |
| A5 | Issue add | COVERED | PARTIAL | Solution + status are creation-time fields in WPF, hard-coded on web |
| A5 | Board entry display | COVERED | PARTIAL | Board no longer shows issue solution text |
| A6 | Missing-logs banner | COVERED | PARTIAL | `ScrollViewer MaxHeight="96"` not ported |
| A7 | Add-user box | COVERED | PARTIAL | Auditor called it a "strict subset"; it is the reverse — web requires 3 fields |
| A7 | Avatar colour | COVERED | PARTIAL | **There is no hash on the web side** — a 12-name hardcoded lookup |
| A7 | AddUserAsync / DeactivateAsync | COVERED | CORE-SURVIVES | Substantive behavior is in `UserRepository.cs` |
| A8 | Dark mode toggle | COVERED | PARTIAL | Admin-only on the web |
| A8 | Export now / Backup now | COVERED | PARTIAL ×2 | Both report failure as success |
| A8 | Run retention now | COVERED | PARTIAL | Outcome string discarded |
| A8 | Template editor (×2 rows) | COVERED | PARTIAL | Validation is a silent no-op — the exact defect the WPF VM was changed to fix |
| A8 | Tags: new / edit | COVERED | PARTIAL ×2 | Editor lost the live preview chip and curated pickers; default colour changed |
| A9 | StandupIssueDialog | COVERED | PARTIAL | Solution + Status not expressible at create |
| A9 | Common dialog chrome | COVERED | PARTIAL | No header/footer bars anywhere; Esc/Enter on 2 of the web dialogs; the cited `tag-picker` evidence **is not a modal at all** |
| B1 | ValidateSmartFillAsync | COVERED | CORE-SURVIVES | Correct verdict, wrong evidence — the cited `/api/smartfill/validate` route **has no component caller** |
| B1 | Apply pipeline | COVERED | PARTIAL | The journal-gone check is structurally inert under WAL **and** unsurfaced |
| B3 | Live re-resolve + reentry guard | COVERED | PARTIAL | The broadcast half is covered; the self-switch reset half is **vacuous — nobody wired the switcher** |
| B5 | Progress% (create disabled) | COVERED | PARTIAL | Not merely uncovered — **inverted**. Web makes it an authored, save-gating field |
| B7 | SharePoint rule table | COVERED | PARTIAL | Only the `Error` level is observable; `Ok`/`Warning` are computed and discarded |
| B8 | Backup now | COVERED | PARTIAL | Null-means-no-backup is coalesced into a success message |
| B9 | Run retention now | COVERED | PARTIAL | Four documented non-exception outcomes reach nobody |
| B10 | Live theme swap | COVERED | PARTIAL | Mechanism is real; the *completeness* of the swap is not |
| B11 | "No password/session/role" | COVERED | PARTIAL | "Nothing is lost" is false — the deletion strands every existing non-admin user behind a 401 |
| D | WeakReferenceMessenger → SignalR | COVERED | PARTIAL | Transport ported; the Angular Task List subscribes to nothing |
| D | Modal dialog blocking | COVERED | PARTIAL | The auditor's cited call site was the backup-restore flow, which **does not exist on the web** |
| D | SelectUserDialog / Gantt | COVERED | CORE-SURVIVES ×2 | Citation defects: `App.xaml.cs` has 218 lines, not 222-229; the Gantt math is already in Core |

**Citation defects found in the audits themselves** (verdicts unaffected, but the inventory should be corrected): `SelectUserDialog` cited a line range in a file that is shorter than the citation; `TagsCommitted` described as "used in the Backlog editor" when it has no subscriber anywhere; `TagSelectDialog` and `Theme.xaml` cited under the wrong directories; the re-entrancy guard list has 10 members, not 9; A1's own summary says "7 of 12 COVERED" while its table shows 5 of 13.

---

## What lives in Core and is therefore untouched

121 of 369 audited behaviors are CORE-SURVIVES — the code is physically inside `src/TimesheetApp.Core/`, which M10 does not delete. Three whole sections are 100% in this category and need no further thought:

- **Section C (18/18)** — the entire data layer: `PRAGMA user_version` migrations v1→v11, all 18 tables' DDL, seeding, the `Backlogs`-has-no-`is_active` and `Tags`-hard-delete constraints, `TimeLogs UNIQUE(user_id, task_id, work_date)`, the `StandupIssues` CASCADE, all hardcoded enums, ISO date/`REAL` hours conventions, and both connection profiles.
- **B2 (9/9)** — every TimeLog rule: `>0`, ≤1 decimal, Mon–Fri, no holiday, cell ≤8h, day total ≤8h, `AwayFromZero` rounding, clear-means-DELETE, the upsert SQL.
- **B4 (10/10)** — every standup rule: both schemas, both status enums, ad-hoc backlog resolution, the issue CASCADE, per-(user,day,section,team) order scoping, the today/yesterday edit lock, the owner gate, and the deliberate issue-collaboration exemption.

Plus, across the other sections: `ScheduleStateService.Evaluate` (all 9 chip rules), `WorkingDayCalculator`, `GanttBuilder`, `BacklogContinuationService` (guard + clone + tag copy + not-Done filter + audit row), `SetTagsAsync`'s replace-all-plus-one-audit-row guarantee, `ReportAggregator` (all 4 reports + the drill-down tree), `ExportService` (Excel + Markdown), `ExportHubService`'s folder/dedupe/best-effort logic, `RetentionService` + `PruneArchiver`'s entire destructive pipeline, `BackupService` + `DbBackupHelper`, `TeamBootstrapService`, `DefaultTaskSyncService`, the R6 anti-leak SQL in four repositories, and `CurrentUserService`'s resolution algorithm.

**Two of the inventory's documented bugs turned out to be already fixed in Core** and are not M10 concerns: the "DAYS LOGGED always N/N" denominator (now `ReportAggregator.DaysLoggedStat`) and the holiday-blind N-day banner window (now `TimeLogService.LastNWorkingDaysAsync` delegating to `IWorkingDayCalculator`).

**The distinction that decides several verdicts:** a Core method with no surviving caller is *not* coverage. `RestoreAsync`, `ListBackups`, `AutoBackupIfDueAsync`, `ExportHubService.BackfillAsync`, `BackfillMissingWeeksAsync`, `BackfillMissingMonthsAsync`, `ExportMonthAsync`, and `SmartInputService`'s `FillFull8h` branch all survive in Core as **unreachable code** once `src/TimesheetApp/` is gone. They are in the MISSING table, not this one.

---

## Blast radius of the deletion

Verified against the working tree, not copied from `STATE.md`.

**Directories removed** — `src/TimesheetApp/` in full: **78 source files** (excluding `bin/`/`obj/`).

| Path | Files | Contents |
|---|---:|---|
| `src/TimesheetApp/ViewModels/` | 20 | incl. `MainViewModel`, `TimesheetViewModel`, `TaskListViewModel`, `SettingsViewModel`, `SmartInputPanelVm`, `TeamFilterViewModel` |
| `src/TimesheetApp/Views/Tabs/` | 16 | 8 tabs × `.xaml` + `.xaml.cs` |
| `src/TimesheetApp/Views/Dialogs/` | 10 | 5 dialogs × `.xaml` + `.xaml.cs` |
| `src/TimesheetApp/Views/Converters/` | 9 | incl. `AvatarBrushConverter`, `OverEightTagConverter` |
| `src/TimesheetApp/Views/Controls/` | 9 | `TagPicker`, `TagSelectDialog`, `TeamFilter`, `DeadlineNoteDialog`, `BindingProxy` |
| `src/TimesheetApp/Views/Theme/` | 3 | **`Theme.xaml`, `Palette.Light.xaml`, `Palette.Dark.xaml`** — the only record of the intended dark palette |
| `src/TimesheetApp/Views/Behaviors/` | 2 | `ComboBoxSearch`, `RoundedClip` |
| `src/TimesheetApp/Services/` | 3 | `CurrentTeamService.cs`, `ThemeService.cs`, `IThemeService.cs` |
| `src/TimesheetApp/` (root) | 5 | `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `TimesheetApp.csproj` |

**Test files removed — 23 files, 205 `[Fact]`/`[Theory]`.** This is *larger* than the "13 files / 179 tests" figure in circulation, which counts only `ViewModels/`:

| Path | Files | Tests |
|---|---:|---:|
| `src/TimesheetApp.Tests/ViewModels/` | 13 | 179 |
| `src/TimesheetApp.Tests/Views/` | 7 | 9 (incl. `WpfStaCollection.cs`, 0 tests) |
| `src/TimesheetApp.Tests/Services/CurrentTeamServiceTests.cs` | 1 | 9 — constructs WPF's `CurrentTeamService` directly (`:38`) |
| `src/TimesheetApp.Tests/Services/CurrentTeamPerUserTests.cs` | 1 | 4 — same (`:63`) |
| `src/TimesheetApp.Tests/DependencyInjectionTests.cs` | 1 | 4 — builds `App.ConfigureServices`, `using TimesheetApp.ViewModels` |

**.NET test-count delta:** `TimesheetApp.Tests` **652 → 447**. `TimesheetApp.ApiTests` **212**, unaffected. Repo total **864 → 659** (−205, −23.7%). Note `src/TimesheetApp.Tests/Services/*` mostly resolves `using TimesheetApp.Services;` to **Core** (`RootNamespace` is `TimesheetApp`), so only the two files above actually depend on the WPF project — do not delete the rest by directory name.

**Project-file edits required:**

1. `src/TimesheetApp.sln` — remove the `TimesheetApp` project entry (line 6, GUID `{5C25D2E0-A848-4BB5-92F9-D28C81ADA52C}`) and its `GlobalSection` build-configuration rows. Four projects remain: `TimesheetApp.Core`, `TimesheetApp.Api`, `TimesheetApp.Tests`, `TimesheetApp.ApiTests`.
2. `src/TimesheetApp.Tests/TimesheetApp.Tests.csproj:21` — remove `<ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />`. Then `<TargetFramework>net8.0-windows</TargetFramework>` and `<UseWPF>false</UseWPF>` can drop to plain `net8.0` once the STA `Views/` tests are gone.
3. `src/TimesheetApp.Core/TimesheetApp.Core.csproj` — `<InternalsVisibleTo Include="TimesheetApp" />` becomes dead. Its comment above it explains it exists because `DateHelpers` is called by `ReportsViewModel`/`TimesheetViewModel`, which will no longer exist. Harmless if left, but remove it and the comment.
4. **No build or deploy script changes needed.** `deploy-local.bat` and `start-web.bat` reference only `src/TimesheetApp.Api` and `src/timesheet-web`. No CI workflow references the WPF project.

---

## Open questions for the human

1. **Do all existing employees have web passwords yet?** This is the hard gate. `DatabaseInitializer.cs:331` added `password_hash` with no backfill and `:337` promoted only `MIN(id)` to admin, so on the production DB every other user is almost certainly `NULL` → a hard 401 with self-recovery explicitly blocked (`AuthEndpoints.cs:91-93`) and no bulk provisioning path. The row contents were **not** verified — the production DB was deliberately not opened. *Somebody must check this before M10 ships.*
2. **What replaces backup Restore?** The API's refusal to expose `RestoreAsync` is correct engineering, but no CLI, script, or runbook exists in the repo. Is a documented "stop the service, swap the file" procedure acceptable, or does an offline restore tool need to be built first?
3. **Which of the four lost scheduled behaviors get a hosted service, and which get accepted?** Auto-backup (BK-03), export-hub backfill, weekly standup-archive backfill, monthly task-list archive backfill. All four are the same shape: a Core method whose only trigger was `App.xaml.cs`. One `BackgroundService` could cover all four.
4. **Is retention's manual-only trigger intended?** It is arguably *safer* than a silent startup purge. Confirm, then record it — and note `RetentionEnabled` becomes a dead flag (`App.xaml.cs:91` was its only consumer).
5. **Is the entry-target user picker's removal signed off?** `TimesheetEndpoints.cs:56-62` treats "one user cannot view another's individual grid" as a deliberate privacy decision. That is a product call, not an oversight — but it needs an owner's yes.
6. **Should dark mode really be admin-only on the web?** Every other admin gate on Settings protects global shared state. This one protects a per-user cosmetic preference and looks unintended.
7. **The Settings UI points operators at "appsettings.json on the host", but `TimesheetApp.Api` has no `appsettings.json`** (`Program.cs:53`); paths resolve to `%APPDATA%\TimesheetApp\appsettings.json` unless `TimesheetApp:ConfigPath` is set. Fix the copy — an operator following it edits the wrong file, and that file is the one holding the production DB path.
8. **Port `Palette.Dark.xaml` values before the file is deleted.** It is the sole record of the intended dark hexes for the ~9 unmapped tokens and the 23 hardcoded component colours.
9. **A pre-deletion dark-mode walkthrough of every page** is the one visual check with no automated substitute — and the WPF reference disappears at the same moment.
10. **Two items were flagged `[ASSUMED]` and never closed:** D6's "every `ShowDialog()` flow was rewritten correctly" (spot-checked only) and D9's per-flag trace of the re-entrancy guards (2 of 10 traced; the refuter confirmed those two carry the only business semantics). Low risk, but not verified.

---

*22 section audits + 19 adversarial refutations, read in full. Blast-radius figures re-derived from the working tree. No build was run, no test was run, and no SQLite file was opened at any point.*
