import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { isObservable, firstValueFrom } from 'rxjs';

import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

describe('authGuard', () => {
  let auth: AuthService;
  let httpMock: HttpTestingController;
  let router: jasmine.SpyObj<Router>;
  let loginTree: UrlTree;

  beforeEach(() => {
    loginTree = {} as UrlTree; // opaque marker -- the guard never inspects it, only returns it
    router = jasmine.createSpyObj('Router', ['parseUrl']);
    router.parseUrl.and.returnValue(loginTree);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
      ],
    });

    auth = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // The guard ignores both parameters -- these are opaque stand-ins so the call site matches CanActivateFn.
  function runGuard() {
    return TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
  }

  async function resolveGuard(): Promise<boolean | UrlTree> {
    const result = runGuard();
    return isObservable(result) ? firstValueFrom(result) : result;
  }

  it('on a fresh load (session not yet checked), asks the server and allows a valid session', async () => {
    const pending = resolveGuard(); // subscribes synchronously -- registers the /api/me request below
    httpMock.expectOne('/api/me').flush({ id: 1, name: 'Nhan', isAdmin: false });

    expect(await pending).toBeTrue();
  });

  it('on a fresh load, asks the server and redirects to /login when there is no valid session', async () => {
    const pending = resolveGuard();
    httpMock.expectOne('/api/me')
      .flush({ message: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(await pending).toBe(loginTree);
  });

  it('once the session is known and authenticated, allows navigation with NO further network call', async () => {
    auth.login('nhan', 'pw').subscribe();
    httpMock.expectOne('/api/auth/login').flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });

    expect(await resolveGuard()).toBeTrue();
    httpMock.verify(); // no /api/me call -- the guard must not re-probe a session it already knows
  });

  it('once the session is known and NOT authenticated, redirects with NO further network call', async () => {
    auth.clearSession();

    expect(await resolveGuard()).toBe(loginTree);
    httpMock.verify();
  });
});
