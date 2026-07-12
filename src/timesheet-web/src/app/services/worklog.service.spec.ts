import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { WorklogService } from './worklog.service';
import { TimeLogDto, WeekBacklogGroup } from '../api/models';

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
