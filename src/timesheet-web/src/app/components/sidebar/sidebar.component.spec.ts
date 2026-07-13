import { provideLocationMocks } from '@angular/common/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { AuthService, AuthUser } from '../../core/auth.service';
import { SidebarComponent } from './sidebar.component';

/**
 * M9/P6a, the OTHER half of the admin gate. `adminGuard` makes `/users` and `/settings` UNREACHABLE; this is
 * what stops the sidebar OFFERING them in the first place.
 *
 * A hidden link is not a guard and a guard is not a hidden link -- the URL is still typeable, and a link the
 * user can click but never follow is a bug report waiting to happen. Both exist, and both are tested.
 */

const ADMIN: AuthUser = { id: 1, name: 'Root', isAdmin: true };
const MEMBER: AuthUser = { id: 2, name: 'Nhan', isAdmin: false };
/** `isAdmin` is `boolean | undefined` -- every generated model field is optional. Absent must NOT be admin. */
const UNDECLARED: AuthUser = { id: 3, name: 'Skew' };

describe('SidebarComponent: the ADMIN section', () => {
  let fixture: ComponentFixture<SidebarComponent>;

  function setUp(user: AuthUser | null): void {
    TestBed.configureTestingModule({
      imports: [SidebarComponent],
      providers: [
        provideRouter([]),
        provideLocationMocks(),
        {
          provide: AuthService,
          useValue: {
            currentUser: signal(user),
            sessionChecked: signal(true),
            logout: () => of(void 0),
          },
        },
      ],
    });

    fixture = TestBed.createComponent(SidebarComponent);
    fixture.detectChanges();
  }

  /** Every nav link actually RENDERED, by its routerLink. Read off the DOM, never off the `admin` array --
   *  the array is still fully populated for an ordinary user; what changed is that it is not rendered. */
  function links(): string[] {
    return fixture.debugElement
      .queryAll(By.css('a.sb__item'))
      .map(a => (a.nativeElement as HTMLAnchorElement).getAttribute('href') ?? '');
  }

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  it('shows Users and Settings to an ADMIN', () => {
    setUp(ADMIN);

    expect(links()).toContain('/users');
    expect(links()).toContain('/settings');
    expect(text()).toContain('ADMIN');
  });

  /**
   * 🔴 THE test. Break the sidebar's gate and this goes red.
   *
   * Asserted on the RENDERED ANCHORS, not on `component.isAdmin()` -- a gate that is correct in the model and
   * absent from the template is precisely the bug this is here to catch, and a test of the signal alone would
   * be perfectly green against a sidebar that still offers both links to everyone.
   */
  it('🔴 renders NEITHER Users NOR Settings for an ordinary member', () => {
    setUp(MEMBER);

    expect(links()).not.toContain('/users');
    expect(links()).not.toContain('/settings');

    // ...and not the bare section heading over nothing, either.
    expect(text()).not.toContain('ADMIN');
  });

  it('🔴 hides them when isAdmin is UNDEFINED -- absent fails closed, same as the guard', () => {
    setUp(UNDECLARED);

    expect(links()).not.toContain('/users');
    expect(links()).not.toContain('/settings');
  });

  /** The ordinary member must keep the whole WORKSPACE section -- hiding admin must not hide the app. */
  it('still shows every workspace link to an ordinary member', () => {
    setUp(MEMBER);

    expect(links()).toEqual(['/log', '/backlog', '/tasklist', '/daily', '/reports']);
  });
});
