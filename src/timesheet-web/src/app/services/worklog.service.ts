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
  backlogGet as backlogGetFn,
  backlogUpdate as backlogUpdateFn,
  login as loginFn,
  logout as logoutFn,
  me as meFn,
  smartFillApply as smartFillApplyFn,
  smartFillValidate as smartFillValidateFn,
  taskCreate as taskCreateFn,
  taskSetActive as taskSetActiveFn,
  taskSetOrder as taskSetOrderFn,
  timesheetClearCell as timesheetClearCellFn,
  timesheetSaveCell as timesheetSaveCellFn,
  timesheetWeek as timesheetWeekFn,
} from '../api/functions';
import {
  BacklogDto, BacklogUpdateRequest, LoginResponse, MeResponse, SavedBody, SmartFillTaskRequest, TaskItemDto,
  TimeLogDto, WeekBacklogGroup,
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
 * ONLY the methods M8.4 actually runs through are wired: auth, the week grid, Smart Fill, and the cell write.
 * The other five screens (backlog, task list, daily report, reports, settings) keep their view models and
 * their `of(...)` stubs until their own milestone — their routes have no response schema in the OpenAPI
 * document yet (see ng-openapi-gen.json), so there is nothing honest to generate for them.
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
  // TASKS — POST /api/tasks · PUT /api/tasks/{id}/order
  // =====================================================================================================

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
  // NOT YET ON THE WIRE — the five screens outside M8.4.
  //
  // These keep returning empty streams ON PURPOSE. Their routes exist in C# but declare no response schema
  // in the OpenAPI document (they return `IResult` via `Results.Ok(x)`, which ApiExplorer cannot infer a type
  // from), so there is nothing honest to generate for them — a generated method would be typed `void` for an
  // endpoint that in fact returns data. Each screen's own milestone annotates its C# with `.Produces<T>()`,
  // regenerates, and wires these up. Until then an empty stream is the truthful answer.
  // =====================================================================================================
  getUsers(): Observable<User[]> { return of([]); }                 // TODO: GET /api/users
  getBacklogs(): Observable<Backlog[]> { return of([]); }           // TODO: GET /api/backlogs
  getLogGroups(): Observable<LogGroup[]> { return of([]); }         // TODO: superseded by getWeek() in W4
  getTaskCards(): Observable<TaskCard[]> { return of([]); }         // TODO: GET /api/tasklist
  getDailyEntries(date: string): Observable<DailyEntry[]> { return of([]); }   // TODO
  getTeamBoard(date: string): Observable<TeamMember[]> { return of([]); }      // TODO
  getMetrics(): Observable<Metric[]> { return of([]); }             // TODO: GET /api/reports/metrics
  getMissing(): Observable<string[]> { return of([]); }             // TODO
  getWeekly(): Observable<WeeklyRow[]> { return of([]); }           // TODO
  getMonthly(): Observable<MonthlyRow[]> { return of([]); }         // TODO
  getDrilldown(): Observable<TreeNode | null> { return of(null); }  // TODO
  getTags(): Observable<Tag[]> { return of([]); }                   // TODO
  getTemplates(): Observable<TaskTemplate[]> { return of([]); }     // TODO
  getContacts(): Observable<string[]> { return of([]); }            // TODO
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
