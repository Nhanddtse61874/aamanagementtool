# Project State

## Current Position

**Phase:** Step 6 — Plan (complete; Plan Checker running)
**Status:** waiting_for_user
**Last updated:** 2026-07-12

## Current Milestone

**Milestone:** M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular)
**Started:** 2026-07-12
**Target:** no deadline

Current slice: **M8.1 + M8.2 + M8.3 — Backend Foundation** (Core extraction · API host + DB + concurrency · auth).
Spec: `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (approved).

## Next Action

M8.1 plan is written (`docs/superpowers/plans/2026-07-12-M8.1-core-extraction.md`) and the Plan Checker is validating it against the 11 dimensions. When it returns: fix any BLOCK findings, get the user's execution-mode choice (subagent-driven vs inline), then run STEP 7 wave by wave. **Baseline measured, not assumed: 548 passed / 0 failed / 0 skipped in 5 s.**

## Open Blockers

- **M8.4–M8.8 (all UI slices) are blocked on UI design.** The user is producing the design separately and will hand it over. Backend slices (M8.1–M8.3) proceed in the meantime and do not depend on it.

## Key Decisions Made

- 2026-07-12: **Milestone M8 opened as a fresh start; WPF-era state files were NOT backfilled.** — `validate-state` returned FAIL (PROJECT.md absent; STATE.md had none of the required sections; ROADMAP said "Active: none" while STATE said the P18/P19/P20 batch was awaiting UAT — it had in fact already been merged to `main`). Repairing state files for an architecture about to be replaced is wasted work. The old narrative is preserved verbatim in `.planning/STATE-ARCHIVE-wpf-p1-p20.md`.
- 2026-07-12: **Keep SQLite, server-hosted, WAL + pooling — explicitly as an interim database.** — No Azure/AWS available. The API is the only writer process, so N concurrent users ≠ N concurrent writers; write contention is solved by architecture, not by engine. Exit cost is enumerated (5 constructs, ~15 call sites) in the spec §13 rather than left to be rediscovered.
- 2026-07-12: **Optimistic concurrency (`row_version` + HTTP 409) + SignalR.** — No table has a version column today and Task List commits every inline edit as a bare `UPDATE`, so concurrent editors silently overwrite each other **right now**. No database engine fixes this; it is an application-layer problem.
- 2026-07-12: **`row_version` DOES apply to `TimeLogs`** (reversal of the first draft). — The draft excluded it on the argument that a cross-user collision was impossible. That argument rested on current behaviour (Log Work's team view is read-only), not on an enforced invariant. User asked what happens if someone *can* edit another person's hours; retrofitting after the endpoints and Angular grid exist would be far more expensive than one more column in the same migration.
- 2026-07-12: **Auth = username + password, cookie session, on the existing `Users` table.** — No Active Directory, so Windows Auth is out. ASP.NET Core **Identity** rejected: it drags in EF Core (the app is Dapper-only) and creates a second user table alongside `Users`. Cookie over JWT: "remember me" is one flag (`IsPersistent`) vs. a refresh-token scheme, and an `HttpOnly` cookie is not readable by XSS.
- 2026-07-12: **Authorization = a single `is_admin` boolean**, gating exactly 3 destructive endpoints (run retention, restore backup, deactivate team). — User does not want edit-level permissions, and that is reasonable for a trusted 10–50 person team. But those three are *destructive*, not merely privileged, and today any user can trigger them.
- 2026-07-12: **Three surveyed bugs are fixed in Core during migration, not ported.** — Two Smart Fill implementations (only one runs, and it disagrees with its own validator about holidays); `DAYS LOGGED` can never read `3 / 5`; `LastNWorkingDays` ignores holidays. Two of them exist because business logic leaked into WPF ViewModels, where the web client could not reuse it and would be free to reinvent the same mistake.
- 2026-07-12: **`parallelization: true`.** — User asked for speed via multiple concurrent team agents. Speed comes from parallel execution, **not** from removing human checkpoints: `mode` stays `interactive`, and a stuck or surprising result stops and asks rather than being resolved unilaterally.
- 2026-07-12: **Deployment — one designated workstation hosts the API; the team reaches it over the LAN.** — The company has **no server and no always-on machine**, only workstations. Rejected: every user running their own API against a shared `.db` (destroys the single-writer premise SQLite depends on — N processes on N hosts over SMB is exactly what sqlite.org warns against, and it kills WAL, SignalR *and* auth-as-a-boundary at once). Consequence, and it is the largest operational risk in the design: **all company data now sits on one workstation's disk**, so hourly online backup to the network share is a `must_have`. See spec §2.
- 2026-07-12: **HTTP, not HTTPS — knowingly accepted risk.** — User decision; no certificate infrastructure. The session cookie therefore crosses the LAN in plaintext, so anyone who can capture packets can assume any identity, including the admin who can permanently delete three months of data. Recorded rather than hidden. Switching to HTTPS later is a one-line config change; the design does not foreclose it.
- 2026-07-12: **M8.1 is a pure `git mv` with an exactly-548 gate; the bug fixes are excluded from it.** — Rev. 1 of the spec asked for both, which is impossible: the fixes delete code that existing tests cover. A gate that cannot be met invites the cheapest way out — deleting tests. Separating them keeps a red step trivially diagnosable.

## Approved Mode

**Mode B** — approved 2026-07-12.

Gate score: 4/5 Mode B signals (2 domains · high risk · formal QA needed · cross-role). A **hard exclusion** also applies independently of the score — *"compliance, security audit, or data migration impact"* — and this slice has both: schema v10 is a data migration against live data, and auth is a security surface.

## Config

See `.planning/config.json` — Mode B, `granularity: standard`, `parallelization: true`, `model_profile: quality`, `commit_atomic: true`, all three Process 2.0 flags (`memory_recall`, `telemetry`, `sandbox_verify`) enabled.

## Notes

- **Feature inventory:** `.planning/M8-FEATURE-INVENTORY.md` — the as-built record of the WPF app (7 screens, 8 dialogs, every business rule, the 16-table schema, ~40 light/dark design tokens). It is both the migration scope and the design brief for the UI redesign. Anything not in it is not being migrated.
- **Why the migration is cheaper than it looks:** of 50 files in `Services/`, exactly one (`ThemeService`) touches WPF. `Data/` (29 files) and `Models/` (3) are entirely WPF-free, and `IConnectionFactory` already abstracts the database. The work is mostly wrapping an API around business logic that already exists — not rewriting it.
- **Why it is riskier than it looks:** the data layer is built around SQLite living on a shared OneDrive folder (`Pooling=false`, `journal_mode=DELETE`, plus conflict-copy and journal-gone defences). All of that is obsolete on a server and gets deleted — but Core is shared with WPF until M8.10, so the two hosts need *opposite* connection policies from the same factory.
- **WPF is deleted last (M8.10), not first.** It keeps running against Core throughout, which keeps the 548 tests exercising the exact code the API depends on. That is the safety net for the whole migration.
- `.claude/memory/` (process-memory store) does not exist yet, so `workflow.memory_recall` is currently a no-op despite being enabled.
