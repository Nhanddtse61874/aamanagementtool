import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * `POST /api/auth/login` is the one request this interceptor must NEVER redirect on. A wrong password is
 * a normal 401 from that route -- it is the login page's OWN error to show. Redirecting away from
 * `/login` because the login attempt itself 401'd is an infinite loop: land on /login -> submit -> 401 ->
 * bounced back to /login -> ...
 */
const LOGIN_PATH = '/api/auth/login';

/**
 * Global session-loss handler. `ng serve`'s proxy makes every API call same-origin (see the class comment
 * on `WorklogService`), so a 401 here is a REAL "you are not signed in" from the API's cookie auth (M8.3
 * overrode ASP.NET's default 302-to-a-route-that-does-not-exist), never a network/CORS artifact.
 *
 * Every other error (400 validation, 404, 409 conflict, 5xx, network failure) passes through untouched --
 * those are the calling feature's to handle. In particular this must NOT swallow anything: the caller
 * still needs the original error to show its own message (e.g. the login page's "wrong password").
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && req.url !== LOGIN_PATH) {
        auth.clearSession();
        router.navigateByUrl('/login');
      }
      return throwError(() => error);
    }),
  );
};
