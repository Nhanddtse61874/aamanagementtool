import { HttpClient, HttpEvent, HttpHandler, HttpRequest } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder } from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';

/** The header the API reads the caller's SignalR connection id from (`ClientContextFilter` ->
 *  `IClientContext.ConnectionId`). One constant, so the send side cannot drift from the C# literal. */
export const CONNECTION_ID_HEADER = 'X-Connection-Id';

/** The hub method the server invokes on us (`SignalRChangeNotifier.ClientMethod`). Sent as
 *  `SendAsync("DataChanged", kind, teamId)`, so the handler receives two arguments -- neither of which we
 *  need: any change to this team's data means "re-read the week", and a re-read is authoritative. */
const DATA_CHANGED = 'DataChanged';

/** Same-origin relative path. The `ng serve` proxy forwards `/hubs` with `ws: true`. An absolute
 *  `http://localhost:5080/...` here would be cross-site, and the `SameSite=Lax` auth cookie would not be
 *  sent -- the hub requires `[Authorize]`, so the connection would simply fail to authenticate. */
const HUB_URL = '/hubs/data';

/**
 * The live cross-user push channel, plus the connection id that keeps our own writes from echoing back.
 *
 * <b>Why the connection id matters more than it looks.</b> The server excludes the acting caller from its own
 * broadcast (`GroupExcept(..., exceptConnectionId)`), but it can only do that if we TELL it who we are, and
 * the only channel for that is the `X-Connection-Id` header on the mutating request. Omit it and we receive
 * our own change, re-fetch the week, and CLOBBER THE CONFLICT DIALOG the 409 just raised -- the user's chance
 * to save their work disappears while they are looking at it.
 *
 * <b>Why `withAutomaticReconnect` is not optional.</b> Hub group membership does not survive a reconnect: a
 * new connection gets a new connection id and starts in ZERO groups. `DataHub.OnConnectedAsync` rejoins the
 * team groups -- but only if the client actually reconnects. Without it, cross-user sync appears to work
 * perfectly right up until the first Wi-Fi blip, and then silently never works again.
 *
 * The reconnect also MINTS A NEW CONNECTION ID, which is why `connectionId` is a signal that is re-read on
 * every state change rather than captured once: a stale id excludes a connection that no longer exists, and
 * we would start receiving our own echoes again.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private hub?: HubConnection;

  private readonly _connectionId = signal<string | null>(null);
  /** `null` when there is no live hub connection -- in which case no header is sent, and the server has no
   *  way to exclude us. That degrades to "we see our own echo and re-fetch", which is wasteful but not
   *  wrong; it is not worth blocking a write on. */
  readonly connectionId = this._connectionId.asReadonly();

  private readonly _dataChanged = new Subject<void>();
  /** Fires when SOMEONE ELSE changed this team's data. The server already excluded us, so there is no echo
   *  of our own writes to filter out here. */
  readonly dataChanged: Observable<void> = this._dataChanged.asObservable();

  /** Idempotent: a second call while already connected (or connecting) is a no-op. */
  start(): void {
    if (this.hub) return;

    const hub = new HubConnectionBuilder()
      .withUrl(HUB_URL)
      .withAutomaticReconnect()
      .build();
    this.hub = hub;

    hub.on(DATA_CHANGED, () => this._dataChanged.next());

    // A reconnect mints a NEW connection id. Re-read it, or we keep sending the dead one and the server
    // starts echoing our own writes back to us.
    hub.onreconnected(id => this._connectionId.set(id ?? null));
    hub.onreconnecting(() => this._connectionId.set(null));
    hub.onclose(() => this._connectionId.set(null));

    hub.start()
      .then(() => this._connectionId.set(hub.connectionId ?? null))
      // A failed hub connection must NOT break the page: the grid still reads and writes over plain HTTP.
      // Live sync is a degradation, not an outage.
      .catch(() => this._connectionId.set(null));
  }

  async stop(): Promise<void> {
    const hub = this.hub;
    this.hub = undefined;
    this._connectionId.set(null);
    if (hub) await hub.stop();
  }
}

/**
 * Adds `X-Connection-Id` to every request that goes through it.
 *
 * It wraps the EXISTING `HttpHandler` -- the head of the app's interceptor chain -- rather than the raw
 * backend, so the 401 interceptor still runs on these requests exactly as it does on every other. This is
 * the one and only reason it is not simply another `HttpInterceptorFn`: registering one of those would mean
 * editing `app.config.ts`, which this wave does not own.
 */
class ConnectionIdHandler implements HttpHandler {
  constructor(
    private readonly next: HttpHandler,
    private readonly connectionId: () => string | null,
  ) {}

  handle(req: HttpRequest<unknown>): Observable<HttpEvent<unknown>> {
    const id = this.connectionId();
    return this.next.handle(
      id ? req.clone({ setHeaders: { [CONNECTION_ID_HEADER]: id } }) : req,
    );
  }
}

/**
 * A real `HttpClient` that stamps the caller's SignalR connection id on everything it sends.
 *
 * MUTATING calls go through this one; reads can go through the plain `HttpClient` (a read changes nothing,
 * so there is no echo to suppress). It IS an `HttpClient`, so it can be handed straight to the generated
 * `api/fn/*` functions -- which take `http` as a parameter precisely so it can be substituted -- with no cast
 * and no edit to a single generated file.
 */
@Injectable({ providedIn: 'root' })
export class ConnectionIdHttpClient extends HttpClient {
  constructor(handler: HttpHandler, realtime: RealtimeService) {
    super(new ConnectionIdHandler(handler, () => realtime.connectionId()));
  }
}
