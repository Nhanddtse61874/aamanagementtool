import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';

import { ChangePasswordComponent } from './change-password.component';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';

/**
 * Go-live blocker fix. `POST /api/auth/set-password` has had zero callers anywhere in the SPA -- an
 * admin-created account, and the seeded `admin`/`admin` bootstrap login, had no in-app way to change a
 * password someone else set. This is the screen that closes that gap.
 *
 * Three behaviors this milestone calls out by name:
 *   1. a confirm mismatch is caught CLIENT-side and never reaches the API
 *   2. a 400 shows the SERVER's own sentence verbatim (AuthEndpoints.cs owns that copy, not this component)
 *   3. success clears the form and reports through ToastService, the app's existing idiom
 */
describe('ChangePasswordComponent', () => {
  let fixture: ComponentFixture<ChangePasswordComponent>;
  let component: ChangePasswordComponent;
  let api: jasmine.SpyObj<Pick<WorklogService, 'setPassword'>>;
  let toast: jasmine.SpyObj<ToastService>;

  beforeEach(() => {
    api = jasmine.createSpyObj('WorklogService', ['setPassword']);
    toast = jasmine.createSpyObj('ToastService', ['show']);

    TestBed.configureTestingModule({
      imports: [ChangePasswordComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    });

    fixture = TestBed.createComponent(ChangePasswordComponent);
    component = fixture.componentInstance;
    fixture.detectChanges(); // smoke-check: form + template construct without throwing
  });

  it('does not call the API when the form is invalid (empty fields)', () => {
    component.submit();
    expect(api.setPassword).not.toHaveBeenCalled();
  });

  // THE test. A mismatch must be caught before any request goes out -- never sent to the server to reject.
  it('🔴 blocks the request when the confirmation does not match the new password, before any call is made', () => {
    component.form.setValue({ currentPassword: 'old', newPassword: 'newpw', confirmPassword: 'typo' });

    component.submit();

    expect(api.setPassword).not.toHaveBeenCalled();
    expect(component.error()).toBe('New password and confirmation do not match.');
  });

  it('calls setPassword with the current and new password on a matching confirmation', () => {
    api.setPassword.and.returnValue(of(void 0));
    component.form.setValue({ currentPassword: 'old', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();

    expect(api.setPassword).toHaveBeenCalledWith('old', 'newpw');
  });

  it('clears the form and reports success through the toast on success', () => {
    api.setPassword.and.returnValue(of(void 0));
    component.form.setValue({ currentPassword: 'old', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();

    expect(component.form.getRawValue()).toEqual({ currentPassword: '', newPassword: '', confirmPassword: '' });
    expect(toast.show).toHaveBeenCalledWith('Password changed.');
    expect(component.submitting()).toBeFalse();
    expect(component.error()).toBeNull();
  });

  // 🔴 The server's own sentence, VERBATIM (AuthEndpoints.cs:99) -- not a paraphrase invented here.
  it('shows the server sentence verbatim on a 400 for a wrong current password', () => {
    api.setPassword.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400,
      error: { error: 'Current password is incorrect.' },
    })));
    component.form.setValue({ currentPassword: 'wrong', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();

    expect(component.error()).toBe('Current password is incorrect.');
    expect(toast.show).not.toHaveBeenCalled();
    expect(component.submitting()).toBeFalse();
  });

  // 🔴 The OTHER 400 sentence AuthEndpoints.cs owns -- a freshly migrated account with no password yet.
  it('shows the server sentence verbatim on a 400 for an account with no password set', () => {
    api.setPassword.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400,
      error: { error: 'No password is set for this account. Ask an administrator to set one.' },
    })));
    component.form.setValue({ currentPassword: 'anything', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();

    expect(component.error()).toBe('No password is set for this account. Ask an administrator to set one.');
  });

  it('shows a distinct message when the API cannot be reached at all', () => {
    api.setPassword.and.returnValue(throwError(() => new HttpErrorResponse({ status: 0 })));
    component.form.setValue({ currentPassword: 'old', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();

    expect(component.error()).toBe('Cannot reach the server. Check your connection and try again.');
  });

  // A never-resolving Subject, not `of(...)`: `of` completes SYNCHRONOUSLY, which would clear `submitting`
  // before the second `submit()` call and defeat the point of this test -- the call must still be genuinely
  // IN FLIGHT when the second submit is attempted.
  it('ignores a second submit while one is already in flight', () => {
    api.setPassword.and.returnValue(new Subject<void>());
    component.form.setValue({ currentPassword: 'old', newPassword: 'newpw', confirmPassword: 'newpw' });

    component.submit();
    component.submit();

    expect(api.setPassword).toHaveBeenCalledTimes(1);
  });
});
