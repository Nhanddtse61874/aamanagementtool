import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { WorklogService } from './worklog.service';
import { CONNECTION_ID_HEADER, RealtimeService } from '../core/realtime.service';
import {
  ArchivedFileDto, BacklogCreateRequest, BacklogDto, BacklogUpdateRequest, MissingLogWarning, SettingDto,
  TaskItemDto, TaskUpdateRequest, TimeLogDto, TimesheetWeeklyReportResponse, WeekBacklogGroup,
} from '../api/models';

describe('WorklogService (generated transport)', () => {
  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // ===================================================================================================
  // SAME-ORIGIN. This is the single easiest thing to break and the hardest to notice.
  //
  // If any call ever goes out as `http://localhost:5080/...` it is CROSS-SITE, and a `SameSite=Lax` cookie
  // is not sent on a cross-site XHR. Login would still 200 (it SETS the cookie), and every call after it
  // would go out anonymous -> 401 -> the interceptor bounces to /login -> redirect loop. The failure appears
  // nowhere near its cause. These assertions pin the URLs to relative paths so the dev proxy keeps working.
  // ===================================================================================================
  it('calls the API on same-origin RELATIVE paths, never an absolute host', () => {
    service.getWeek('2026-07-06').subscribe();
    const req = httpMock.expectOne(r => r.url === '/api/timesheet/week');

    expect(req.request.url.startsWith('http')).toBeFalse();
    expect(req.request.url).toBe('/api/timesheet/week');
    req.flush([]);
  });

  it('sends monday and allUsers as query params on the week read', () => {
    service.getWeek('2026-07-06', true).subscribe();
    const req = httpMock.expectOne(r => r.url === '/api/timesheet/week');

    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('monday')).toBe('2026-07-06');
    expect(req.request.params.get('allUsers')).toBe('true');
    req.flush([]);
  });

  // ===================================================================================================
  // THE READ CARRIES A PER-CELL VERSION. Without this the write path is dead: PUT /api/timesheet/cell
  // needs an expectedVersion, and the ONLY place the client can learn one is this response.
  // ===================================================================================================
  it('parses a per-cell rowVersion out of the week grid, and null for an empty cell', () => {
    const wire: WeekBacklogGroup[] = [{
      backlogId: 7,
      backlogCode: 'BL-1',
      project: 'Apollo',
      tasks: [{
        taskId: 42,
        backlogCode: 'BL-1',
        taskName: 'Do the thing',
        orderIndex: 0,
        mon: { hours: 4, rowVersion: 9 },   // a cell WITH hours has a version
        tue: { hours: null, rowVersion: null }, // an empty cell has none — and null is the right expectedVersion
        wed: { hours: null, rowVersion: null },
        thu: { hours: null, rowVersion: null },
        fri: { hours: null, rowVersion: null },
      }],
    }];

    let got: WeekBacklogGroup[] | undefined;
    service.getWeek('2026-07-06').subscribe(g => (got = g));
    httpMock.expectOne(r => r.url === '/api/timesheet/week').flush(wire);

    const row = got![0].tasks![0];
    expect(row.taskId).toBe(42);
    expect(row.mon!.hours).toBe(4);
    expect(row.mon!.rowVersion).toBe(9);
    expect(row.tue!.hours).toBeNull();
    expect(row.tue!.rowVersion).toBeNull();
  });

  // ===================================================================================================
  // THE WRITE. `expectedVersion` is MEANINGFUL, not "unknown": null asserts "I believe this cell is empty".
  // Send the cell's real version when it has hours; null ONLY when it genuinely has none.
  // ===================================================================================================
  it('PUTs the cell with the version it was given, and returns the NEW version the server hands back', () => {
    let newVersion: number | undefined;
    service.saveHours(42, '2026-07-06', 4, 9).subscribe(v => (newVersion = v));

    const req = httpMock.expectOne(r => r.url === '/api/timesheet/cell');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      taskId: 42, date: '2026-07-06', hours: 4, expectedVersion: 9,
    });

    // The write RETURNS the next expectedVersion. Never re-read it: between the write committing and a
    // re-read another client can write, and you would hold THEIR version with YOUR data.
    req.flush({ rowVersion: 10 });
    expect(newVersion).toBe(10);
  });

  it('sends expectedVersion: null for a cell that genuinely has no hours yet', () => {
    service.saveHours(42, '2026-07-07', 2, null).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/timesheet/cell');
    expect(req.request.body.expectedVersion).toBeNull();
    // NOT 0, and NOT omitted-as-undefined: under the five-case table those are different assertions.
    expect(req.request.body.expectedVersion).not.toBe(0);
    req.flush({ rowVersion: 1 });
  });

  it('refuses a 200 that carries no rowVersion rather than silently corrupting the version chain', () => {
    let err: Error | undefined;
    service.saveHours(42, '2026-07-06', 4, 9).subscribe({ error: e => (err = e) });

    httpMock.expectOne(r => r.url === '/api/timesheet/cell').flush({});

    expect(err).toBeDefined();
    expect(err!.message).toContain('rowVersion');
  });

  it('requires a version to clear a cell — a clear has no "I believe it is empty" case', () => {
    service.clearHours(42, '2026-07-06', 9).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/timesheet/cell');
    expect(req.request.method).toBe('DELETE');
    expect(req.request.body).toEqual({ taskId: 42, date: '2026-07-06', expectedVersion: 9 });
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  // ===================================================================================================
  // SMART FILL returns a FLAT TimeLogDto[] — not a week grid — spanning only the filled dates. The caller
  // merges it by (taskId, workDate). This test pins the SHAPE so a future change cannot quietly turn it
  // into something a caller would be tempted to `set()` over the whole grid with.
  // ===================================================================================================
  it('returns Smart Fill apply as a flat TimeLogDto[] carrying each new rowVersion', () => {
    const wire: TimeLogDto[] = [
      { id: 1, userId: 3, taskId: 42, workDate: '2026-07-08', hours: 4, rowVersion: 5 },
      { id: 2, userId: 3, taskId: 42, workDate: '2026-07-09', hours: 4, rowVersion: 6 },
    ];

    let got: TimeLogDto[] | undefined;
    service.smartFillApply([{ taskId: 42, cells: [
      { date: '2026-07-08', hours: 4 }, { date: '2026-07-09', hours: 4 },
    ] }]).subscribe(r => (got = r));

    const req = httpMock.expectOne(r => r.url === '/api/smartfill/apply');
    expect(req.request.method).toBe('POST');
    req.flush(wire);

    // Flat rows, each with its own (taskId, workDate) and its own new version — NOT a grid, and it spans
    // only the dates that were filled. Merging is the caller's job; replacing the grid from this would wipe
    // the days it does not mention.
    expect(got!.length).toBe(2);
    expect(got![0].taskId).toBe(42);
    expect(got![0].workDate).toBe('2026-07-08');
    expect(got![0].rowVersion).toBe(5);
    expect(got![1].rowVersion).toBe(6);
  });

  it('POSTs Smart Fill validate without applying anything', () => {
    service.smartFillValidate([{ taskId: 42, cells: [{ date: '2026-07-08', hours: 4 }] }]).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/smartfill/validate');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ tasks: [{ taskId: 42, cells: [{ date: '2026-07-08', hours: 4 }] }] });
    req.flush(null);
  });

  // ===================================================================================================
  // AUTH. LoginRequest is (username, password) and NOTHING else — there is no `rememberMe` on the wire.
  // `IsPersistent = true` is unconditional server-side, so "stay logged in" already holds.
  // ===================================================================================================
  it('logs in with username and password only — there is no rememberMe field', () => {
    service.login('nhan', 'pw').subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ username: 'nhan', password: 'pw' });
    expect('rememberMe' in req.request.body).toBeFalse();
    req.flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });
  });

  it('reads the caller context from /api/me', () => {
    service.me().subscribe();
    const req = httpMock.expectOne(r => r.url === '/api/me');
    expect(req.request.method).toBe('GET');
    req.flush({ id: 1, name: 'Nhan', isAdmin: false, memberTeamIds: [1], activeTeamId: 1 });
  });
});

// =====================================================================================================
// MOVE TO NEXT MONTH — GET /api/backlogs/{id}, then a CHECKED PUT to the same route.
//
// 🔴 This block needs its OWN TestBed, and the reason is the whole point of the block.
//
// `ConnectionIdHttpClient` only stamps `X-Connection-Id` when there IS a connection id, and in the plain
// TestBed above there is no live hub, so `RealtimeService.connectionId()` is null and the header is omitted
// from EVERYTHING. A header assertion there would pass on the plain client too — it would pin nothing while
// looking like it pinned the most dangerous convention in this file. So: stub a "connected" hub.
// =====================================================================================================
describe('WorklogService — Move to next month', () => {
  const CONN = 'hub-conn-1';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  /** The record exactly as `GET /api/backlogs/{id}` returns it — including the `rowVersion` that is the sole
   *  reason the GET is mandatory: the WEEK read carries no version for a BACKLOG. */
  const backlog: BacklogDto = {
    id: 7, backlogCode: 'PCT-1', project: 'Alpha', note: 'keep me',
    progressPercent: 40, periodMonth: '2026-07', rowVersion: 3,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        // A hub that IS connected. `ConnectionIdHttpClient` reads only `connectionId()`.
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('READS on the plain client — a read notifies nobody, so it must not widen X-Connection-Id', () => {
    let got: BacklogDto | undefined;
    service.getBacklog(7).subscribe(b => (got = b));

    const req = httpMock.expectOne(r => r.url === '/api/backlogs/7');
    expect(req.request.method).toBe('GET');
    expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();
    req.flush(backlog);

    expect(got!.rowVersion).toBe(3);
  });

  it('WRITES on the MUTATING client — on the plain one the write echoes back to us and clobbers our screen', () => {
    // The body a Move sends. `toUpdateRequest` (move-month.ts) is what builds it for real and is tested there;
    // what is pinned HERE is that this transport hands it over intact, on the right client, at the right URL.
    const body: BacklogUpdateRequest = {
      backlogCode: 'PCT-1', project: 'Alpha', startDate: null, endDate: null,
      periodMonth: '2026-08', type: null, assigneeUserId: null,
      deadlineInternal: null, deadlineExternal: null,
      roughEstimateHours: null, officialEstimateHours: null,
      progressPercent: 40, note: 'keep me', pcaContactId: null,
      expectedVersion: 3, auditNote: null,
    };

    service.updateBacklog(7, body).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/backlogs/7');
    expect(req.request.method).toBe('PUT');

    // 🔴 The header the server EXCLUDES us from its own SignalR broadcast by. Omit it and this write echoes
    // straight back to the person who made it: the page re-fetches and clobbers their own screen.
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);

    // The WHOLE record goes back. PUT /api/backlogs/{id} REPLACES it — an omitted field is written as NULL,
    // not left alone — so `note` and `progressPercent` surviving the trip is load-bearing, not incidental.
    expect(req.request.body.periodMonth).toBe('2026-08');
    expect(req.request.body.note).toBe('keep me');
    expect(req.request.body.progressPercent).toBe(40);
    expect(req.request.body.expectedVersion).toBe(3);

    req.flush({ rowVersion: 4 });
  });
});

// =====================================================================================================
// DRAG TO REORDER — PUT /api/tasks/{id}/order. BUMP-ONLY.
//
// 🔴 This block has its OWN TestBed with a CONNECTED hub, for exactly the reason the block above spells out,
// and the reason is not boilerplate: `ConnectionIdHttpClient` only stamps `X-Connection-Id` when there IS a
// connection id. In the plain TestBed at the top of this file there is no live hub, so the header is omitted
// from EVERYTHING — and `setTaskOrder` written on the WRONG client (`this.http`) would emit a byte-for-byte
// IDENTICAL request there. A body-only assertion in that block would pass against the bug it exists to catch.
//
// (The plan's own draft of this test lived in the plain block. It would have pinned nothing.)
// =====================================================================================================
describe('WorklogService — task order (bump-only)', () => {
  const CONN = 'hub-conn-2';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends ONLY orderIndex — a version here would 409-STORM an ordinary drag', () => {
    service.setTaskOrder(42, 3).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks/42/order');
    expect(req.request.method).toBe('PUT');

    // 🔴 EXACTLY this, and nothing else. `reorderPlan` emits one write PER ROW, so a checked variant would see
    // row 1's write invalidate the version rows 2..n are already holding — a 409 on the happy path, every drag.
    expect(req.request.body).toEqual({ orderIndex: 3 });
    expect('expectedVersion' in req.request.body).toBeFalse();
    expect('rowVersion' in req.request.body).toBeFalse();

    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('WRITES on the MUTATING client — a reorder is N writes, so N echoes would re-fetch the week N times', () => {
    service.setTaskOrder(42, 3).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks/42/order');

    // The header the server EXCLUDES us from its own SignalR broadcast by. Omit it and every one of the N
    // writes a single drag makes echoes straight back to the person who made it.
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);

    req.flush(null, { status: 204, statusText: 'No Content' });
  });
});

// =====================================================================================================
// DRAG TO TRASH — PUT /api/tasks/{id}/active. A SOFT delete, and BUMP-ONLY.
//
// 🔴 A CONNECTED hub again, and for the third time the reason is not boilerplate — it is the only thing that
// makes the header assertion below MEAN anything. `ConnectionIdHttpClient` stamps `X-Connection-Id` only when
// there IS a connection id. In the plain TestBed at the top of this file there is none, so the header is
// omitted from EVERYTHING — and `setTaskActive` written on the WRONG client (`this.http`) would emit a
// BYTE-IDENTICAL request there. A body-only assertion in that block would pass against the very bug it exists
// to catch. (The plan's own draft of this test is a body-only assertion, and it does not say which block it
// belongs in.)
// =====================================================================================================
describe('WorklogService — soft delete (bump-only)', () => {
  const CONN = 'hub-conn-3';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends ONLY isActive — this route declares no version, and inventing one would be dead code', () => {
    service.setTaskActive(42, false).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks/42/active');
    expect(req.request.method).toBe('PUT');

    // 🔴 EXACTLY this, and nothing else. The C# says so in as many words: "Rule #9: bump-only BY DESIGN, no
    // *CheckedAsync sibling -- ignore any rowVersion on the DTO (TaskActiveRequest carries none to ignore)".
    // The route's only declared outcomes are 204 and 404 — there is no 409 for a version to provoke.
    expect(req.request.body).toEqual({ isActive: false });
    expect('expectedVersion' in req.request.body).toBeFalse();
    expect('rowVersion' in req.request.body).toBeFalse();

    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('WRITES on the MUTATING client — on the plain one the delete echoes back and clobbers our own screen', () => {
    service.setTaskActive(42, false).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks/42/active');

    // The header the server EXCLUDES us from its own SignalR broadcast by. This is the assertion the whole
    // separate-TestBed dance above exists for: on the plain `http` the request is otherwise identical.
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);

    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('is a SOFT delete: `true` RESTORES through the same route, so the flag is passed through, not hard-coded',
    () => {
      service.setTaskActive(42, true).subscribe();

      const req = httpMock.expectOne(r => r.url === '/api/tasks/42/active');

      // Nothing is destroyed — `SetActiveAsync` only flips `is_active`. A `setTaskActive` that hard-coded
      // `false` into the body would pass the first test in this block and still be wrong; this is what stops
      // that, and it is the assertion that keeps the restore path open for a later milestone.
      expect(req.request.body).toEqual({ isActive: true });

      req.flush(null, { status: 204, statusText: 'No Content' });
    });
});

// =====================================================================================================
// THE BACKLOG SCREEN's TRANSPORT (M8.6/T5) — the backlog grid, the task list + checked task update, and the
// user / PCA lookups.
//
// 🔴 A CONNECTED hub again, for the FOURTH time, and for the fourth time it is not boilerplate. It is the only
// thing that makes the header assertions below mean anything: `ConnectionIdHttpClient` stamps `X-Connection-Id`
// only when there IS a connection id, so in the plain TestBed at the top of this file it is omitted from
// EVERYTHING — and `createBacklog` or `updateTask` written on the WRONG client (`this.http`) would emit a
// BYTE-IDENTICAL request there. Every assertion that could catch the mistake would pass.
//
// The reads are asserted here too, and NEGATIVELY (`headers.has(...)` is FALSE). That direction matters as
// much: a read pushed onto `mutatingHttp` widens the header's blast radius for a call that notifies nobody.
// =====================================================================================================
describe('WorklogService — the backlog screen (M8.6)', () => {
  const CONN = 'hub-conn-4';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // ---- the seven READS: right URL, right verb, relative path, and NOT on the mutating client -------------
  const READS: { name: string; call: (s: WorklogService) => void; url: string }[] = [
    { name: 'getBacklogList',       call: s => s.getBacklogList().subscribe(),        url: '/api/backlogs' },
    { name: 'getBacklogAudit',      call: s => s.getBacklogAudit(7).subscribe(),      url: '/api/backlogs/7/audit' },
    { name: 'getTasks',             call: s => s.getTasks(7).subscribe(),             url: '/api/tasks' },
    { name: 'getUsersActive',       call: s => s.getUsersActive().subscribe(),        url: '/api/users' },
    { name: 'getUserNames',         call: s => s.getUserNames().subscribe(),          url: '/api/users/names' },
    { name: 'getPcaContactsActive', call: s => s.getPcaContactsActive().subscribe(),  url: '/api/pca-contacts' },
    { name: 'getPcaContactNames',   call: s => s.getPcaContactNames().subscribe(),    url: '/api/pca-contacts/names' },
  ];

  READS.forEach(({ name, call, url }) => {
    it(`${name} GETs ${url} on the PLAIN client — a read notifies nobody, so it must not carry the header`, () => {
      call(service);

      const req = httpMock.expectOne(r => r.url === url);
      expect(req.request.method).toBe('GET');
      expect(req.request.url.startsWith('http')).toBeFalse();   // same-origin, or the auth cookie stops being sent
      expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();

      req.flush([]);
    });
  });

  // ===================================================================================================
  // 🔴 `/api/users/names`, NEVER `/api/users/all`. The `/all` route is AdminPolicy-gated: an ordinary user
  // reading it gets a 403, which takes the screen's whole forkJoin down with it — and the BACKLOG EDITOR is
  // a screen an ordinary user can reach. `/names` is the route that exists so a DEACTIVATED assignee's name
  // can still render on a record she is already on — `/api/users` omits her.
  //
  // 🔴 UPDATED M9 P5. This test's original comment said `/all` was "deliberately absent from the generated
  // client, and a C# contract test keeps it absent". THAT IS NO LONGER TRUE: M9 P2a tagged both `/all` routes
  // and they ARE in the client now (as `getUsersAll()` / `getPcaContactsAll()`), because the Users and
  // Settings screens cannot list a DEACTIVATED row without them. The old contract test is gone, replaced by
  // `SettingsEndpointsTests.The_admin_gated_full_list_is_403_for_a_NON_admin`.
  //
  // The ASSERTION below is unchanged and still exactly right — it is now the only thing pinning the backlog
  // editor off the admin route, so it matters MORE than it did, not less.
  // ===================================================================================================
  it('resolves assignee names from /api/users/names — the admin-gated /all would 403 an ordinary user', () => {
    service.getUserNames().subscribe();

    httpMock.expectNone(r => r.url === '/api/users/all');
    httpMock.expectOne(r => r.url === '/api/users/names').flush([{ id: 3, name: 'Departed Person' }]);
  });

  // ===================================================================================================
  // The grid searches CLIENT-side (`filterRows`), because `rebuildOptions` builds the four dropdowns FROM THE
  // LOADED ROWS. If this read ever started sending `?term=`, typing in the search box would silently delete
  // options out of the Project / Type / Assignee / Month dropdowns as the user typed.
  // ===================================================================================================
  it('reads the WHOLE backlog list — it must not send the endpoint\'s ?term= filter', () => {
    service.getBacklogList().subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/backlogs');
    expect(req.request.params.has('term')).toBeFalse();
    req.flush([]);
  });

  it('sends backlogId as a QUERY param on the task read, not as a path segment', () => {
    service.getTasks(7).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks');
    expect(req.request.params.get('backlogId')).toBe('7');
    req.flush([]);
  });

  // ===================================================================================================
  // 🔴 THE TASK READ IS THE ONLY CARRIER OF `status` AND `rowVersion`. Lose either and the update below is
  // dead: `rowVersion` IS the next `expectedVersion`, and `status` must be round-tripped or the checked PUT
  // (which binds `Status = req.Status` with no null-guard) wipes the column the Task List screen is built on.
  // ===================================================================================================
  it('parses status and rowVersion off each task — the two fields the checked update cannot be built without',
    () => {
      const wire: TaskItemDto[] = [
        { id: 42, backlogId: 7, taskName: 'Do the thing', orderIndex: 0, status: 'In-process', rowVersion: 9, isActive: true },
      ];

      let got: TaskItemDto[] | undefined;
      service.getTasks(7).subscribe(t => (got = t));
      httpMock.expectOne(r => r.url === '/api/tasks').flush(wire);

      expect(got![0].status).toBe('In-process');
      expect(got![0].rowVersion).toBe(9);
    });

  // ===================================================================================================
  // THE CHECKED TASK WRITE. On the MUTATING client, and carrying a round-tripped `status`.
  // ===================================================================================================
  it('PUTs a task on the MUTATING client, with status ROUND-TRIPPED and the version it was given', () => {
    const body: TaskUpdateRequest = {
      taskName: 'Renamed', orderIndex: 2, status: 'In-process', expectedVersion: 9,
    };

    service.updateTask(42, body).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasks/42');
    expect(req.request.method).toBe('PUT');

    // 🔴 The header the server EXCLUDES us from its own SignalR broadcast by. Omit it and this write echoes
    // straight back to the person who made it: the editor re-fetches and clobbers their own screen.
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);

    // 🔴 `status` survives the trip. `TaskUpdateRequest` types it optional, so a body built from the editor's
    // form alone — which never shows a status — would compile clean and land `status: null` on the server,
    // silently wiping Todo / In-process / Done / Pending off the task.
    expect(req.request.body.status).toBe('In-process');
    expect(req.request.body.expectedVersion).toBe(9);
    expect(req.request.body.taskName).toBe('Renamed');
    expect(req.request.body.orderIndex).toBe(2);

    req.flush({ rowVersion: 10 });
  });

  // ===================================================================================================
  // THE CREATE. On the MUTATING client, and its RESPONSE is load-bearing: the new id is the `backlogId` of
  // every task insert that follows, and the first rowVersion is the next checked PUT's `expectedVersion`.
  // ===================================================================================================
  it('POSTs a new backlog on the MUTATING client and hands back the new id and first rowVersion', () => {
    const body: BacklogCreateRequest = { backlogCode: 'PCT-9', project: 'Alpha', periodMonth: '2026-07' };

    let created: BacklogDto | undefined;
    service.createBacklog(body).subscribe(b => (created = b));

    const req = httpMock.expectOne(r => r.url === '/api/backlogs');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);
    expect(req.request.body.backlogCode).toBe('PCT-9');

    req.flush({ id: 12, backlogCode: 'PCT-9', project: 'Alpha', periodMonth: '2026-07', rowVersion: 1 });

    expect(created!.id).toBe(12);
    expect(created!.rowVersion).toBe(1);
  });
});

// =====================================================================================================
// M9 P5 — THE REMAINING TRANSPORT. Task List, Reports, the file exports, the tag joins, Users, Teams,
// Tags, PCA, Templates, Holidays, Default tasks, Standup, Settings, Ops, active-team.
//
// 🔴 A CONNECTED HUB, FOR THE FIFTH TIME, AND FOR THE FIFTH TIME IT IS NOT BOILERPLATE — it is the ONLY
// thing that makes ANY of the client assertions below mean anything, in BOTH directions.
//
// `ConnectionIdHttpClient` stamps `X-Connection-Id` only when there IS a connection id. In the plain TestBed
// at the top of this file there is none, so the header is omitted from EVERYTHING — and a write sent on the
// READ client (`this.http`) would emit a BYTE-IDENTICAL request there. Every body-only assertion would pass
// against the very bug it exists to catch. That is why the mutations below are asserted HERE, with a live
// id, and why the reads are asserted NEGATIVELY here too: a read pushed onto `mutatingHttp` widens the
// header's blast radius for a call that notifies nobody, and only a connected hub can catch that either.
// =====================================================================================================
describe('WorklogService — M9 P5: the remaining READS ride the PLAIN client', () => {
  const CONN = 'hub-conn-5';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  const READS: { name: string; call: (s: WorklogService) => void; url: string; body?: unknown }[] = [
    { name: 'getTaskListScreen', call: s => s.getTaskListScreen(2026, 7).subscribe(), url: '/api/tasklist', body: {} },
    { name: 'exportTaskListMarkdown', call: s => s.exportTaskListMarkdown(2026, 7).subscribe(), url: '/api/tasklist/export', body: '# md' },
    { name: 'getWeeklyReport', call: s => s.getWeeklyReport('2026-07-06').subscribe(), url: '/api/reports/weekly', body: {} },
    { name: 'getMonthlyReport', call: s => s.getMonthlyReport(2026, 7).subscribe(), url: '/api/reports/monthly', body: {} },
    { name: 'getMissingLogs', call: s => s.getMissingLogs().subscribe(), url: '/api/reports/missing-logs' },
    { name: 'getBacklogTags', call: s => s.getBacklogTags(7).subscribe(), url: '/api/backlogs/7/tags' },
    { name: 'getTask', call: s => s.getTask(42).subscribe(), url: '/api/tasks/42', body: {} },
    { name: 'getTaskTags', call: s => s.getTaskTags(42).subscribe(), url: '/api/tasks/42/tags' },
    { name: 'getUsersAll', call: s => s.getUsersAll().subscribe(), url: '/api/users/all' },
    { name: 'getTeamsActive', call: s => s.getTeamsActive().subscribe(), url: '/api/teams' },
    { name: 'getTeamsAll', call: s => s.getTeamsAll().subscribe(), url: '/api/teams/all' },
    { name: 'getTeamMembers', call: s => s.getTeamMembers(3).subscribe(), url: '/api/teams/3/members' },
    { name: 'getTagList', call: s => s.getTagList().subscribe(), url: '/api/tags' },
    { name: 'getPcaContactsAll', call: s => s.getPcaContactsAll().subscribe(), url: '/api/pca-contacts/all' },
    { name: 'getTemplateList', call: s => s.getTemplateList().subscribe(), url: '/api/templates' },
    { name: 'getHolidayList', call: s => s.getHolidayList().subscribe(), url: '/api/holidays' },
    { name: 'getDefaultTasks', call: s => s.getDefaultTasks().subscribe(), url: '/api/default-tasks' },
    { name: 'getStandupMyDay', call: s => s.getStandupMyDay().subscribe(), url: '/api/standup/entries', body: {} },
    { name: 'getStandupBoard', call: s => s.getStandupBoard().subscribe(), url: '/api/standup/board' },
    { name: 'getSetting', call: s => s.getSetting('MissingLogsNDays').subscribe(), url: '/api/settings/MissingLogsNDays', body: {} },
  ];

  READS.forEach(({ name, call, url, body }) => {
    it(`${name} GETs ${url} on the PLAIN client — a read notifies nobody, so it must not carry the header`, () => {
      call(service);

      const req = httpMock.expectOne(r => r.url === url);
      expect(req.request.method).toBe('GET');
      expect(req.request.url.startsWith('http')).toBeFalse();   // same-origin, or the auth cookie stops being sent
      expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();

      req.flush(body ?? []);
    });
  });
});

// =====================================================================================================
// 🔴 EVERY MUTATION, ON THE MUTATING CLIENT. This is the block the whole connected-hub dance exists for.
//
// The server excludes the caller from its own SignalR broadcast using the `X-Connection-Id` we send. Omit it
// and the write echoes straight back to the person who made it: their screen re-fetches and clobbers itself,
// mid-edit. Every write below is one `this.http` typo away from that, and the request would look IDENTICAL.
// =====================================================================================================
describe('WorklogService — M9 P5: every mutation rides the MUTATING client', () => {
  const CONN = 'hub-conn-6';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  const WRITES: { name: string; call: (s: WorklogService) => void; url: string; method: string }[] = [
    // ---- backlog + task extras ----
    { name: 'setBacklogTags', call: s => s.setBacklogTags(7, [1, 2], 3).subscribe(), url: '/api/backlogs/7/tags', method: 'PUT' },
    { name: 'continueBacklog', call: s => s.continueBacklog(7, '2026-08').subscribe(), url: '/api/backlogs/7/continue', method: 'POST' },
    { name: 'setTaskStatus', call: s => s.setTaskStatus(42, 'Done', 9).subscribe(), url: '/api/tasks/42/status', method: 'PUT' },
    { name: 'setTaskExtended', call: s => s.setTaskExtended(42, { type: 'IT', assigneeUserId: 3, expectedVersion: 9 }).subscribe(), url: '/api/tasks/42/extended', method: 'PUT' },
    { name: 'setTaskTags', call: s => s.setTaskTags(42, [1], 9).subscribe(), url: '/api/tasks/42/tags', method: 'PUT' },
    // ---- users [ADMIN] ----
    { name: 'createUser', call: s => s.createUser('Zoe').subscribe(), url: '/api/users', method: 'POST' },
    { name: 'renameUser', call: s => s.renameUser(3, 'Zoe Q', 1).subscribe(), url: '/api/users/3', method: 'PUT' },
    { name: 'setUserUsername', call: s => s.setUserUsername(3, 'zoe', 1).subscribe(), url: '/api/users/3/username', method: 'PUT' },
    { name: 'setUserAdmin', call: s => s.setUserAdmin(3, true, 1).subscribe(), url: '/api/users/3/admin', method: 'PUT' },
    { name: 'setUserActive', call: s => s.setUserActive(3, true).subscribe(), url: '/api/users/3/active', method: 'PUT' },
    { name: 'adminSetPassword', call: s => s.adminSetPassword(3, 'pw').subscribe(), url: '/api/auth/users/3/set-password', method: 'POST' },
    // ---- teams [ADMIN] ----
    { name: 'createTeam', call: s => s.createTeam('Alpha').subscribe(), url: '/api/teams', method: 'POST' },
    { name: 'renameTeam', call: s => s.renameTeam(3, 'Beta', 1).subscribe(), url: '/api/teams/3', method: 'PUT' },
    { name: 'setTeamMembers', call: s => s.setTeamMembers(3, [1, 2], 1).subscribe(), url: '/api/teams/3/members', method: 'PUT' },
    { name: 'setTeamActive', call: s => s.setTeamActive(3, false).subscribe(), url: '/api/teams/3/active', method: 'PUT' },
    // ---- tags [ADMIN] ----
    { name: 'createTag', call: s => s.createTag({ text: 'bug', color: '#f00', icon: 'b' }).subscribe(), url: '/api/tags', method: 'POST' },
    { name: 'updateTag', call: s => s.updateTag(1, { text: 'bug', expectedVersion: 2 }).subscribe(), url: '/api/tags/1', method: 'PUT' },
    { name: 'deleteTag', call: s => s.deleteTag(1).subscribe(), url: '/api/tags/1', method: 'DELETE' },
    // ---- pca contacts [ADMIN] ----
    { name: 'createPcaContact', call: s => s.createPcaContact('Pat').subscribe(), url: '/api/pca-contacts', method: 'POST' },
    { name: 'renamePcaContact', call: s => s.renamePcaContact(1, 'Pat Q', 1).subscribe(), url: '/api/pca-contacts/1', method: 'PUT' },
    { name: 'setPcaContactActive', call: s => s.setPcaContactActive(1, false).subscribe(), url: '/api/pca-contacts/1/active', method: 'PUT' },
    // ---- templates [ADMIN] ----
    { name: 'createTemplate', call: s => s.createTemplate({ templateName: 'T', taskName: 'row', orderIndex: 0 }).subscribe(), url: '/api/templates', method: 'POST' },
    { name: 'deleteTemplate', call: s => s.deleteTemplate(1).subscribe(), url: '/api/templates/1', method: 'DELETE' },
    { name: 'deleteTemplateByName', call: s => s.deleteTemplateByName('T').subscribe(), url: '/api/templates', method: 'DELETE' },
    // ---- holidays [ADMIN] ----
    { name: 'upsertHoliday', call: s => s.upsertHoliday('2026-01-01', 'New Year').subscribe(), url: '/api/holidays', method: 'POST' },
    { name: 'deleteHoliday', call: s => s.deleteHoliday('2026-01-01').subscribe(), url: '/api/holidays/2026-01-01', method: 'DELETE' },
    // ---- default tasks [ADMIN] ----
    { name: 'createDefaultTask', call: s => s.createDefaultTask('Standup', 0).subscribe(), url: '/api/default-tasks', method: 'POST' },
    { name: 'setDefaultTaskActive', call: s => s.setDefaultTaskActive(1, false).subscribe(), url: '/api/default-tasks/1/active', method: 'PUT' },
    { name: 'syncDefaultTasks', call: s => s.syncDefaultTasks().subscribe(), url: '/api/default-tasks/sync', method: 'POST' },
    // ---- standup ----
    { name: 'createStandupEntry', call: s => s.createStandupEntry({ workDate: '2026-07-06', taskText: 'x' }).subscribe(), url: '/api/standup/entries', method: 'POST' },
    { name: 'updateStandupEntry', call: s => s.updateStandupEntry(1, { taskText: 'y' }).subscribe(), url: '/api/standup/entries/1', method: 'PUT' },
    { name: 'deleteStandupEntry', call: s => s.deleteStandupEntry(1).subscribe(), url: '/api/standup/entries/1', method: 'DELETE' },
    { name: 'reorderStandupEntry', call: s => s.reorderStandupEntry(1, 2).subscribe(), url: '/api/standup/entries/reorder', method: 'PUT' },
    { name: 'quickImportStandup', call: s => s.quickImportStandup('2026-07-06', '2026-07-07').subscribe(), url: '/api/standup/quick-import', method: 'POST' },
    { name: 'createStandupIssue', call: s => s.createStandupIssue(1, { issueText: 'i' }).subscribe(), url: '/api/standup/entries/1/issues', method: 'POST' },
    { name: 'updateStandupIssue', call: s => s.updateStandupIssue(1, 2, { issueText: 'i', expectedVersion: 4 }).subscribe(), url: '/api/standup/entries/1/issues/2', method: 'PUT' },
    { name: 'deleteStandupIssue', call: s => s.deleteStandupIssue(1, 2).subscribe(), url: '/api/standup/entries/1/issues/2', method: 'DELETE' },
    { name: 'archiveStandupWeek', call: s => s.archiveStandupWeek('2026-07-06').subscribe(), url: '/api/standup/archive', method: 'POST' },
    // ---- settings k/v [ADMIN] ----
    { name: 'setSetting', call: s => s.setSetting('MissingLogsNDays', '5').subscribe(), url: '/api/settings/MissingLogsNDays', method: 'PUT' },
    // ---- ops [ADMIN] ----
    { name: 'runBackup', call: s => s.runBackup().subscribe(), url: '/api/ops/backup/run', method: 'POST' },
    { name: 'runExport', call: s => s.runExport().subscribe(), url: '/api/ops/export/run', method: 'POST' },
    { name: 'previewRetention', call: s => s.previewRetention().subscribe(), url: '/api/ops/retention/preview', method: 'POST' },
    { name: 'runRetention', call: s => s.runRetention().subscribe(), url: '/api/ops/retention/run', method: 'POST' },
    // ---- me ----
    { name: 'setActiveTeam', call: s => s.setActiveTeam(3).subscribe(), url: '/api/me/active-team', method: 'PUT' },
  ];

  WRITES.forEach(({ name, call, url, method }) => {
    it(`${name} ${method}s ${url} on the MUTATING client — on the plain one it would echo back and clobber the screen`, () => {
      call(service);

      const req = httpMock.expectOne(r => r.url === url);
      expect(req.request.method).toBe(method);
      expect(req.request.url.startsWith('http')).toBeFalse();

      // 🔴 THE assertion. The header the server EXCLUDES us from its own SignalR broadcast by. Without a
      // connected hub (see the block comment) this would pass even if the call rode `this.http`.
      expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe(CONN);

      req.flush({});
    });
  });
});

// =====================================================================================================
// 🔴 THE TEAM FILTER. The single most dangerous parameter added in M9, and it fails in the WORST direction.
//
// `teamIds: []` reads like "no teams". It is not. The generated RequestBuilder serialises a query array with
// explode:true — ONE `?teamIds=` entry PER ELEMENT — so an EMPTY array appends NOTHING and the key is
// ABSENT from the URL. And the server reads an ABSENT key as `ctx.MemberTeamIds`: EVERY TEAM YOU BELONG TO.
//
// So "filter to nothing" shows EVERYTHING. These tests pin that inversion in place so a future reader cannot
// talk themselves into "[] must surely mean empty".
// =====================================================================================================
describe('WorklogService — M9 P5: the team filter inverts on an empty array', () => {
  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends ONE ?teamIds= entry PER TEAM — the repeated-key shape the server intersects against membership', () => {
    service.getTaskListScreen(2026, 7, [4, 9]).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/tasklist');
    // NOT "4,9" in a single entry: the server int.TryParses each entry and silently DROPS what does not
    // parse, so a comma-joined value would intersect to the empty set — no rows, no error, no clue.
    expect(req.request.params.getAll('teamIds')).toEqual(['4', '9']);
    expect(req.request.params.get('year')).toBe('2026');
    expect(req.request.params.get('month')).toBe('7');
    req.flush({});
  });

  it('🔴 an EMPTY teamIds array is BYTE-IDENTICAL to omitting it — and the server reads that as ALL MY TEAMS', () => {
    service.getTaskListScreen(2026, 7, []).subscribe();
    service.getTaskListScreen(2026, 7, undefined).subscribe();

    const reqs = httpMock.match(r => r.url === '/api/tasklist');
    expect(reqs.length).toBe(2);

    // The key is not merely empty — it is ABSENT. There is NO value the client can send through this
    // parameter that means "no teams", which is exactly why the SCREEN must render its empty state locally
    // instead of calling with [].
    expect(reqs[0].request.params.has('teamIds')).toBeFalse();
    expect(reqs[0].request.urlWithParams).toBe(reqs[1].request.urlWithParams);

    reqs.forEach(r => r.flush({}));
  });

  it('inverts the same way on the weekly report, the monthly report and the standup board', () => {
    service.getWeeklyReport('2026-07-06', { teamIds: [] }).subscribe();
    service.getMonthlyReport(2026, 7, { teamIds: [] }).subscribe();
    service.getStandupBoard('2026-07-06', []).subscribe();

    const weekly = httpMock.expectOne(r => r.url === '/api/reports/weekly');
    const monthly = httpMock.expectOne(r => r.url === '/api/reports/monthly');
    const board = httpMock.expectOne(r => r.url === '/api/standup/board');

    expect(weekly.request.params.has('teamIds')).toBeFalse();
    expect(monthly.request.params.has('teamIds')).toBeFalse();
    expect(board.request.params.has('teamIds')).toBeFalse();

    weekly.flush({});
    monthly.flush({});
    board.flush([]);
  });

  it('passes a real selection through verbatim, inventing no sentinel', () => {
    service.getWeeklyReport('2026-07-06', { userId: 3, project: 'Apollo', teamIds: [1] }).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/reports/weekly');
    expect(req.request.params.get('monday')).toBe('2026-07-06');
    expect(req.request.params.get('userId')).toBe('3');
    expect(req.request.params.get('project')).toBe('Apollo');
    expect(req.request.params.getAll('teamIds')).toEqual(['1']);
    req.flush({});
  });
});

// =====================================================================================================
// THE TWO HAND-WRITTEN FILE EXPORTS — GET /api/export/excel · GET /api/export/markdown
//
// NOT generated, deliberately: both routes return binary / raw text, which ng-openapi-gen cannot type, so
// "Export" is kept out of includeTags on purpose. Hand-written means hand-tested — in particular the two
// things the generator would otherwise have got right for free: a SAME-ORIGIN relative URL, and the
// repeated-key teamIds shape.
// =====================================================================================================
describe('WorklogService — M9 P5: the hand-written file exports', () => {
  const CONN = 'hub-conn-7';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('requests the xlsx as a BLOB, on a same-origin RELATIVE path, on the PLAIN client', () => {
    let got: Blob | undefined;
    service.exportExcel(2026, 7).subscribe(b => (got = b));

    const req = httpMock.expectOne(r => r.url === '/api/export/excel');
    expect(req.request.method).toBe('GET');

    // 🔴 NEVER an absolute URL. `http://localhost:5080/...` is CROSS-SITE, and a SameSite=Lax cookie is not
    // sent on a cross-site request — the download would go out anonymous and 401.
    expect(req.request.url.startsWith('http')).toBeFalse();
    expect(req.request.url).toBe('/api/export/excel');

    // A spreadsheet is not JSON. responseType must be blob or HttpClient will try to parse it.
    expect(req.request.responseType).toBe('blob');

    // A download notifies nobody, so it stays on the plain client (the hub IS connected here — see above).
    expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();

    const blob = new Blob(['xlsx-bytes']);
    req.flush(blob);
    expect(got).toBe(blob);
  });

  it('requests the markdown as a BLOB too — it is a download, not a preview', () => {
    service.exportMarkdown(2026, 7).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/export/markdown');
    expect(req.request.method).toBe('GET');
    expect(req.request.url.startsWith('http')).toBeFalse();
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['# md']));
  });

  it('sends year, month and the optional filter — and ONE ?teamIds= entry per team', () => {
    service.exportExcel(2026, 7, { userId: 3, project: 'Apollo', teamIds: [4, 9] }).subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/export/excel');
    expect(req.request.params.get('year')).toBe('2026');
    expect(req.request.params.get('month')).toBe('7');
    expect(req.request.params.get('userId')).toBe('3');
    expect(req.request.params.get('project')).toBe('Apollo');

    // `append`, not `set`. The server hand-reads the raw query and int.TryParses each entry; a comma-joined
    // "4,9" would parse to nothing and intersect to the empty set.
    expect(req.request.params.getAll('teamIds')).toEqual(['4', '9']);
    req.flush(new Blob(['x']));
  });

  it('🔴 omits teamIds entirely when the filter is empty — the same ALL-MY-TEAMS inversion as the generated calls', () => {
    service.exportExcel(2026, 7, { teamIds: [] }).subscribe();
    service.exportMarkdown(2026, 7).subscribe();

    const excel = httpMock.expectOne(r => r.url === '/api/export/excel');
    const md = httpMock.expectOne(r => r.url === '/api/export/markdown');

    // `EffectiveTeamIds` reads an ABSENT key as ctx.MemberTeamIds — every team the caller belongs to. The
    // stakes are highest on the markdown route, whose teamIds is NULLABLE server-side (null = EVERY TEAM).
    expect(excel.request.params.has('teamIds')).toBeFalse();
    expect(md.request.params.has('teamIds')).toBeFalse();

    // And no userId / project keys when they were not asked for.
    expect(md.request.params.has('userId')).toBeFalse();
    expect(md.request.params.has('project')).toBeFalse();

    excel.flush(new Blob(['x']));
    md.flush(new Blob(['y']));
  });

  it('reads the Task List month export as TEXT via the GENERATED client — a string, not a blob', () => {
    let got: string | undefined;
    service.exportTaskListMarkdown(2026, 7).subscribe(m => (got = m));

    const req = httpMock.expectOne(r => r.url === '/api/tasklist/export');
    // Unlike the two /api/export/* routes, THIS one IS generated: text/markdown types cleanly as a string,
    // and taskListExport already sets responseType 'text' itself. There was nothing to hand-write.
    expect(req.request.responseType).toBe('text');
    req.flush('# July');

    expect(got).toBe('# July');
  });
});

// =====================================================================================================
// THE BODIES. Transport is pinned above; this block pins what actually goes IN each request and what comes
// back out — the parts a wrong body would corrupt SILENTLY.
// =====================================================================================================
describe('WorklogService — M9 P5: request bodies and response shapes', () => {
  const CONN = 'hub-conn-8';

  let service: WorklogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: () => CONN } },
      ],
    });
    service = TestBed.inject(WorklogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends expectedVersion on every CHECKED write — without it the server cannot detect a lost update', () => {
    service.setBacklogTags(7, [1, 2], 3).subscribe();
    const tags = httpMock.expectOne(r => r.url === '/api/backlogs/7/tags');
    expect(tags.request.body).toEqual({ tagIds: [1, 2], expectedVersion: 3 });
    tags.flush({ rowVersion: 4 });

    service.setTaskStatus(42, 'Done', 9).subscribe();
    const status = httpMock.expectOne(r => r.url === '/api/tasks/42/status');
    expect(status.request.body).toEqual({ status: 'Done', expectedVersion: 9 });
    status.flush({ rowVersion: 10 });

    service.setTeamMembers(3, [1, 2], 5).subscribe();
    const members = httpMock.expectOne(r => r.url === '/api/teams/3/members');
    expect(members.request.body).toEqual({ userIds: [1, 2], expectedVersion: 5 });
    members.flush({ rowVersion: 6 });

    service.setUserAdmin(3, true, 1).subscribe();
    const admin = httpMock.expectOne(r => r.url === '/api/users/3/admin');
    expect(admin.request.body).toEqual({ isAdmin: true, expectedVersion: 1 });
    admin.flush({ rowVersion: 2 });
  });

  it('sends ONLY isActive on the four bump-only soft-delete routes — no version, in either direction', () => {
    const calls: { call: () => void; url: string }[] = [
      { call: () => service.setUserActive(3, true).subscribe(), url: '/api/users/3/active' },
      { call: () => service.setTeamActive(3, false).subscribe(), url: '/api/teams/3/active' },
      { call: () => service.setPcaContactActive(1, false).subscribe(), url: '/api/pca-contacts/1/active' },
      { call: () => service.setDefaultTaskActive(1, true).subscribe(), url: '/api/default-tasks/1/active' },
    ];

    calls.forEach(({ call, url }) => {
      call();
      const req = httpMock.expectOne(r => r.url === url);
      expect('expectedVersion' in req.request.body).toBeFalse();
      expect('rowVersion' in req.request.body).toBeFalse();
      // The flag is PASSED THROUGH, never hard-coded: `true` RESTORES through the same route.
      expect(Object.keys(req.request.body)).toEqual(['isActive']);
      req.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  it('sends templateName as a QUERY param on the delete-whole-template route, not a path segment', () => {
    service.deleteTemplateByName('Sprint').subscribe();

    const req = httpMock.expectOne(r => r.url === '/api/templates');
    expect(req.request.method).toBe('DELETE');
    expect(req.request.params.get('templateName')).toBe('Sprint');
    req.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('puts the holiday date in the PATH on delete and in the BODY on upsert — there is no holiday id', () => {
    service.upsertHoliday('2026-01-01', 'New Year').subscribe();
    const up = httpMock.expectOne(r => r.url === '/api/holidays');
    expect(up.request.method).toBe('POST');
    expect(up.request.body).toEqual({ date: '2026-01-01', description: 'New Year' });
    up.flush(null, { status: 204, statusText: 'No Content' });

    service.deleteHoliday('2026-01-01').subscribe();
    const del = httpMock.expectOne(r => r.url === '/api/holidays/2026-01-01');
    expect(del.request.method).toBe('DELETE');
    del.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends the standup archive date as a QUERY param and hands back a SERVER path, not a download', () => {
    let got: ArchivedFileDto | undefined;
    service.archiveStandupWeek('2026-07-06').subscribe(a => (got = a));

    const req = httpMock.expectOne(r => r.url === '/api/standup/archive');
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('date')).toBe('2026-07-06');

    req.flush({ path: 'C:\\share\\standup\\2026-W28.md' });

    // 🔴 A path on the SERVER. The browser cannot open it — it is there to be SHOWN, not fetched.
    expect(got!.path).toBe('C:\\share\\standup\\2026-W28.md');
  });

  it('hands back the new id from the two standup creates — the caller needs it and must not re-read', () => {
    let entryId: number | undefined;
    service.createStandupEntry({ workDate: '2026-07-06', taskText: 'x' }).subscribe(id => (entryId = id));
    httpMock.expectOne(r => r.url === '/api/standup/entries').flush(77);
    expect(entryId).toBe(77);

    let issueId: number | undefined;
    service.createStandupIssue(77, { issueText: 'blocked' }).subscribe(id => (issueId = id));
    httpMock.expectOne(r => r.url === '/api/standup/entries/77/issues').flush(5);
    expect(issueId).toBe(5);
  });

  // ===================================================================================================
  // 🔴 THE FOUR REPORTS STAT CARDS ARE CLIENT-SIDE ARITHMETIC. There is NO /api/reports/metrics — the API
  // has exactly three /api/reports/* routes and this is what they carry. `getMetrics()`'s old TODO comment
  // named a route that never existed; this test is what makes the alternative concrete.
  // ===================================================================================================
  it('carries everything the stat cards need on the WEEKLY response — no metrics route is needed, or exists', () => {
    const wire: TimesheetWeeklyReportResponse = {
      dayTotals: [
        { date: '2026-07-06', totalHours: 8 },
        { date: '2026-07-07', totalHours: 6 },
      ],
      daysLogged: { logged: 2, workingDays: 5 },
      detailRows: [],
    };

    let got: TimesheetWeeklyReportResponse | undefined;
    service.getWeeklyReport('2026-07-06').subscribe(r => (got = r));
    httpMock.expectOne(r => r.url === '/api/reports/weekly').flush(wire);

    // Total hours, days logged, working days — three of the four cards, all derivable from this ONE response.
    expect(got!.dayTotals!.reduce((sum, d) => sum + (d.totalHours ?? 0), 0)).toBe(14);
    expect(got!.daysLogged!.logged).toBe(2);
    expect(got!.daysLogged!.workingDays).toBe(5);
  });

  it('returns missing logs as MissingLogWarning[] — a bare userName, deliberately no id — and takes no params', () => {
    const wire: MissingLogWarning[] = [{ userName: 'Zoe' }, { userName: 'Pat' }];

    let got: MissingLogWarning[] | undefined;
    service.getMissingLogs().subscribe(m => (got = m));

    const req = httpMock.expectOne(r => r.url === '/api/reports/missing-logs');
    // The route accepts NO client parameters at all: N is a server-side setting (a client-supplied N would
    // let anyone request an arbitrarily large scan window) and the team scope is internal.
    expect(req.request.params.keys().length).toBe(0);
    req.flush(wire);

    // The 4th stat card is just this length.
    expect(got!.length).toBe(2);
    expect(got![0].userName).toBe('Zoe');
  });

  it('returns the tag JOINS as bare id arrays — resolve them against getTagList()', () => {
    let backlogTags: number[] | undefined;
    service.getBacklogTags(7).subscribe(t => (backlogTags = t));
    httpMock.expectOne(r => r.url === '/api/backlogs/7/tags').flush([3, 8]);
    expect(backlogTags).toEqual([3, 8]);

    let taskTags: number[] | undefined;
    service.getTaskTags(42).subscribe(t => (taskTags = t));
    httpMock.expectOne(r => r.url === '/api/tasks/42/tags').flush([3]);
    expect(taskTags).toEqual([3]);
  });

  it('reads an UNSET setting as a 200 with a null value — not a 404, and not an error', () => {
    let got: SettingDto | undefined;
    service.getSetting('MissingLogsNDays').subscribe(s => (got = s));

    // Every key is unset on a fresh database. The caller falls back to the documented default; treating this
    // as a failure would break every settings form on first run.
    httpMock.expectOne(r => r.url === '/api/settings/MissingLogsNDays')
      .flush({ key: 'MissingLogsNDays', value: null });

    expect(got!.value).toBeNull();
  });
});

// =====================================================================================================
// THE VENDORED-STUB GUARD IS GONE — and so is the thing it guarded. M9 P7.
//
// Two tests lived here. Both asserted the stub layer's CONVENTION, not any behaviour of the app:
//
//   1. 'still exposes every stub, still empty, and still making NO http call' — that getLogGroups(),
//      getDailyEntries(), getTeamBoard() and getTags() each returned `[]` and touched no network.
//   2. 'keeps each real method and its stub as SEPARATE, DIFFERENTLY-NAMED members' — that a real method
//      and the stub it displaced coexisted under different names while the screen moved between them.
//
// Both pinned a contract that has now been deliberately retired: M9 P7 deleted those last four stubs, whose
// consumers had ALL migrated to the generated DTOs, and deleted `models/worklog.models.ts` with them. The
// tests are removed rather than emptied — an assertion over an empty list passes while asserting nothing,
// which is a worse lie than no test at all. Every real method they name (getTagList, getStandupMyDay,
// getStandupBoard, getWeeklyReport, …) is covered by the transport suites above, which is where the
// behaviour actually lives.
//
// Nothing here asserted a SECURITY property. The admin-gating tests — `/api/users/names` vs the
// AdminPolicy-gated `/api/users/all` (403 for an ordinary user), and the [ADMIN] route rows — are in the
// transport describe above and are UNTOUCHED.
// =====================================================================================================
