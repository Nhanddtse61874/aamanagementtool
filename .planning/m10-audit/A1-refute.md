# A1 (Shell — MainWindow.xaml) — Adversarial Refutation

Read-only audit. Every verdict traced end-to-end on both sides (route → component → template, or
converter → binding). No build, no run, no DB touched.

**Framing:** `src/TimesheetApp/MainWindow.xaml` and `ViewModels/MainViewModel.cs` both DIE in M10.
Nothing in this section lives in Core, so every claim here is a genuine "find the web equivalent or
lose it" question — no CORE-SURVIVES outcomes apply.

## Verdicts

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| Sidebar workspace nav (Log Work / Backlog / Task List / Daily Report / Reports) | COVERED | No | COVERED | WPF `MainWindow.xaml:77-95` (5 RadioButtons, keys `timesheet/backlog/tasklist/dailyreport/reports`); default `MainViewModel.cs:165` `_activeView = "timesheet"`. Web `sidebar.component.ts:41-47` (5 NavItems, same labels, same order) → `sidebar.component.html:12-24` (`routerLink` + `routerLinkActive="is-active"`) → `app.routes.ts:21-45` (all 5 routes exist with real `loadComponent`); default matches via `app.routes.ts:20` `redirectTo: 'log'`. [VERIFIED] |
| Sidebar ADMIN nav (Users / Settings) | COVERED | No | COVERED | WPF `MainWindow.xaml:97-103` — unconditional; `MainViewModel.cs` has **zero** `IsAdmin` references (grep: no matches), so any WPF user could open both. Web `sidebar.component.ts:39` `isAdmin` computed (`=== true`, fails closed) → `sidebar.component.html:29-41` gated `@if` → `app.routes.ts:53,59` `canActivate: [adminGuard]`. Both destinations reachable, gate is real (link + guard). **Deliberate narrowing, not accidental loss:** a non-admin who could reach these screens in WPF cannot in web — documented at `sidebar.component.ts:29-38` and `app.routes.ts:46-49`. Flagging so the tightening is a recorded decision, not a surprise. [VERIFIED] |
| Per-view reload on nav switch (OnActiveViewChanged reloads the destination VM) | COVERED | No | COVERED | WPF `MainViewModel.cs:170-182` (switch → `LoadAsync`/`ActivateTabAsync`). Web: no custom `RouteReuseStrategy` anywhere (grep over `src/` returns only `withComponentInputBinding` at `app.config.ts:9`), so Angular's default destroys + recreates on every route change. All 7 destinations do load on construction/init: `log-work.component.ts:203`, `backlog.component.ts:100`, `task-list.component.ts:157`, `daily-report.component.ts:162,178`, `reports.component.ts:164-168`, `users.component.ts:90-92`, `settings.component.ts:139`. Reports even mirrors WPF's 4-part reload (`MainViewModel.cs:255-262` LoadUsers+Banner+Monthly+Weekly ≈ `reports.component.ts:165-167` loadTargets+loadBanner+reloadAll). **Caveat:** WPF has a *second* trigger for this same method — `MainViewModel.cs:159` reloads the active view on team change. That path has no web equivalent, but only because the upstream trigger (team switcher) does not exist — see Omission 1 below, not a defect of this claim. [VERIFIED] |
| Current-user chip: name + initial avatar | COVERED | **YES** | **PARTIAL** | Name + initial ARE covered: `sidebar.component.ts:23-27` (`initials()`, `charAt(0).toUpperCase()`, `'?'` fallback — matches `MainViewModel.cs:107-108` exactly) → `sidebar.component.html:55,59`, fed by `auth.service.ts:33`. **Not covered: the per-user avatar colour.** WPF binds the chip background through `AvatarBrushConverter` (`MainWindow.xaml:43`), which hashes the name over a 5-colour palette so *every* user gets a stable, distinct colour across restarts (`AvatarBrushConverter.cs:16-19,34-37`). The web sidebar hardcodes one colour for everyone: `sidebar.component.html:55` `background:var(--accent)`. The web *does* own an avatar-colour helper, but it is (a) not used by the sidebar at all and (b) narrower where it is used — `worklog.service.ts:192-199` is a **12-name hardcoded lookup** with a single `#0E7C66` fallback, so any user not in that literal list is indistinguishable. Severity: cosmetic. But "initial avatar" is not fully ported. [VERIFIED] |
| "Active" status label + green dot indicator | COVERED | No | COVERED | WPF `MainWindow.xaml:51` hardcoded `Text="Active"`, `:53` `Ellipse Fill="#22C55E"` — always on, bound to nothing. Web `sidebar.component.html:60` hardcoded `Active`, `sidebar.component.scss:36` `.dot { background: #22C55E }` — also always on, bound to nothing. Behaviour identical. **Auditor's "verbatim" is inaccurate** (cosmetic drift: the dot moved from a sibling next to the text to an absolutely-positioned badge on the avatar, `scss:35-36`; the label recoloured from grey `SectionLabel` to green `#22A356`, `scss:38`). Cosmetics only — the feature is present. [VERIFIED] |

## Omissions from the A1 claim list (not claimed COVERED, but they live in MainWindow.xaml and DIE)

These are outside the 5 claims I was asked to attack, but they are in the audited file and the
inventory did not list them. Raising them because M10 removes the only oracle we could compare against.

**1. Active-team switcher (TM-05) — `MainWindow.xaml:59-69` + `MainViewModel.cs:112-160` — NOT COVERED.**
This is the highest-value finding in the section. The WPF switcher is fully wired: `ComboBox` bound to
`AvailableTeams` with a two-way `ActiveTeam` that persists through
`ICurrentTeamService.SetActiveTeamAsync` (`MainViewModel.cs:127-133`), refreshed live off the
`DataKind.Teams` messenger broadcast (`MainViewModel.cs:90-93`), with a re-entrancy guard and a
visibility rule (`ShowTeamSwitcher`, `:123`).

The web equivalent is **dead static markup** — `sidebar.component.html:46-52` is a bare `<select>` with
two hardcoded `<option>` literals (`Architect Improvement`, `Plus Team`), no binding, no `(change)`, no
service call. `SidebarComponent` (`sidebar.component.ts`) never imports or injects anything team-related.

This is confirmed by the web codebase's own comment at
`team-filter.component.ts:142`: *"the web app has no team switcher yet: `WorklogService.setActiveTeam()`
exists and has no caller"* — the exact "a method that exists but nobody calls is not coverage" trap.
Switching your active team is unavailable in the web app. Deleting WPF removes the only way to do it.

**2. XC-08 conflict-copy banner — `MainWindow.xaml:118-132` + `MainViewModel.cs:185,289-296`.** OneDrive
conflict-copy detection surfaced in the shell. No web equivalent expected (server-hosted DB), but it
should be an explicit "intentionally dropped", not an omission.

**3. XC-09 lingering-journal banner + Dismiss — `MainWindow.xaml:134-153` + `MainViewModel.cs:190-193`.**
Data-integrity warning with a dismiss command. Same note as above — needs an explicit drop decision.

**4. Startup user auto-provisioning — `MainViewModel.cs:268-287`.** An unmapped Windows account is
auto-created and mapped so the app opens straight into a usable session. The web replaces this with
explicit login (`app.routes.ts:11-14`), which is a deliberate model change — worth recording as such.

**Verified as covered (checked while tracing, no action needed):** the Daily Report shell toolbar
(`MainWindow.xaml:220-229` — Prev/Next day, date picker, Archive week, status) is fully present at
`daily-report.component.html:9,11,21` and `daily-report.component.ts:234,239,492`, with Archive week
additionally admin-gated on the web side.
