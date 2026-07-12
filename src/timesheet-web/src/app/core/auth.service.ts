import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { WorklogService } from '../services/worklog.service';
import { MeResponse } from '../api/models';

/**
 * The subset of the wire's user shapes this app actually needs to know who is signed in.
 *
 * `POST /api/auth/login` returns `LoginResponse {id, isAdmin, name, username}` and `GET /api/me` returns
 * `MeResponse {id, isAdmin, name, activeTeamId, memberTeamIds}` -- two different DTOs for two different
 * routes. Both carry these three fields, so login and a session-restore check can share ONE state shape
 * without an extra round-trip after login just to reconcile field names.
 */
export type AuthUser = Pick<MeResponse, 'id' | 'name' | 'isAdmin'>;

/**
 * Client-side session state, layered on top of `WorklogService`'s already-generated auth transport
 * (`login` / `logout` / `me` -- wired in M8.4/W2 against `POST /api/auth/login`, `POST /api/auth/logout`,
 * `GET /api/me`, all same-origin relative paths). This service owns none of the wire calls; it owns the
 * REACTIVE state the route guard, the 401 interceptor, the login page and the sidebar all read from.
 *
 * There is no client-side persistence (no localStorage, no token) and none is needed: the only thing that
 * survives a reload is the `TimesheetApp.Auth` cookie itself (HttpOnly, IsPersistent server-side). A full
 * page reload always starts `currentUser` back at `null` until something calls `checkSession()` -- that
 * "something" is the route guard, on the first navigation of a fresh app load.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly worklog = inject(WorklogService);

  private readonly _currentUser = signal<AuthUser | null>(null);
  /** `null` = not authenticated (or not yet known -- see `sessionChecked`). */
  readonly currentUser = this._currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this._currentUser() !== null);

  private readonly _sessionChecked = signal(false);
  /**
   * True once SOME server round-trip (login, logout, or a session-restore `checkSession()`) has resolved
   * at least once in this app instance. The guard uses this to avoid re-probing `/api/me` on every
   * in-app navigation -- only the FIRST navigation of a fresh load needs to ask the server who it is.
   */
  readonly sessionChecked = this._sessionChecked.asReadonly();

  login(username: string, password: string): Observable<AuthUser> {
    return this.worklog.login(username, password).pipe(
      map((user): AuthUser => ({ id: user.id, name: user.name, isAdmin: user.isAdmin })),
      tap(user => {
        this._currentUser.set(user);
        this._sessionChecked.set(true);
      }),
    );
  }

  /**
   * Always clears local state and always completes -- even if the network call itself fails. The user
   * clicked "log out"; leaving them looking logged-in because a request timed out is the worse failure.
   */
  logout(): Observable<void> {
    return this.worklog.logout().pipe(
      tap(() => this.clearSession()),
      catchError(() => {
        this.clearSession();
        return of(void 0);
      }),
    );
  }

  /**
   * Ask the server who (if anyone) the current cookie belongs to. Used by the route guard exactly once
   * per fresh app load. A 401 here is not an error -- it is the normal, expected answer for "nobody is
   * signed in" -- so it resolves to `null` rather than throwing.
   */
  checkSession(): Observable<AuthUser | null> {
    return this.worklog.me().pipe(
      map((user): AuthUser => ({ id: user.id, name: user.name, isAdmin: user.isAdmin })),
      tap(user => {
        this._currentUser.set(user);
        this._sessionChecked.set(true);
      }),
      catchError(() => {
        this.clearSession();
        return of(null);
      }),
    );
  }

  /** Called by the 401 interceptor when ANY authenticated call comes back unauthorized -- the session was
   *  lost server-side (cookie expired, process restarted without a surviving key ring, etc.) mid-use. */
  clearSession(): void {
    this._currentUser.set(null);
    this._sessionChecked.set(true);
  }
}
