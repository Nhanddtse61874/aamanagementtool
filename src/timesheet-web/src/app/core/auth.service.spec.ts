import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AuthService } from './auth.service';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('starts signed out, with the session not yet checked', () => {
    expect(service.isAuthenticated()).toBeFalse();
    expect(service.currentUser()).toBeNull();
    expect(service.sessionChecked()).toBeFalse();
  });

  // =================================================================================================
  // LOGIN. There is no rememberMe on the wire -- see worklog.service.spec.ts for that contract test.
  // This file only covers the REACTIVE STATE login drives, not the transport underneath it.
  // =================================================================================================
  it('login() populates currentUser from the login response and marks the session checked', () => {
    service.login('nhan', 'pw').subscribe();

    httpMock.expectOne('/api/auth/login')
      .flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.currentUser()).toEqual({ id: 1, name: 'Nhan', isAdmin: false });
    expect(service.sessionChecked()).toBeTrue();
  });

  it('login() leaves currentUser null on a failed attempt (wrong password)', () => {
    let err: unknown;
    service.login('nhan', 'wrong').subscribe({ error: e => (err = e) });

    httpMock.expectOne('/api/auth/login')
      .flush({ message: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(err).toBeTruthy(); // the login page needs this to show its own error message
    expect(service.isAuthenticated()).toBeFalse();
    expect(service.currentUser()).toBeNull();
  });

  // =================================================================================================
  // SESSION RESTORE. A hard reload loses all signal state; the cookie does not. checkSession() is the
  // guard's only way to tell "still logged in after a reload" from "never was".
  // =================================================================================================
  it('checkSession() populates currentUser when the cookie is still valid', () => {
    service.checkSession().subscribe();

    httpMock.expectOne('/api/me')
      .flush({ id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 1, memberTeamIds: [1] });

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.currentUser()).toEqual({ id: 1, name: 'Nhan', isAdmin: false });
    expect(service.sessionChecked()).toBeTrue();
  });

  it('checkSession() resolves to null on a 401 rather than throwing -- "not logged in" is a normal answer', () => {
    let result: unknown;
    let threw = false;
    service.checkSession().subscribe({
      next: r => (result = r),
      error: () => (threw = true),
    });

    httpMock.expectOne('/api/me')
      .flush({ message: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(threw).toBeFalse();
    expect(result).toBeNull();
    expect(service.isAuthenticated()).toBeFalse();
    expect(service.sessionChecked()).toBeTrue();
  });

  // =================================================================================================
  // LOGOUT.
  // =================================================================================================
  it('logout() clears currentUser', () => {
    service.login('nhan', 'pw').subscribe();
    httpMock.expectOne('/api/auth/login').flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });
    expect(service.isAuthenticated()).toBeTrue();

    service.logout().subscribe();
    httpMock.expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });

    expect(service.isAuthenticated()).toBeFalse();
    expect(service.currentUser()).toBeNull();
  });

  it('logout() clears local state -- and never errors -- even when the server call itself fails', () => {
    service.login('nhan', 'pw').subscribe();
    httpMock.expectOne('/api/auth/login').flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });

    let threw = false;
    service.logout().subscribe({ error: () => (threw = true) });
    httpMock.expectOne('/api/auth/logout')
      .flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' });

    // The user clicked "log out"; leaving them looking signed-in because of a transient failure is the
    // worse outcome, so logout() swallows the error rather than surfacing it -- see the class comment.
    expect(threw).toBeFalse();
    expect(service.isAuthenticated()).toBeFalse();
  });

  // =================================================================================================
  // clearSession() -- what the 401 interceptor calls on session loss mid-use (see auth.interceptor.ts).
  // =================================================================================================
  it('clearSession() drops currentUser and marks the session checked', () => {
    service.login('nhan', 'pw').subscribe();
    httpMock.expectOne('/api/auth/login').flush({ id: 1, username: 'nhan', name: 'Nhan', isAdmin: false });

    service.clearSession();

    expect(service.isAuthenticated()).toBeFalse();
    expect(service.currentUser()).toBeNull();
    expect(service.sessionChecked()).toBeTrue();
  });
});
