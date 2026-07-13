import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';

import { SavedBody, UserDto } from '../../api/models';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { UsersComponent } from './users.component';

/**
 * The Users screen. Until M9/P2 this was a mockup whose "Add user" button was LITERALLY
 * `this.toast.show('User added')` — it did not read the name, it made no call, and it told the admin the user
 * had been added. So these tests are mostly about the things that were previously FAKE.
 *
 * 🔴 EVERY ASSERTION HERE IS ON THE WIRE OR ON THE DOM, never on the component's own signals. In M8.5 six of
 * seven tests stayed green against a completely dead feature precisely because they all asserted on component
 * state. `expect(component.users().length).toBe(3)` proves nothing about whether a user can log in.
 */

/** Rob has no username — the exact ghost a one-step create leaves behind. Dana is DEACTIVATED. */
const ROWS: UserDto[] = [
  { id: 1, name: 'Nhan', username: 'nhan', isActive: true, isAdmin: true, rowVersion: 4 },
  { id: 2, name: 'Dana', username: 'dana', isActive: false, isAdmin: false, rowVersion: 2 },
  { id: 3, name: 'Rob', username: null, isActive: true, isAdmin: false, rowVersion: 1 },
];

const CREATED: UserDto = { id: 9, name: 'Zoe', username: null, isActive: true, isAdmin: false, rowVersion: 1 };

describe('UsersComponent', () => {
  let api: jasmine.SpyObj<WorklogService>;
  let fixture: ComponentFixture<UsersComponent>;

  /** Every row's action buttons, by their visible label. */
  function buttons(label: string): HTMLButtonElement[] {
    return fixture.debugElement.queryAll(By.css('button'))
      .map(d => d.nativeElement as HTMLButtonElement)
      .filter(b => (b.textContent ?? '').trim().startsWith(label));
  }

  function type(selector: string, value: string): void {
    const input = fixture.debugElement.query(By.css(selector)).nativeElement as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  beforeEach(() => {
    api = jasmine.createSpyObj<WorklogService>('WorklogService', [
      'getUsersAll', 'createUser', 'setUserUsername', 'adminSetPassword',
      'setUserActive', 'setUserAdmin', 'renameUser', 'avatarColor',
    ]);
    api.getUsersAll.and.returnValue(of(ROWS));
    api.createUser.and.returnValue(of(CREATED));
    api.setUserUsername.and.returnValue(of({ rowVersion: 2 } satisfies SavedBody));
    api.adminSetPassword.and.returnValue(of(void 0));
    api.setUserActive.and.returnValue(of(void 0));
    api.setUserAdmin.and.returnValue(of({ rowVersion: 5 } satisfies SavedBody));
    api.renameUser.and.returnValue(of({ rowVersion: 5 } satisfies SavedBody));
    api.avatarColor.and.returnValue('#0E7C66');

    TestBed.configureTestingModule({
      imports: [UsersComponent],
      providers: [{ provide: WorklogService, useValue: api }, ToastService],
    });

    fixture = TestBed.createComponent(UsersComponent);
    fixture.detectChanges();
  });

  // ── the read ────────────────────────────────────────────────────────────────────────────────────────

  /**
   * `getUsersAll()`, NOT `getUsersActive()`. `GET /api/users` is `GetActiveAsync` and can never return a
   * deactivated user — so "Activate" would have nothing to act on. Dana appearing in the DOM is the proof.
   */
  it('lists DEACTIVATED users too — otherwise Activate has nothing to act on', () => {
    expect(api.getUsersAll).toHaveBeenCalled();
    expect(api.getUsersActive).toBeUndefined();   // not even wired — it cannot show Dana

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Dana');
    expect(buttons('Activate').length).toBe(1);   // exactly Dana's row
  });

  /** The ghost, surfaced. Rob has no username, so nobody can log in as Rob, and the screen must say so. */
  it('marks an account with no username as unable to log in', () => {
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('No login');
  });

  it('shows a retry, not an empty table, when the read fails', () => {
    api.getUsersAll.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));

    const f = TestBed.createComponent(UsersComponent);
    f.detectChanges();

    const text = (f.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Could not load users');
    expect(f.debugElement.queryAll(By.css('.urow')).length).toBe(0);
  });

  // ── the three-step create ───────────────────────────────────────────────────────────────────────────

  /**
   * 🔴 THE TEST THIS SCREEN EXISTS FOR, DRIVEN THROUGH THE REAL BUTTON.
   *
   * `POST /api/users` writes `new User(0, name, null, true)` — no username, no password hash — and returns a
   * cheerful 200. An account created by that call alone CANNOT BE LOGGED INTO BY ANYONE, and nothing says so.
   *
   * MUTATION-CHECK: delete any one of the three calls in `createUserFully` and this goes red. It asserts on
   * all three spies AND on the arguments that thread between them (the id from step 1, the version from
   * step 1), so a flow that fires them in the wrong order or concurrently cannot pass it either.
   */
  it('ADD USER makes all three calls — create, set username, set password', () => {
    buttons('+ Add user')[0].click();
    fixture.detectChanges();

    type('input[placeholder="Zoe Nguyen"]', 'Zoe');
    type('input[placeholder="zoe"]', 'zoe');
    type('input[type="password"]', 'hunter2');

    buttons('Create user')[0].click();
    fixture.detectChanges();

    expect(api.createUser).toHaveBeenCalledOnceWith('Zoe');
    // ← the id AND the rowVersion that step 1 returned. Not `undefined`, not `0`.
    expect(api.setUserUsername).toHaveBeenCalledOnceWith(9, 'zoe', 1);
    // ← WITHOUT THIS CALL THE ACCOUNT HAS NO PASSWORD AND CANNOT BE LOGGED INTO.
    expect(api.adminSetPassword).toHaveBeenCalledOnceWith(9, 'hunter2');
  });

  it('refuses to create until all three fields are filled — there is no valid half-account', () => {
    buttons('+ Add user')[0].click();
    fixture.detectChanges();

    type('input[placeholder="Zoe Nguyen"]', 'Zoe');
    type('input[placeholder="zoe"]', 'zoe');
    // password deliberately left empty

    expect(buttons('Create user')[0].disabled).toBeTrue();
    expect(api.createUser).not.toHaveBeenCalled();
  });

  /**
   * 🔴 The partial failure, reported PERSISTENTLY. The account exists and cannot be logged into; a toast that
   * fades after two seconds is exactly how it would stay in the list looking healthy forever.
   */
  it('a failed password step leaves a PERSISTENT banner saying the account cannot be logged into', () => {
    api.adminSetPassword.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));

    buttons('+ Add user')[0].click();
    fixture.detectChanges();
    type('input[placeholder="Zoe Nguyen"]', 'Zoe');
    type('input[placeholder="zoe"]', 'zoe');
    type('input[type="password"]', 'hunter2');
    buttons('Create user')[0].click();
    fixture.detectChanges();

    const notice = fixture.debugElement.query(By.css('.notice'));
    expect(notice).not.toBeNull();
    expect((notice.nativeElement as HTMLElement).textContent).toContain('CANNOT BE LOGGED INTO');
  });

  it('does not create twice when Create is double-clicked', () => {
    // The re-entrancy guard. Without it the three-step flow runs twice and makes two accounts, the second
    // racing the first's version. The Subject holds step 1 open so the second click lands mid-flight.
    const inFlight = new Subject<UserDto>();
    api.createUser.and.returnValue(inFlight);

    buttons('+ Add user')[0].click();
    fixture.detectChanges();
    type('input[placeholder="Zoe Nguyen"]', 'Zoe');
    type('input[placeholder="zoe"]', 'zoe');
    type('input[type="password"]', 'hunter2');

    const create = buttons('Create user')[0];
    create.click();
    fixture.detectChanges();
    create.click();          // the impatient second click, while the first is still in flight
    fixture.detectChanges();

    expect(api.createUser).toHaveBeenCalledTimes(1);

    inFlight.next(CREATED);
    inFlight.complete();
  });

  // ── the checked writes ──────────────────────────────────────────────────────────────────────────────

  /**
   * 🔴 Rename and set-admin are two CHECKED writes on ONE row. Handing both the version the row was LOADED
   * with lands the rename and 409s the admin grant, silently. The second write must carry what the FIRST
   * write returned.
   */
  it('chains the rowVersion when a save changes more than one checked field', () => {
    buttons('Edit')[0].click();          // Nhan, rowVersion 4, isAdmin true
    fixture.detectChanges();

    const inputs = fixture.debugElement.queryAll(By.css('.uedit input'));
    const name = inputs[0].nativeElement as HTMLInputElement;
    name.value = 'Nhan Q';
    name.dispatchEvent(new Event('input'));

    // The Administrator box binds [checked], not [ngModel], so it is already reflecting `true` here — a
    // microtask-deferred NgModel write would still show it UNCHECKED, and this click would then be a silent
    // no-op that dropped the change entirely.
    const admin = inputs[2].nativeElement as HTMLInputElement;
    expect(admin.checked).withContext('Nhan is an admin, so the box starts ticked').toBeTrue();

    admin.click();                                               // true -> false
    fixture.detectChanges();

    buttons('Save')[0].click();
    fixture.detectChanges();

    expect(api.renameUser).toHaveBeenCalledOnceWith(1, 'Nhan Q', 4);   // the loaded version
    expect(api.setUserAdmin).toHaveBeenCalledOnceWith(1, false, 5);    // ← what the rename RETURNED. Not 4.
  });

  it('makes NO write when the edit form is saved unchanged', () => {
    buttons('Edit')[0].click();
    fixture.detectChanges();
    buttons('Save')[0].click();
    fixture.detectChanges();

    expect(api.renameUser).not.toHaveBeenCalled();
    expect(api.setUserAdmin).not.toHaveBeenCalled();
    expect(api.setUserUsername).not.toHaveBeenCalled();
  });

  it('on a 409, re-reads the list rather than leaving a stale version on screen', () => {
    api.renameUser.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    api.getUsersAll.calls.reset();

    buttons('Edit')[0].click();
    fixture.detectChanges();
    const name = fixture.debugElement.query(By.css('.uedit input')).nativeElement as HTMLInputElement;
    name.value = 'Nhan Q';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    buttons('Save')[0].click();
    fixture.detectChanges();

    expect(TestBed.inject(ToastService).message()).toContain('Someone else changed this user');
    expect(api.getUsersAll).toHaveBeenCalledTimes(1);   // the re-read
  });

  // ── the bump-only writes ────────────────────────────────────────────────────────────────────────────

  it('Deactivate is SOFT and passes the flag through — it does not delete', () => {
    buttons('Deactivate')[0].click();   // Nhan (active)
    fixture.detectChanges();

    expect(api.setUserActive).toHaveBeenCalledOnceWith(1, false);
  });

  it('Activate restores through the SAME route, with the flag inverted', () => {
    buttons('Activate')[0].click();     // Dana (inactive)
    fixture.detectChanges();

    expect(api.setUserActive).toHaveBeenCalledOnceWith(2, true);
  });

  it('Password sets a new password without asking for the old one — the repair path for a ghost', () => {
    buttons('Password')[2].click();     // Rob, the account with no username
    fixture.detectChanges();

    type('.uedit input[type="password"]', 's3cret');
    buttons('Set password')[0].click();
    fixture.detectChanges();

    expect(api.adminSetPassword).toHaveBeenCalledOnceWith(3, 's3cret');
  });
});
