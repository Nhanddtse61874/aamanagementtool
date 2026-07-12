import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { LoginComponent } from './login.component';
import { AuthService } from '../../core/auth.service';

describe('LoginComponent', () => {
  let fixture: ComponentFixture<LoginComponent>;
  let component: LoginComponent;
  let auth: jasmine.SpyObj<Pick<AuthService, 'login'>>;
  let router: jasmine.SpyObj<Router>;

  beforeEach(() => {
    auth = jasmine.createSpyObj('AuthService', ['login']);
    router = jasmine.createSpyObj('Router', ['navigateByUrl']);

    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
      ],
    });

    fixture = TestBed.createComponent(LoginComponent);
    component = fixture.componentInstance;
    fixture.detectChanges(); // smoke-check: form + template construct without throwing
  });

  it('does not call the API when the form is invalid (empty fields)', () => {
    component.submit();
    expect(auth.login).not.toHaveBeenCalled();
  });

  it('navigates to /log on a successful login', () => {
    auth.login.and.returnValue(of({ id: 1, name: 'Nhan', isAdmin: false }));
    component.form.setValue({ username: 'nhan', password: 'pw' });

    component.submit();

    expect(auth.login).toHaveBeenCalledWith('nhan', 'pw');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/log');
    expect(component.error()).toBeNull();
  });

  // The one behavior this milestone calls out by name: a wrong password must show an error, not bounce
  // the user back through the 401 interceptor's redirect (which would read as "login is broken").
  it('shows an error and does not navigate on a wrong password (401)', () => {
    auth.login.and.returnValue(throwError(() => new HttpErrorResponse({ status: 401 })));
    component.form.setValue({ username: 'nhan', password: 'wrong' });

    component.submit();

    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect(component.error()).toBe('Incorrect username or password.');
    expect(component.submitting()).toBeFalse(); // re-enabled so the user can retry
  });

  it('shows a distinct message when the API cannot be reached at all', () => {
    auth.login.and.returnValue(throwError(() => new HttpErrorResponse({ status: 0 })));
    component.form.setValue({ username: 'nhan', password: 'pw' });

    component.submit();

    expect(component.error()).toBe('Cannot reach the server. Check your connection and try again.');
  });

  it('ignores a second submit while one is already in flight', () => {
    auth.login.and.returnValue(of({ id: 1, name: 'Nhan', isAdmin: false }));
    component.form.setValue({ username: 'nhan', password: 'pw' });

    component.submit();
    component.submit();

    expect(auth.login).toHaveBeenCalledTimes(1);
  });
});
