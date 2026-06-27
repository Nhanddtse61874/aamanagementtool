# P10 Multi-Team — FEATURE / UX RESEARCH

**Phase:** P10 / M4 — Multi-Team (happypowerprocess STEP 4, Mode B)
**Date:** 2026-06-27
**Author:** Feature/UX research agent
**Scope:** Define correct UX/behavior for the team model. NOT implementation.
**Inputs read:** CLAUDE.md · REQUIREMENTS.md (TM-01..10) · UPCOMING-FEATURES.md (P10) · existing MainWindow / MainViewModel / SettingsTab / ReportsViewModel / UsersTab / DailyBoardTab / TaskListTab / JsonAppConfig / CurrentUserService.

**Tag legend:** `[VERIFIED]` = read directly in code/reqs this session · `[CITED]` = stated in REQ/decision docs · `[ASSUMED]` = inferred beyond literal reqs (genuine recommendation, needs spec-author sign-off).

---

## 0. Grounding — existing patterns to reuse (so the new UX is consistent)

- `[VERIFIED]` **Sidebar layout** (`MainWindow.xaml`): a `DockPanel` with brand at top (`DockPanel.Dock=Top`), the **current-user chip at the bottom** (`DockPanel.Dock=Bottom`, avatar + name + "Active" + green dot), and the WORKSPACE/ADMIN nav `RadioButton` list in the middle. There is open vertical real estate **between the brand and the nav** and **between the nav and the user chip**.
- `[VERIFIED]` **Current-user chip** binds `CurrentUserName` / `CurrentUserInitial` on `MainViewModel`; resolved once at startup via `CurrentUserService` + `MainViewModel.ResolveCurrentUserAsync`. It is the natural visual anchor to place the team switcher next to.
- `[VERIFIED]` **ActiveView routing**: `MainViewModel.ActiveView` (string key) + `OnActiveViewChanged` reloads each view (`Backlogs.LoadAsync`, `TaskList.LoadAsync`, `DailyReport.LoadAsync`, `ActivateTabAsync` for timesheet/reports). This is the exact hook a team switch must re-trigger.
- `[VERIFIED]` **App-local persistence** = `JsonAppConfig` (`%APPDATA%\TimesheetApp\appsettings.json`) with nullable-defaulting fields and a private `Save()`. Adding `ActiveTeamId` here mirrors `DbPath`/`BackupFolderPath` exactly (matches TM-05 `[ASSUMED]` "persisted app-local").
- `[VERIFIED]` **Existing single-select view filter** = `ReportsViewModel.SelectedTarget` ("Whole team (all)" sentinel `UserId==0` + per-user options) and `TaskList` Month/Year combos. The multi-team checkbox filter is a new *multi-select* pattern — no existing multi-select control to copy, so it's net-new (see §2).
- `[VERIFIED]` **Settings list+overlay-editor pattern** (Templates, Tags, PCA contacts) = a `New …` button + `ItemsControl` of bordered rows with Edit/Delete, plus a `#66000000` modal overlay card bound to a nullable editor VM (`NullToCollapsedConverter`). Team CRUD + membership should reuse this pattern verbatim.
- `[VERIFIED]` **Soft-delete UX precedent** = PCA contacts / Users: a "Deactivate" danger-ghost button, an "Inactive" badge, item stays in list but hidden from pickers. Team deactivation should mirror this.
- `[VERIFIED]` **Cross-tab live refresh** = `WeakReferenceMessenger` + `DataChangedMessage(DataKind.*)`. TM-03 already calls for a `DataKind.Teams` broadcast — switchers/filters subscribe to it.
- `[CITED]` Decisions locked in UPCOMING-FEATURES P10 + TM-01..10: team is a top-level entity; User↔Team M2M (incl. 50-50 member); ONE active team for editing; multi-team checkbox VIEW on Backlog / Task List / Reports / Daily-Board; DEFAULT backlog per team; first-run setup + validated defaults; migrate existing data into "Architect Improvement".

---

## 1. Active-team switcher UX (TM-05 / TM-06)

**Recommendation:**

- `[ASSUMED]` **Location:** a compact team switcher in the **sidebar, directly ABOVE the current-user chip** (its own `DockPanel.Dock=Bottom` border placed before the user chip, or grouped with it into one "identity" stack). Rationale: the active team is *part of "who/where I am acting"* — same mental bucket as the current user. Keeping it out of the per-view toolbars avoids implying it's a per-view filter (the checkbox filter is the per-view thing). It is global and always visible.
- `[ASSUMED]` **Control:** a `ComboBox` styled as a chip (team name + a small "team" glyph, e.g. 👥). Label it "Active team" (mirrors the chip's "Active" caption).
- `[CITED/VERIFIED]` **What it shows:** ONLY the current user's **active** teams (`UserTeams` ⋈ active `Teams` for `CurrentUser.Id`). A team the user doesn't belong to never appears. TM-05 says "lists the current user's active teams."
- `[ASSUMED]` **On switch:**
  1. Persist new `ActiveTeamId` app-local immediately (per machine/user) — TM-05.
  2. Reload the working views (re-run the equivalent of `OnActiveViewChanged` for whatever view is open + invalidate cached team-scoped data). Concretely: re-trigger `Timesheet.Load`, `Backlogs.Load`, `DailyReport.Load`, and `TaskList`/`Reports` if open.
  3. Broadcast `DataKind.Teams` (or a new `DataKind.ActiveTeam`) so all subscribers refresh.
- `[ASSUMED]` **Does it reset the multi-team checkbox filter?** **YES — recommended.** On active-team switch, reset each view's checkbox filter back to its default (= the new active team only). Rationale: the filter default is "active team only" (§2); after switching, the most predictable state is "I'm now looking at my new active team." Persisting a stale multi-team selection across an active-team switch is confusing (user could switch to Team B but still see Team A's data checked). Document this as an explicit decision — see OPEN QUESTIONS Q3.
- `[ASSUMED]` **Persistence:** `ActiveTeamId` in `JsonAppConfig` (new nullable `int?`). On startup, validate it against the user's current memberships; if the saved team no longer exists / user was removed / team deactivated → fall back to the user's first active team (and if none, see §6 edge). Defaults to the migrated/first team (TM-05).
- `[ASSUMED]` **User with only ONE team:** **Hide the switcher entirely** (or render it as a non-interactive label). A combobox with one option is noise. Show it only when `userTeams.Count >= 2`. This keeps the dominant small-team case (the migration puts everyone in one team) visually clean. The multi-team checkbox filter should likewise collapse to nothing/disabled when the user has only one team (§2).

---

## 2. Multi-team checkbox filter UX (TM-07)

Applies to **4 screens**: Backlog list, Task List, Reports, Daily-Report Board. Timesheet/Log-Work entry is **excluded** (active-team only) `[CITED]`.

**Recommendation:**

- `[ASSUMED]` **Placement:** a small **"Teams" multi-select control in each screen's existing top toolbar/header**, next to the existing filters (Task List already has Month/Year combos in a `WrapPanel`; Reports has the Target/Project/Week/Month row; Daily Board has the shared date toolbar; Backlog has its own header). Use a **dropdown button that opens a checkbox list** ("Teams ▾" → popup with a checkbox per team + "All my teams" / "Active only" quick toggles). A flat inline row of checkboxes is acceptable when ≤3 teams but the dropdown scales and matches toolbar density. `[ASSUMED]` dropdown-of-checkboxes is the cleaner pattern; confirm with spec author (Q4).
- `[CITED]` **Default selection = the active team only** (TM-07 acceptance + `[ASSUMED]` note in req). NOT all teams. This keeps the default view identical to today's single-team behavior and matches the active-team working scope.
- `[CITED]` **Persistence:** per screen, **for the session** (TM-07: "selection persists per screen for the session"). Do NOT persist across app restarts (restart → back to default = active team). And per §1, reset to default when the active team changes. `[ASSUMED]` "session" = in-memory on the VM, not written to `JsonAppConfig`.
- `[ASSUMED]` **How each row/card shows its team** (TM-07: "indicate team per row/card where ambiguous"):
  - **Only show the team indicator when >1 team is checked.** When exactly one team is selected (the common case) the indicator is redundant clutter — hide it.
  - **Backlog list / Task List grid:** a small **team chip/column** (team name) on each backlog row, styled like the existing tag chips (neutral/gray, distinct from custom tags + system warning/late chips so it reads as metadata, not a status). `[ASSUMED]` a dedicated "Team" column is cleaner in the grid than a chip jammed among status chips.
  - **Daily Board:** group/label cards by team — either a **team section header** ("— Architect Improvement —") above that team's user cards, or a small team chip on each user card. Section headers read better when multiple teams are shown. `[ASSUMED]` section-grouping for the board.
  - **Reports:** team becomes a **grouping dimension** (see §8 / TM-08) — e.g. top-level group "Team → Project → Backlog → Task → Date", and a Team column in the monthly/weekly tables.
- `[ASSUMED]` **DEFAULT-per-team rows labeled by team:** **Yes.** Because each team has its own DEFAULT backlog (TM-04), when multiple teams are shown, the Annual-Leave/Meeting rows must carry the team label, otherwise two identical "Annual Leave" rows are indistinguishable. Use the same team chip/column. (The DEFAULT backlog itself stays hidden as a "backlog" entity per existing behavior — only its *tasks'* logged rows surface in reports, and those get the team label.)
- `[ASSUMED]` **Single-team users:** the Teams filter is hidden/disabled (nothing to multi-select), exactly like the switcher (§1).

---

## 3. Active team vs view filter interaction (TM-06 vs TM-07)

This is the highest-confusion risk and deserves explicit UX.

- `[CITED]` **Editing is ALWAYS scoped to the active team only**, regardless of what's checked in the view filter. Viewing multiple teams is read-aggregation; creating/editing (new backlog, new standup entry, timesheet cell) always targets the active team. TM-06 is unambiguous; the checkbox filter (TM-07) is display-only.
- `[ASSUMED]` **Avoid confusion — recommended affordances:**
  1. **"You are working as Team X" indicator.** The active-team switcher in the sidebar IS this indicator (always visible, always shows the working team). Style it prominently (chip with team glyph), distinct from the per-view checkbox filter.
  2. **On the Backlog screen**, the "**＋ New backlog**" / create affordances should show the target team inline — e.g. button caption or a hint "New backlog in **Team X**" (= active team), so the user never wonders which team a new backlog lands in. Same for the Daily-Report **Input** tab ("Adding to **Team X**"). `[ASSUMED]`
  3. **When viewing multiple teams but acting on the active team**, any create/edit affordance on a row that belongs to a *non-active* team should be **disabled or hidden** (you can view another team's backlog but not edit it from this aggregated view — you'd switch active team first). This prevents the "I edited a row and it saved to the wrong team" class of bug. `[ASSUMED]` — confirm (Q5): is cross-team editing from the aggregated view ever allowed, or strictly switch-then-edit?
  4. **Visual cue:** in the aggregated grid, rows belonging to the active team could be subtly emphasized (e.g. the team chip uses the accent color for the active team, gray for others), reinforcing "this is your working team." `[ASSUMED]` nice-to-have.

---

## 4. First-run setup flow (TM-09)

**Context:** `[VERIFIED]` Today's first-run is *zero-config*: `MainViewModel.ResolveCurrentUserAsync` auto-creates a user named after the Windows account when the DB has no users — no dialog. The existing philosophy is "open straight to a usable app."

**Recommendation — keep it nearly zero-friction, ONE small dialog:**

- `[ASSUMED]` **Do NOT build a multi-step wizard.** It's a 2-5 person internal tool; a heavy onboarding wizard is over-engineered (CLAUDE.md "Simplicity First"). Instead:
  - On a **fresh DB (no teams)**, auto-create defaults silently, then show **one lightweight first-run dialog** that lets the user (a) **name the first team** (pre-filled with a sensible default like "My Team", editable) and (b) optionally confirm their display name. Everything else is auto-applied.
  - If the user dismisses/cancels, proceed with the default-named team — never block app entry (matches existing zero-config ethos).
- `[CITED/ASSUMED]` **What gets created/applied on fresh DB (TM-09 acceptance):**
  - Exactly **one active team** (named from the dialog; default "My Team" `[ASSUMED]` — req says "default-named team the user can rename").
  - The team's **DEFAULT backlog** + **DefaultTasks** seeded under it (TM-04).
  - The current user inserted (existing behavior) **and** added to `UserTeams` for that team. `[ASSUMED]` membership wiring is new.
  - `ActiveTeamId` persisted app-local to that team.
  - **Validated default settings:** N-days warning = 3 (`[VERIFIED]` `ReportsViewModel.DefaultNDays`), auto-backup = off (`[VERIFIED]` `JsonAppConfig` default false), backup retention = 30 (`[VERIFIED]` `DefaultBackupKeepCount`), archive/backup folders = unset (gated until chosen — existing behavior). The point of TM-09 is **no null/invalid setting state** — assert each setting resolves to its documented default.
- `[ASSUMED]` **Migration path (existing DB) is distinct from first-run:** TM-02 silently creates "Architect Improvement" and assigns everything — **no dialog**, it's a data migration. First-run dialog is ONLY for a genuinely empty DB (no teams AND nothing to migrate). Don't show the naming dialog to upgrading users.
- `[ASSUMED]` **Reuse the existing modal-overlay style** (`#66000000` card) rather than a separate `Window`, for visual consistency and to keep it WPF-VM-friendly (mirrors how `SelectUserDialog` is invoked via a `Func` delegate from the VM — keep the VM WPF-free).

---

## 5. Team membership management UX — Settings (TM-03)

**Recommendation — reuse the Settings list+overlay pattern; add a "Teams" section:**

- `[ASSUMED]` **Section layout:** a new **"Teams" `SectionTitle`** in `SettingsTab` (near Users conceptually, but Settings is where CRUD lists live). Contains:
  - A `New team` button + `NewTeamName` box (mirrors PCA "Add contact").
  - An `ItemsControl` of team rows: team name (rename inline like PCA), an **active/Inactive badge**, a **Deactivate** danger-ghost button, and a **"Members…"** button opening the membership editor.
- `[ASSUMED]` **Membership editor = per-team member list (recommended over a global user×team matrix).**
  - Open a modal overlay (existing card style) titled "Members of {team}". Show **all active users**, each with a **checkbox** = "is a member of this team." Toggling persists to `UserTeams`.
  - Rationale: a full user×team **matrix grid** is the "enterprise" answer and gets unwieldy as either axis grows; for a small tool, a per-team checklist is simpler and matches the existing one-entity-at-a-time editor pattern. `[ASSUMED]` — but a matrix is a legitimate alternative for power users (Q6).
- `[CITED/ASSUMED]` **50-50 member:** the M2M model + checkbox-per-user naturally supports a user being checked in ≥2 teams (TM-03 acceptance explicitly allows ≥2). No special UI — they simply appear as members of both teams, and **both teams appear in that user's active-team switcher**. No "percentage" field is needed (the 50-50 is just dual membership; allocation isn't modeled). `[ASSUMED]` confirm no allocation/percentage is required (Q7).
- `[CITED/ASSUMED]` **Deactivating a team with data:** soft-delete (`is_active=0`), mirroring Users/PCA. The team:
  - disappears from active-team switchers and view filters (TM-03: "hides it from switchers/filters but preserves its data"),
  - its backlogs/standup/logs remain in the DB and still resolve the team name in historical reports (no `is_active` filter on report joins — consistent with XC-06),
  - **Guard:** if a user's *current active team* is the one being deactivated, fall back their active team to another of their teams (or surface the §6 "no active team" state). `[ASSUMED]`
  - **Guard:** `[ASSUMED]` warn (don't hard-block) when deactivating a team that still has active backlogs/recent standup — "This team has data; it will be hidden but preserved." Don't allow *deleting* a team with data (only deactivate), consistent with soft-delete-only philosophy (XC-06).

---

## 6. Edge UX

- `[ASSUMED]` **User removed from their active team (while it's their active team):** on next startup (or on receiving `DataKind.Teams`), validate `ActiveTeamId` ∈ user's memberships. If not, **silently fall back to the user's first remaining active team** and persist that. If they belong to none → see "no teams" below. Don't show a scary error; a quiet status message ("Active team changed to X") is enough. `[ASSUMED]`
- `[ASSUMED]` **User belongs to NO active team** (removed from all / all their teams deactivated): the working views (Log Work, Backlog, Daily Input) should show a **friendly empty state** — "You're not a member of any team. Ask an admin to add you, or create a team in Settings." Don't crash; don't auto-create a team for them (that's first-run territory). `[ASSUMED]` — this is a genuine gap not covered by reqs (Q8).
- `[ASSUMED]` **Switching to a team with no backlogs:** Log Work shows only that team's DEFAULT tasks (Annual Leave/Meeting — always present per TM-04); Backlog list shows an empty state ("No backlogs yet — create one"). This is normal, not an error. `[VERIFIED]` DEFAULT backlog always exists per team so the timesheet is never fully empty.
- `[CITED/ASSUMED]` **"Chưa log" warning across teams (RPT-04):** `[VERIFIED]` today it scans **all active users** globally. Decision needed (Q9): under teams, should "not logged" be evaluated **per active team** (only members of the team you're viewing) or **globally per user** (a user who logged in *any* team isn't "missing")? **Recommendation `[ASSUMED]`:** evaluate **globally per user** for the "did this person log at all" intent (a 50-50 member who logged in Team A shouldn't be flagged as missing just because Team B's view is open), but **filter the displayed list to the checked teams' members**. This keeps the banner meaningful without double-flagging multi-team members.
- `[CITED]` **Annual Leave / Meeting logged under active team only:** Yes — TM-04 acceptance: "Annual Leave/Meeting hours log under the **active** team's DEFAULT." Leave/Meeting is entered on the Log Work grid, which is active-team-scoped (TM-06). So leave attributes to the active team at time of entry. In multi-team reports, each team's leave is separable (TM-08). `[ASSUMED]` UX note: if a 50-50 member's leave should split across teams, that is NOT modeled (leave lands wholly on the active team) — flag as accepted limitation (Q10).

---

## 7. Interaction with M3 Task List (month + Gantt) and Daily-Report board under multi-team viewing

- `[VERIFIED]` **Task List** is scoped per **month** (Month/Year combos) + Grid/Gantt toggle. Under multi-team:
  - The new **Teams filter** combines with the month filter (AND): show checked teams' backlogs for the selected month.
  - **Grid:** add the team chip/column (§2); switching teams or month reloads (existing `LoadAsync`).
  - **Gantt:** `[ASSUMED]` when multiple teams are shown, **either** add a team-section grouping in the left label gutter (group bars by team) **or** color/prefix each bar's label with the team. Section-grouping is clearer; the schedule-state coloring (normal/warning/late) must remain the bar's primary color, so the team should be a **label/gutter** distinction, NOT the bar color. The working-day axis (weekends + Holidays skipped, HOL-02) is global/shared so it's unaffected by team. `[ASSUMED]`
  - System chips (warning/late, TL-07/08) are computed per-backlog and are team-agnostic in math → they just render per row regardless of team.
- `[VERIFIED]` **Daily-Report Board** renders one card per **active user** for the selected date (`DailyBoardTab`, dynamic count). Under multi-team:
  - With the default (active team only): show cards for **that team's members** for the date. `[ASSUMED]` This is a *behavior change* from today's "all active users" — confirm the board should scope to team members, not all users (Q11). Recommendation: yes, scope to the checked teams' members (a standup board is per-team).
  - With multiple teams checked: **group cards under team section headers** (§2). A 50-50 member appears under each team they're in (their entries are filtered by that team's `StandupEntries.team_id`, so each card shows only that team's standup rows). `[ASSUMED]`
  - **Input tab** stays active-team only (TM-06) — the user enters standup for their active team; a "Adding to Team X" hint (§3) clarifies.
- `[VERIFIED]` Both screens already use `DataChangedMessage` for live refresh; add `DataKind.Teams`/active-team to their subscriptions so a switch/membership change refreshes them.

---

## OPEN QUESTIONS (for the spec author)

1. **Q1 — Switcher placement:** sidebar above the user chip (recommended) vs. a top-bar control vs. grouped into the user chip? Confirm sidebar-bottom.
2. **Q2 — Hide switcher for single-team users?** Recommended yes (hide when <2 teams). Confirm.
3. **Q3 — Reset the per-view checkbox filter on active-team switch?** Recommended yes (reset to new active team). Confirm — this is a real behavioral choice.
4. **Q4 — Multi-team filter control shape:** dropdown-of-checkboxes (recommended, scales) vs. inline checkbox row (simpler for ≤3 teams)?
5. **Q5 — Cross-team editing from the aggregated view:** strictly "switch active team, then edit" (recommended — disable edit on non-active-team rows) vs. allow editing any visible row?
6. **Q6 — Membership editor shape:** per-team member checklist (recommended) vs. global user×team matrix grid?
7. **Q7 — 50-50 member:** is dual membership sufficient, or is an allocation/percentage field expected? (Recommend dual-membership only; no percentage.)
8. **Q8 — "No team" state:** what should a user with zero team memberships see? (Recommend friendly empty state; no auto-create.) Not covered by reqs.
9. **Q9 — RPT-04 "chưa log" semantics under teams:** evaluate per-team or globally-per-user? (Recommend global "did they log anywhere," display filtered to checked teams.)
10. **Q10 — Leave/Meeting for a 50-50 member:** lands wholly on the active team (recommended/accepted limitation) — confirm no split is required.
11. **Q11 — Daily Board scope:** scope to (checked) teams' members (recommended, behavior change) vs. keep showing all active users? Also confirm board grouping = team section headers.
12. **Q12 — Team indicator style on Backlog/Task List:** dedicated "Team" column (recommended in grids) vs. a chip among the tag chips? And confirm DEFAULT (Annual Leave/Meeting) rows carry the team label when >1 team shown.
