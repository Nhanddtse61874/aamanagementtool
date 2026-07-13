import { provideLocationMocks } from '@angular/common/testing';
import { Signal, computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Route, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { Observable, isObservable, firstValueFrom, of } from 'rxjs';

import { routes } from '../app.routes';
import { adminGuard } from './admin.guard';
import { AuthService, AuthUser } from './auth.service';

/**
 * 🔴 THE ONE LIVE SECURITY GAP M9/P6 CLOSED, and the tests that must go RED if it re-opens.
 *
 * Before this guard, `/users` and `/settings` sat behind `authGuard` ALONE -- any signed-in user could reach
 * them -- and `AuthService.currentUser().isAdmin` was read by NOTHING in the whole app. Both screens run
 * entirely on admin-gated routes (`/api/users/all`, `/api/pca-contacts/all`, `/api/teams/all`, the team
 * membership read), so a non-admin who navigated there 403'd on every call at once.
 *
 * The tests come in two halves, and BOTH are needed:
 *
 *   1. THE GUARD'S DECISION -- admin in, non-admin out, anonymous to /login, `undefined` fails CLOSED.
 *   2. 🔴 THE GUARD IS ACTUALLY ATTACHED -- driven through the REAL `routes` table from `app.routes.ts`, by
 *      real `Router.navigateByUrl`. A guard that is perfect and wired to nothing is the M8.5 failure mode
 *      exactly: six of seven tests green against a completely dead feature. Half 1 alone would stay green if
 *      someone deleted `canActivate: [adminGuard]` from both routes.
 */

const ADMIN: AuthUser = { id: 1, name: 'Root', isAdmin: true };
const MEMBER: AuthUser = { id: 2, name: 'Nhan', isAdmin: false };

/** A `MeResponse` that arrived with NO `isAdmin` at all. `isAdmin` is `boolean | undefined` -- every field of
 *  every generated model is optional -- so this shape is reachable, and it must NOT be admin. */
const UNDECLARED: AuthUser = { id: 3, name: 'Skew' };

/**
 * The real `AuthService`'s shape, as far as the two guards read it.
 *
 * 🔴 `isAuthenticated` is here for `authGuard`, NOT for `adminGuard` -- and the fact that it is needed at all
 * is itself worth recording. The route tests below navigate the REAL table, where `/users` is a CHILD of the
 * layout route that carries `authGuard`. Angular resolves guards ROOT-DOWN, so `authGuard` runs FIRST and
 * `adminGuard` only ever sees a caller it has already admitted. Omit this member and every route test dies in
 * `auth.guard.ts`, which is exactly how this was discovered.
 */
interface AuthStub {
  sessionChecked: Signal<boolean>;
  currentUser: Signal<AuthUser | null>;
  isAuthenticated: Signal<boolean>;
  checkSession(): Observable<AuthUser | null>;
}

function authStub(user: AuthUser | null, sessionChecked = true): AuthStub {
  const currentUser = signal(user);
  return {
    sessionChecked: signal(sessionChecked),
    currentUser,
    isAuthenticated: computed(() => currentUser() !== null),
    // The real one also writes the two signals; the guard reads only the returned value on this path.
    checkSession: () => of(user),
  };
}

describe('adminGuard', () => {
  describe('the decision', () => {
    let router: jasmine.SpyObj<Router>;
    let logTree: UrlTree;
    let loginTree: UrlTree;

    /** Opaque markers -- the guard never inspects a UrlTree, it only returns one. Keyed by path so a test can
     *  say WHICH redirect happened, which is the whole difference between "denied" and "signed out". */
    function setUp(user: AuthUser | null, sessionChecked = true): void {
      logTree = { toString: () => '/log' } as UrlTree;
      loginTree = { toString: () => '/login' } as UrlTree;

      router = jasmine.createSpyObj('Router', ['parseUrl']);
      router.parseUrl.and.callFake((url: string) => (url === '/login' ? loginTree : logTree));

      TestBed.configureTestingModule({
        providers: [
          { provide: Router, useValue: router },
          { provide: AuthService, useValue: authStub(user, sessionChecked) },
        ],
      });
    }

    async function resolve(): Promise<boolean | UrlTree> {
      const result = TestBed.runInInjectionContext(() =>
        adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
      return isObservable(result) ? firstValueFrom(result) : (result as boolean | UrlTree);
    }

    it('lets an ADMIN through', async () => {
      setUp(ADMIN);

      expect(await resolve()).toBeTrue();
    });

    /** 🔴 THE test. Break `admin.guard.ts` to let a non-admin through and this is what goes red. */
    it('🔴 REDIRECTS a signed-in NON-ADMIN -- it does not return true', async () => {
      setUp(MEMBER);

      const result = await resolve();

      expect(result).not.toBeTrue();
      expect(result).toBe(logTree);
      expect(router.parseUrl).toHaveBeenCalledWith('/log');
    });

    /**
     * ...and NOT to `/login`. A non-admin has a perfectly good session: bouncing them to a login form they do
     * not need reads as "you have been signed out", and `authGuard` would wave them straight back in -- a loop.
     */
    it('sends the non-admin to /log, NOT to /login -- they are signed in, just not an admin', async () => {
      setUp(MEMBER);

      expect(await resolve()).not.toBe(loginTree);
      expect(router.parseUrl).not.toHaveBeenCalledWith('/login');
    });

    it('sends an ANONYMOUS caller to /login -- it must not offer them /log either', async () => {
      setUp(null);

      expect(await resolve()).toBe(loginTree);
      expect(router.parseUrl).toHaveBeenCalledWith('/login');
    });

    /**
     * 🔴 FAILS CLOSED. `isAdmin?: boolean` -- Swashbuckle emits no `required` for a C# record, so the field
     * can be absent. `isAdmin === true` denies; a cast, a `?? true`, or reading it off a widened type would
     * ADMIT this user.
     */
    it('🔴 DENIES a user whose isAdmin is undefined -- absent is not admin', async () => {
      setUp(UNDECLARED);

      expect(await resolve()).toBe(logTree);
    });

    /** A fresh load has no session yet: the guard must ASK the server rather than assume. */
    it('asks the server on a fresh load, and lets a verified ADMIN through', async () => {
      setUp(ADMIN, /* sessionChecked */ false);

      expect(await resolve()).toBeTrue();
    });

    it('asks the server on a fresh load, and still REDIRECTS a verified non-admin', async () => {
      setUp(MEMBER, /* sessionChecked */ false);

      expect(await resolve()).toBe(logTree);
    });
  });

  /**
   * 🔴 HALF TWO: the guard is ON THE REAL ROUTES.
   *
   * Driven through `provideRouter(routes)` -- the ACTUAL table from `app.routes.ts`, not a copy of it. There
   * is no `RouterOutlet` here, so the lazy components are resolved but never instantiated: what is under test
   * is the guard chain, not the screens.
   */
  describe('🔴 is attached to the REAL route table', () => {
    let router: Router;

    function setUp(user: AuthUser | null): void {
      TestBed.configureTestingModule({
        providers: [
          provideRouter(routes),
          provideLocationMocks(),
          { provide: AuthService, useValue: authStub(user) },
        ],
      });
      router = TestBed.inject(Router);
    }

    it('🔴 a NON-ADMIN navigating to /users does NOT land on /users', async () => {
      setUp(MEMBER);

      await router.navigateByUrl('/users');

      expect(router.url).not.toBe('/users');
      expect(router.url).toBe('/log');
    });

    it('🔴 a NON-ADMIN navigating to /settings does NOT land on /settings', async () => {
      setUp(MEMBER);

      await router.navigateByUrl('/settings');

      expect(router.url).not.toBe('/settings');
      expect(router.url).toBe('/log');
    });

    it('an ADMIN reaches /users and /settings', async () => {
      setUp(ADMIN);

      await router.navigateByUrl('/users');
      expect(router.url).toBe('/users');

      await router.navigateByUrl('/settings');
      expect(router.url).toBe('/settings');
    });

    /** The guard must not have been stapled to the whole layout route by accident -- every other screen is
     *  open to any signed-in user, and gating them would lock ordinary users out of the entire app. */
    it('does NOT gate the ordinary screens -- a non-admin still reaches /log and /backlog', async () => {
      setUp(MEMBER);

      await router.navigateByUrl('/backlog');
      expect(router.url).toBe('/backlog');

      await router.navigateByUrl('/log');
      expect(router.url).toBe('/log');
    });

    /**
     * The structural half, and it is not redundant with the navigations above: it names the guard. A future
     * refactor that swapped `adminGuard` for a lookalike which happened to allow MEMBER would still redirect
     * in the tests above only by luck; this asserts the exact function is the one mounted.
     */
    it('mounts adminGuard by identity on exactly /users and /settings', () => {
      const children = routes.find(r => r.path === '')?.children ?? [];
      const guarded = children
        .filter((r: Route) => (r.canActivate ?? []).includes(adminGuard))
        .map((r: Route) => r.path);

      expect(guarded.sort()).toEqual(['settings', 'users']);
    });
  });
});
