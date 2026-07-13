# M8.5 — Log Work task actions: add, move-to-next-month, drag-to-reorder/delete

**Date:** 2026-07-13
**Status:** approved
**Base:** M8.4 complete — `main`, 939 tests green (658 Core/WPF + 157 API + 124 Angular).

---

## 1. Why this exists

M8.4/W4 **removed three controls** from the Log Work screen, and said so at the time:

- **`+ Add task`** — the vendored Angular design called `toast.show('Task added')` and **added nothing**.
- **`Move to next month ▶`** — **no handler at all**.
- **`🗑 Drop a task here to delete`** — **no drop handler**.

The agent's reasoning was that *a button which says "Task added" while adding nothing is worse than no button on a first click-through.* The user has asked for all three, working.

**All three exist in the WPF app and are the source of truth.** This spec is a migration, not an invention.

---

## 2. The correction that shaped this spec

There are **two** "next month" features in this codebase, and they are **not alternatives** — they are different actions, on different screens, with different meanings:

| | **Move to next month** | **Continue** |
|---|---|---|
| Screen | **Log Work** (`TimesheetTab`) | **Task List** (`TaskListTab`) |
| Meaning | *"Nothing was done on this ticket. Push it forward."* | *"Work is under way but unfinished. Carry the remainder forward."* |
| Code | `TimesheetViewModel.MoveMonthAsync` | `IBacklogContinuationService.ContinueAsync` (P20) |
| Effect | **Bumps `period_month` on the SAME row.** The ticket leaves this month. | **Clones the backlog into a NEW row** at the target period, `Type = Continue`, copies tags + not-Done tasks, writes a `continued` audit row. **The original stays**, with its logged hours. |
| Rows after | 1 (updated) | 2 (original + copy) |

**This spec implements Move only.** Continue belongs to the Task List screen and to its own milestone.

*(The first draft of this brainstorm presented these as a choice. That was a false dilemma — an artefact of finding two "next month" things in the code and assuming one had to win. The user corrected it.)*

---

## 3. What blocks a pure-Angular fix — and why it cannot be skipped

`BacklogEndpoints.cs` has **zero `.Produces<T>()`**. Every one of its 17 routes returns `IResult` via `Results.Ok(x)`, and **ApiExplorer cannot infer a response schema from that**. So OpenAPI describes them as:

```json
"/api/backlogs/{id}": { "get": { "responses": { "200": { "description": "OK" } } } }
```

No content. No schema. The generated TypeScript client therefore **does not contain them at all** — `ng-openapi-gen.json` includes only `Auth`, `Timesheet` and `SmartFill`, the three tags whose C# declares a response type.

M8.4/W2 left a **self-enforcing invariant** in that config file, and it is load-bearing:

> *"A route joins this list when, and only when, its C# gains a `.Produces<T>()`. **Annotate first, then generate. Never widen this list to 'fix' a missing method.**"*

The reason is not tidiness. Generating an undeclared route emits a method typed **`void` for an endpoint that in fact returns data** — **a client that lies, committed to the repo, that a later wave would reasonably trust.** M8.4 already shipped one near-miss of exactly this shape: codegen *succeeded* against a document that described 74 of 80 routes as empty, and would have handed Wave 4 a `void`-typed week grid.

**Therefore the C# annotation is Wave A of this work, not an optional cleanup.** The alternative — hand-writing HTTP calls outside the generated client — is precisely the hand-maintained-wire-types bug that the whole M8.4 design exists to prevent.

---

## 4. The five routes

All already exist and are tested. Only their **OpenAPI description** is missing.

| Route | Success | Also |
|---|---|---|
| `GET /api/backlogs/{id}` | **200** `BacklogDto` | 404 |
| `PUT /api/backlogs/{id}` | **200** `SavedBody(rowVersion)` | 400 `ValidationBody` · 404 · 409 `ConflictBody` |
| `POST /api/tasks` | **200** `TaskItemDto` | 404 |
| `PUT /api/tasks/{id}/active` | **204** | 404 |
| `PUT /api/tasks/{id}/order` | **204** | 404 |

Response shapes were **read from the handlers**, not assumed.

---

## 4.1 Correction: Log Work has **no** read-only team view

The first draft of this spec said all three controls are *"disabled in the read-only team view"*, copying WPF's `IsEnabled="{Binding CanEdit}"`. **That view does not exist on the web Log Work screen.** `LogWorkComponent` calls `getWeek(monday)`; `allUsers` defaults to `false` and is **never** passed `true`. `grep -r readOnly src/app` returns **nothing**.

So there is nothing to guard, and **no guard is written.** The alternative — a `readonly readOnly = signal(false)` that is always `false` — is a variable pretending to check a mode that cannot be entered: **the same species of lie as the `toast.show('Task added')` button M8.4/W4 deleted.**

**When a later milestone adds `allUsers` to Log Work, the guard lands with it.** *(User's decision, 2026-07-13.)*

---

## 5. The three features

### 5.1 `+ Add task`

- A button in the group's expanded footer — the same place WPF puts it.
- Opens a **modal dialog** (name, OK/Cancel). *User's explicit choice: match WPF rather than an inline input.*
- `POST /api/tasks { backlogId, taskName, orderIndex }` where **`orderIndex` = the group's current task count** (append at the end). This is exactly what `RequestGroupVm.AddTaskAsync` does: `InsertAsync(new TaskItem(0, BacklogId, name, Tasks.Count, true))`.
- On success: **re-fetch the week** — the new task must appear as a row.
- **No read-only guard** — see §4.1. Log Work has no team view.

### 5.2 `Move to next month ▶`

- Same footer, right-aligned — as in WPF.
- **Hidden for the hidden `DEFAULT` backlog.** It holds the recurring default tasks and **must appear in EVERY month**, so it must never belong to one. WPF guards this **twice** — the UI hides the button (`CanMoveMonth => BacklogCode != "DEFAULT"`) *and* the command refuses it. **Do both.** Defence in depth is deliberate here.
- Flow:
  ```
  GET  /api/backlogs/{id}        -> the full backlog + its rowVersion
  next = (periodMonth ?? monthOf(the Monday currently displayed)) + 1 month
  PUT  /api/backlogs/{id}        -> every field unchanged except periodMonth = next
                                     expectedVersion = the rowVersion just read
  ```
- **The `GET` is mandatory, not an optimisation.** `PUT /api/backlogs/{id}` requires the **full** `BacklogUpdateRequest` (15 fields) *and* an `expectedVersion` — and `WeekBacklogGroup` carries `BacklogId`, `PeriodMonth` and `Type` but **not the backlog's `rowVersion`**. There is nowhere else to get it.
- **Month derivation.** WPF uses `backlog.PeriodMonth ?? SelectedMonth` (its month filter). The web Log Work screen is a **week** grid with no month filter, so the fallback is **the month of the Monday currently on screen**. *User's explicit choice.* December rolls to the following January.
- **No read-only guard** — see §4.1.
- **Audited.** The server already passes `changedByUserId` / `changedByName` on every checked backlog write (M8.3 Wave-2 rule #4). Nothing to do client-side.
- **On success:** re-fetch the week. **The ticket leaves the current view** — that is the whole point of the action.

#### 409 on Move does NOT reuse the cell conflict dialog

M8.4's conflict dialog was built for a **timesheet cell**, and its flow is *"keep their value / overwrite with mine"* — a **merge** decision between two numbers. **A moved backlog has no value to merge.** Someone else changed the ticket; there is nothing to reconcile.

So on a 409 from `PUT /api/backlogs/{id}`: **show the message, re-fetch the week, and stop.** If the user still wants the ticket moved, they click Move again — which now reads the fresh version.

**Do not silently retry.** The other person's change may well have *been* a move, and a blind retry would push the ticket two months forward.

### 5.3 Drag-and-drop: reorder + trash

*User's explicit choice: keep WPF's full drag-and-drop rather than a simpler delete button.*

- **New dependency: `@angular/cdk`** (`@angular/cdk/drag-drop`).
- A **⠿ grip** on every task row. In WPF the same grip does **both** jobs, and its tooltip says so: *"Drag to reorder, or drop on the trash to delete."*
- **Drag within the group** → reorder → `PUT /api/tasks/{id}/order { orderIndex }` **for each row whose index changed**.
- **Drag onto the trash zone** → **soft delete** → `PUT /api/tasks/{id}/active { isActive: false }`. WPF does exactly this (`SetActiveAsync(taskId, false)`). **Nothing is hard-deleted.**
- The trash zone is pinned just above the day-totals footer — the same position as WPF. **No read-only guard** — see §4.1.
- On either: re-fetch.

#### 🔴 Both writes are BUMP-ONLY, and that is deliberate

`SetOrderAsync` and `SetActiveAsync` have **no `*CheckedAsync` sibling, by design.** From M8.2's recorded decisions:

> *`SetOrderAsync` runs **once per row** during a drag, so a single check-and-bump template would **409-storm on an ordinary reorder**.*

They still **bump** `row_version` — *"bumping without checking is safe; checking without bumping is the bug the mechanism exists to prevent."* **Do not add a version check to either. Do not send a `rowVersion` on these requests.** An agent that "fixes" this will make a normal drag unusable.

---

## 6. Waves

| Wave | Scope | Gate |
|---|---|---|
| **A** — C# | `.Produces<T>()` on the five routes above. **Metadata only — zero behaviour change.** | **815 .NET tests, unchanged.** Any movement in that number is a bug. |
| **B** — codegen | Add the `Backlog` tag to `ng-openapi-gen.json`; regenerate `src/app/api/`; **commit the output**. | `npm run build` clean with the API **not running**. The generated `BacklogDto` carries `rowVersion` and `periodMonth`; `TaskItemDto` carries `id`. |
| **C** — Angular | The three features + `@angular/cdk`. | `npm test` — 124 existing plus the new ones. Zero failures. |

Sequential. B depends on A (a route cannot be generated before it is described); C depends on B (it calls the generated client).

🔴 **Wave B must not run the API against the real database.** `Program.cs` defaults `DbPath` to the desktop app's app-local `timesheet.db` — the user's **live production data** — and startup runs migrations and bootstrap against it. Pin **all three** seams (`TimesheetApp:DbPath`, `:ConfigPath`, `:KeyRingPath`) to a temp directory. **`DbPath` alone is not enough**: `Program.cs` uses `||`, so setting `DbPath` *without* `ConfigPath` silently falls back to the **fully default production config**. Afterwards, prove it: the real DB must have **no `-wal`/`-shm` beside it** — SQLite in WAL mode always creates those on open, so their absence is the proof.

---

## 7. Testing

Automated, all pure functions — `npm test` exists for exactly this:

- **Month derivation.** `periodMonth ?? monthOf(displayedMonday)`, then `+1`. Including **December → the following January**.
- **The `DEFAULT` guard.** The button is hidden, *and* the action refuses, for `BacklogCode == "DEFAULT"`.
- **Reorder index sequence.** Moving row 3 above row 1 produces the correct `orderIndex` for **every** row whose position changed — not just the dragged one.
- **Add task `orderIndex`** = the group's current task count.
- **No `rowVersion` is sent** on `/order` or `/active`. (A test that fails if someone adds one.)

Not automated, and honestly so: the **feel** of dragging, and whether `@angular/cdk` behaves inside a grid with sticky headers. That is a click-through.

---

## 8. Risks

**Drag-and-drop is the largest unknown.** `@angular/cdk/drag-drop` inside a table with a sticky header and a sticky footer can be awkward. If it fights, the fallback is a 🗑 button per row plus ↑↓ buttons — but the user chose drag-and-drop, so try the real thing first and **report rather than silently downgrade**.

**One drag = N writes** (one `PUT /order` per displaced row). On a shared-drive SQLite database that may feel slow. WPF has the identical shape, so it is not a regression — but it is worth measuring rather than assuming.

---

## 9. Out of scope

- **`Continue` is not added to Log Work.** It is the Task List screen's button and its own milestone (§2).
- **Nothing is hard-deleted.** Delete means `is_active = false`.
- The five other stubbed Angular screens (Backlog, Task List, Daily Report, Reports, Users, Settings) are untouched.
