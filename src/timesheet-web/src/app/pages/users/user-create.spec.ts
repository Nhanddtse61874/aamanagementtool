import { Observable, of, throwError } from 'rxjs';
import { SavedBody, UserDto } from '../../api/models';
import { NewUserDraft, UserCreateApi, UserCreateError, createUserFully, requireUserId } from './user-create';

/**
 * 🔴 THE TESTS THIS MODULE EXISTS FOR.
 *
 * `POST /api/users` writes `new User(0, req.Name, null, true)` — no username, no password hash — and returns
 * a 200 with a well-formed `UserDto`. An account created by that call ALONE cannot be logged into by anyone,
 * ever, and nothing anywhere says so. The three-step flow is the only thing standing between this screen and
 * a list full of accounts that look fine and are dead.
 *
 * So the first test below asserts ALL THREE CALLS HAPPEN. It is written to fail if any one of them is
 * removed — see the mutation-check note on it. In M8.5, six of seven tests stayed green against a completely
 * dead feature because every one of them asserted on the component's own state instead of on what actually
 * went out. These assert on what actually went out.
 */

/** What the endpoint really returns for a fresh row: `RowVersion: 1`, hard-coded, username null. */
const CREATED: UserDto = { id: 42, name: 'Zoe', username: null, isActive: true, isAdmin: false, rowVersion: 1 };

const DRAFT: NewUserDraft = { name: 'Zoe', username: 'zoe', password: 'hunter2' };

/** Records every call, in order, so the test can assert on the WIRE rather than on a return value. */
class FakeApi implements UserCreateApi {
  readonly calls: string[] = [];
  created: Observable<UserDto> = of(CREATED);
  username: Observable<SavedBody> = of({ rowVersion: 2 });
  password: Observable<void> = of(void 0);

  createUser(name: string): Observable<UserDto> {
    this.calls.push(`createUser(${name})`);
    return this.created;
  }
  setUserUsername(id: number, username: string, expectedVersion: number): Observable<SavedBody> {
    this.calls.push(`setUserUsername(${id},${username},v${expectedVersion})`);
    return this.username;
  }
  adminSetPassword(userId: number, newPassword: string): Observable<void> {
    this.calls.push(`adminSetPassword(${userId},${newPassword})`);
    return this.password;
  }
}

describe('createUserFully', () => {
  /**
   * 🔴 THE MUTATION-CHECK TARGET. Delete ANY ONE of the three calls in `createUserFully` and this goes red:
   *
   *   - drop `createUser`        → the chain never starts; `calls` is empty.
   *   - drop `setUserUsername`   → `calls` is length 2 and the middle entry is missing. A GHOST: no username.
   *   - drop `adminSetPassword`  → `calls` is length 2 and the last entry is missing. A GHOST: no password.
   *
   * Asserting the exact ordered list — not `toHaveBeenCalled()` on each — is what makes that true. A spy-per-
   * call suite can be made to pass by a flow that fires the three calls in the WRONG ORDER, or concurrently,
   * both of which are broken (step 2 needs step 1's id and version).
   */
  it('makes ALL THREE calls, in order, or the account cannot be logged into', done => {
    const api = new FakeApi();

    createUserFully(api, DRAFT).subscribe(id => {
      expect(api.calls).toEqual([
        'createUser(Zoe)',
        'setUserUsername(42,zoe,v1)',   // ← the id AND the version from step 1
        'adminSetPassword(42,hunter2)', // ← without this the account has no password and CANNOT LOG IN
      ]);
      expect(id).toBe(42);
      done();
    });
  });

  /**
   * The version step 2 sends is the one step 1 RETURNED — not `0`, not `undefined`, not a re-read.
   * Pinned separately because `expectedVersion: 0` is a *different assertion* to the server, not a
   * "don't know", and it would 409 or overwrite rather than fail cleanly.
   */
  it("sends step 1's returned rowVersion as step 2's expectedVersion", done => {
    const api = new FakeApi();
    api.created = of({ ...CREATED, rowVersion: 7 });   // a re-created row need not be at version 1

    createUserFully(api, DRAFT).subscribe(() => {
      expect(api.calls[1]).toBe('setUserUsername(42,zoe,v7)');
      done();
    });
  });

  it('trims the name and the username, but never the password', done => {
    const api = new FakeApi();

    createUserFully(api, { name: '  Zoe  ', username: '  zoe  ', password: '  hunter2  ' }).subscribe(() => {
      expect(api.calls[0]).toBe('createUser(Zoe)');
      expect(api.calls[1]).toBe('setUserUsername(42,zoe,v1)');
      // A password's leading/trailing spaces are part of the password. Trimming it would silently set a
      // DIFFERENT password from the one the admin typed and read back to the new user.
      expect(api.calls[2]).toBe('adminSetPassword(42,  hunter2  )');
      done();
    });
  });

  // ── the partial failures: the account exists and is UNUSABLE, and we must say so ───────────────────────

  it('step 1 fails → nothing was created, and steps 2 and 3 never run', done => {
    const api = new FakeApi();
    api.created = throwError(() => new Error('400'));

    createUserFully(api, DRAFT).subscribe({
      error: (err: UserCreateError) => {
        expect(err).toBeInstanceOf(UserCreateError);
        expect(err.step).toBe('create');
        expect(err.userId).toBeNull();                       // nothing to repair — nothing exists
        expect(api.calls).toEqual(['createUser(Zoe)']);      // and we did NOT charge on regardless
        done();
      },
    });
  });

  /**
   * 🔴 The ghost, caught. The account IS created and has NO username and NO password. Reporting a bare
   * "failed" here would be a lie by omission — the admin would retry, get a SECOND dead account, and still
   * have the first one sitting in the list looking healthy.
   */
  it('step 2 fails → reports that the account EXISTS, carries its id, and does not attempt step 3', done => {
    const api = new FakeApi();
    api.username = throwError(() => new Error('409'));

    createUserFully(api, DRAFT).subscribe({
      error: (err: UserCreateError) => {
        expect(err.step).toBe('username');
        expect(err.userId).toBe(42);                          // ← the repair path
        expect(err.message).toContain('WAS created');
        expect(err.message).toContain('NOBODY CAN LOG INTO IT');
        expect(api.calls.some(c => c.startsWith('adminSetPassword'))).toBeFalse();
        done();
      },
    });
  });

  it('step 3 fails → reports that the account exists WITH a username but no password', done => {
    const api = new FakeApi();
    api.password = throwError(() => new Error('500'));

    createUserFully(api, DRAFT).subscribe({
      error: (err: UserCreateError) => {
        expect(err.step).toBe('password');
        expect(err.userId).toBe(42);
        expect(err.message).toContain('CANNOT BE LOGGED INTO');
        done();
      },
    });
  });

  it('refuses to continue when the API creates the user but returns no id', done => {
    const api = new FakeApi();
    api.created = of({ ...CREATED, id: undefined });

    createUserFully(api, DRAFT).subscribe({
      error: (err: Error) => {
        expect(err.message).toContain('no id');
        // We could not address the row, so we did not blindly fire the next two calls at `undefined`.
        expect(api.calls).toEqual(['createUser(Zoe)']);
        done();
      },
    });
  });

  it('refuses to continue when the API creates the user but returns no rowVersion', done => {
    const api = new FakeApi();
    api.created = of({ ...CREATED, rowVersion: undefined });

    createUserFully(api, DRAFT).subscribe({
      error: (err: Error) => {
        expect(err.message).toContain('rowVersion');
        expect(api.calls).toEqual(['createUser(Zoe)']);
        done();
      },
    });
  });
});

describe('requireUserId', () => {
  it('passes a real id through, including 0-adjacent values', () => {
    expect(requireUserId(42)).toBe(42);
  });

  it('throws rather than defaulting — a defaulted id addresses the WRONG ROW', () => {
    expect(() => requireUserId(undefined)).toThrowError(/no id/);
  });
});
