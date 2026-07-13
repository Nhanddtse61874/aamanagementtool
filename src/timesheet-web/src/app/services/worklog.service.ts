import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { map, Observable, of } from 'rxjs';
import {
  DailyEntry, LogGroup, Metric, MonthlyRow, Tag, TaskCard,
  TaskTemplate, TeamMember, TreeNode, User, WeeklyRow, DayColumn,
} from '../models/worklog.models';

import { ApiConfiguration } from '../api/api-configuration';
import { ConnectionIdHttpClient } from '../core/realtime.service';
import {
  authAdminSetPassword as authAdminSetPasswordFn,
  backlogAudit as backlogAuditFn,
  backlogContinue as backlogContinueFn,
  backlogCreate as backlogCreateFn,
  backlogGet as backlogGetFn,
  backlogList as backlogListFn,
  backlogSetTags as backlogSetTagsFn,
  backlogTags as backlogTagsFn,
  backlogUpdate as backlogUpdateFn,
  defaultTaskCreate as defaultTaskCreateFn,
  defaultTaskList as defaultTaskListFn,
  defaultTaskSetActive as defaultTaskSetActiveFn,
  defaultTaskSync as defaultTaskSyncFn,
  holidayDelete as holidayDeleteFn,
  holidayList as holidayListFn,
  holidayUpsert as holidayUpsertFn,
  login as loginFn,
  logout as logoutFn,
  me as meFn,
  meSetActiveTeam as meSetActiveTeamFn,
  opsBackupRun as opsBackupRunFn,
  opsExportRun as opsExportRunFn,
  opsRetentionPreview as opsRetentionPreviewFn,
  opsRetentionRun as opsRetentionRunFn,
  pcaContactCreate as pcaContactCreateFn,
  pcaContactListActive as pcaContactListActiveFn,
  pcaContactListAll as pcaContactListAllFn,
  pcaContactNames as pcaContactNamesFn,
  pcaContactRename as pcaContactRenameFn,
  pcaContactSetActive as pcaContactSetActiveFn,
  reportsMissingLogs as reportsMissingLogsFn,
  reportsMonthly as reportsMonthlyFn,
  reportsWeekly as reportsWeeklyFn,
  settingGet as settingGetFn,
  settingSet as settingSetFn,
  smartFillApply as smartFillApplyFn,
  smartFillValidate as smartFillValidateFn,
  standupArchiveWeek as standupArchiveWeekFn,
  standupBoard as standupBoardFn,
  standupEntryCreate as standupEntryCreateFn,
  standupEntryDelete as standupEntryDeleteFn,
  standupEntryReorder as standupEntryReorderFn,
  standupEntryUpdate as standupEntryUpdateFn,
  standupIssueCreate as standupIssueCreateFn,
  standupIssueDelete as standupIssueDeleteFn,
  standupIssueUpdate as standupIssueUpdateFn,
  standupMyDay as standupMyDayFn,
  standupQuickImport as standupQuickImportFn,
  tagCreate as tagCreateFn,
  tagDelete as tagDeleteFn,
  tagList as tagListFn,
  tagUpdate as tagUpdateFn,
  taskCreate as taskCreateFn,
  taskGet as taskGetFn,
  taskList as taskListFn,
  taskListExport as taskListExportFn,
  taskListScreen as taskListScreenFn,
  taskSetActive as taskSetActiveFn,
  taskSetExtended as taskSetExtendedFn,
  taskSetOrder as taskSetOrderFn,
  taskSetStatus as taskSetStatusFn,
  taskSetTags as taskSetTagsFn,
  taskTags as taskTagsFn,
  taskUpdate as taskUpdateFn,
  teamCreate as teamCreateFn,
  teamListActive as teamListActiveFn,
  teamListAll as teamListAllFn,
  teamMembers as teamMembersFn,
  teamRename as teamRenameFn,
  teamSetActive as teamSetActiveFn,
  teamSetMembers as teamSetMembersFn,
  templateCreate as templateCreateFn,
  templateDelete as templateDeleteFn,
  templateDeleteByName as templateDeleteByNameFn,
  templateList as templateListFn,
  timesheetClearCell as timesheetClearCellFn,
  timesheetSaveCell as timesheetSaveCellFn,
  timesheetWeek as timesheetWeekFn,
  userCreate as userCreateFn,
  userListActive as userListActiveFn,
  userListAll as userListAllFn,
  userNames as userNamesFn,
  userRename as userRenameFn,
  userSetActive as userSetActiveFn,
  userSetAdmin as userSetAdminFn,
  userSetUsername as userSetUsernameFn,
} from '../api/functions';
import {
  ArchivedFileDto, BacklogAuditDto, BacklogCreateRequest, BacklogDto, BacklogListItemDto, BacklogUpdateRequest,
  DefaultTaskDto, HolidayDto, LoginResponse, MeResponse, MissingLogWarning, NamedRefDto, PcaContactDto,
  RetentionPreview, SavedBody, SettingDto, SettingsOpsResult, SettingsStandupEntryCreateRequest,
  SettingsStandupEntryUpdateRequest, SettingsStandupIssueCreateRequest, SettingsStandupIssueUpdateRequest,
  SettingsTagCreateRequest, SettingsTagUpdateRequest, SettingsTemplateCreateRequest, SettingsUserStandup,
  SmartFillTaskRequest, TagDto, TaskExtendedRequest, TaskItemDto, TaskListScreenDto, TaskTemplateDto,
  TaskUpdateRequest, TeamDto, TimeLogDto, TimesheetMonthlyReportResponse, TimesheetWeeklyReportResponse,
  UserDto, WeekBacklogGroup,
} from '../api/models';

/**
 * The optional filter the two Reports reads and the two exports all share.
 *
 * 🔴 `teamIds: []` DOES NOT MEAN "NO TEAMS" — see THE TEAM FILTER block inside the class. It means "ALL MY
 * TEAMS", the exact inverse. Pass `undefined` when you are not filtering, and do not call at all when the
 * user's team selection is empty.
 */
export interface ReportFilter {
  readonly userId?: number;
  readonly project?: string;
  readonly teamIds?: number[];
}

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
 * The one exception to "generated" is `exportExcel` / `exportMarkdown`, which are HAND-WRITTEN blob
 * downloads. That is deliberate and is explained on those two methods: their routes return binary / raw text
 * rather than JSON, so the "Export" tag is kept out of `includeTags` on purpose and no method is generated.
 * They are still same-origin relative paths, and for exactly the same reason.
 *
 * WIRED, AND REAL:
 *   - M8.4 — auth, the week grid, Smart Fill, the cell write.
 *   - M8.6/T5 — the Backlog screen's transport: the backlog list / create / audit, the task list and the
 *     checked task update, and the four user + PCA lookups.
 *   - M9/P5 — EVERYTHING ELSE. The Task List screen, Reports (+ the two file exports), the backlog/task tag
 *     joins and narrow task writes, Users, Teams, Tags, PCA contacts, Templates, Holidays, Default tasks,
 *     Standup, the key/value settings store, Ops, and the active-team switch. There is no route left in
 *     `../api/functions` that this service does not expose.
 *
 * 🔴 SO WHY IS THERE STILL A BLOCK OF `of(...)` STUBS AT THE BOTTOM? Because a REAL METHOD AND ITS STUB ARE
 * NOT THE SAME OBJECT, and the stub is STILL LIVE. Every stub is still CONSUMED by a Phase-2 component that
 * binds the VENDORED view model under `strictTemplates`:
 *
 *     getUsers()        -> User[]         users.component.ts
 *     getTaskCards()    -> TaskCard[]     task-list.component.ts        getTags()      -> Tag[]
 *     getDailyEntries() -> DailyEntry[]   daily-report.component.ts     getTeamBoard() -> TeamMember[]
 *     getMetrics()      -> Metric[]       reports.component.ts          getMissing()   -> string[]
 *     getWeekly() / getMonthly() / getDrilldown()                       ... and the rest
 *     getContacts() / getTeams() / getTemplates() / getHolidays()       settings.component.ts
 *
 * The vendored view models DO NOT MATCH the wire DTOs. `User` is `{name, active}` with NO `id` at all where
 * `UserDto` is `{id?, name?, isActive?, …}`; `getHolidays()` is `string[]` where the wire is `HolidayDto[]`.
 * So RETYPING a stub in place does not "wire up a screen" — it BREAKS THE BUILD of a component this task is
 * not allowed to touch, and Phase 1 must end GREEN.
 *
 * 🔴 THE CONVENTION, AND IT IS LOAD-BEARING: a real method is ADDED BESIDE its stub under a NEW NAME. Each
 * Phase-2 agent then swaps ITS OWN component over to the real method and DELETES THE STUB IT ORPHANS — the
 * stub's trailing comment names its replacement, so nobody has to guess. `getPcaContactsActive()` sitting
 * next to `getContacts()` is that convention, not duplication; M8.6/T6 ran it to completion for the first
 * time when `BacklogComponent` moved onto `getBacklogList()` and `getBacklogs()` was deleted with it.
 *
 * 🔴 DO NOT DELETE A STUB YOU DID NOT ORPHAN.
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
  // 🔴 `/api/users/all` and `/api/pca-contacts/all` ARE STILL A TRAP FOR THESE FOUR CALLERS — but the reason
  // CHANGED IN M9 P2a, and the old reason is now FALSE. THE COMMENT THAT USED TO SIT HERE SAID THEY WERE "not
  // in the generated client on purpose", pinned out by a contract test
  // (`Admin_gated_list_is_NOT_tagged_and_so_never_joins_the_generated_client`). THAT TEST NO LONGER EXISTS AND
  // THAT CLAIM IS NO LONGER TRUE: both routes are now tagged and BOTH ARE IN THE CLIENT, deliberately, because
  // the M9 Users/Settings screens cannot list a DEACTIVATED row without them. They are exposed here as
  // `getUsersAll()` and `getPcaContactsAll()`, both marked [ADMIN].
  //
  // WHAT DID NOT CHANGE IS THE HAZARD: both are `.RequireAuthorization(AuthSetup.AdminPolicy)`, so an ordinary
  // user reading one gets a 403 — which takes the screen's whole forkJoin down with it. The BACKLOG EDITOR is
  // reachable by a non-admin, so IT MUST KEEP USING THE FOUR METHODS BELOW and must never reach for the `/all`
  // pair. `/names` exists precisely for that: it is how a DEPARTED assignee's name still renders on a record
  // she is already on, without an admin route.
  //
  // The guard did not weaken, it MOVED — from "the route is absent from our client" (a proxy, which never
  // stopped anyone with a cookie and curl) to `SettingsEndpointsTests.The_admin_gated_full_list_is_403_for_a
  // _NON_admin`, which asserts the property itself. See THE ADMIN CONTRACT block further down.
  //
  // All four below are OPEN, and all four are READS -> the plain `http`.
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
  // 🔴 THE ADMIN CONTRACT — READ THIS BEFORE CALLING ANY METHOD MARKED [ADMIN] BELOW.
  //
  // About thirty of the methods added in M9/P5 hit a route carrying `.RequireAuthorization(AuthSetup
  // .AdminPolicy)`. They are in the generated client ON PURPOSE, and that is a REVERSAL of the M8.6 rule the
  // class comment above used to state: until M9 there was no admin-only SCREEN, so an admin route in the
  // client could only ever 403, and `/api/users/all` + `/api/pca-contacts/all` were kept out by a contract
  // test. M9 BUILDS those screens, and they cannot be built without those two routes — only `/all` returns
  // DEACTIVATED rows, and "Activate" has nothing to act on without them. The old test is gone; the guard did
  // not weaken, it MOVED to `SettingsEndpointsTests.The_admin_gated_full_list_is_403_for_a_NON_admin`, which
  // asserts the security property itself rather than a proxy for it.
  //
  // An ordinary user calling an [ADMIN] method gets a 403 — and a 403 inside a `forkJoin` takes THE WHOLE
  // SCREEN down with it, not just the panel that asked. So:
  //
  //     🔴 A SCREEN A NON-ADMIN CAN REACH MUST NEVER CALL A METHOD MARKED [ADMIN].
  //
  // 🔴 AND THE CLIENT-SIDE HALF OF THAT GUARD DOES NOT EXIST YET. `ng-openapi-gen.json`'s comment describes
  // `core/admin.guard.ts` in the present tense; there is no such file as of this commit. Routing a non-admin
  // to /users or /settings today 403s them. Writing that guard is a LATER M9 TASK, and it is not this one's.
  // =====================================================================================================

  // =====================================================================================================
  // 🔴 THE TEAM FILTER — `teamIds`, on getTaskListScreen / exportTaskListMarkdown / getWeeklyReport /
  // getMonthlyReport / getStandupBoard / exportExcel / exportMarkdown.
  //
  // 🔴 AN EMPTY ARRAY DOES NOT MEAN "NO TEAMS". IT MEANS "ALL MY TEAMS" — THE EXACT INVERSE.
  //
  // The generated `RequestBuilder` serialises a query array with `explode: true` — one `?teamIds=` entry PER
  // ELEMENT (see `QueryParameter.append`). An empty array therefore iterates ZERO times and appends NOTHING,
  // so `teamIds: []` goes out BYTE-IDENTICAL to `teamIds: undefined`: the key is genuinely ABSENT from the
  // URL, not present-and-empty. And the server reads an ABSENT key as "every team the caller belongs to":
  //
  //     if (!http.Request.Query.TryGetValue("teamIds", out var raw))
  //         return ctx.MemberTeamIds;              // <- TimesheetEndpoints/BacklogEndpoints.EffectiveTeamIds
  //
  // (`GET /api/standup/board` spells the same thing differently and means the same:
  //  `teamIds is { Length: > 0 } ? teamIds : ctx.MemberTeamIds`.)
  //
  // So a screen that "filters to nothing" by passing `[]` does NOT render an empty table. It renders
  // EVERYTHING — every backlog of every team the user belongs to, which is the worst possible direction for
  // this bug to fail in.
  //
  // These methods therefore pass `teamIds` through VERBATIM AND INVENT NO SENTINEL. There is no value they
  // could send that means "no teams": a fake id (`-1`) is INTERSECTED away server-side into the same empty
  // set that already means "all". THE SCREEN must special-case an empty selection and render its empty state
  // LOCALLY, without calling. That is Phase 2's contract and it is not this transport's to fake.
  // =====================================================================================================

  // =====================================================================================================
  // THE TASK LIST SCREEN — GET /api/tasklist · GET /api/tasklist/export
  // =====================================================================================================

  /**
   * The whole Task List screen in one read: the Gantt model plus the rows. A READ -> the plain `http`.
   *
   * 🔴 `teamIds: []` means ALL MY TEAMS, not none — see THE TEAM FILTER block above.
   */
  getTaskListScreen(year: number, month: number, teamIds?: number[]): Observable<TaskListScreenDto> {
    return taskListScreenFn(this.http, this.rootUrl, { year, month, teamIds })
      .pipe(map(r => r.body));
  }

  /**
   * The month's task list as MARKDOWN TEXT — not a blob, and not JSON.
   *
   * Unlike the two `/api/export/*` routes below, this one IS in the generated client: the generator can type
   * a `text/markdown` body as a `string`, and `taskListExport` already sets `responseType: 'text'` internally.
   * So there is nothing to hand-write here. A caller that wants a *download* wraps the string in a `Blob`
   * itself; a caller that wants a preview just renders it.
   *
   * A READ -> the plain `http`. 🔴 `teamIds: []` means ALL MY TEAMS — see THE TEAM FILTER block above, and
   * note the stakes are highest on this route: `BuildMonthMarkdownAsync`'s `teamIds` is NULLABLE server-side
   * and null means EVERY TEAM, i.e. a markdown dump of the whole company's backlogs.
   *
   * 400 (the month has no data — "no data => no file", TL-09) arrives as an HttpErrorResponse. It is the
   * caller's to handle: there is no empty document to show.
   */
  exportTaskListMarkdown(year: number, month: number, teamIds?: number[]): Observable<string> {
    return taskListExportFn(this.http, this.rootUrl, { year, month, teamIds })
      .pipe(map(r => r.body));
  }

  // =====================================================================================================
  // REPORTS — GET /api/reports/weekly · GET /api/reports/monthly · GET /api/reports/missing-logs
  //
  // 🔴 THERE IS NO `/api/reports/metrics`, AND THERE DOES NOT NEED TO BE. The Reports screen's four stat
  // cards are CLIENT-SIDE ARITHMETIC over what `getWeeklyReport` already returns — `dayTotals` (a
  // WeeklyDayTotal[] of per-day hours) and `daysLogged` (a DaysLoggedStat of `{logged, workingDays}`) —
  // plus `getMissingLogs().length`. Do not go looking for a metrics route: the API has exactly three
  // `/api/reports/*` routes and this is all of them. (The `getMetrics()` stub's old TODO comment named that
  // route as if it existed. It never did. The comment is corrected in the stub block below.)
  //
  // All three are READS -> the plain `http`.
  // =====================================================================================================

  /**
   * One week's report: per-day totals, the days-logged stat, and the detail rows.
   *
   * 🔴 `filter.teamIds: []` means ALL MY TEAMS, not none — see THE TEAM FILTER block above.
   */
  getWeeklyReport(monday: string, filter?: ReportFilter): Observable<TimesheetWeeklyReportResponse> {
    return reportsWeeklyFn(this.http, this.rootUrl, {
      monday, userId: filter?.userId, project: filter?.project, teamIds: filter?.teamIds,
    }).pipe(map(r => r.body));
  }

  /**
   * One month's report: the per-backlog/task totals and the project tree.
   *
   * 🔴 `filter.teamIds: []` means ALL MY TEAMS, not none — see THE TEAM FILTER block above.
   */
  getMonthlyReport(
    year: number, month: number, filter?: ReportFilter,
  ): Observable<TimesheetMonthlyReportResponse> {
    return reportsMonthlyFn(this.http, this.rootUrl, {
      year, month, userId: filter?.userId, project: filter?.project, teamIds: filter?.teamIds,
    }).pipe(map(r => r.body));
  }

  /**
   * The users who have not logged in the last N days.
   *
   * 🔴 TAKES NO ARGUMENTS, AND THAT IS THE CONTRACT, not an omission. The route accepts NO client parameters
   * at all: N is the shared app-wide setting (SET-02), read SERVER-side, because a client-supplied N would let
   * anyone request an arbitrarily large scan window. The team scope is likewise internal — it is the caller's
   * ACTIVE team, applied inside `GetUsersMissingLogsAsync`. There is no `teamIds` here to get wrong.
   *
   * `MissingLogWarning` is a bare `{ userName }` — deliberately no id. The route does not expose who they are
   * beyond the name it already shows.
   */
  getMissingLogs(): Observable<MissingLogWarning[]> {
    return reportsMissingLogsFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE TWO FILE EXPORTS — GET /api/export/excel · GET /api/export/markdown
  //
  // 🔴 HAND-WRITTEN, AND THAT IS DELIBERATE — DO NOT "FIX" IT BY REGENERATING. Both routes return BINARY or
  // RAW TEXT, not JSON. `ng-openapi-gen` cannot type either usefully, so the "Export" tag is kept OUT of
  // `includeTags` in ng-openapi-gen.json ON PURPOSE and no client method is generated for them. The C# says
  // so in as many words. Adding "Export" to includeTags would produce a client that calls `response.json()`
  // on a spreadsheet.
  //
  // 🔴 NEVER AN ABSOLUTE URL. `this.rootUrl` is `''`, so these are same-origin relative paths — and they must
  // stay that way. `ng serve` (:4200) -> Kestrel (:5080) is CROSS-SITE, and a `SameSite=Lax` cookie is NOT
  // SENT on a cross-site request: the download would go out anonymous and 401. See the class comment.
  //
  // READS -> the plain `http`. A download notifies nobody.
  //
  // 🔴 `filter.teamIds: []` means ALL MY TEAMS — see THE TEAM FILTER block above. It applies here EXACTLY as
  // it does to the generated calls, because `EffectiveTeamIds` hand-reads the raw query string and an
  // `HttpParams` with zero appended `teamIds` entries leaves the key ABSENT, just like the RequestBuilder.
  // (The routes do not DECLARE `teamIds` for ApiExplorer — which is precisely why they could not be generated
  // with a team filter — but the handler reads it off HttpContext regardless, so sending it works.)
  // =====================================================================================================

  /** The month's timesheet as an .xlsx. The caller owns the object-URL / anchor dance. */
  exportExcel(year: number, month: number, filter?: ReportFilter): Observable<Blob> {
    return this.http.get(`${this.rootUrl}/api/export/excel`, {
      params: this.exportParams(year, month, filter),
      responseType: 'blob',
    });
  }

  /** The month's timesheet as a markdown document. A download, like the xlsx — hence a Blob, not a string. */
  exportMarkdown(year: number, month: number, filter?: ReportFilter): Observable<Blob> {
    return this.http.get(`${this.rootUrl}/api/export/markdown`, {
      params: this.exportParams(year, month, filter),
      responseType: 'blob',
    });
  }

  /**
   * The query the two exports share.
   *
   * 🔴 `append`, NOT `set`, for teamIds — the server expects ONE ENTRY PER TEAM (`?teamIds=1&teamIds=2`), the
   * same shape the generated RequestBuilder emits. `set` would collapse them into one comma-joined value that
   * `int.TryParse` then throws away silently, leaving an EMPTY intersection: no rows, no error, no clue.
   *
   * And an empty/absent `teamIds` appends nothing, which the server reads as ALL MY TEAMS. See THE TEAM
   * FILTER block above — that inversion is the caller's to respect, not this helper's to paper over.
   */
  private exportParams(year: number, month: number, filter?: ReportFilter): HttpParams {
    let params = new HttpParams().set('year', year).set('month', month);
    if (filter?.userId !== undefined) params = params.set('userId', filter.userId);
    if (filter?.project !== undefined) params = params.set('project', filter.project);
    for (const teamId of filter?.teamIds ?? []) params = params.append('teamIds', teamId);
    return params;
  }

  // =====================================================================================================
  // BACKLOG + TASK EXTRAS — the tag joins, "continue to next month", and the three narrow task writes.
  //
  // Every write here is CHECKED: the body carries an `expectedVersion` and the server 409s if the record
  // moved under you. The version comes from the record you already loaded (`getBacklog` / `getTask` /
  // `getTasks`) — never from a re-read, which is racy. See `saveHours`' comment for why.
  //
  // 🔴 The two `*Tags` GETs return `number[]` — TAG IDS, not `TagDto`s. Resolve them against `getTagList()`.
  // =====================================================================================================

  /** The tag ids on one backlog. A READ -> the plain `http`. */
  getBacklogTags(backlogId: number): Observable<number[]> {
    return backlogTagsFn(this.http, this.rootUrl, { id: backlogId }).pipe(map(r => r.body));
  }

  /** REPLACES a backlog's tag set. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  setBacklogTags(backlogId: number, tagIds: number[], expectedVersion: number): Observable<SavedBody> {
    return backlogSetTagsFn(this.mutatingHttp, this.rootUrl, {
      id: backlogId, body: { tagIds, expectedVersion },
    }).pipe(map(r => r.body));
  }

  /**
   * Copy a backlog forward into another period. A MUTATION -> `mutatingHttp`.
   *
   * Returns the NEW `BacklogDto` — carrying the new id and its first `rowVersion`, both of which the caller
   * needs for anything it does next. Do not discard it and re-read.
   */
  continueBacklog(backlogId: number, targetPeriod: string): Observable<BacklogDto> {
    return backlogContinueFn(this.mutatingHttp, this.rootUrl, {
      id: backlogId, body: { targetPeriod },
    }).pipe(map(r => r.body));
  }

  /** ONE task by id. A READ -> the plain `http`. Carries the `rowVersion` the three writes below need. */
  getTask(taskId: number): Observable<TaskItemDto> {
    return taskGetFn(this.http, this.rootUrl, { id: taskId }).pipe(map(r => r.body));
  }

  /**
   * Set ONLY a task's status. A CHECKED write, and a MUTATION -> `mutatingHttp`.
   *
   * 🔴 This is the NARROW route, and it is the right one for the Task List's status dropdown. Do not reach
   * for `updateTask` (`PUT /api/tasks/{id}`) to change a status: that one REPLACES name, order AND status in
   * a single write, so it needs the name and order round-tripped too — and a body built from a status
   * dropdown alone would compile clean and blank the task's name. This route touches status and nothing else.
   */
  setTaskStatus(taskId: number, status: string, expectedVersion: number): Observable<SavedBody> {
    return taskSetStatusFn(this.mutatingHttp, this.rootUrl, {
      id: taskId, body: { status, expectedVersion },
    }).pipe(map(r => r.body));
  }

  /**
   * Set a task's `type` and `assigneeUserId`. A CHECKED write, and a MUTATION -> `mutatingHttp`.
   *
   * Both fields are NULLABLE and both are written verbatim — passing `{ type: null }` CLEARS the type, it
   * does not "leave it alone". Build the body from the loaded task, not from a partially-filled form.
   */
  setTaskExtended(taskId: number, body: TaskExtendedRequest): Observable<SavedBody> {
    return taskSetExtendedFn(this.mutatingHttp, this.rootUrl, { id: taskId, body })
      .pipe(map(r => r.body));
  }

  /** The tag ids on one task. A READ -> the plain `http`. */
  getTaskTags(taskId: number): Observable<number[]> {
    return taskTagsFn(this.http, this.rootUrl, { id: taskId }).pipe(map(r => r.body));
  }

  /** REPLACES a task's tag set. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  setTaskTags(taskId: number, tagIds: number[], expectedVersion: number): Observable<SavedBody> {
    return taskSetTagsFn(this.mutatingHttp, this.rootUrl, {
      id: taskId, body: { tagIds, expectedVersion },
    }).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // USERS — the admin-only half. The two OPEN reads (`getUsersActive` / `getUserNames`) are up in PEOPLE.
  //
  // 🔴 EVERY METHOD IN THIS SECTION IS [ADMIN] — see THE ADMIN CONTRACT above. A non-admin gets a 403.
  // =====================================================================================================

  /**
   * [ADMIN] EVERY user, DEACTIVATED INCLUDED. A READ -> the plain `http`.
   *
   * 🔴 This is the route the Users tab is BUILT ON, and the reason the M8.6 "keep /all out of the client"
   * rule was reversed in M9 P2a: `GET /api/users` is `GetActiveAsync` and can NEVER return a deactivated
   * user, so "Activate" would have nothing to act on. Only this route can. It is admin-gated (403 for
   * everyone else), which is exactly why no screen a non-admin can reach may call it.
   */
  getUsersAll(): Observable<UserDto[]> {
    return userListAllFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Create a user. A MUTATION -> `mutatingHttp`. Returns the new row, id and first version included. */
  createUser(name: string): Observable<UserDto> {
    return userCreateFn(this.mutatingHttp, this.rootUrl, { body: { name } }).pipe(map(r => r.body));
  }

  /** [ADMIN] Rename a user (their DISPLAY name). A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  renameUser(id: number, name: string, expectedVersion: number): Observable<SavedBody> {
    return userRenameFn(this.mutatingHttp, this.rootUrl, { id, body: { name, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /** [ADMIN] Change a user's LOGIN name. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  setUserUsername(id: number, username: string, expectedVersion: number): Observable<SavedBody> {
    return userSetUsernameFn(this.mutatingHttp, this.rootUrl, { id, body: { username, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /** [ADMIN] Grant or revoke admin. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  setUserAdmin(id: number, isAdmin: boolean, expectedVersion: number): Observable<SavedBody> {
    return userSetAdminFn(this.mutatingHttp, this.rootUrl, { id, body: { isAdmin, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /**
   * [ADMIN] SOFT delete / restore a user. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 BUMP-ONLY: the body is `{ isActive }` and nothing else — this route declares no `expectedVersion` and
   * inventing one would be dead code. `true` RESTORES through the same route, so the flag is passed through
   * verbatim rather than hard-coded. This is the write the Users tab's Activate button makes, on a row only
   * `getUsersAll()` can show it.
   */
  setUserActive(id: number, isActive: boolean): Observable<void> {
    return userSetActiveFn(this.mutatingHttp, this.rootUrl, { id, body: { isActive } })
      .pipe(map(() => void 0));
  }

  /**
   * [ADMIN] Set ANOTHER user's password — the admin reset. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 NOT the self-service change. That is `POST /api/auth/set-password`, which requires the CURRENT
   * password and is open to any authenticated caller; this one requires none and is admin-gated. The target
   * id is a ROUTE parameter, never a body field, so a caller cannot smuggle a different victim in the JSON.
   */
  adminSetPassword(userId: number, newPassword: string): Observable<void> {
    return authAdminSetPasswordFn(this.mutatingHttp, this.rootUrl, { id: userId, body: { newPassword } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // TEAMS — `GET /api/teams` is OPEN; EVERYTHING ELSE IN THIS SECTION IS [ADMIN], the membership READ
  // included. See THE ADMIN CONTRACT above.
  // =====================================================================================================

  /** The ACTIVE teams. OPEN to any authenticated caller. A READ -> the plain `http`. */
  getTeamsActive(): Observable<TeamDto[]> {
    return teamListActiveFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] EVERY team, deactivated included — the Settings tab's list. A READ -> the plain `http`. */
  getTeamsAll(): Observable<TeamDto[]> {
    return teamListAllFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /**
   * [ADMIN] The USER IDS in one team — not users, ids. A READ -> the plain `http`.
   *
   * 🔴 This READ is admin-gated too, unlike every other read in this file. A non-admin screen that merely
   * wants to *show* a team's members cannot use it.
   */
  getTeamMembers(teamId: number): Observable<number[]> {
    return teamMembersFn(this.http, this.rootUrl, { id: teamId }).pipe(map(r => r.body));
  }

  /** [ADMIN] Create a team. A MUTATION -> `mutatingHttp`. */
  createTeam(name: string): Observable<TeamDto> {
    return teamCreateFn(this.mutatingHttp, this.rootUrl, { body: { name } }).pipe(map(r => r.body));
  }

  /** [ADMIN] Rename a team. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  renameTeam(id: number, name: string, expectedVersion: number): Observable<SavedBody> {
    return teamRenameFn(this.mutatingHttp, this.rootUrl, { id, body: { name, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /**
   * [ADMIN] REPLACES a team's membership with exactly `userIds`. A CHECKED write, and a MUTATION.
   *
   * 🔴 It REPLACES. An id you omit is REMOVED from the team, not left alone — so build this from the full
   * membership `getTeamMembers()` returned, never from the one checkbox the user just ticked.
   */
  setTeamMembers(id: number, userIds: number[], expectedVersion: number): Observable<SavedBody> {
    return teamSetMembersFn(this.mutatingHttp, this.rootUrl, { id, body: { userIds, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /** [ADMIN] SOFT delete / restore a team. BUMP-ONLY — no version. A MUTATION -> `mutatingHttp`. */
  setTeamActive(id: number, isActive: boolean): Observable<void> {
    return teamSetActiveFn(this.mutatingHttp, this.rootUrl, { id, body: { isActive } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // TAGS — `GET /api/tags` is OPEN (every screen that renders a tag chip needs it). The three WRITES are
  // [ADMIN]. See THE ADMIN CONTRACT above.
  // =====================================================================================================

  /** Every tag. OPEN. A READ -> the plain `http`. This is what resolves the ids `get*Tags()` hands back. */
  getTagList(): Observable<TagDto[]> {
    return tagListFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Create a tag. A MUTATION -> `mutatingHttp`. */
  createTag(body: SettingsTagCreateRequest): Observable<TagDto> {
    return tagCreateFn(this.mutatingHttp, this.rootUrl, { body }).pipe(map(r => r.body));
  }

  /** [ADMIN] Update a tag's text / colour / icon. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  updateTag(id: number, body: SettingsTagUpdateRequest): Observable<SavedBody> {
    return tagUpdateFn(this.mutatingHttp, this.rootUrl, { id, body }).pipe(map(r => r.body));
  }

  /** [ADMIN] Delete a tag. A MUTATION -> `mutatingHttp`. */
  deleteTag(id: number): Observable<void> {
    return tagDeleteFn(this.mutatingHttp, this.rootUrl, { id }).pipe(map(() => void 0));
  }

  // =====================================================================================================
  // PCA CONTACTS — the admin-only half. The two OPEN reads are up in PEOPLE.
  // 🔴 EVERY METHOD IN THIS SECTION IS [ADMIN].
  // =====================================================================================================

  /** [ADMIN] EVERY PCA contact, deactivated included — the Settings tab's list. A READ -> plain `http`. */
  getPcaContactsAll(): Observable<PcaContactDto[]> {
    return pcaContactListAllFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Create a PCA contact. A MUTATION -> `mutatingHttp`. */
  createPcaContact(name: string): Observable<PcaContactDto> {
    return pcaContactCreateFn(this.mutatingHttp, this.rootUrl, { body: { name } }).pipe(map(r => r.body));
  }

  /** [ADMIN] Rename a PCA contact. A CHECKED write, and a MUTATION -> `mutatingHttp`. */
  renamePcaContact(id: number, name: string, expectedVersion: number): Observable<SavedBody> {
    return pcaContactRenameFn(this.mutatingHttp, this.rootUrl, { id, body: { name, expectedVersion } })
      .pipe(map(r => r.body));
  }

  /** [ADMIN] SOFT delete / restore a PCA contact. BUMP-ONLY — no version. A MUTATION -> `mutatingHttp`. */
  setPcaContactActive(id: number, isActive: boolean): Observable<void> {
    return pcaContactSetActiveFn(this.mutatingHttp, this.rootUrl, { id, body: { isActive } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // TASK TEMPLATES — `GET /api/templates` is OPEN. The three writes are [ADMIN].
  //
  // A "template" is a NAMED GROUP of rows sharing `templateName`, one row per task. There is no template
  // ENTITY — which is why there are two deletes: one row (`deleteTemplate`) and one whole group
  // (`deleteTemplateByName`).
  // =====================================================================================================

  /** Every template row. OPEN. A READ -> the plain `http`. Group them by `templateName` on the client. */
  getTemplateList(): Observable<TaskTemplateDto[]> {
    return templateListFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Add ONE row to a template. A MUTATION -> `mutatingHttp`. */
  createTemplate(body: SettingsTemplateCreateRequest): Observable<TaskTemplateDto> {
    return templateCreateFn(this.mutatingHttp, this.rootUrl, { body }).pipe(map(r => r.body));
  }

  /** [ADMIN] Delete ONE template ROW by id. A MUTATION -> `mutatingHttp`. */
  deleteTemplate(id: number): Observable<void> {
    return templateDeleteFn(this.mutatingHttp, this.rootUrl, { id }).pipe(map(() => void 0));
  }

  /**
   * [ADMIN] Delete an ENTIRE template — every row sharing `templateName`. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 `templateName` is a QUERY param on `DELETE /api/templates` (the collection), not a path segment.
   */
  deleteTemplateByName(templateName: string): Observable<void> {
    return templateDeleteByNameFn(this.mutatingHttp, this.rootUrl, { templateName })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // HOLIDAYS — `GET /api/holidays` is OPEN (the week grid rejects a write on a holiday, so every user needs
  // to see them). The two writes are [ADMIN].
  // =====================================================================================================

  /** The holidays, optionally narrowed to a year / month. OPEN. A READ -> the plain `http`. */
  getHolidayList(year?: number, month?: number): Observable<HolidayDto[]> {
    return holidayListFn(this.http, this.rootUrl, { year, month }).pipe(map(r => r.body));
  }

  /**
   * [ADMIN] Add or update ONE holiday, keyed by date. A MUTATION -> `mutatingHttp`.
   *
   * An UPSERT: posting a date that already exists overwrites its description rather than failing.
   */
  upsertHoliday(date: string, description?: string): Observable<void> {
    return holidayUpsertFn(this.mutatingHttp, this.rootUrl, { body: { date, description } })
      .pipe(map(() => void 0));
  }

  /** [ADMIN] Delete a holiday. The ISO date is the PATH key — there is no id. A MUTATION -> `mutatingHttp`. */
  deleteHoliday(date: string): Observable<void> {
    return holidayDeleteFn(this.mutatingHttp, this.rootUrl, { date }).pipe(map(() => void 0));
  }

  // =====================================================================================================
  // DEFAULT TASKS — the rows seeded into every new backlog. `GET` is OPEN; the three writes are [ADMIN].
  // =====================================================================================================

  /** The default tasks. OPEN. A READ -> the plain `http`. */
  getDefaultTasks(): Observable<DefaultTaskDto[]> {
    return defaultTaskListFn(this.http, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Add a default task. A MUTATION -> `mutatingHttp`. */
  createDefaultTask(taskName: string, orderIndex: number): Observable<DefaultTaskDto> {
    return defaultTaskCreateFn(this.mutatingHttp, this.rootUrl, { body: { taskName, orderIndex } })
      .pipe(map(r => r.body));
  }

  /** [ADMIN] SOFT delete / restore a default task. BUMP-ONLY — no version. A MUTATION -> `mutatingHttp`. */
  setDefaultTaskActive(id: number, isActive: boolean): Observable<void> {
    return defaultTaskSetActiveFn(this.mutatingHttp, this.rootUrl, { id, body: { isActive } })
      .pipe(map(() => void 0));
  }

  /**
   * [ADMIN] Push the default-task set into EXISTING backlogs. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 A BULK, CROSS-BACKLOG WRITE — not a settings toggle. It reaches into backlogs the caller is not
   * looking at, which is why it notifies and why it is admin-gated.
   */
  syncDefaultTasks(): Observable<void> {
    return defaultTaskSyncFn(this.mutatingHttp, this.rootUrl, {}).pipe(map(() => void 0));
  }

  // =====================================================================================================
  // STANDUP / DAILY REPORT — the entries, the issues, the team board.
  //
  // Everything here is OPEN to any authenticated caller EXCEPT `archiveStandupWeek`, which is [ADMIN].
  //
  // 🔴 Standup is the one area whose notifications are TEAM-SCOPED rather than global: the server sends to
  // the entry's real `TeamId`, not the `teamId: 0` broadcast sentinel every other settings entity uses. That
  // changes nothing about how you CALL these — writes still go on `mutatingHttp` — but it does mean the echo
  // you would receive from a write on the wrong client is a REAL one that would land on your own board.
  // =====================================================================================================

  /**
   * The caller's OWN standup for one day — yesterday's entries and today's. A READ -> the plain `http`.
   *
   * `date` defaults SERVER-side to today when omitted.
   */
  getStandupMyDay(date?: string): Observable<SettingsUserStandup> {
    return standupMyDayFn(this.http, this.rootUrl, { date }).pipe(map(r => r.body));
  }

  /**
   * The whole TEAM's board for one day — one `SettingsUserStandup` per member. A READ -> the plain `http`.
   *
   * 🔴 `teamIds: []` means ALL MY TEAMS, not none — see THE TEAM FILTER block above.
   */
  getStandupBoard(date?: string, teamIds?: number[]): Observable<SettingsUserStandup[]> {
    return standupBoardFn(this.http, this.rootUrl, { date, teamIds }).pipe(map(r => r.body));
  }

  /** Create a standup entry. A MUTATION -> `mutatingHttp`. Returns the NEW ENTRY'S ID — keep it. */
  createStandupEntry(body: SettingsStandupEntryCreateRequest): Observable<number> {
    return standupEntryCreateFn(this.mutatingHttp, this.rootUrl, { body }).pipe(map(r => r.body));
  }

  /** Update a standup entry. A MUTATION -> `mutatingHttp`. */
  updateStandupEntry(entryId: number, body: SettingsStandupEntryUpdateRequest): Observable<void> {
    return standupEntryUpdateFn(this.mutatingHttp, this.rootUrl, { entryId, body })
      .pipe(map(() => void 0));
  }

  /** Delete a standup entry. A MUTATION -> `mutatingHttp`. */
  deleteStandupEntry(entryId: number): Observable<void> {
    return standupEntryDeleteFn(this.mutatingHttp, this.rootUrl, { entryId }).pipe(map(() => void 0));
  }

  /**
   * Drag one entry onto another. A MUTATION -> `mutatingHttp`.
   *
   * The server computes the new order from the PAIR — the client sends who moved and what it landed on, not
   * a rewritten index list. (Unlike `setTaskOrder`, which rewrites every row.)
   */
  reorderStandupEntry(draggedId: number, targetId: number): Observable<void> {
    return standupEntryReorderFn(this.mutatingHttp, this.rootUrl, { body: { draggedId, targetId } })
      .pipe(map(() => void 0));
  }

  /** Copy a day's entries onto another day. A MUTATION -> `mutatingHttp`. Returns HOW MANY were imported. */
  quickImportStandup(sourceDate: string, targetDate: string): Observable<number> {
    return standupQuickImportFn(this.mutatingHttp, this.rootUrl, { body: { sourceDate, targetDate } })
      .pipe(map(r => r.body));
  }

  /** Add an issue to an entry. A MUTATION -> `mutatingHttp`. Returns the NEW ISSUE'S ID. */
  createStandupIssue(entryId: number, body: SettingsStandupIssueCreateRequest): Observable<number> {
    return standupIssueCreateFn(this.mutatingHttp, this.rootUrl, { entryId, body }).pipe(map(r => r.body));
  }

  /** Update an issue. A CHECKED write (`body.expectedVersion`), and a MUTATION -> `mutatingHttp`. */
  updateStandupIssue(
    entryId: number, issueId: number, body: SettingsStandupIssueUpdateRequest,
  ): Observable<SavedBody> {
    return standupIssueUpdateFn(this.mutatingHttp, this.rootUrl, { entryId, issueId, body })
      .pipe(map(r => r.body));
  }

  /** Delete an issue. A MUTATION -> `mutatingHttp`. */
  deleteStandupIssue(entryId: number, issueId: number): Observable<void> {
    return standupIssueDeleteFn(this.mutatingHttp, this.rootUrl, { entryId, issueId })
      .pipe(map(() => void 0));
  }

  /**
   * [ADMIN] Archive the standup week containing `date` to a file on the SERVER. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 Returns `ArchivedFileDto` — a SERVER-SIDE PATH, not a download. The browser cannot open it. It is
   * there to be SHOWN ("written to \\share\..."), not fetched. If you need bytes in the browser, the export
   * routes above are what do that.
   */
  archiveStandupWeek(date: string): Observable<ArchivedFileDto> {
    return standupArchiveWeekFn(this.mutatingHttp, this.rootUrl, { date }).pipe(map(r => r.body));
  }

  // =====================================================================================================
  // THE KEY/VALUE SETTINGS STORE — GET is OPEN, PUT is [ADMIN].
  //
  // 🔴 DELIBERATELY UNVERSIONED: no `expectedVersion` anywhere on this route, by design. Do not add one.
  // =====================================================================================================

  /**
   * Read one setting. OPEN. A READ -> the plain `http`.
   *
   * 🔴 AN UNSET KEY IS A 200 WITH A NULL VALUE, NOT A 404 — every key is unset on a fresh database. The
   * caller's correct response to a null is to fall back to the documented default (e.g. 3, for the
   * missing-logs N-day window), NOT to treat it as an error.
   */
  getSetting(key: string): Observable<SettingDto> {
    return settingGetFn(this.http, this.rootUrl, { key }).pipe(map(r => r.body));
  }

  /** [ADMIN] Write one setting. A MUTATION -> `mutatingHttp`. A null value is a 400, not a delete. */
  setSetting(key: string, value: string): Observable<void> {
    return settingSetFn(this.mutatingHttp, this.rootUrl, { key, body: { value } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // OPS — backup / export / retention. 🔴 ALL FOUR ARE [ADMIN], and all four are POSTs.
  //
  // They go on `mutatingHttp` because they are non-GET. None of them actually notifies over SignalR today,
  // so the header is INERT on these four — but "every non-GET goes on mutatingHttp" is the rule precisely
  // because it is mechanically checkable, and it fails SAFE: the cost of the header where it is not needed
  // is one header, while the cost of omitting it where it IS needed is a write that echoes back and clobbers
  // the user's own screen. If a notifier call is ever added to one of these, the client is already right.
  // =====================================================================================================

  /** [ADMIN] Run a backup now. */
  runBackup(): Observable<SettingsOpsResult> {
    return opsBackupRunFn(this.mutatingHttp, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /** [ADMIN] Run the scheduled export now. */
  runExport(): Observable<SettingsOpsResult> {
    return opsExportRunFn(this.mutatingHttp, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /**
   * [ADMIN] What retention WOULD delete. A POST that changes NOTHING — the preview is the whole point.
   *
   * Call this before `runRetention()` and show the user what they are about to lose.
   */
  previewRetention(): Observable<RetentionPreview> {
    return opsRetentionPreviewFn(this.mutatingHttp, this.rootUrl, {}).pipe(map(r => r.body));
  }

  /**
   * [ADMIN] DESTRUCTIVE, AND ASYNCHRONOUS. Actually deletes what `previewRetention()` listed.
   *
   * 🔴 The route answers **202 Accepted**, not 204: the work is handed to a background job and the response
   * means "started", NOT "finished". A caller that re-reads immediately will see the OLD data and conclude
   * nothing happened. There is no completion signal on this route — do not synthesise one.
   */
  runRetention(): Observable<void> {
    return opsRetentionRunFn(this.mutatingHttp, this.rootUrl, {}).pipe(map(() => void 0));
  }

  // =====================================================================================================
  // ME — PUT /api/me/active-team
  // =====================================================================================================

  /**
   * Switch the caller's own active team. A MUTATION -> `mutatingHttp`.
   *
   * 🔴 THE ONE MUTATING ROUTE IN THE WHOLE API THAT NOTIFIES NOBODY, and that is a recorded decision, not an
   * oversight: `DataChangedAsync` sends to a team group MINUS the caller — i.e. to everyone EXCEPT the one
   * person whose scope actually changed. Nobody else's view moves when Alice switches teams. So the header
   * this client stamps is inert here; it rides along for uniformity, and the C# explicitly warns the next
   * person not to "helpfully" add a notifier call back.
   *
   * 400 (the team is not one of yours, or is not active) arrives as an HttpErrorResponse.
   */
  setActiveTeam(teamId: number): Observable<void> {
    return meSetActiveTeamFn(this.mutatingHttp, this.rootUrl, { body: { teamId } })
      .pipe(map(() => void 0));
  }

  // =====================================================================================================
  // THE VENDORED STUBS — empty streams, ON PURPOSE, and STILL LIVE.
  //
  // 🔴 DO NOT RETYPE ONE IN PLACE. Every stub below is still the data source of a component that binds the
  // VENDORED view model under `strictTemplates` — changing its return type breaks that component's build, and
  // those components belong to OTHER (Phase-2) tasks. Add a real method BESIDE the stub under a new name; the
  // screen's own task then swaps its component over and deletes the stub. See the class comment.
  //
  // 🔴 THE REASON THESE ARE STILL STUBS HAS CHANGED — AND THE OLD REASON IS NOW FALSE. It used to be that
  // their C# routes returned a bare `IResult` via `Results.Ok(x)`, which ApiExplorer cannot infer a schema
  // from, so there was nothing honest to generate. THAT IS NO LONGER SO: M9 P3/P4 annotated every one of
  // those routes with `.Produces<T>()` and regenerated, and EVERY ONE of them now has a real, typed method
  // above (the stub's trailing comment names it). Nothing below is blocked on the API any more.
  //
  // They survive for ONE reason only: their CONSUMERS have not moved yet. The moment a Phase-2 agent points
  // its component at the real method, the stub it was using is dead and that agent deletes it. Not before —
  // and not by anyone else.
  //
  // (`getMetrics()` is the one that never had a route at all, and never will. See its comment.)
  // =====================================================================================================
  getUsers(): Observable<User[]> { return of([]); }                 // -> getUsersAll() [ADMIN] (the tab needs INACTIVE rows) + getUsersActive(); users.component.ts
  getLogGroups(): Observable<LogGroup[]> { return of([]); }         // -> getWeek() (M8.4/W4). No consumer left.
  getTaskCards(): Observable<TaskCard[]> { return of([]); }         // -> getTaskListScreen(); task-list.component.ts
  getDailyEntries(date: string): Observable<DailyEntry[]> { return of([]); }   // -> getStandupMyDay(); daily-report.component.ts
  getTeamBoard(date: string): Observable<TeamMember[]> { return of([]); }      // -> getStandupBoard(); daily-report.component.ts
  getMetrics(): Observable<Metric[]> { return of([]); }             // 🔴 NO SUCH ROUTE. The old "TODO: GET /api/reports/metrics" was WRONG — /api/reports/* is weekly, monthly, missing-logs and NOTHING ELSE. The four stat cards are CLIENT-SIDE arithmetic over getWeeklyReport() (dayTotals + daysLogged) and getMissingLogs().length. Do not go hunting for a route; there never was one.
  getMissing(): Observable<string[]> { return of([]); }             // -> getMissingLogs() (returns MissingLogWarning[], not string[]); reports.component.ts
  getWeekly(): Observable<WeeklyRow[]> { return of([]); }           // -> getWeeklyReport(); reports.component.ts
  getMonthly(): Observable<MonthlyRow[]> { return of([]); }         // -> getMonthlyReport(); reports.component.ts
  getDrilldown(): Observable<TreeNode | null> { return of(null); }  // -> getMonthlyReport().projectTree (TeamNode[]) — the SAME read, not a second one; reports.component.ts
  getTags(): Observable<Tag[]> { return of([]); }                   // -> getTagList(); task-list.component.ts
  getTemplates(): Observable<TaskTemplate[]> { return of([]); }     // -> getTemplateList(); settings.component.ts
  getContacts(): Observable<string[]> { return of([]); }            // -> getPcaContactsActive(), or getPcaContactsAll() [ADMIN] for the Settings tab; settings.component.ts
  getTeams(): Observable<string[]> { return of([]); }               // -> getTeamsActive(), or getTeamsAll() [ADMIN] for the Settings tab; settings.component.ts
  getHolidays(): Observable<string[]> { return of([]); }            // -> getHolidayList() (returns HolidayDto[], not ISO strings); settings.component.ts

  // ---- mutations still to connect ----
  saveProgress(key: string, pct: number): Observable<void> { return of(void 0); }        // -> updateBacklog(): progressPercent rides the WHOLE-RECORD checked PUT. GET the backlog first — see the BACKLOGS section.
  toggleUser(name: string): Observable<void> { return of(void 0); }                      // -> setUserActive(id, isActive) [ADMIN]. Takes an ID, not a name.
  toggleHoliday(iso: string): Observable<void> { return of(void 0); }                    // -> upsertHoliday(date) / deleteHoliday(date) [ADMIN]. Two routes, not one toggle.
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
