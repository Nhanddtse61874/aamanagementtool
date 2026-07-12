# Project State

## Current Position

**Phase:** Step 7 — Execute (M8.2 Wave 3.5, in flight)
**Status:** in_progress
**Last updated:** 2026-07-12

**Branch:** `m8.2/w3-integrate` — HEAD `3a89801` (all four W3 agents merged). Cut from `feature/m8-core-extraction-2026-07-12`. **Never merged to `main`.**
**Suite:** **616 passed, 0 failed, 0 warnings** at `3a89801` (last gated merge). ~7 s.

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
| **M8.2 W3.5** — consolidation | 🔄 **IN FLIGHT — see "Resume here" below** |
| **M8.2 W4** — `ActiveTeamId` per-user | ⬜ Unblocked (W3-C merged). Run **after** W3.5 — do not parallelize, they collide on ViewModel test files. |
| **M8.3** — API + auth + SignalR | ⬜ |
| **M8.4** — Angular shell + Log Work | ⬜ **First slice the user can actually click.** |

Plan: `docs/superpowers/plans/2026-07-12-M8.2-core-hardening.md`
Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (rev. 3)

---

## ▶ RESUME HERE

### Wave 3 is closed. All four agents merged into `m8.2/w3-integrate` (`3a89801`), 616 green, zero conflicts.

| Agent | Commit | Scope |
|---|---|---|
| W3-A | `7f969f4` | TimeLogs |
| W3-B | `cea0da3` | Backlogs, Tasks |
| W3-C | `7a7c363` | Standup, Users |
| W3-D | `281128e` | Teams, Tags, PcaContacts |

### Wave 3.5 is IN FLIGHT — one agent, worktree `.worktrees/w35`, branch `m8.2/w35`, base `3a89801`

```bash
git -C .worktrees/w35 log -1 --format='%h %s'   # still 3a89801 = agent didn't finish
```
If it did not finish, **inspect the worktree; if the work is incomplete, discard and re-dispatch** from the W3.5 brief. Do not commit a half-done consolidation — a red step is only diagnosable if each wave gates green.

**When it lands:** merge `m8.2/w35`, gate on the suite, then run **Wave 4** (`ActiveTeamId` → per-user). Wave 4 is unblocked but must run **after** W3.5, not beside it — both touch the ViewModel test files.

### Why W3.5 exists (do not skip it)

Four parallel agents each solved their own repositories correctly, and in doing so produced **two incompatible concurrency APIs** plus a shared file nobody would touch:

- **W3-A** returned the new `row_version` from the checked write (`Task<long>`).
- **W3-B and W3-D** returned `void` and offered `GetRowVersionAsync` to re-read it. **That read-back is racy** — between your write committing and your re-read, someone else can write; you pick up *their* version while holding *your* data, and your next save passes the check and silently overwrites them. The lost update, laundered through the read-back.
- **`Models/Entities.cs` was left half-done.** `User`/`StandupIssue` got `RowVersion = 0`; `Team`/`Tag`/`PcaContact` got `= 1`; **`Backlog`, `TaskItem` and `TimeLog` got nothing at all** — the three the app actually contends on. W3-A and W3-B both, correctly, refused to edit a file four agents shared. So it stayed empty.

**User-approved ruling (2026-07-12): normalize on W3-A's shape** — a separate `<Verb>CheckedAsync` returning `Task<long>`, bump-only keeps its original signature, `GetRowVersionAsync` is deleted. Unify `RowVersion = 0` (fail-closed sentinel: no real row is ever at 0). Project `row_version` in every SELECT.

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

### Five of my own claims turned out false — all caught by agents *running* them

This is the most important lesson in this file. Do not trust a claim about **how a failure presents** unless it has been executed.

| I claimed | Truth, measured |
|---|---|
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
