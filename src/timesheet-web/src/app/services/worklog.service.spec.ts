import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { WorklogService } from './worklog.service';
import { CONNECTION_ID_HEADER, RealtimeService } from '../core/realtime.service';
import { BacklogDto, BacklogUpdateRequest, TimeLogDto, WeekBacklogGroup } from '../api/models';

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
