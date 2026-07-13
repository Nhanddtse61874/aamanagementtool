import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';

import { WorklogService } from '../services/worklog.service';
import {
  CONNECTION_ID_HEADER, ConnectionIdHttpClient, DataChange, DataKind, RealtimeService,
} from './realtime.service';

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

/**
 * 🔴 M9/P6d — THE FEED NOW SAYS *WHAT* CHANGED.
 *
 * `hub.on(DATA_CHANGED, () => this._dataChanged.next())` threw away BOTH of the server's arguments. The
 * server has always sent `SendAsync("DataChanged", kind, teamId)`; the client has always discarded them. So
 * `dataChanged` was `Observable<void>`, no screen could tell a tag edit from a timesheet write, and every
 * change anywhere re-fetched everything. WPF's ViewModels have filtered on `DataKind` since P8; the web
 * could not, at all.
 *
 * These tests drive the REAL handler the service registers on the REAL hub — the builder's `build()` is
 * stubbed, but everything downstream of it is production code. A test that merely `next()`d the private
 * subject would be perfectly green against a handler that still ignores its arguments.
 */
describe('🔴 dataChanged carries WHAT changed', () => {
  /** The handlers `RealtimeService.start()` registers, captured off a fake hub. */
  let handlers: Map<string, (...args: unknown[]) => void>;
  let service: RealtimeService;

  beforeEach(() => {
    handlers = new Map();

    const hub = {
      on: (name: string, cb: (...args: unknown[]) => void) => { handlers.set(name, cb); },
      onreconnected: () => undefined,
      onreconnecting: () => undefined,
      onclose: () => undefined,
      start: () => Promise.resolve(),
      stop: () => Promise.resolve(),
      connectionId: 'conn-1',
    };

    // `withUrl` and `withAutomaticReconnect` both return the builder, so stubbing `build` is enough to hand
    // the service our hub without touching a socket.
    spyOn(HubConnectionBuilder.prototype, 'build').and.returnValue(hub as unknown as HubConnection);

    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(RealtimeService);
    service.start();
  });

  /** Fire the hub method exactly as the server does: `SendAsync("DataChanged", kind, teamId)`. */
  function serverSends(kind: number, teamId: number): void {
    handlers.get('DataChanged')!(kind, teamId);
  }

  it('registers a handler for the server\'s DataChanged method', () => {
    expect(handlers.has('DataChanged')).toBeTrue();
  });

  /**
   * 🔴 THE test. Revert the handler to `() => this._dataChanged.next()` and this goes red — nothing else in
   * the suite would.
   */
  it('🔴 emits the (kind, teamId) the server sent — it does not discard them', () => {
    const seen: DataChange[] = [];
    service.dataChanged.subscribe(e => seen.push(e));

    serverSends(DataKind.Logs, 42);

    expect(seen).toEqual([{ kind: DataKind.Logs, teamId: 42 }]);
  });

  /**
   * 🔴 THE WIRE FORMAT, AND THE ONE THAT WOULD HAVE COST A DAY.
   *
   * `DataKind` is a bare C# enum and `Program.cs` calls `builder.Services.AddSignalR()` with NO
   * `.AddJsonProtocol(...)` — there is not one `JsonStringEnumConverter` in the solution. SignalR's default
   * System.Text.Json protocol therefore serialises the enum as its INTEGER ORDINAL. The server sends `3`,
   * never `"Logs"`.
   *
   * So a consumer that wrote `if (e.kind === 'Logs')` would compile CLEAN under `strict` — and match
   * NOTHING, forever, on a feed whose entire purpose is filtering. This pins the ordinals so that cannot be
   * "tidied" into strings without a red test.
   */
  it('🔴 kind is the NUMERIC ordinal of the C# enum, never its name', () => {
    const seen: DataChange[] = [];
    service.dataChanged.subscribe(e => seen.push(e));

    serverSends(7, 0);   // DataKind.Tags, teamId 0 = the global broadcast sentinel

    expect(seen[0].kind).toBe(DataKind.Tags);
    expect(seen[0].kind).toBe(7);
    expect(typeof seen[0].kind).toBe('number');

    // The declaration order of `TimesheetApp.Core/Services/DataChangedMessage.cs`, which is what the ordinals
    // ARE. If that enum is ever reordered, these numbers move and both sides must move together.
    expect([
      DataKind.Backlogs, DataKind.Tasks, DataKind.Users, DataKind.Logs, DataKind.Templates,
      DataKind.DefaultTasks, DataKind.Standup, DataKind.Tags, DataKind.PcaContacts, DataKind.Holidays,
      DataKind.Teams,
    ]).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  });

  /** `teamId: 0` is the reserved BROADCAST sentinel for the seven entities with no team column at all — it is
   *  NOT a team. It must arrive intact, or a global change (every tag edit) is indistinguishable from noise. */
  it('carries teamId 0 — the global-broadcast sentinel — verbatim', () => {
    const seen: DataChange[] = [];
    service.dataChanged.subscribe(e => seen.push(e));

    serverSends(DataKind.Tags, 0);

    expect(seen[0].teamId).toBe(0);
  });

  it('emits once per server push, in order', () => {
    const seen: DataChange[] = [];
    service.dataChanged.subscribe(e => seen.push(e));

    serverSends(DataKind.Backlogs, 1);
    serverSends(DataKind.Standup, 2);

    expect(seen).toEqual([
      { kind: DataKind.Backlogs, teamId: 1 },
      { kind: DataKind.Standup, teamId: 2 },
    ]);
  });

  /**
   * 🔴 THE PRODUCTION CONSUMERS SURVIVE — and this is a COMPILE-TIME assertion wearing a runtime coat.
   *
   * `backlog.component.ts` and `log-work.component.ts` both do `.subscribe(() => this.refresh.next())`. A
   * zero-arg callback IS assignable to `(v: DataChange) => void`, which is the only reason widening the
   * payload did not force an edit into two components this task was forbidden to touch. If that ever stops
   * being true, THIS FILE stops compiling — which is the point of writing it down as a typed local.
   */
  it('still accepts a zero-arg subscriber — the two screens that ignore the payload keep working', () => {
    let reloads = 0;
    const refreshEverything: () => void = () => { reloads++; };

    service.dataChanged.subscribe(refreshEverything);

    serverSends(DataKind.Logs, 1);

    expect(reloads).toBe(1);
  });
});
