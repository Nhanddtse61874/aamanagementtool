# Project State

## Current Position

**Phase:** Step 8 — UAT (M9 **COMPLETE and merged to `main`**; awaiting the user's click-through)
**Status:** waiting_for_user
**Last updated:** 2026-07-14

## ▶ RESUME HERE

**`main` @ `8c12f20`.** M9 merged. **The app is RUNNING** — API :5080 (**real DB**), web :4200.
**Suite: 1882 green** — 1170 .NET (681 Core/WPF + 489 API) + 712 Angular. 0 warnings.

# 🎉 NO SCREEN IN THE APP IS FAKE ANY MORE.

| Screen | |
|---|---|
| Login · **Log Work** · **Backlog** | ✅ M8.4 · M8.5 · M8.6 |
| **Task List** (incl. the **Gantt**) · **Daily Report** · **Reports** · **Users** · **Settings** | ✅ **M9** |

**The WPF app can now be deleted (M10) — but do not, until the user has clicked through everything.**

---

## 🔴 OPEN — the user has clicked NOTHING yet. They chose to test only at the very end.

**M8.5:** OT-13 (delete → reorder → reload) · OT-14 (delete → add → reload)
**M8.6:** OT-15 … OT-19
**M9:** OT-20 … OT-25 (below)

### 🔴 The three that would be silent data loss

- **OT-16** — Backlog editor: open a backlog with a **start date and a PCA contact** (both **hidden on edit**). Change **only the note**. Save. Reload, reopen. **Both must still be there.**
- **OT-17** — Backlog editor: rename a task that is **`Done`** on the Task List. Save. Check the Task List. **It must still be `Done`.**
- **OT-20** — Task List: change **only the progress %**. Reload. **Type, Assignee and PCA must all survive.**

*(All three are the SAME trap — a checked `PUT` replaces the whole record, and TypeScript cannot catch a dropped field because the generated DTOs are all-optional. It has now appeared in **four** places.)*

### The rest
- **OT-21** — Task List: a backlog past its internal deadline shows **⚠ Late**; one behind pace inside 2 working days shows **⚠ At risk**. Toggle to **Gantt** — the bars must **skip weekends and holidays**.
- **OT-22** — Users: create a user → set username → set password → **log in as them.** *(Miss any step and you made a ghost.)*
- **OT-23** — **Log in as a NON-admin.** `/users` and `/settings` must be **hidden from the sidebar AND unreachable by URL.**
- **OT-24** — Settings: create a **new team**. It must get a **`DEFAULT` backlog** — check Log Work shows its default tasks. *(Before M9 it did not.)*
- **OT-25** — Daily Report: yesterday and today are editable; **the day before yesterday is not.**

---

## 🔴🔴 THE DB-SAFETY RULE WAS INSUFFICIENT FOR THREE MILESTONES

Every brief since M8.4 said *"pin all three seams — `DbPath`, `ConfigPath`, `KeyRingPath`."* **NOT SUFFICIENT.**

```csharp
// JsonAppConfig.cs
_dbPath = model?.DbPath ?? defaultDbPath;
```

**The `DbPath` you pass is only a DEFAULT.** If `ConfigPath` points at a config file that **EXISTS** and carries a `DbPath` key, **that file's `DbPath` WINS.** And the real `%APPDATA%\TimesheetApp\appsettings.json` names the **live company database**.

**An agent that pinned all three seams AND aimed `ConfigPath` at the production config would have PASSED THE CHECK AND OPENED THE LIVE DATABASE.**

### 🔴 THE TRUE INVARIANT: `ConfigPath` MUST POINT AT A PATH THAT DOES NOT EXIST.
A fresh `mktemp -d` guarantees it. Pin all three **into that fresh directory**.

**The proof is what actually saved us, not the rule:** grep the startup log for the substring **`no users yet, nothing to bootstrap`** — it fires only on `all.Count == 0`. *(Grep the SUBSTRING: the line reads "**Admin bootstrap:** …" — space, lowercase `b`. `AdminBootstrap` is only the logger category.)*

---

## 🔴 THE GATE LIES IN BOTH DIRECTIONS

- **False GREEN:** a running API holds `TimesheetApp.Core.dll` open → the API project **fails to build** and `dotnet test` **still exits 0 printing `Passed!`**. **Nothing may listen on 5080. BOTH `Passed!` lines must appear.** An absent `TimesheetApp.ApiTests.dll` line **is a failed gate**.
- **False RED:** a pre-existing **~15%** race in the ApiTests host-startup path. A lone `SqliteException: no such table: Backlogs` is **not a regression — re-run.** **Baseline the untouched tree first.**

## 🔴 "Don't edit a test to reconcile a gate" HAS AN EXCEPTION — and it has a hard limit

The rule stops you deleting a test that caught a real regression. **It does not apply when you deliberately changed the contract the test was pinning.** I wrote the gate as a fixed number **five times** and was wrong five times.

🔴 **BUT IT IS NOT A LICENCE TO DELETE A GUARD.** M9's plan, as first written, licensed an agent to delete a **security** test. **If the test you are about to move asserts a security property — STOP AND REPORT. That is a plan decision, never an agent's.**

## 🔴 A gate that cannot move is a gate that cannot notice
`System.Text.Json` binds record ctor params **by NAME** and **defaults the missing** — so swapping a DTO leaves old tests **deserialising happily into `default`, green, asserting nothing.**

---

## Known gaps, honestly named

- **Task List cannot group by team.** `TaskListRowDto` carries **no `teamId` and no team name.** The filter correctly says *when* to band — there is **no value to band by**. Needs a field on the DTO + a regen.
- **The Daily Report backlog picker is wider than WPF's.** WPF scopes it to the **active team**; the only web route scopes to **all your teams**, and `BacklogListItemDto` has no `teamId` to narrow by. **Not a leak** — every backlog is server-authorised for that user, and the entry is stamped with the *active* team regardless. A fidelity divergence.
- **A demoted admin keeps admin for up to 30 days** — `AdminPolicy` reads the **cookie claim**, fixed at login. The four `/api/ops/*` routes re-check the DB every request, so destructive operations are blocked immediately. **The Users screen states both.** Closing it needs a token-version claim or a shorter cookie.
- **`GET /api/default-tasks` is active-only** and there is no `GetAllAsync` — deactivate one and you can never see it again. **WPF has the identical hole.** The UI presents it as one-way rather than a toggle it cannot round-trip.
- **`RestoreAsync` is deliberately NOT exposed** — it overwrites the live `.db` under open connections and corrupts live readers. There is also no backup-list route.
- **The ApiTests startup race** (~15%). Characterised, not fixed.

## 🔴 STILL BLOCKING EVERYONE BUT THE USER

**How does the web app reach anyone else's browser?** Unchanged since M8. The dev loop works because `ng serve` proxies same-origin — **the only transport where a `SameSite=Lax` cookie survives.** Production hosting collides with the deferred *"the company has no server"* blocker. **Finishing every screen does not change this.**

## Config
`.planning/config.json` — Mode A · `parallelization: true` · `commit_atomic: true` · Process 2.0 flags on.
