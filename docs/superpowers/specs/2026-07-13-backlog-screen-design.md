# M8.6 — The Backlog screen: list, create, edit

**Date:** 2026-07-13
**Status:** approved
**Base:** `main` @ `fdd2026` — M8.5 merged. **995 tests green** (658 Core/WPF + 172 API + 165 Angular).
**Branch:** `feature/m8.6-backlog-screen-2026-07-13`

---

## 1. Why this exists

The user clicked **"+ New backlog"**, got a toast, and nothing happened.

`backlog.component.html:7` —
```html
<button class="btn btn-primary" (click)="notify('New backlog created')">+ New backlog</button>
```

It announces a creation that never happened. The **Edit** button (`:40`) does the same. The list is structurally empty, because `WorklogService.getBacklogs()` is `of([])` — and the screen's own empty state admits it (`backlog.component.html:46`): *"Connect `WorklogService.getBacklogs()` or clear your filters."*

The Backlog screen is still the vendored design's shell. This milestone makes it real.

---

## 2. The invariant that governs the whole milestone

ASP.NET Core's ApiExplorer **cannot infer a response schema from a handler that returns `IResult` via `Results.Ok(x)`**. A route with no `.Produces<T>()` is described in OpenAPI as `"200": { "description": "OK" }` — no content, no schema — and `ng-openapi-gen` therefore **omits it entirely**.

`ng-openapi-gen.json` carries the rule, and it is load-bearing:

> *"A route joins this list when, and only when, its C# gains a `.Produces<T>()`. **Annotate first, then generate. Never widen this list to 'fix' a missing method.**"*

Generating an undeclared route emits a method typed `void` for an endpoint that in fact returns data — **a client that lies, committed to the repo, that a later wave would reasonably trust.**

M8.5 turned this from a comment into a build failure: `src/TimesheetApp.ApiTests/OpenApiContractTests.cs` (15 `InlineData` cases) asserts, per route, that a response **schema** exists, the route is **tagged**, and the **`operationId`** matches the `.WithName`. **Every route this milestone adds gets its rows there too.**

### So "+ New backlog" toasts for exactly one reason

`POST /api/backlogs` **exists, works, is tested, and re-reads the row after insert so it can return the server-assigned `CreatedAt` and `RowVersion`** (`BacklogEndpoints.cs:62-91`). It simply carries no `.WithTags` / `.Produces<T>()` — so there is **no `backlogCreate` function in the generated client to call.** `[VERIFIED]`

**The C# annotation is Wave A, not optional cleanup.**

---

## 3. Scope (user's decision, 2026-07-13)

**List + Create + Edit + Assignee + PCA contact + the task sub-editor + audit history.**

**Out of scope, deferred to their own slice:** the **tag picker** (a popup of checkable coloured chips with type-to-filter, replace-all on save, version riding the parent backlog) and the **template applier**. They are the two heaviest components on the screen and the two least used. A backlog created in M8.6 therefore carries **no tags** — a real, accepted loss.

**Also out of scope, and consistent with Log Work:** no **TEAM column** and no **`TeamFilter`** control. Log Work has no team view either; when a later milestone adds one, the Backlog screen's lands with it. The screen scopes to the caller's teams implicitly, as every route already does.

---

## 4. The six WPF behaviours we are NOT porting (user's decision, 2026-07-13)

The WPF Backlog screen is the source of truth for *what the feature is*. It is **not** the source of truth for *how it behaves*, because six of those behaviours are defects. All six are `[VERIFIED]` against code.

### 4.1 The `DEFAULT` pseudo-backlog is visible, and editable — and the XAML says it isn't

`RequestsTab.xaml:229-230` asserts:
```xml
<!-- DEFAULT backlog never reaches this editor (it has no Edit affordance),
     so no extra guard is needed here. -->
```

**This is false.** `BacklogsViewModel` is the **only** list screen in the app that does not filter `DEFAULT` — Task List (`TaskListViewModel.cs:18,220`), the standup picker (`DailyReportViewModel.cs:208`), Smart Input (`SmartInputPanelVm.cs:100`) and retention all exclude it. The Edit button (`RequestsTab.xaml:99-103`) has **no visibility binding at all** and renders on every row.

And it corrupts on save: `DEFAULT`'s project is the literal string `"DEFAULT"`, which is **not** in `BacklogProjects.All` (`ARCS`, `PlusArcs`, `ARMS`, `Other` — `Entities.cs:61-65`). So opening it shows a **blank** Project combo, and saving writes `project = ''`.

**→ The web screen excludes `DEFAULT` from the list.** Its recurring tasks (Annual Leave / Meeting / Other) are managed in **Settings** (`SET-04: DefaultTasks sync`, `SettingsTab.xaml:206-211`) and reconciled into every team's DEFAULT backlog by `IDefaultTaskSyncService`. The DEFAULT backlog is a *materialization target*, not something you edit here. Hiding it costs nothing.

**Excluded client-side, not server-side.** `GET /api/backlogs` keeps returning it, because **Log Work needs it** (`ReadModels.cs:68`: *"EVERY backlog item (incl. DEFAULT…)"*). The filter belongs to the screen that wants it — exactly as `TaskListViewModel` does it.

### 4.2 Code and Project are not validated

`IsNullOrWhiteSpace` never appears in `RequestsViewModel.cs`. A backlog with an **empty code and empty project** saves fine, so long as it has one task.

The API is stricter and correct (`BacklogEndpoints.cs:70-71`):
```csharp
if (string.IsNullOrWhiteSpace(req.BacklogCode) || string.IsNullOrWhiteSpace(req.Project))
    return Results.BadRequest(new ValidationBody("BacklogCode and Project are required."));
```

**→ Adopt the API's rule.** Validate client-side *and* surface the server's 400 verbatim if it ever fires. This one cannot be ported even if we wanted to — the API would reject it.

### 4.3 An invalid estimate or progress does not block the save — it silently writes `NULL`

`ParseEstimate` / `ParseProgress` (`RequestEditorViewModel.cs:112-131`) set `ErrorMessage` and return `null`. Then `SaveNewAsync:194` unconditionally does `Editor.ErrorMessage = null;` and saves anyway. `SaveEditAsync` never even looks at `ErrorMessage`.

There is a **test asserting this behaviour** — `RequestsViewModelTests.cs:346-360`, `SaveNew_does_not_persist_out_of_range_progress`: set `ProgressText = "150"`, save, assert the row **saved** with `ProgressPercent == null`.

**→ A parse error blocks the save.** The field shows the error and Save is refused.

### 4.4 Renaming or reordering an existing task in the editor is silently discarded

The row's `TextBox` is `TwoWay`-bound to `EditableTaskRowVm.TaskName`, and ▲▼ reindex the VM. But `SaveEditAsync` **only ever `InsertAsync`es rows with `ExistingTaskId == 0`** (`RequestsViewModel.cs:243-247`). There is no update call and no `SetOrderAsync` for existing rows. Rename an existing task, press Save, and the change is gone.

**→ The web editor persists both.** Both endpoints already exist: `PUT /api/tasks/{id}` (checked) and `PUT /api/tasks/{id}/order` (bump-only). **`PUT /api/tasks/{id}` is not annotated — Wave A must annotate it.**

### 4.5 The "≥ 1 task" rule is create-only

`SaveNewAsync:190-195` guards it. `SaveEditAsync:221-250` does not — remove every task and it saves.

**→ Enforce it on both.**

### 4.6 A deactivated assignee or PCA contact is silently cleared on save

The **grid** resolves names from **all** users (`_users.GetAllAsync()`, `RequestsViewModel.cs:96` — deliberate, so a deactivated assignee's name still shows). The **editor dropdowns** offer only **active** ones. `ForEdit` (`RequestEditorViewModel.cs:194-197`) falls back to the `Unassigned` / `NoPcaContact` sentinel when the saved value is no longer active — and the sentinel maps to `null` on save.

So: open a backlog whose assignee has since left, press Save without touching anything, and **the assignee is gone.** Same for PCA contact. No warning.

**→ The dropdown keeps the current value even when inactive**, rendered as `Name (inactive)`, so an untouched save round-trips it. New assignments still offer only active users. This needs `GET /api/users/all` and `GET /api/pca-contacts/all` — both exist, neither is annotated.

---

## 5. Two server bugs, and one that turned out not to be

### 5.1 🔴 A user in zero teams creates a permanently invisible backlog — and gets `200 OK`

`POST /api/backlogs` correctly refuses to take a team from the wire. It takes it from the session (`BacklogEndpoints.cs:73`):
```csharp
int? teamId = currentTeam.ActiveTeamId > 0 ? currentTeam.ActiveTeamId : null;
```

`ApiCurrentTeamService.InitializeAsync` (`:57-70`) resolves `ActiveTeamId` to the persisted team if still a membership, else the first available, **else `0`**. So a caller in **no team** inserts a row with `team_id = NULL`, and then:

- `GET /api/backlogs/{id}` 404s it — `AuthorizedBacklogAsync` (`:407-424`) rejects `TeamId is not { } teamId` **for everyone, admins included**.
- `GET /api/backlogs` never returns it — `team_id IN (…)` cannot match `NULL`.
- No SignalR fires — `if (teamId is { } tid)` (`:89`).
- **And the handler still returns `200` with the full `BacklogDto`.**

The UI would render it, then lose it on the next refresh. This is the **same permanent-invisibility end state M8.3 already paid for through `UPDATE`** — reached this time through `INSERT`. The `UPDATE` door was closed *structurally* (`BacklogUpdateRequest` has no `TeamId` field; the handler re-reads the entity and `with{}`-patches only the DTO's fields). **The `INSERT` door is still open, and nobody has walked through it because no client can call the route yet.**

**→ Fix:** refuse. `400 ValidationBody` with a sentence a human can act on. Three lines, and it converts silent permanent data loss into a clear error.

### 5.2 `POST /api/tasks` can return a 400 it never declared

M8.5's own defect. The handler `BadRequest(new ValidationBody("TaskName is required."))` at `BacklogEndpoints.cs:228`, but the annotation block (`:242-245`) declares only `.Produces<TaskItemDto>()` and `.Produces(404)`. The generated client has no typed 400. `BacklogUpdate`'s block is complete; `TaskCreate`'s is not.

**→ Fix while we are in the file.**

### 5.3 The duplicate-`backlog_code` "latent 500" is NOT reachable — and we are not adding a guard

`POST /api/backlogs` does not check for a duplicate code, and there is no unique index. `GetByCodeAsync` uses `QuerySingleOrDefaultAsync`, which **throws** on two rows. That looked like a latent 500.

**It is not.** `GetByCodeAsync` has **zero production call sites.** `[VERIFIED]` — `grep -rn GetByCodeAsync src/ --include=*.cs` returns only its own declaration, its own implementation, and **two comments explicitly routing around it**:

```csharp
// BacklogContinuationService.cs:34
// Uses SearchAsync (not GetByCodeAsync, which throws when the code is non-unique across months).

// DefaultTaskSyncService.cs:39
// P10 (R2): DEFAULT is unique per team — resolve per team, NEVER the global GetByCodeAsync.
```

It is dead code with two warning signs nailed to it.

**And duplicate codes across months are correct by design** — that is precisely what `Continue` produces (same code, next month, `Type = Continue`). A uniqueness check on `backlog_code` would **break Continue.**

**→ No duplicate guard.** WPF has none, nothing 500s, and inventing a business rule the application does not have is what *Simplicity First* forbids. `GetByCodeAsync` is flagged as dead-and-dangerous in the PR description, not deleted (*Surgical Changes*).

---

## 6. Wave A — C#

**Seven routes annotated, ONE route added. Two DTOs added. Two bugs fixed.** Everything else is metadata only — no behaviour change to any existing caller.

### 6.1 The routes

| Route | `.WithName` | Tag | `.Produces<T>()` | Why |
|---|---|---|---|---|
| `GET /api/backlogs` | `BacklogList` | `Backlogs` | `List<BacklogListItemDto>` | the list |
| `POST /api/backlogs` | `BacklogCreate` | `Backlogs` | `BacklogDto` · 400 `ValidationBody` | **the button** |
| 🆕 **`GET /api/backlogs/{id}/audit`** | `BacklogAudit` | `Backlogs` | `List<BacklogAuditDto>` · 404 | **the change-history panel** |
| `PUT /api/tasks/{id}` | `TaskUpdate` | `Tasks` | `SavedBody` · 400 · 404 · 409 `ConflictBody` | **§4.4 rename + reorder** |
| `GET /api/users` | `UserListActive` | `Users` | `List<UserDto>` | assignee dropdown |
| `GET /api/users/all` | `UserListAll` | `Users` | `List<UserDto>` | **§4.6** + list name resolution |
| `GET /api/pca-contacts` | `PcaContactListActive` | `PcaContacts` | `List<PcaContactDto>` | PCA combo |
| `GET /api/pca-contacts/all` | `PcaContactListAll` | `PcaContacts` | `List<PcaContactDto>` | **§4.6** |

Plus: `POST /api/tasks` gains its missing `.Produces<ValidationBody>(400)` (§5.2).

🆕 **`GET /api/backlogs/{id}/audit` does not exist today.** `[VERIFIED]` — grep for an audit route across `src/TimesheetApp.Api/Endpoints/` returns **nothing**, while `IBacklogRepository.GetAuditAsync(int)` (`:46`) has been sitting there unexposed since v2. Without this route the change-history panel would render an empty box forever. It is ~10 lines: the repository method exists, and `AuthorizedBacklogAsync` (`BacklogEndpoints.cs:407-424`) is the same team guard every other single-backlog route already uses — so a 404 for another team's backlog comes for free.

```csharp
public sealed record BacklogAuditDto(
    int Id, string Field, string? OldValue, string? NewValue,
    string? ChangedByName, DateTimeOffset ChangedAt, string? Note);
```
Projected from `BacklogAuditEntry` (`Entities.cs:69-72`), dropping `BacklogId` (it is the path param) and `ChangedByUserId` (the panel renders the **name**, and WPF audits by name precisely so a deleted user's history still reads).

🔴 **`SettingsEndpoints.cs` maps ~40 routes and NOT ONE is annotated.** Annotate **exactly these four**. Do not annotate a neighbour "while you're there" — a route that gains a `Users` / `PcaContacts` tag joins the generated client the moment `includeTags` grows, and an undeclared one would arrive typed `void`.

### 6.2 `BacklogListItemDto` — a new DTO, mirroring WPF's `BacklogListItem`

```csharp
public sealed record BacklogListItemDto(
    int Id, string BacklogCode, string Project, int TaskCount,
    string? PeriodMonth, string? Type, int? AssigneeUserId);
```

The `TASKS` column needs a per-backlog count of **active** tasks. No DTO carries one and no endpoint returns one — it is a mockup column today.

The endpoint calls `SearchAsync(term, EffectiveTeamIds(...))`, then **the already-existing, already-batched `ITaskRepository.GetActiveByBacklogsAsync(backlogIds)`** — the single `IN` query WPF added as an explicit N+1 fix (`RequestsViewModel.cs`, read #4). **Zero new repository code. No N+1.**

**Why a separate DTO rather than `TaskCount` on `BacklogDto`:** `BacklogDto` is the **editor's** shape and is what `GET /{id}` and `POST` return. Putting a count on it would force every single-backlog read to compute something nobody uses. Two shapes, two purposes — exactly as WPF has it.

**No `AssigneeName`, no `TeamId`, no `RowVersion` on the list item:**
- the client already needs the full users list for the editor, so it resolves the name itself — and doing it that way is what *gives* us §4.6 for free, because `users/all` includes the deactivated ones;
- no TEAM column (§3);
- the Edit button does a fresh `GET /{id}`, which returns the authoritative `rowVersion`. A version carried on a list row would be **stale by construction** and is precisely the kind of thing that gets narrowed with a `!` and silently overwrites someone.

### 6.3 Gate for Wave A

**The existing 830 .NET tests are UNCHANGED.** New tests are added on top. **Movement in the 830 is a bug — do not reconcile a mismatch by deleting or editing a test.**

---

## 7. Wave B — codegen

`ng-openapi-gen.json`:
```json
"includeTags": ["Auth", "Timesheet", "SmartFill", "Backlogs", "Tasks", "Users", "PcaContacts"]
```
Then `npm run gen:api`; **commit the generated output.**

🔴 **The API must NOT run against the real database.** `Program.cs` defaults `DbPath` to `C:\Users\Admin\Documents\TimesheetApp\timesheet.db` — the user's **live production data** — and startup runs `DatabaseInitializer` + `TeamBootstrapService` + `AdminBootstrap` against whatever it points at.

**Pin all three seams** to a throwaway temp directory: `TimesheetApp:DbPath`, `TimesheetApp:ConfigPath`, `TimesheetApp:KeyRingPath`. **`DbPath` alone is not enough** — `Program.cs` uses `||`, so setting `DbPath` *without* `ConfigPath` silently falls back to the **fully default production config**.

**Proof it worked:** the startup log must read `AdminBootstrap: no users yet, nothing to bootstrap`. That line is unforgeable evidence the process opened an **empty** database. *(Do NOT try to prove it by asserting the real DB has no `-wal` sidecar — the user's app is running right now, and a live SQLite database in WAL mode **always** has one. That test would abort on a false alarm.)*

**Gate:** `npm run build` clean **with the API not running**. `backlogList`, `backlogCreate`, `taskUpdate`, `userListActive`, `userListAll`, `pcaContactListActive`, `pcaContactListAll` all exist under `src/app/api/fn/`. `BacklogListItemDto`, `BacklogCreateRequest`, `TaskUpdateRequest`, `UserDto`, `PcaContactDto` all exist under `src/app/api/models/`.

---

## 8. Wave C — Angular

The reference implementation is `src/timesheet-web/src/app/pages/log-work/`. Follow it.

### 8.1 Pure functions in their own files, each with its own spec

> *"Everything correctness-critical is a pure function… **a pure function is the only kind you can actually pin down with a test**."* — `grid-state.ts:8-11`

**`backlog-form.ts`**
- `validate(form): string | null` — Code required, Project required (§4.2); estimate/progress parse errors block (§4.3); ≥ 1 active task (§4.5).
- `parseEstimate(text): { value: decimal | null, error: string | null }` — empty → `null`, no error. Unparseable or `< 0` → error. **Never silently `null`.**
- `parseProgress(text)` — same, `0..100`.
- `periodMonth(year, month): string` — `` `${year}-${String(month).padStart(2,'0')}` ``.
- `toCreateRequest(form): BacklogCreateRequest` — the 14 domain fields. **No `teamId`** (the server sets it; it is not on the wire).
- 🔴 **`toUpdateRequest(dto: BacklogDto, form): BacklogUpdateRequest`** — **starts from the loaded DTO and overrides only the fields the edit form actually shows.**

  **It cannot start from the form.** `PUT /api/backlogs/{id}` **replaces the whole record** (`BacklogRepository.cs:147-157` writes all 15 data columns unconditionally), and **half the fields are hidden on edit** — both dates, both deadlines, progress, PCA contact. Build the request from the form and every one of them is written as `NULL`. This is the same trap that once nulled a `team_id` and made a backlog invisible to everyone, permanently, while every test passed.

  `expectedVersion: requireRowVersion(dto.rowVersion)` — **never `dto.rowVersion!`**, never `0`. `move-month.ts:35-54` is the exemplar; copy its shape.

**`backlog-list.ts`**
- `buildRows(items: BacklogListItemDto[], users: UserDto[]): Row[]` — joins the assignee name, and **excludes `backlogCode === 'DEFAULT'`** (§4.1).
- `filterRows(rows, { term, project, type, assignee, month })` — AND across all five; `term` matches **code OR project**, `Contains`, case-insensitive (WPF's rule).
- `rebuildOptions(rows)` — the four dropdowns are built **from the loaded data**, not from a hard-coded literal, with an `All` sentinel and `Distinct().OrderBy()`. A filter whose selected value vanishes from the data resets to `All` (WPF's self-healing behaviour, `RequestsViewModel.cs:136-140`).

  *(Today they are hard-coded: `monthOpts = ['All','2026-06','2026-07','2026-08']`, `assigneeOpts` is a list of bare display names. Mockup props.)*

- **Filtering stays client-side.** `GET /api/backlogs?term=` exists, but the dataset is a few hundred rows for a 2–5 person team, WPF filters in memory, and client-side gives instant feedback with no round trip. SignalR keeps it fresh.

**`task-edit.ts` — 🔴 the milestone's most dangerous function**

`planTaskWrites(loaded: TaskItemDto[], rows: EditRow[], backlogId): { deletes, inserts, updates }`

**`PUT /api/tasks/{id}` writes name, order AND status in one checked call.** `[VERIFIED]`:
```csharp
public sealed record TaskUpdateRequest(string TaskName, int OrderIndex, string Status, long ExpectedVersion);
```
```sql
-- TaskRepository.cs:106-110
UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex, status = @Status,
                 row_version = row_version + 1
  WHERE id = @Id AND (@ExpectedVersion IS NULL OR row_version = @ExpectedVersion)
  RETURNING row_version;
```

**So rename and reorder are ONE write, not two.** `PUT /api/tasks/{id}/order` is not used by this screen at all. That removes an ordering hazard by removing the call that could be ordered wrongly — the checked rename and the bump-only reorder would otherwise have had to run in exactly one order (rename first), because the bump-only write invalidates the checked write's `expectedVersion` *on the same row*.

### 🔴 `status` MUST come from the loaded task, NEVER from the form

**The Backlog editor does not show a task's status.** But the `UPDATE` above writes `status` **unconditionally**. Build `TaskUpdateRequest` from the form — which knows only name and order — and `Status` binds to `null` (Swashbuckle emits no `required` for a positional C# record), so the save writes **`status = NULL`** on every task it touches.

**That silently wipes `Todo` / `In-process` / `Done` / `Pending`, and the entire Task List screen is built on that column.** It is the same whole-record trap as `toUpdateRequest`, one level down, and it is *harder* to see because the field is not on the form at all.

```ts
// The ONLY correct shape.
{ taskName: row.name.trim(),
  orderIndex: row.orderIndex,           // from the reindex below
  status: loaded.status ?? 'Todo',      // 🔴 ROUND-TRIPPED. Never omitted, never from the form.
  expectedVersion: requireRowVersion(loaded.rowVersion) }   // never `!`, never 0
```

### The gap

`SetActiveAsync` soft-deletes by setting `is_active = 0` and **leaves `order_index` untouched** (`TaskRepository.cs:123-129`), while the read is `WHERE is_active = 1 ORDER BY order_index`. So survivors sit at 1,2,3 — a **gap** — and `ORDER BY` with a **tie** is arbitrary. This trap ate two revisions of the M8.5 plan.

> **The save reindexes EVERY surviving row 0..n**, matching WPF's `ActiveTasks` (`RequestEditorViewModel.cs:150-158`). Self-healing, and it cannot tie. A new task's `orderIndex` comes from the same reindex. **Only rows whose `(name, orderIndex)` actually changed produce a write** — an untouched, already-contiguous list writes nothing.

### Write order

1. `PUT /api/backlogs/{id}` — checked, the **backlog's own** version. *(On create: `POST /api/backlogs` → then the tasks, using the returned `id`.)*
2. **deletes** — `PUT /api/tasks/{id}/active { isActive: false }` — **bump-only, no version**
3. **inserts** — `POST /api/tasks { backlogId, taskName, orderIndex }` — no status; the server defaults it to `Todo`
4. **updates** — `PUT /api/tasks/{id}` — **checked**, name + order + status

Steps 2–4 touch **disjoint rows**, so no step can invalidate another's `expectedVersion`. There is no intra-save ordering hazard, and there is no 409-storm: **each row is written at most once, with its own version.**

**Do not send a `rowVersion` on `/active`.** It carries none by design. An agent that "fixes" this makes every delete fail.

**Not transactional.** Four phases of independent HTTP calls; a failure part-way leaves partial state. WPF has the identical shape (`SaveEditAsync` is a sequence of independent repo calls), so it is not a regression — but the editor must **re-read on any failure**, never leave the dialog showing stale rows as if the save had succeeded.

### 8.2 Components

**`backlog.component`** — the list. `OnPush` (it is not today). Real data via `forkJoin([getBacklogs(), getUsersAll()])`. `loading` and `loadError` signals (it has neither today). SignalR in two lines:
```ts
this.realtime.dataChanged.pipe(takeUntilDestroyed()).subscribe(() => this.refresh.next());
this.realtime.start();
```
`refresh` is a **`Subject`, not a signal** — `toObservable()` replays a signal's current value, which double-fetches on every load (`log-work.component.ts:161-168`).

**`backlog-editor.component`** — the modal. A presentational dialog with its own scoped backdrop, following `add-task-dialog.component.ts`. There is **no global modal class**; each dialog ships its own.

Field visibility follows WPF's rule verbatim (`RequestsTab.xaml:201-202, 229-230`): *operational* fields — start/end date, both deadlines, progress, PCA contact — are **create-only**, because they are edited inline on the Task List. Code, project, assignee, month/year, type, both estimates, note and tasks are editable in both modes. **Audit history** (`field: old → new`, then `ChangedByName · dd/MM/yyyy HH:mm`, newest first) shows on **edit only**.

🔴 **House button classes are `btn`, `btn-primary`, `btn-ghost`, `btn-soft`, `btn-danger`, `btn-sm` — and nothing else.** `.mini-ghost` **does not exist**; it was invented in an earlier plan and grepping `src/` returns zero hits.

### 8.3 Errors — 400, 404 and 409 are three different things

One handler per entry point, **shaped to the routes it actually calls**. A handler for a status a route cannot return is dead code.

- **400** `{ error }` — a business rule said no. **A return value, not an exception.** Show the server's own sentence.
- **404** — the backlog was deleted, or is not in your team.
- **409** `{ table, id, deleted, detail, message }` — 🔴 **toast + re-read. NOT the merge dialog.**

  The conflict dialog was built for a **timesheet cell**, and its flow is *"keep theirs / overwrite with mine"* — a **merge decision between two numbers**. A backlog edit has no single number to merge. Someone else changed the record; say so, re-read, stop. **Do not silently retry** — their change may have been a `Continue` or a `Move`, and a blind retry would apply this edit on top of it.

  This is the precedent `onMoveError` already set (`log-work.component.ts:606-612`).

### 8.4 Two things I have shipped wrong before

🔴 **Every `async` handler bound to a template output needs a `catch` that never re-throws.** Anything that escapes is an **unhandled promise rejection** — it surfaces in the console and **nowhere the user can see**. The row simply never appears, with no error, and they sit there clicking Save again. *(I shipped this bug three times in M8.5 — in a plan that diagnoses the exact hazard.)*

🔴 **A re-entrancy guard on every async mutation** (`saving`, `creating`). These are **corruption guards, not politeness**: a second click's GET can land *after* the first chain's PUT, read the already-bumped version, and apply the mutation twice.

---

## 9. Testing

Automated:

**The two that matter most — both are silent data loss, and both pass a naive test:**

- 🔴 **`toUpdateRequest` carries every HIDDEN field across.** Load a `BacklogDto` with a start date, an end date, both deadlines, a progress value and a PCA contact — **all six are invisible on the edit form**. Edit only the note. Assert **all six survive** in the request. Build it from the form and every one is `null`.
- 🔴 **`planTaskWrites` round-trips `status`.** Load a task with `status: 'Done'`. Rename it. Assert the emitted `TaskUpdateRequest` still carries **`status: 'Done'`**. This is the one the Task List depends on, and the form does not have the field at all — so *forgetting* it is the default outcome, not an unlikely slip.

The rest:

- **Reindex rewrites every surviving row.** Fixture where `orderIndex` values (1,2,3) deliberately diverge from array positions (0,1,2) — the shape a soft delete leaves behind. Assert `new Set(indices).size === indices.length` — **no ties**.
- **An untouched, contiguous task list emits ZERO writes.** Guards against a save that "reindexes" everything on every open.
- **`DEFAULT` is excluded from the list** — and is still present in what the API returned (proving the filter is the screen's, not the server's; Log Work needs the API to keep returning it).
- **A deactivated assignee round-trips.** Load a backlog whose assignee is inactive, save without touching it, assert the request still carries that `assigneeUserId` (§4.6).
- **A parse error blocks the save.** `progress = "150"` → Save refused, **no request sent at all**.
- **No `rowVersion` on `/active`.** A test that fails if someone adds one.
- **Contract tests** for all seven new routes: schema, tag, `operationId`.

Not automated, and honestly so: whether the modal *feels* right, and the audit panel's layout. That is a click-through.

---

## 10. Waves

| Wave | Scope | Gate |
|---|---|---|
| **A** — C# | 7 routes annotated · `BacklogListItemDto` · orphan-team 400 · `POST /api/tasks` 400 | **The existing 830 are UNCHANGED.** New tests on top. |
| **B** — codegen | `includeTags` += `Users`, `PcaContacts`; regenerate; commit | `npm run build` clean with the API **not running**. |
| **C** — Angular | pure functions + list + editor | `npm test` — the existing 165 plus the new. Zero failures. |

Sequential. **B depends on A** (a route cannot be generated before it is described). **C depends on B** (it calls the generated client).

---

## 11. Out of scope

- **Tags** and **templates** — their own slice (§3).
- **TEAM column / `TeamFilter`** — lands with Log Work's (§3).
- **`Continue`** — it belongs to the **Task List** screen (M8.7), not here. `RequestsTab.xaml` has no Continue affordance; grep confirms it.
- **Deleting a backlog** — it does not exist and never will. `IBacklogRepository.cs:50`: *"No `SetActiveAsync` — Backlogs are NOT soft-deletable (decision 4)."* There is no `is_active` column on `Backlogs`.
- **Server-side search / paging** — the endpoint supports `?term=`; the screen does not need it yet (§8.1).
- **A duplicate-`backlog_code` guard** — deliberately not added (§5.3).
