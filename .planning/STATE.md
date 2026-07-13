# Project State

## Current Position

**Phase:** Step 6 — Plan (M8.5) · Plan Checker in flight
**Status:** waiting_for_user
**Last updated:** 2026-07-13

**Next Action:** Plan Checker, then `subagent-driven-development` or `executing-plans` per the user's choice.
**Approved Mode:** **Mode A** — approved 2026-07-13. Gate scored **1/5** Mode B signals (2 domains: C# + Angular). No hard exclusion: no migration, no auth change, no release gate, no compliance impact. *(M8 itself ran Mode B; M8.5 is materially smaller — the endpoints and the contracts already exist.)*

**Plan:** `docs/superpowers/plans/2026-07-13-M8.5-log-work-task-actions.md` (`15c950d`) — six tasks, three sequential waves.

### M8 — SHIPPED. `main` has it.

**Branch:** `feature/m8.5-log-work-actions-2026-07-13`, cut from `main`.
**`main`:** M8.1 → M8.4 merged (`feature/m8-web-migration-2026-07-13`). 30 stale branches and 12 worktrees cleaned.
**Suite:** **939 passed, 0 failed, 0 warnings** — 658 `TimesheetApp.Tests` + 157 `TimesheetApp.ApiTests` + **124 Angular**. Was 548 at the start of M8.2.

| | | |
|---|---|---|
| **M8.1** | extract `TimesheetApp.Core` from the WPF project | 548 |
| **M8.2** | schema v10 · optimistic concurrency · the backup blocker · `ActiveTeamId` per-user | 628 |
| **M8.3** | ASP.NET Core API · cookie auth · **80 routes** · SignalR | 809 |
| **M8.4** | Angular: login, guarded shell, **Log Work working against the real API** | **939** |

**Waiting on the user:** ① click through the web app (`.planning/M8.4-UAT.md` — 🔴 **the admin password prints ONCE** on first API start) · ② click through the WPF app (M8.2's OT-8, which no test proves) · ③ **decide how the web app reaches anyone else's browser** — nobody but the user can use it until that is chosen.

### M8.5 — IN PLANNING. Restore the three Log Work controls M8.4/W4 removed.

Spec: `docs/superpowers/specs/2026-07-13-log-work-task-actions-design.md` (`d97bd3e`).

`+ Add task`, `Move to next month`, and the drag-to-reorder / drag-to-trash zone. All three were **fake in the vendored design** (`toast.show('Task added')` with no handler), so the WPF app is the source of truth. **All five endpoints they need already exist** — but `BacklogEndpoints.cs` has **zero `.Produces<T>()`**, so OpenAPI describes none of them and the generated client does not contain them. **Annotating the C# is Wave A, not optional cleanup.**

### M8.3 — COMPLETE. The API exists, is authenticated, team-scoped, conflict-aware and live.

| Wave | | |
|---|---|---|
| **W0** — Core prerequisites | ✅ | Credential surface (auth had **nothing** to stand on: `password_hash`/`is_admin` were columns *nothing read*) + **the version-aware service layer M8.2 stopped one step short of**. 628 → 656. |
| **W1** — API shell + every shared contract | ✅ | Host boots with scope validation ON · scoped identity · `SqliteProfile.Server` · auth + Data Protection · **three** error channels · OpenAPI. 656 → 690. |
| **W2** — 4 endpoint agents in parallel | ✅ | **79 routes, zero collisions.** 690 → 795. |
| **W2.5** — honest fixtures | ✅ | **Mutation-tested: 5 kills, 0 false alarms.** 795 → 805. |
| **W3** — SignalR | ✅ | Group per team · rejoin on reconnect (**proven**: a forced reconnect yields a new `ConnectionId` and the message still arrives) · no self-echo. 805 → **809**. |

**79 API routes. 153 API tests.** OpenAPI is exposed — M8.4 generates its TypeScript client from it.

### The four traps M8.3 paid for (all found by an agent RUNNING it, none by reasoning)

1. **`[FromQuery] int[]?` never binds to `null`.** Minimal-API array binding turns *both* "key absent" and "key present but empty" into an **empty array**. So `teamIds is null ? memberships : intersect(...)` is **always false** → an empty list reaches the repository → matches nothing → **every unfiltered list endpoint silently returns zero rows.** Compiles clean. Found only because an *unrelated* search test failed with "the collection was empty."
2. **`teamIds = null` means EVERY TEAM.** `GetExportRowsAsync` has **no `userId` parameter at all**, and `ExportFilter.TeamIds` is a *trailing optional* — so the 4-arg ctor every WPF call site uses **compiles, looks complete, and exports the whole company**. On the wire the filter is attacker-controlled; `TM-06` doesn't catch it because it tests the WPF path, where the filter is membership-bounded by construction.
3. **A whole-record update overwrites every column.** `UpdateCheckedAsync(Backlog, …)` writes all 15, including `team_id`. A DTO that merely *omits* `teamId` → `team_id = NULL` → **the backlog drops out of every team and is invisible to everyone, permanently** — and every test still passes.
4. **`ApiFactory` cannot prove SignalR delivery.** It replaces `IChangeNotifier` with a recording double (correct, and necessary for the contract tests) — which means **no test built on it can ever observe the real hub firing.** The honest fixture for one layer is a lying fixture for the layer beneath it. W3 needed a second factory.

### Open, recorded, not blocking

- **A fresh install cannot bootstrap itself.** v10 promotes `MIN(id)` to admin, so a database with **zero users has zero admins** → `AdminBootstrap` no-ops → nobody can log in → and `/api/users` is admin-gated, so the first user cannot be created over HTTP. Safe today (M8.3 targets the *existing* desktop DB, which always has an admin). **Belongs in the deploy runbook.**
- **A demoted admin keeps admin for up to 30 days** — `RequireClaim("is_admin","1")` reads the *cookie* claim, fixed at login. The four destructive `/api/ops/*` routes additionally check `ctx.IsAdmin` (DB-fresh), so the blast radius is bounded.
- **`RestoreAsync` is deliberately NOT exposed.** It overwrites the live `.db` in place under open connections — it corrupts live readers. Needs its own design.
- **Retention runs on a bare `Task.Run`**, not a hosted queue (a queue needs `Program.cs`, which Wave 2 was forbidden). No shutdown awareness.
- **An unreproduced test flake** (~1 run in 4, a *different* test each time, never reproduces in isolation). `TestDb`'s process-global `ClearAllPools()` was removed — it was a **no-op there** (`Pooling=false`) that still reached into every other pool. `ApiFactory` still calls it, and there the call **is** load-bearing (`Pooling=true`; it releases the file handle). **Hypothesis, not evidence** — nobody has reproduced the flake, so nothing further was changed on a hunch.

**Two W1 concerns awaiting a decision (neither blocks W2):**
1. **Greenfield deadlock (latent).** Migration v10 promotes `MIN(id)` to admin — so a database with **zero users has zero admins**. `AdminBootstrap` then no-ops, nobody can log in, and `/api/users` is admin-gated, so the first user cannot be created over HTTP. **Safe today** (M8.3 targets the existing desktop DB, which always has an admin) but **a fresh install cannot bootstrap itself.** Belongs in the deploy runbook.
2. **A demoted admin keeps admin for up to 30 days.** `RequireClaim("is_admin","1")` reads the **cookie** claim, fixed at login (30-day sliding). `IClientContext.IsAdmin` is DB-fresh, so the two can disagree. W2-D's brief now requires the four destructive `/api/ops/*` routes to check `ctx.IsAdmin` **as well as** the policy.

## Current Milestone

**Milestone:** M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular 17)
**Started:** 2026-07-12
**Target:** no deadline

| Slice | State |
|---|---|
| **M8.1** — extract `TimesheetApp.Core` (net8.0) | ✅ **DONE, gated.** 81 files moved, zero C# edited, one XAML line. 548→548 green, WPF still runs. |
| **M8.2 W1** — schema v10 | ✅ **DONE** (`4b00e66`). 560 green. |
| **M8.2 W2** — WAL profile · backup blocker · 3 bug fixes | ✅ **DONE, merged, gated** (`fe794c2`). **583 green.** 3 agents in parallel, 0 conflicts. |
| **M8.2 W3** — `row_version` across 8 repositories | ✅ **DONE, merged, gated** (`3a89801`). **616 green.** 4 agents in parallel, **0 merge conflicts** — but they built **two incompatible APIs**; see W3.5. |
| **M8.2 W3.5** — consolidation | ✅ **DONE, merged, gated** (`49cb9d0`). 621 green. One API; `row_version` reachable end to end. |
| **M8.2 W4** — `ActiveTeamId` per-user | ✅ **DONE, merged, gated** (`6ac5621`). **628 green.** |
| **M8.2** | ✅ **COMPLETE.** 548 → 628. Gate OT-1..OT-7 green; **OT-8 (WPF still launches) is a MANUAL check the user must do.** |
| **M8.3** — API + auth + SignalR | 🔄 **Step 6.** Plan rev. 2 written (`f53cbed`) after Plan Checker returned **BLOCK** on rev. 1. Re-check in flight. |
| **M8.4** — Angular shell + Log Work | ⬜ **First slice the user can actually click.** |

Plan: `docs/superpowers/plans/2026-07-12-M8.2-core-hardening.md`
Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (rev. 3)

---

## ▶ RESUME HERE

### M8.2 is CLOSED. 628 green on `m8.2/w3-integrate`.

| Wave | Commit | What |
|---|---|---|
| W1 | `4b00e66` | schema v10 |
| W2 | `fe794c2` | WAL profile · the backup blocker · 3 bug fixes |
| W3 (A/B/C/D) | `3a89801` | `row_version` across 8 repositories — 4 agents, 0 merge conflicts |
| W3.5 | `49cb9d0` | consolidation — **one** concurrency API; `row_version` reachable end to end |
| W4 | `6ac5621` | `ActiveTeamId` → `Users.active_team_id` |

**The one gate item still open: OT-8 — "the WPF app still launches and works."** No test proves it; **the user must click it.** Everything else (OT-1..OT-7) is green and automated.

### Next: M8.3 — API + auth + SignalR. Currently at STEP 6.

Plan: `docs/superpowers/plans/2026-07-12-M8.3-api-auth-signalr.md` (**rev. 2**, `f53cbed`).

**Rev. 1 was BLOCKED by the Plan Checker** — six BLOCKs. Read the rev.-2 preamble before executing; it lists the four false premises rev. 1 rested on. Waves: **W0** (Core credential methods — auth has *nothing* to stand on today) → **W1a** (host boots, scoped identity, no captive deps) → **W1b** (auth, 409 contract, DTOs, OpenAPI) → **W2** (4 endpoint agents) → **W3** (SignalR).

**Do not start W2 before W0 and W1 are merged and green.** Every one of the six BLOCKs was a thing W2's four agents would have needed and none would have owned.

### The three lessons M8.2 paid for. Do not re-learn them.

1. **A file that N parallel agents need is a file the controller writes FIRST.** `ConcurrencyConflictException` was done right (controller wrote it up front). `Models/Entities.cs` was not — four agents each needed it, none owned it, so `Backlog`/`TaskItem`/`TimeLog` got no `RowVersion` at all and the whole mechanism was unreachable. That cost an entire consolidation wave.
2. **A decision in `STATE.md` is NOT a decision an agent knows.** "The checked methods are additive; originals stay bump-only" was recorded *before* Wave 3 ran. W3-A followed it. W3-B, W3-C and W3-D each re-derived a *different* API — not from disagreement, but because their briefs never repeated it. **Restate every cross-cutting decision verbatim in every brief that could touch it.**
3. **Route overlap is invisible to a file-overlap check.** M8.3 rev. 1 gave `tags` to two agents in two different files. Zero file overlap — and both would map `/api/tags`, giving `AmbiguousMatchException` → 500 on every tags request, found only at the merge gate. **Own routes, not just files.**

**The lesson, again:** a file shared by N parallel agents will be edited by none of them. Hand it to the controller *before* the fan-out, not after.

---

## Open Blockers

- **The concurrency mechanism is not yet reachable end-to-end.** The write path is done; the **read** path never projects `row_version`, and the records in `Models/Entities.cs` do not carry it — so a client has no way to *obtain* the `expectedVersion` it is required to send back. Fixed in **W3.5 / C-1**. **The API (M8.3) cannot call the checked path until this lands.**
- **UI design for M8.4–M8.8 is the user's to supply.** The Angular bundle is vendored at `src/timesheet-web/` (Angular 17, standalone, signals) but covers only ~35–45% — the shell, not the behaviour. No dialogs, no `TeamFilter`, no `TagPicker`, no Gantt, no login, no 409 UX. Its models are UI-shaped: `User` has no `id`, `TaskCard` has no `ScheduleState` (so the Late/At-risk chips *cannot* render), and `HoursMap` is keyed by **array index** — one filter or reorder and hours land on the wrong task. **Do not hand-maintain those types: expose OpenAPI from the API and generate the client.**
- **No host machine exists.** The company has no server. The design assumes **one designated workstation hosts the API** (spec §2), which is what restores the single-writer property SQLite needs. The user has deferred solving this. It does not block M8.2/M8.3, but M8.4's UAT needs it.

---

## ⚠ Things that will bite the next session

### The worktree gotcha — this cost three agents

**`isolation: "worktree"` on the Agent tool gave the correct base only 1 time in 3.** The other two got `e469fbd` — a commit from *before* M8 existed, where `TimesheetApp.Core` does not exist at all. Both agents correctly reported BLOCKED rather than adapting to the old paths, and one explained exactly why that mattered:

> *"The bug I was sent to fix does not exist on the base I was given. A green commit here would be a fix to code that never ships — `PruneArchiver` would keep its `File.Copy`, land next to WAL, and the data-loss route would stay wide open while the wave reports it closed. That is the single worst outcome available."*

**Do not use `isolation: "worktree"`. Create worktrees yourself with an explicit base, verify each one, then dispatch agents with the absolute path:**

```bash
git worktree add .worktrees/<name> -b <branch> feature/m8-core-extraction-2026-07-12
ls .worktrees/<name>/src/TimesheetApp.Core   # must exist
```

Every agent prompt should open with a **Step 0** that re-verifies the base and stops if it is wrong.

### EIGHT of my own claims turned out false — all caught by agents *running* them

This is the most important lesson in this file. Do not trust a claim about **how a failure presents** unless it has been executed. Note the rate: this did not stop after I noticed it. It kept happening, and every time it was caught by an agent that ran the thing instead of believing me.

| I claimed | Truth, measured |
|---|---|
| `TeamBootstrapService`'s backfill must bump `StandupEntries.row_version` (I put it in a brief) | **`StandupEntries` has no `row_version` column.** v10 versions 8 tables and that is not one. An agent obeying me would have shipped `UPDATE StandupEntries SET row_version = row_version + 1` → **`no such column`** → **every user's app crashes at startup, every launch.** The agent checked the schema instead of trusting the brief. |
| "A checked write never takes an optional param and never returns void" — I wrote this as a **rule in a brief four agents were about to follow** | **False for the entity that matters most.** `UpsertCheckedAsync(TimeLog log, long? expectedVersion)` is nullable **by design** (the five-case table), and `DeleteCheckedAsync(...)` returns `Task`, not `Task<long>`. An agent obeying the rule would have "normalized" `TimeLogRepository` and **destroyed the five-case behaviour W3-A established by measurement.** |
| "Four test files hand-implement `IAppConfig` — that is the risk when removing a member" | **Right fact, wrong risk.** Those four fakes compiled untouched (an extra public member is legal). The actual breakage was **Moq mocks** — `SetupGet(c => c.ActiveTeamId)` — in four *different* files. "Run the full suite" was the right instruction for the wrong reason. |
| Dapper fails **silently** (returns `null`) if the column is renamed without the DTO | **Throws.** `SqliteException: no such column`, 28 tests red. **This was the sole reason the `UserRepository` fix was scheduled a wave *later* than the migration** — which would have committed a red tree and handed three parallel agents a broken baseline. Schema rename and the SQL that reads it are **atomically coupled**. |
| `busy_timeout` / `synchronous` are connection-string settings | **Not valid keywords.** Silently swallowed. They must be `PRAGMA`s. |
| `IDbBackupHelper` is ctor-injected into **5** services | **4.** (`IJournalWarningSink`: **2**.) `PruneArchiver` injects neither. M8.3's DI registration must use the real list. |
| Deleting `SmartInputPanelVm.BuildPlan` orphans **9 tests** | **0.** `BuildPlan` is `private`; the 9 tests go through public commands and still pass. Following me would have meant **deleting 8 good tests — including a security-boundary test — to hit a predicted number.** |
| `ConcurrencyConflictException`'s `Message` carries cell detail for `TimeLogs` | It does not. `BuildMessage` has no such parameter. **My bug, in the contract file I wrote to prevent this exact class of divergence.** Fixed in W3.5 / C-2. |

The corrected pitfall research carries a banner about this: a `[VERIFIED]` tag means the author *believed* they had checked it, not that they *executed* it.

**Agents are not exempt.** W3-D added `RowVersion` to three records in `Entities.cs` justifying it as *"following this file's existing convention for `Backlog`."* **There was no such convention** — `git show c9c26b0:…/Entities.cs | grep RowVersion` returns nothing; `Backlog` had no `RowVersion` at all. It had confused `ReadModels.cs` (which does set that precedent, and which the plan cites) with `Entities.cs`. The *action* was right and the *citation* was invented. Check the citation, not just the diff — a plausible reason is the easiest thing in the world to generate.

### Why 548 tests were green while a data-loss route sat wide open

W2-B found it. **Every pre-existing backup test faked the database with a text file** (`"LIVE-DB"`) or six `0x09` bytes. A text file has no pages, no header, and **cannot have a `-wal`** — it is precisely the fixture that let `File.Copy` look correct for four phases. The tests were *structurally incapable* of catching the bug. Fixtures are now real SQLite databases, and the bug appeared immediately:

```
Snapshot RetentionService trusts before PERMANENTLY DELETING:
  Expected: ["live-row", "alpha", "beta", "gamma"]
  Actual:   ["live-row"]          ← 3 of 4 committed rows silently gone
```

### Never use `:memory:` for a concurrency test

Each connection gets its **own** database, so a conflict can never occur — **the test passes while asserting nothing.** Every one of the 583 uses a temp-file database. Keep that convention.

---

## Key Decisions Made

- 2026-07-12: **Milestone M8 opened as a fresh start; WPF-era state files were NOT backfilled.** — `validate-state` returned FAIL (no `PROJECT.md`; `STATE.md` had none of the required sections; the three files contradicted each other). Repairing state for an architecture about to be replaced is wasted work. The old narrative is preserved verbatim in `.planning/STATE-ARCHIVE-wpf-p1-p20.md`.
- 2026-07-12: **Deployment — one designated workstation hosts the API.** — No server exists. Rejected: every user running their own API against a shared `.db` (destroys the single-writer premise; N processes on N hosts over SMB is exactly what sqlite.org warns against, and it kills WAL, SignalR *and* auth-as-a-boundary at once). Consequence: **all company data sits on one workstation's disk**, so hourly online backup to the network share is a `must_have`.
- 2026-07-12: **HTTP, not HTTPS — knowingly accepted risk.** — No certificate infrastructure. The session cookie crosses the LAN in plaintext, so anyone who can capture packets can assume any identity — including the admin who can permanently delete three months of data. Recorded, not hidden. Switching to HTTPS later is a one-line config change.
- 2026-07-12: **Keep SQLite (interim), WAL on the host's local disk.** — No managed DB available. The API is the only writer process, so N users ≠ N writers. Exit cost enumerated in spec §15: 5 constructs, ~15 call sites, **no SQLite date functions anywhere** (normally the most expensive thing to port).
- 2026-07-12: **Optimistic concurrency (`row_version` + 409) + SignalR.** — No table had a version column, and Task List commits every inline edit as a bare `UPDATE`, so concurrent editors overwrite each other **today**. No engine fixes this.
- 2026-07-12: **`row_version` DOES apply to `TimeLogs`** (reversal of the first draft). — The draft excluded it on the argument that a cross-user collision was impossible. That rested on *current behaviour* (team view is read-only), not an enforced invariant. Retrofitting after the endpoints and grid exist would cost far more.
- 2026-07-12: **The rule is: ALWAYS BUMP, CHECK SELECTIVELY.** — Two templates, not one. `SetOrderAsync` runs once per row during a drag, so a single check-and-bump template would **409-storm on an ordinary reorder**. Bumping without checking is safe; **checking without bumping is the bug the mechanism exists to prevent** — the spec's own Smart Fill carve-out reintroduced it in an earlier draft.
- 2026-07-12: **The checked repository methods are ADDITIVE; the originals stay unchecked.** — Making `expectedVersion` optional-defaulting-to-`null` is a trap: under the five-case table, every WPF re-edit of an already-filled cell becomes `null` + row-exists → **conflict on every second edit**. Originals stay bump-only; `*CheckedAsync` is new. Only the API path is protected, which is correct — WPF is deleted at M8.10.
- 2026-07-12: **Auth = username + password, cookie session, on the existing `Users` table.** — No AD. ASP.NET Identity rejected (drags in EF Core; creates a second user table). `PasswordHasher<T>` is usable standalone from `Microsoft.Extensions.Identity.Core` — 3 packages, no EF Core.
- 2026-07-12: **Authorization = one `is_admin` boolean**, gating exactly 3 destructive endpoints (run retention, restore backup, deactivate team). The user does not want edit-level permissions; those three are *destructive*, not merely privileged.
- 2026-07-12: **Shared contracts are the controller's job, never an agent's.** — Four agents needed `ConcurrencyConflictException`; left to invent it they would have produced four incompatible versions and the merge would be where we found out. Same lesson missed once and re-learned: `Models/Entities.cs` (W3.5/C-1).
- 2026-07-12: **A checked write RETURNS the new `row_version` (`Task<long>`); it never returns `void`.** — User-approved at the W3 merge. A `void` write forces the caller to re-read the version, and that read-back is **racy**: between the write committing and the re-read, another client can write; you then hold *their* version number with *your* data, and your next save passes the check and silently overwrites them — the exact lost update the mechanism exists to prevent. Returning the version from the same statement that performed the write (`RETURNING row_version`) closes it by construction. Also kills the CS0854 Moq breakage that the optional-parameter shape forced on three test files. `GetRowVersionAsync` is **deleted** — the version now arrives on the entity from the SELECT (W3.5/C-1).
- 2026-07-12: **A decision recorded in `STATE.md` is NOT a decision an agent knows.** — The "additive `*CheckedAsync`, originals stay bump-only" decision above was already written down *before* Wave 3 ran. W3-A followed it; **W3-B, W3-C and W3-D each independently re-derived a different shape** (optional parameter, `void` return), because their briefs did not restate it. Agents see only their brief. **Every cross-cutting decision must be re-stated verbatim in every brief that could touch it** — otherwise N agents produce N designs and the merge is where you find out.

## Approved Mode

**Mode B** — approved 2026-07-12. Gate scored 4/5, and a hard exclusion applies independently: *"compliance, security audit, or data migration impact"* — this slice has both.

## Config

`.planning/config.json` — Mode B · `parallelization: true` · `mode: interactive` · `model_profile: quality` · `commit_atomic: true` · Process 2.0 flags all on.

## Notes

- **The real database has been migrated to v10** and the WPF app runs on it. Backup before that: `~/Documents/TimesheetApp/timesheet.db.20260712-pre-v10.bak`. **Migrations are forward-only — `git revert` does not undo a changed `.db` file.**
- **`.planning/M8.1-UAT.md`** — five checks, deferred by the user. The one that matters if the WPF Reports screen ever misbehaves: `ReportsTab.xaml:4` now says `;assembly=TimesheetApp.Core`, and XAML binding failures are **silent at build time**.
- **Feature inventory:** `.planning/M8-FEATURE-INVENTORY.md` — the as-built record of the WPF app (7 screens, 8 dialogs, every business rule, 16 tables, ~40 design tokens). It is both the migration scope and the design brief. Anything not in it is not being migrated.
- **`.claude/memory/`** does not exist, so `workflow.memory_recall` is a no-op despite being enabled.
