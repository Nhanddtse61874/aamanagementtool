import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { WorklogService } from '../services/worklog.service';
import { CONNECTION_ID_HEADER, ConnectionIdHttpClient, RealtimeService } from './realtime.service';

/**
 * The header that keeps a write from echoing back to the person who made it.
 *
 * `ClientContextFilter` reads `X-Connection-Id` off the request and hands it to
 * `IChangeNotifier.DataChangedAsync(kind, teamId, exceptConnectionId)`, which is how the server EXCLUDES the
 * caller from its own SignalR broadcast. Without the header the server has nobody to exclude: our own change
 * comes straight back to us, the page re-fetches the week, and the re-fetch CLOBBERS THE CONFLICT DIALOG the
 * 409 just raised — the user's chance to keep their work vanishes while they are looking at it.
 *
 * It is one header, it is invisible, and nothing else in the app fails if it is missing. Hence this test.
 */
describe('X-Connection-Id', () => {
  let http: HttpTestingController;
  let connectionId: ReturnType<typeof signal<string | null>>;

  function setUp(id: string | null): void {
    connectionId = signal<string | null>(id);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([])),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId, start: () => undefined } },
      ],
    });

    http = TestBed.inject(HttpTestingController);
  }

  afterEach(() => http.verify());

  it('is stamped on a request made through ConnectionIdHttpClient', () => {
    setUp('conn-abc-123');
    const client = TestBed.inject(ConnectionIdHttpClient);

    client.get('/api/anything').subscribe();

    const req = http.expectOne('/api/anything');
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe('conn-abc-123');
    req.flush({});
  });

  it('is OMITTED when there is no live hub connection -- rather than sent as "null"', () => {
    setUp(null);
    const client = TestBed.inject(ConnectionIdHttpClient);

    client.get('/api/anything').subscribe();

    const req = http.expectOne('/api/anything');
    expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();
    req.flush({});
  });

  it('picks up a NEW connection id after a reconnect, instead of pinning the dead one', () => {
    setUp('first-connection');
    const client = TestBed.inject(ConnectionIdHttpClient);

    // A reconnect mints a new connection id. Excluding the OLD one excludes a connection that no longer
    // exists -- so we would start receiving our own echoes again, silently.
    connectionId.set('second-connection');

    client.get('/api/anything').subscribe();

    const req = http.expectOne('/api/anything');
    expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe('second-connection');
    req.flush({});
  });

  // The point of the whole exercise: it must be on the routes that actually MUTATE, which are the only ones
  // that broadcast. A test that only proved the plumbing works in isolation would not catch WorklogService
  // quietly using the plain HttpClient.
  describe('is carried by every MUTATING call WorklogService makes', () => {
    it('PUT /api/timesheet/cell (saveHours)', () => {
      setUp('conn-1');
      TestBed.inject(WorklogService).saveHours(7, '2026-07-13', 4, 11).subscribe();

      const req = http.expectOne('/api/timesheet/cell');
      expect(req.request.method).toBe('PUT');
      expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe('conn-1');
      req.flush({ rowVersion: 12 });
    });

    it('DELETE /api/timesheet/cell (clearHours)', () => {
      setUp('conn-1');
      TestBed.inject(WorklogService).clearHours(7, '2026-07-13', 11).subscribe();

      const req = http.expectOne('/api/timesheet/cell');
      expect(req.request.method).toBe('DELETE');
      expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe('conn-1');
      req.flush(null);
    });

    it('POST /api/smartfill/apply', () => {
      setUp('conn-1');
      TestBed.inject(WorklogService)
        .smartFillApply([{ taskId: 7, cells: [{ date: '2026-07-13', hours: 4 }] }])
        .subscribe();

      const req = http.expectOne('/api/smartfill/apply');
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.get(CONNECTION_ID_HEADER)).toBe('conn-1');
      req.flush([]);
    });
  });

  // The week read notifies nobody, so there is no echo to suppress and no reason to widen the header's
  // blast radius. This is a deliberate choice, pinned so it stays one.
  it('is NOT sent on the week read, which mutates nothing', () => {
    setUp('conn-1');
    TestBed.inject(WorklogService).getWeek('2026-07-13').subscribe();

    const req = http.expectOne(r => r.url === '/api/timesheet/week');
    expect(req.request.headers.has(CONNECTION_ID_HEADER)).toBeFalse();
    req.flush([]);
  });
});

describe('the save contract WorklogService puts on the wire', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([])),
        provideHttpClientTesting(),
        { provide: RealtimeService, useValue: { connectionId: signal(null), start: () => undefined } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends expectedVersion: null VERBATIM for an empty cell -- it is a claim, not a missing value', () => {
    TestBed.inject(WorklogService).saveHours(7, '2026-07-13', 4, null).subscribe();

    const req = http.expectOne('/api/timesheet/cell');
    // `null` must survive serialisation as an explicit null. If it were dropped from the body, the server
    // would bind `ExpectedVersion` to null anyway -- but a `0` here would be a DIFFERENT assertion entirely.
    expect(req.request.body).toEqual({ taskId: 7, date: '2026-07-13', hours: 4, expectedVersion: null });
    req.flush({ rowVersion: 1 });
  });

  it('sends the real version for a populated cell', () => {
    TestBed.inject(WorklogService).saveHours(7, '2026-07-13', 6, 11).subscribe();

    const req = http.expectOne('/api/timesheet/cell');
    expect(req.request.body.expectedVersion).toBe(11);
    req.flush({ rowVersion: 12 });
  });

  it('hands back the version the WRITE returned', () => {
    let got: number | undefined;
    TestBed.inject(WorklogService).saveHours(7, '2026-07-13', 6, 11).subscribe(v => (got = v));

    http.expectOne('/api/timesheet/cell').flush({ rowVersion: 12 });

    expect(got).toBe(12);
  });

  // A 200 whose body is not the body we asked for is not impossible (a proxy's error page, a version skew).
  // Failing loudly beats a silent fallback to 0 or null, which would feed a WRONG expectedVersion into the
  // next save -- and that either 409s spuriously or silently overwrites another user.
  it('REFUSES a 200 that carries no rowVersion rather than guessing one', () => {
    let error: Error | undefined;
    TestBed.inject(WorklogService).saveHours(7, '2026-07-13', 6, 11)
      .subscribe({ error: (e: Error) => (error = e) });

    http.expectOne('/api/timesheet/cell').flush({});

    expect(error).toBeTruthy();
    expect(error?.message).toContain('rowVersion');
  });
});
