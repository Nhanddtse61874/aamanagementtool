import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { map } from 'rxjs';
import { AuthService, AuthUser } from './auth.service';

/**
 * The CLIENT-SIDE HALF of the M9/P2 admin decision. `ng-openapi-gen.json` has described this file in the
 * present tense since P2; until now there was no such file.
 *
 * <b>What it is actually protecting.</b> ~30 of the generated API methods hit a route carrying
 * `.RequireAuthorization(AuthSetup.AdminPolicy)` -- including `GET /api/users/all` and
 * `GET /api/pca-contacts/all`, which are in the client ON PURPOSE (only `/all` returns DEACTIVATED rows, and
 * the Users screen's "Activate" button has nothing to act on without them). The API 403s a non-admin, and the
 * API is right to. But a 403 inside a `forkJoin` takes THE WHOLE SCREEN down, not just the panel that asked:
 * without this guard a non-admin who reaches `/users` sees every call fail at once, and the screen looks
 * BROKEN rather than hidden. This is what stops them ever getting there.
 *
 * The server remains the real boundary -- this guard is UX, not security, and deleting it would leak nothing.
 * The security property is asserted on the API side, in
 * `SettingsEndpointsTests.The_admin_gated_full_list_is_403_for_a_NON_admin`.
 *
 * <b>Why it does not re-probe the session in practice.</b> Angular resolves guards ROOT-DOWN, and `/users` /
 * `/settings` are children of the layout route that already carries `authGuard` -- so by the time this runs,
 * `checkSession()` has resolved and `sessionChecked()` is true. The observable branch below is defence in
 * depth for a future route that mounts this guard WITHOUT `authGuard` above it; it is not dead code, it is
 * the branch that keeps this guard correct on its own.
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.sessionChecked()) {
    return decide(auth.currentUser(), router);
  }
  return auth.checkSession().pipe(map(user => decide(user, router)));
};

/**
 * 🔴 `isAdmin === true`, NOT `isAdmin` -- AND THAT IS NOT PEDANTRY.
 *
 * `AuthUser` is `Pick<MeResponse, 'id' | 'name' | 'isAdmin'>`, and every field of every generated model is
 * OPTIONAL (`isAdmin?: boolean`) because Swashbuckle emits no `required` for a C# record. So `isAdmin` is
 * `boolean | undefined`, and a `MeResponse` that arrived without the field -- a version skew, a proxy's
 * rewritten body -- would make a truthiness check evaluate `undefined` and correctly deny. Fine. But the
 * INVERSE spelling (`!user.isAdmin` guarding the deny branch) reads the same and is also fine, while
 * `user.isAdmin ?? true` or a cast would not be. The explicit `=== true` states the intent that cannot be
 * misread: ONLY a literal `true` is admin. Everything else -- false, undefined, absent -- FAILS CLOSED.
 */
function decide(user: AuthUser | null, router: Router): boolean | UrlTree {
  // Not signed in at all. Send them to log in, not to the app's landing page -- they cannot see that either.
  if (user === null) return router.parseUrl('/login');

  // Signed in, but not an admin. `/log` is the app's default landing route (`'' -> redirectTo: 'log'`).
  // Emphatically NOT `/login`: they have a perfectly good session, and bouncing them to a login form they do
  // not need would read as "you have been signed out" and send them round a loop.
  return user.isAdmin === true || router.parseUrl('/log');
}
