import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';

import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

// ===================================================================================================
// These three branches are the whole reason this interceptor exists. Get any one wrong and the failure
// mode is either a silent redirect loop (exempting nothing on /api/auth/login) or a user stuck on a
// dead, logged-out-looking page forever (exempting too much).
// ===================================================================================================
describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let router: jasmine.SpyObj<Router>;
  let auth: jasmine.SpyObj<Pick<AuthService, 'clearSession'>>;

  beforeEach(() => {
    router = jasmine.createSpyObj('Router', ['navigateByUrl']);
    auth = jasmine.createSpyObj('AuthService', ['clearSession']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
        { provide: AuthService, useValue: auth },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('redirects to /login on a 401 from a normal request, and clears local session state', () => {
    let caught: unknown;
    http.get('/api/timesheet/week').subscribe({ error: e => (caught = e) });

    httpMock.expectOne('/api/timesheet/week')
      .flush({ message: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(router.navigateByUrl).toHaveBeenCalledWith('/login');
    expect(auth.clearSession).toHaveBeenCalled();
    expect(caught).toBeTruthy(); // the error is NOT swallowed -- the caller still sees it
  });

  it('does NOT redirect on a 401 from the login request itself -- that is the login page\'s own error', () => {
    let caught: unknown;
    http.post('/api/auth/login', { username: 'nhan', password: 'wrong' })
      .subscribe({ error: e => (caught = e) });

    httpMock.expectOne('/api/auth/login')
      .flush({ message: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect(auth.clearSession).not.toHaveBeenCalled();
    expect(caught).toBeTruthy(); // the login page still needs this to show "wrong password"
  });

  it('passes a non-401 error through untouched', () => {
    let caught: unknown;
    http.put('/api/timesheet/cell', {}).subscribe({ error: e => (caught = e) });

    httpMock.expectOne('/api/timesheet/cell')
      .flush({ message: 'Conflict' }, { status: 409, statusText: 'Conflict' });

    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect(auth.clearSession).not.toHaveBeenCalled();
    expect(caught).toBeTruthy();
  });
});
