import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Guards the layout route that wraps every page except `/login` (see `app.routes.ts`). A hard reload
 * loses ALL in-memory signal state, but the session cookie survives it -- so the FIRST navigation of a
 * fresh load must ask the server via `checkSession()`. Every navigation after that reuses the
 * already-known answer (`sessionChecked`); only a brand new app load re-asks.
 */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.sessionChecked()) {
    return auth.isAuthenticated() || router.parseUrl('/login');
  }
  return auth.checkSession().pipe(map(user => (user ? true : router.parseUrl('/login'))));
};
