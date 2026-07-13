import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { map, Observable, of } from 'rxjs';
import {
  Backlog, DailyEntry, LogGroup, Metric, MonthlyRow, Tag, TaskCard,
  TaskTemplate, TeamMember, TreeNode, User, WeeklyRow, DayColumn,
} from '../models/worklog.models';

import { ApiConfiguration } from '../api/api-configuration';
import { ConnectionIdHttpClient } from '../core/realtime.service';
import {
  backlogAudit as backlogAuditFn,
  backlogCreate as backlogCreateFn,
  backlogGet as backlogGetFn,
  backlogList as backlogListFn,
  backlogUpdate as backlogUpdateFn,
  login as loginFn,
  logout as logoutFn,
  me as meFn,
  pcaContactListActive as pcaContactListActiveFn,
  pcaContactNames as pcaContactNamesFn,
  smartFillApply as smartFillApplyFn,
  smartFillValidate as smartFillValidateFn,
  taskCreate as taskCreateFn,
  taskList as taskListFn,
  taskSetActive as taskSetActiveFn,
  taskSetOrder as taskSetOrderFn,
  taskUpdate as taskUpdateFn,
  timesheetClearCell as timesheetClearCellFn,
  timesheetSaveCell as timesheetSaveCellFn,
  timesheetWeek as timesheetWeekFn,
  userListActive as userListActiveFn,
  userNames as userNamesFn,
} from '../api/functions';
import {
  BacklogAuditDto, BacklogCreateRequest, BacklogDto, BacklogListItemDto, BacklogUpdateRequest, LoginResponse,
  MeResponse, NamedRefDto, PcaContactDto, SavedBody, SmartFillTaskRequest, TaskItemDto, TaskUpdateRequest,
  TimeLogDto, UserDto, WeekBacklogGroup,
} from '../api/models';

/**
 * Central data access for the Worklog app.
 *
 * M8.4 / Wave 2 — the transport under the M8.4 methods is now GENERATED, in `../api/**`, from the API's own
 * OpenAPI document (`npm run gen:api`). Nothing in `../api/**` is hand-written or hand-editable: it is the
 * wire contract, and the only way to change it is to change the C# and regenerate.
 *
 * `rootUrl` is `''` (ApiConfiguration), so every generated call is a SAME-ORIGIN RELATIVE path (`/api/...`).
 * That is not cosmetic. `ng serve` (:4200) -> Kestrel (:5080) is cross-site, and a `SameSite=Lax` cookie is
 * NOT SENT on a cross-site XHR: login would 200, and every call after it would go out anonymous -> 401 ->
 * redirect loop. The dev proxy makes the whole app same-origin; an absolute `http://localhost:5080/...`
 * anywhere in this file silently re-breaks it. Never introduce one.
 *
 * WIRED, AND REAL:
 *   - M8.4 — auth, the week grid, Smart Fill, the cell write.
 *   - M8.6/T5 — the Backlog screen's transport: the backlog list / create / audit, the task list and the
 *     checked task update, and the four user + PCA lookups.
 *
 * 🔴 STILL STUBS (the `of(...)` block at the bottom of the class) — AND THEY ARE NOT DEAD CODE YOU MAY
 * RETYPE IN PLACE. Each is still CONSUMED by a screen that binds the VENDORED view model, under
 * `strictTemplates`:
 *
 *     getBacklogs()  -> Backlog[]    backlog.component.ts     (its own task deletes this stub when it
 *                                                              rewrites that component — not before)
 *     getUsers()     -> User[]       users.component.ts       (the Users page is OUT OF SCOPE: no task in
 *                                                              this milestone owns it)
 *     getTaskCards() -> TaskCard[]   task-list.component.ts
 *     getContacts()  -> string[]     settings.component.ts
 *
 * The vendored view models DO NOT MATCH the wire DTOs. `Backlog` is `{code, project, month, assignee}` where
 * `BacklogListItemDto` is `{backlogCode, periodMonth, assigneeUserId}`; `User` is `{name, active}` with NO
 * `id` at all where `UserDto` is `{id?, name?, isActive?, …}`. So changing a stub's return type does not
 * "wire up a screen" — it BREAKS THE BUILD of a component the current task is not allowed to touch.
 *
 * Hence the convention: a real method is ADDED BESIDE its stub under a NEW NAME, and each screen's own task
 * then swaps its component over and deletes the stub it was using. That is why `getBacklogList()` sits next
 * to `getBacklogs()`, and `getPcaContactsActive()` next to `getContacts()`. Deliberate, not duplication.
 */
@Injectable({ providedIn: 'root' })
export class WorklogService {
  private readonly http = inject(HttpClient);
  private readonly apiConfig = inject(ApiConfiguration);

  /**
   * M8.4/W4. The SAME transport, plus one header: `X-Connection-Id`.
   *
   * Every MUTATING route calls `IChangeNotifier.DataChangedAsync(kind, teamId, ctx.ConnectionId)`, and the
   * server uses that connection id to EXCLUDE the caller from its own SignalR broadcast. It can only do so
   * if we send it. Omit the header and the write echoes straight back to the person who made it: the page
   * re-fetches the week and CLOBBERS THE CONFLICT DIALOG the 409 just raised, while the user is reading it.
   *
   * Reads deliberately keep using the plain `HttpClient` — a read notifies nobody, so there is no echo to
   * suppress and no reason to widen the header's blast radius.
   */
  private readonly mutatingHttp = inject(ConnectionIdHttpClient);

  /** `''` — see the class comment. Relative paths, or the auth cookie stops being sent. */
  private get rootUrl(): string { return this.apiConfig.rootUrl; }

  // ---- presentation constants (safe to keep) ----
  readonly TYPE_COLORS: Record<string, { bg: string; c: string }> = {
    Investigate: { bg: '#E8EEFB', c: '#2A5BD7' },
    Implement:   { bg: '#E7F2EC', c: '#1B7A4B' },
    Continue:    { bg: '#E3F0F0', c: '#0E7C66' },
    IT:          { bg: '#ECEAFB', c: '#5B48D6' },
    Estimate:    { bg: '#FBF0DE', c: '#B5791F' },
  };

  readonly AVATAR_COLORS: Record<string, string> = {
    An: '#0E7C66', 'An Nguyen': '#2A6FDB', Binh: '#8B5CF6', 'Binh Tran': '#0EA5A0',
    Chi: '#0891B2', 'Chi Le': '#2563EB', 'Dung Pham': '#DB2777', 'Em Vo': '#9333EA',
    'Giang Do': '#0D9488', 'Huy Bui': '#CA8A04', Nhan: '#0E7C66', 'Phuc Hoang': '#7C3AED',
  };

  readonly WEEK_DAYS: DayColumn[] = [
    { dow: 'MON', date: '06/07' }, { dow: 'TUE', date: '07/07' },
    { dow: 'WED', date: '08/07' }, { dow: 'THU', date: '09/07' },
    { dow: 'FRI', date: '10/07' },
  ];

  avatarColor(name: string | null): string {
    return name ? (this.AVATAR_COLORS[name] ?? '#0E7C66') : '';
  }
  typeColor(type: string | null): { bg: string; c: string } | null {
    return type ? (this.TYPE_COLORS[type] ?? { bg: '#EEF1F0', c: '#5C6560' }) : null;
  }

  // =====================================================================================================
  // AUTH — POST /api/auth/login · POST /api/auth/logout · GET /api/me
  //
  // There is NO `rememberMe` on the wire: LoginRequest is (Username, Password) and nothing else.
  // `IsPersistent = true` is UNCONDITIONAL server-side, so "stay logged in across a browser restart" already
  // holds and there is no flag to send. Do not invent one.
  // =====================================================================================================

  login(username: string, password: string): Observable<LoginResponse> {
    return loginFn(this.http, this.rootUrl, { body: { username, password } })
      .pipe(map(r => r.body));
  }

  logout(): Observable<void> {
    return logoutFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** The authenticated caller's own context. 401 here is how the app learns it is logged out. */
  me(): Observable<MeResponse> {
    return meFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE WEEK GRID — GET /api/timesheet/week
  //
  // Returns the Core read model RAW (WeekBacklogGroup[] -> WeekRow[] -> WeekCell), not a DTO. Each day slot
  // is a WeekCell `{ hours, rowVersion }` — the version is PER CELL, because TimeLogs is keyed by
  // (user_id, task_id, work_date) and that is exactly what a checked write versions.
  //
  // `allUsers: true` is the READ-ONLY team aggregate: it SUMS hours across users, so no single row backs a
  // cell and every rowVersion comes back NULL, by construction. Do not try to write from that view, and do
  // not try to synthesise a version for it — there isn't one to synthesise.
  // =====================================================================================================

  getWeek(monday: string, allUsers = false): Observable<WeekBacklogGroup[]> {
    return timesheetWeekFn(this.http, this.rootUrl, { monday, allUsers })
      .pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE CELL WRITE — PUT /api/timesheet/cell
  //
  // ⚠️ SIGNATURE CHANGED IN M8.4/W2, DELIBERATELY. The old `saveHours(key: string, value: string)` took the
  // vendored grid's `${groupIndex}-${taskIndex}-${dayIndex}` key and returned `Observable<void>`. That shape
  // CANNOT EXPRESS THE CONTRACT, in two independent ways:
  //
  //   1. It has nowhere to put `expectedVersion`. The only value it could send is `null` — and null is not
  //      "I don't know", it DELIBERATELY ASSERTS "I believe this cell is EMPTY". Against a cell that already
  //      has hours that is a lie, and the checked write 409s. Every edit of a pre-existing cell, every user,
  //      forever. (And `0` is not a safe stand-in either: under the five-case table it is a DIFFERENT
  //      assertion again, which either 409s spuriously or silently overwrites someone.)
  //   2. Returning `void` throws away the new row_version — which is precisely the caller's NEXT
  //      expectedVersion. STORE WHAT THE WRITE RETURNS; never re-read it. A read-back is racy: another client
  //      can write in between, and you would then hold THEIR version with YOUR data and silently overwrite
  //      them on the next save. That is the lost update this whole mechanism exists to prevent.
  //
  // So: `expectedVersion` is `number | null` and the caller MUST pass the cell's actual rowVersion when the
  // cell has hours, and `null` ONLY when it genuinely has none. The return is the new version.
  //
  // 400 (8h cap / holiday / weekend / >1 decimal) and 409 (conflict) and 404 (task deleted or not your team)
  // all arrive as HttpErrorResponse — they are the caller's to handle, not this transport's to swallow.
  // =====================================================================================================

  saveHours(
    taskId: number,
    workDate: string,
    hours: number,
    expectedVersion: number | null,
  ): Observable<number> {
    return timesheetSaveCellFn(this.mutatingHttp, this.rootUrl, {
      body: { taskId, date: workDate, hours, expectedVersion },
    }).pipe(map(r => requireRowVersion(r.body.rowVersion)));
  }

  /**
   * DELETE /api/timesheet/cell. Unlike a save, a clear has no "I believe it's empty" case, so
   * `expectedVersion` is REQUIRED here (the C# takes a non-nullable `long`) — you cannot clear a cell you do
   * not already hold a version for.
   */
  clearHours(taskId: number, workDate: string, expectedVersion: number): Observable<void> {
    return timesheetClearCellFn(this.mutatingHttp, this.rootUrl, {
      body: { taskId, date: workDate, expectedVersion },
    }).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // SMART FILL — POST /api/smartfill/validate · POST /api/smartfill/apply
  //
  // 🔴 `apply` returns a FLAT TimeLogDto[] — NOT a week grid — and it spans only min..max OF THE FILLED
  // DATES. The caller MERGES it into the cell map by (taskId, workDate), patching hours + rowVersion per
  // returned row.
  //
  //   - Never REPLACE grid state from it: fill Wed–Thu only and a replace wipes Mon/Tue/Fri off the screen,
  //     and a flat list cannot reconstruct the backlog grouping anyway.
  //   - Never IGNORE it either: the caller is excluded from its own SignalR echo, so this response is the one
  //     and only place it learns the new versions. Drop them and your next inline edit sends a stale version
  //     and 409s against your own Smart Fill, on the happy path, every time.
  // =====================================================================================================

  smartFillValidate(tasks: SmartFillTaskRequest[]): Observable<void> {
    return smartFillValidateFn(this.http, this.rootUrl, { body: { tasks } })
      .pipe(map(r => r.body));
  }

  smartFillApply(tasks: SmartFillTaskRequest[]): Observable<TimeLogDto[]> {
    return smartFillApplyFn(this.mutatingHttp, this.rootUrl, { body: { tasks } })
      .pipe(map(r => r.body));
  }

  // =====================================================================================================
  // TASKS — GET /api/tasks · POST /api/tasks · PUT /api/tasks/{id} · PUT /api/tasks/{id}/order
  //         PUT /api/tasks/{id}/active
  // =====================================================================================================

  /**
   * The tasks of ONE backlog. A READ -> the plain `http`.
   *
   * 🔴 LOAD-BEARING, and not merely "the editor needs rows to display". This response is the ONLY carrier of
   * each task's `status` and `rowVersion`, and BOTH are required to write the task back:
   *
   *   1. `rowVersion` IS the `expectedVersion` of the checked `updateTask` below. There is nowhere else to
   *      get it: the WEEK read model carries a version per CELL, never per task row.
   *   2. `status` must be ROUND-TRIPPED. `PUT /api/tasks/{id}` writes name, order AND status in one call and
   *      binds `Status = req.Status` with no null-guard — while the backlog editor never SHOWS a status. Build
   *      the update body from the form alone and `status` lands as null, silently wiping Todo / In-process /
   *      Done / Pending off every task in the backlog — the column the whole Task List screen is built on.
   *
   * `planTaskWrites` (pages/backlog/task-edit.ts) is what consumes this, and it round-trips both.
   */
  getTasks(backlogId: number): Observable<TaskItemDto[]> {
    return taskListFn(this.http, this.rootUrl, { backlogId }).pipe(map(r => r.body));
  }

  /**
   * Append a task to a backlog. A MUTATION, so `mutatingHttp` — see the field's comment. On the plain client
   * the write echoes back to us over SignalR, we re-fetch, and we clobber our own screen.
   *
   * 🔴 `orderIndex` is the CALLER's to compute, and it must not be `tasks.length`. The server takes it
   * verbatim (`InsertAsync(new TaskItem(0, req.BacklogId, …, req.OrderIndex, true))` — it does NOT derive one),
   * and a soft delete leaves a GAP in the surviving indices, so `length` ties with a live row and
   * `ORDER BY order_index` then puts the new task somewhere arbitrary. Pass `nextOrderIndex(group.tasks)`.
   */
  addTask(backlogId: number, taskName: string, orderIndex: number): Observable<TaskItemDto> {
    return taskCreateFn(this.mutatingHttp, this.rootUrl, { body: { backlogId, taskName, orderIndex } })
      .pipe(map(r => r.body));
  }

  /**
   * Rename / reorder / re-status ONE task in a single CHECKED write: `body.expectedVersion` must be the
   * `rowVersion` `getTasks` returned for that row, and the server 409s if anyone changed it in between.
   *
   * A MUTATION, so `mutatingHttp` — see that field's comment.
   *
   * 🔴 The body MUST carry `status`, round-tripped from the loaded task — see `getTasks` above. The generated
   * `TaskUpdateRequest` types it `status?: string | null`, so OMITTING it compiles perfectly clean and wipes
   * the column at runtime. TypeScript cannot catch this one. `planTaskWrites` is where it is prevented.
   *
   * Because a rename and a reorder ride together in THIS one write, the backlog editor never calls
   * `setTaskOrder` — so the rename-before-reorder ordering hazard cannot arise on that screen.
   */
  updateTask(id: number, body: TaskUpdateRequest): Observable<SavedBody> {
    return taskUpdateFn(this.mutatingHttp, this.rootUrl, { id, body }).pipe(map(r => r.body));
  }

  /**
   * Move one task to a new `order_index`. A MUTATION, so `mutatingHttp` — see that field's comment.
   *
   * 🔴 BUMP-ONLY: the body is `{ orderIndex }` and NOTHING ELSE. There is no `expectedVersion` here and there
   * must not be one — that is a recorded M8.2 decision, not an oversight. `reorderPlan` emits a write for EVERY
   * row in the group (it has to: a soft delete leaves a GAP in `order_index`, and a windowed write would then
   * produce a TIE that `ORDER BY order_index` resolves arbitrarily), so one ordinary drag calls this method
   * once per row. A checked variant would therefore 409-STORM on the happy path: row 1's write bumps the
   * group, and rows 2..n would each arrive holding a version the previous write already invalidated.
   *
   * Do not "harden" this by adding a version. The reason it is safe without one is that the client sends the
   * WHOLE new order, so a lost update cannot leave the group half-ordered — the next drag renormalises it.
   */
  setTaskOrder(taskId: number, orderIndex: number): Observable<void> {
    return taskSetOrderFn(this.mutatingHttp, this.rootUrl, { id: taskId, body: { orderIndex } })
      .pipe(map(() => void 0));
  }

  /**
   * SOFT delete — `is_active = false`. NOTHING IS DESTROYED, and that is not a detail: `SetActiveAsync` flips
   * the flag and LEAVES `order_index` alone, while the read is `WHERE is_active = 1 ORDER BY order_index`. So
   * every delete leaves a GAP in the survivors' indices — which is exactly why `reorderPlan` must rewrite EVERY
   * row (a windowed write would TIE) and why `nextOrderIndex` must append past the highest INDEX, not the
   * count. This method is the thing that creates the condition both of those defend against.
   *
   * `isActive: true` RESTORES a deleted task through the same route. The flag is therefore passed through
   * verbatim, never hard-coded to `false` here — the caller decides which direction it is going.
   *
   * 🔴 BUMP-ONLY: the body is `{ isActive }` and NOTHING ELSE. There is no `expectedVersion` here and there
   * must not be one — the C# is explicit that this is by design ("Rule #9: bump-only BY DESIGN, no
   * *CheckedAsync sibling -- ignore any rowVersion on the DTO (TaskActiveRequest carries none to ignore)"),
   * and the route declares exactly TWO outcomes: `204 NoContent` and `404 NotFound`. There is no 400 and no
   * 409 on this path, so a caller that writes handlers for them is writing dead code.
   *
   * A MUTATION, so `mutatingHttp` — see that field's comment. On the plain client this write echoes straight
   * back to us over SignalR, we re-fetch, and we clobber our own screen.
   */
  setTaskActive(taskId: number, isActive: boolean): Observable<void> {
    return taskSetActiveFn(this.mutatingHttp, this.rootUrl, { id: taskId, body: { isActive } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // BACKLOGS — GET /api/backlogs/{id} · PUT /api/backlogs/{id}
  //
  // These two exist as a PAIR, and the read half is not an optimisation you can skip:
  //
  //   - `PUT /api/backlogs/{id}` REPLACES THE WHOLE RECORD. An omitted field is written as NULL, not left
  //     alone. M8.3 already paid for this once: a DTO that merely omitted `teamId` set `team_id = NULL` and
  //     the backlog dropped out of every team — invisible to everyone, permanently, while every test passed.
  //   - It is also a CHECKED write, so it demands an `expectedVersion`.
  //
  // The Log Work screen holds neither. All it has is `WeekBacklogGroup` — the WEEK read model — which carries
  // the backlog's id, code, project, type and assignee and NOTHING else: no `rowVersion`, no `note`, no
  // `progressPercent`, no dates. There is nowhere else to get them. So: GET the record, change the one field,
  // PUT it back. See `move-month.ts` (`toUpdateRequest`), which is where the field-by-field copy lives.
  // =====================================================================================================

  /** A READ -> the plain `http`. A read notifies nobody, so there is no SignalR echo to suppress and no
   *  reason to widen `X-Connection-Id`'s blast radius. */
  getBacklog(id: number): Observable<BacklogDto> {
    return backlogGetFn(this.http, this.rootUrl, { id }).pipe(map(r => r.body));
  }

  /**
   * A CHECKED write: `body.expectedVersion` must be the `rowVersion` the GET above just returned, and the
   * server 409s if anyone changed the record in between.
   *
   * A MUTATION, so `mutatingHttp` — see that field's comment. On the plain client this write echoes straight
   * back to us over SignalR, we re-fetch, and we clobber our own screen.
   */
  updateBacklog(id: number, body: BacklogUpdateRequest): Observable<SavedBody> {
    return backlogUpdateFn(this.mutatingHttp, this.rootUrl, { id, body }).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE BACKLOG GRID — GET /api/backlogs · POST /api/backlogs · GET /api/backlogs/{id}/audit
  //
  // (The single-record GET/PUT pair lives in the section directly above. It predates M8.6 and is unchanged.)
  // =====================================================================================================

  /**
   * Every backlog, as grid rows. A READ -> the plain `http`.
   *
   * 🔴 The endpoint ALSO accepts a `?term=` filter (see `BacklogList$Params`), and this method DELIBERATELY
   * does not expose it. The screen searches CLIENT-side, in `filterRows` (pages/backlog/backlog-list.ts), and
   * it has to: `rebuildOptions` derives the four dropdowns' contents FROM THE LOADED ROWS. Filter on the
   * server and every keystroke in the search box would silently delete entries out of the Project / Type /
   * Assignee / Month dropdowns — the user would watch their own filters vanish as they type. If you ever do
   * need the server-side filter, it needs its own method and its own row set, not this one.
   *
   * Returns EVERY backlog, the hidden `DEFAULT` one included. Excluding it is `buildRows`' job, on the client,
   * because Log Work needs DEFAULT and this endpoint is shared with it.
   */
  getBacklogList(): Observable<BacklogListItemDto[]> {
    return backlogListFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /**
   * Create a backlog. A MUTATION, so `mutatingHttp` — see that field's comment.
   *
   * The returned `BacklogDto` is not discardable: it carries the new `id` (which the task inserts that follow
   * a create need as their `backlogId`) and the first `rowVersion` (which the next checked PUT needs as its
   * `expectedVersion`). `toCreateRequest` (pages/backlog/backlog-form.ts) is what builds the body.
   */
  createBacklog(body: BacklogCreateRequest): Observable<BacklogDto> {
    return backlogCreateFn(this.mutatingHttp, this.rootUrl, { body }).pipe(map(r => r.body));
  }

  /** One backlog's change history. A READ -> the plain `http`. */
  getBacklogAudit(id: number): Observable<BacklogAuditDto[]> {
    return backlogAuditFn(this.http, this.rootUrl, { id }).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // PEOPLE — the two lookup PAIRS behind the editor's Assignee and PCA Contact dropdowns.
  //
  // The split inside each pair is not redundancy. It is a permission boundary AND a lifecycle one:
  //
  //   - `/api/users` · `/api/pca-contacts`             the ACTIVE rows, in full  -> what you may PICK.
  //   - `/api/users/names` · `/api/pca-contacts/names`  id+name for EVERYONE,
  //                                                     DEACTIVATED INCLUDED     -> what you may RENDER.
  //
  // A departed assignee must still render her name on the record she is already on; the active list no longer
  // contains her, so the grid resolves names from `/names` and offers choices from the active list.
  //
  // 🔴 `/api/users/all` and `/api/pca-contacts/all` are a TRAP, and they are not in the generated client on
  // purpose. Both are `.RequireAuthorization(AuthSetup.AdminPolicy)`, so an ordinary user reading one gets a
  // 403 — which takes the screen's whole forkJoin down with it. A contract test
  // (`Admin_gated_list_is_NOT_tagged_and_so_never_joins_the_generated_client`) pins them OUT of the client so
  // they cannot be reached for by accident. `/names` exists precisely because `/all` cannot be used here.
  //
  // All four are READS -> the plain `http`.
  // =====================================================================================================

  /** The ACTIVE users — the Assignee dropdown's options. You do not assign new work to a departed person. */
  getUsersActive(): Observable<UserDto[]> {
    return userListActiveFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** id + name for EVERY user, deactivated included — for RENDERING a name the active list no longer has. */
  getUserNames(): Observable<NamedRefDto[]> {
    return userNamesFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** The ACTIVE PCA contacts — the PCA Contact dropdown's options. */
  getPcaContactsActive(): Observable<PcaContactDto[]> {
    return pcaContactListActiveFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** id + name for EVERY PCA contact, deactivated included — the RENDER half of the pair above. */
  getPcaContactNames(): Observable<NamedRefDto[]> {
    return pcaContactNamesFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE VENDORED STUBS — empty streams, ON PURPOSE, and STILL LIVE.
  //
  // 🔴 DO NOT RETYPE ONE IN PLACE. Every stub marked "bound by" below is still the data source of a component
  // that binds the VENDORED view model under `strictTemplates` — changing its return type breaks that
  // component's build, and those components belong to other tasks (or, for Users, to no task at all). Add a
  // real method BESIDE the stub under a new name; the screen's own task then swaps its component over and
  // deletes the stub. See the class comment for the full shape mismatch this rule exists for.
  //
  // The reason the REST are still stubs is unchanged: their C# routes return `IResult` via `Results.Ok(x)`,
  // which ApiExplorer cannot infer a response type from, so the OpenAPI document declares no schema and there
  // is nothing honest to generate — a generated method would be typed `void` for an endpoint that in fact
  // returns data. Each screen's milestone annotates its C# with `.Produces<T>()`, regenerates, and wires them
  // up. That is exactly what M8.4 did for the timesheet routes and M8.6 just did for the backlog, task, user
  // and PCA routes above.
  // =====================================================================================================
  getUsers(): Observable<User[]> { return of([]); }                 // SUPERSEDED by getUsersActive()/getUserNames(); bound by users.component.ts
  getBacklogs(): Observable<Backlog[]> { return of([]); }           // SUPERSEDED by getBacklogList(); bound by backlog.component.ts
  getLogGroups(): Observable<LogGroup[]> { return of([]); }         // SUPERSEDED by getWeek() in W4
  getTaskCards(): Observable<TaskCard[]> { return of([]); }         // TODO: GET /api/tasklist; bound by task-list.component.ts
  getDailyEntries(date: string): Observable<DailyEntry[]> { return of([]); }   // TODO
  getTeamBoard(date: string): Observable<TeamMember[]> { return of([]); }      // TODO
  getMetrics(): Observable<Metric[]> { return of([]); }             // TODO: GET /api/reports/metrics
  getMissing(): Observable<string[]> { return of([]); }             // TODO
  getWeekly(): Observable<WeeklyRow[]> { return of([]); }           // TODO
  getMonthly(): Observable<MonthlyRow[]> { return of([]); }         // TODO
  getDrilldown(): Observable<TreeNode | null> { return of(null); }  // TODO
  getTags(): Observable<Tag[]> { return of([]); }                   // TODO
  getTemplates(): Observable<TaskTemplate[]> { return of([]); }     // TODO
  getContacts(): Observable<string[]> { return of([]); }            // SUPERSEDED by getPcaContactsActive(); bound by settings.component.ts
  getTeams(): Observable<string[]> { return of([]); }               // TODO
  getHolidays(): Observable<string[]> { return of([]); }            // TODO: ISO date strings

  // ---- mutations still to connect ----
  saveProgress(key: string, pct: number): Observable<void> { return of(void 0); }        // TODO
  toggleUser(name: string): Observable<void> { return of(void 0); }                      // TODO
  toggleHoliday(iso: string): Observable<void> { return of(void 0); }                    // TODO
}

/**
 * Narrow the wire's `rowVersion?: number` to the `number` the caller must have.
 *
 * The field is optional in the generated model only because Swashbuckle does not emit `required` for C#
 * records; `SavedBody(long RowVersion)` is non-nullable and always serialises. But this is the network
 * boundary, and a 200 whose body is not the body we asked for (a proxy's HTML error page, a version skew, a
 * misrouted response) is not an impossible scenario. Failing loudly here is the whole point: a silent
 * fallback to `0` or `null` would feed a WRONG expectedVersion into the next save, which under the five-case
 * table either 409s spuriously or silently overwrites another user. Losing the version is worse than losing
 * the request.
 */
export function requireRowVersion(rowVersion: number | undefined): number {
  if (typeof rowVersion !== 'number') {
    throw new Error(
      'The API accepted the write but returned no rowVersion. Refusing to continue: the next save would ' +
      'send a wrong expectedVersion and could silently overwrite another user.',
    );
  }
  return rowVersion;
}
