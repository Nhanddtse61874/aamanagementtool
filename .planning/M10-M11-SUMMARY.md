# M10 + M11 — SUMMARY

**2026-07-19/20.** Run autonomously at the user's instruction, deciding from choices they had already made in the session. Mode A throughout.

## What shipped

**M10 — the WPF app is deleted** (`daa4192`, merged `7f020f1`). `src/TimesheetApp/` is gone: ViewModels, Views, `CurrentTeamService`, `ThemeService`, `App.xaml.cs` and its whole startup lifecycle. Plus 23 test files, the `ProjectReference`, the `InternalsVisibleTo`, and the solution entry. `TimesheetApp.Core` untouched.

**M11 — configuration** (`8a70440`, `6016724`, `78a87bd`, `3a28eca`). `DbPath`/`ConfigPath`/`KeyRingPath` are required `IConfiguration` keys with fail-fast and no fallback chain; `appsettings.json` now outranks the persisted store; the writable store is renamed out of its name collision; `IsDarkMode` left the server.

## The gate, start to finish

| | .NET | ApiTests | Angular |
|---|---|---|---|
| session start | 689 | 507 | 742 |
| after M9.2 | 691 | 507 | 753 |
| before deletion | 692 | 531 | 772 |
| **after deletion** | **465** | 531 | 772 |
| **final** | **475** | **541** | **775** |

0 warnings throughout. Every number re-run by the controller, never taken from an agent's report.

## Why the deletion was safe on the day it happened and not the morning before

The coverage audit returned **DO NOT DELETE YET** on three blockers. All three closed first:

1. **Auth cutover** — dissolved, not solved. The user confirmed the current database is disposable test data and go-live is a first run, so there is no migrated population to provision. That removed the argument keeping WPF alive as a transition door.
2. **Scheduled jobs** — the four whose only caller was `App.xaml.cs` now run in the API (`a05b721`), once per process start rather than on a timer. Deliberately not a scheduler: the adversarial pass rated one FATAL because it assumes a supervised long-lived server this project does not have, and because job 2's DB copy has no once-per-period guard, so an hourly tick would gut the 30-deep prune window in a day.
3. **Restore** — an offline CLI plus a runbook (`4c075d8`), and `RestoreAsync` now refuses a file that is not a database *before* deleting anything (`0c739f9`).

Plus the backup half of the safety net, which nothing had: admin routes to configure it (`3866d63`), a Settings screen (`81beec4`), and a list that distinguishes *not configured* from *configured and empty*.

**Gate shape was the user's decision:** only silently-failing items blocked the deletion. Roughly seven affordance items are deferred and listed in `.planning/M10-BLOCKERS.md`. Four permanent losses were accepted by name.

## What was wrong, and got corrected by doing it

- **"The first audit run was lost."** It was not. It completed — 410 agents, 796 behaviours — and was merely unreachable from context. A second, smaller audit was run on that false diagnosis. Commit `6620956`'s message still asserts the wrong version.
- **"205 tests die."** 227 do. Both that figure and the memo's 190 counted `[Fact]`/`[Theory]` **attributes**; a Theory with several `InlineData` rows is several cases. 692 → 465 is the executed truth.
- **"`BackupServiceTests`/`WalBackupSafetyTests` are in the blast radius and need relocating."** They are not, and did not. They reference no WPF type and still run. A budgeted cost nobody had to pay.
- **"`RestoreAsync` destroys prod."** Overstated. A `.pre-restore_<stamp>.bak` safety copy is written seconds earlier, so the data is recoverable *by hand*. The real hazard is what happens next: with the `.db` gone the app rebuilds an empty database and runs normally.
- **Plan Checker was skipped without saying so.** Recorded in the CLAUDE.md deviation table as a **breach**, not a deviation — a deviation is agreed in advance.

## Three live defects found and deliberately NOT fixed

Recorded in `STATE.md` with reasons: `NoOpDbBackupHelper` is written, tested and registered nowhere (a genuine fork, and swapping it would remove a safety net at the worst moment); `ExportHubService.cs:145` copies the database ungated on every export run (real, bounded, and *not* the "evicts every backup" the memo claimed); Smart Fill's two implementations disagree on distribution (carried from the audit, **not** verified by the controller).

## 🔴 The oracle is gone

`OT-13…OT-25` was never clicked. M9.1's `G3` is specified as *"matches the old WPF app"*, and there is no app to match against. From here `.planning/M10-COVERAGE-AUDIT.md` **is** the record of what the desktop did. If it is silent on something, that is a gap — not licence to invent what WPF "would have" done. The source is recoverable at `daa4192^`, but recovering it means building WPF on a machine that may no longer have the workload.

## Still owed by a human

- **UAT.** `G-A`/`G-B` (M9.2), `G3`/`G6`/`G10` (M9.1), `OT-13…OT-25`. None of it clicked.
- **Who hosts the API.** The startup jobs run only while the process runs. A console window nobody opens has *worse* liveness for the daily backup than the desktop trigger it replaced. No code can fix that.
- **The deferred PORT items** in `.planning/M10-BLOCKERS.md`.
