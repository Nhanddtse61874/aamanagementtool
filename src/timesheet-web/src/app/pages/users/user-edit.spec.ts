import { Observable, of, throwError } from 'rxjs';
import { SavedBody, UserDto } from '../../api/models';
import { UserEdit, UserEditApi, applyUserEdits, planUserEdits } from './user-edit';

const ORIGINAL: UserDto = {
  id: 42, name: 'Zoe', username: 'zoe', isActive: true, isAdmin: false, rowVersion: 4,
};

/**
 * Records each write with the version it carried — the only way to see the chaining bug.
 *
 * 🔴 The fake returns `expectedVersion + 1`, because that is what a CHECKED WRITE ACTUALLY DOES: it bumps
 * the row it just wrote. A fake that returned versions from its own counter, independent of what it was
 * sent, would be a fake in which the chaining bug CANNOT BE OBSERVED — every write would "return" a fresh
 * version whether or not the caller had threaded the previous one through. Modelling the server's actual
 * behaviour is what gives these tests their teeth.
 */
class FakeApi implements UserEditApi {
  readonly calls: string[] = [];
  fail: string | null = null;

  private saved(label: string, expectedVersion: number): Observable<SavedBody> {
    this.calls.push(label);
    if (this.fail !== null && label.startsWith(this.fail)) return throwError(() => new Error('409'));
    return of({ rowVersion: expectedVersion + 1 });   // a checked write bumps the row it wrote
  }

  renameUser(id: number, name: string, expectedVersion: number): Observable<SavedBody> {
    return this.saved(`rename(${id},${name},v${expectedVersion})`, expectedVersion);
  }
  setUserUsername(id: number, username: string, expectedVersion: number): Observable<SavedBody> {
    return this.saved(`username(${id},${username},v${expectedVersion})`, expectedVersion);
  }
  setUserAdmin(id: number, isAdmin: boolean, expectedVersion: number): Observable<SavedBody> {
    return this.saved(`admin(${id},${isAdmin},v${expectedVersion})`, expectedVersion);
  }
}

describe('planUserEdits', () => {
  it('emits NOTHING for an untouched form — an unchanged name is not a free write', () => {
    // Re-sending an unchanged value bumps row_version and invalidates every other client's held version,
    // 409-ing edits that had nothing to do with this one.
    expect(planUserEdits(ORIGINAL, { name: 'Zoe', username: 'zoe', isAdmin: false })).toEqual([]);
  });

  it('emits only the fields that changed', () => {
    expect(planUserEdits(ORIGINAL, { name: 'Zoe Q', username: 'zoe', isAdmin: false }))
      .toEqual([{ kind: 'rename', name: 'Zoe Q' }]);

    expect(planUserEdits(ORIGINAL, { name: 'Zoe', username: 'zoe', isAdmin: true }))
      .toEqual([{ kind: 'admin', isAdmin: true }]);
  });

  it('trims before comparing — whitespace is not an edit', () => {
    expect(planUserEdits(ORIGINAL, { name: '  Zoe  ', username: ' zoe ', isAdmin: false })).toEqual([]);
  });

  /**
   * 🔴 `isAdmin === true`, not `isAdmin`. The generated field is `boolean | undefined`; a DTO that arrived
   * without it would make `false !== undefined` true and emit a spurious admin write on EVERY save.
   */
  it('treats an ABSENT isAdmin as false, not as "changed"', () => {
    const noFlag: UserDto = { ...ORIGINAL, isAdmin: undefined };

    expect(planUserEdits(noFlag, { name: 'Zoe', username: 'zoe', isAdmin: false })).toEqual([]);
    expect(planUserEdits(noFlag, { name: 'Zoe', username: 'zoe', isAdmin: true }))
      .toEqual([{ kind: 'admin', isAdmin: true }]);
  });

  it('treats an absent username as empty, so setting one for the first time IS an edit', () => {
    // Exactly the state a half-created user is left in — see user-create.ts. This is the repair path.
    const ghost: UserDto = { ...ORIGINAL, username: null };

    expect(planUserEdits(ghost, { name: 'Zoe', username: 'zoe', isAdmin: false }))
      .toEqual([{ kind: 'username', username: 'zoe' }]);
  });
});

describe('applyUserEdits', () => {
  /**
   * 🔴 THE MUTATION-CHECK TARGET, AND THE BUG THE OBVIOUS SPELLING SHIPS.
   *
   * All three are CHECKED writes against the SAME row, and each one BUMPS row_version. Hand all three the
   * version the row was LOADED with (4) and writes 2 and 3 both 409 — the rename lands, the admin grant
   * silently does not.
   *
   * This test fails the moment the chaining is broken: replace `concatMap(version => ...)` with a closure
   * over `startVersion` and the expected `v4,v5,v6` becomes `v4,v4,v4`.
   *
   * A suite that only ever changes ONE field at a time cannot see this. That is why this test changes three.
   */
  it('CHAINS the version — each write sends what the previous write returned', done => {
    const api = new FakeApi();
    const edits: UserEdit[] = [
      { kind: 'rename', name: 'Zoe Q' },
      { kind: 'username', username: 'zq' },
      { kind: 'admin', isAdmin: true },
    ];

    applyUserEdits(api, 42, edits, 4).subscribe(finalVersion => {
      expect(api.calls).toEqual([
        'rename(42,Zoe Q,v4)',   // the loaded version
        'username(42,zq,v5)',    // ← what the rename RETURNED. Not v4.
        'admin(42,true,v6)',     // ← what the username write RETURNED. Not v4.
      ]);
      // And the caller gets the row's final version back, so it can keep editing without a racy re-read.
      expect(finalVersion).toBe(7);
      done();
    });
  });

  it('runs the writes SEQUENTIALLY, never concurrently', done => {
    // concatMap, not mergeMap: write 2 cannot even be built until write 1 has returned its version.
    const api = new FakeApi();
    const edits: UserEdit[] = [
      { kind: 'rename', name: 'A' },
      { kind: 'username', username: 'b' },
    ];

    applyUserEdits(api, 42, edits, 1).subscribe(() => {
      expect(api.calls).toEqual(['rename(42,A,v1)', 'username(42,b,v2)']);
      done();
    });
  });

  it('an empty plan makes NO call and resolves to the version it started with', done => {
    const api = new FakeApi();

    applyUserEdits(api, 42, [], 4).subscribe(v => {
      expect(api.calls).toEqual([]);
      expect(v).toBe(4);
      done();
    });
  });

  it('stops at the first failure — a later write must not run against a version that never landed', done => {
    const api = new FakeApi();
    api.fail = 'username';

    applyUserEdits(api, 42, [
      { kind: 'rename', name: 'Zoe Q' },
      { kind: 'username', username: 'zq' },
      { kind: 'admin', isAdmin: true },
    ], 4).subscribe({
      error: () => {
        // The rename landed and the username 409'd. The admin write is NOT attempted: it would be carrying a
        // version the failed write never produced.
        expect(api.calls).toEqual(['rename(42,Zoe Q,v4)', 'username(42,zq,v5)']);
        done();
      },
    });
  });

  it('refuses to continue when a checked write returns no rowVersion', done => {
    const api: UserEditApi = {
      renameUser: () => of({ rowVersion: undefined }),      // a 200 that is not the body we asked for
      setUserUsername: () => of({ rowVersion: 9 }),
      setUserAdmin: () => of({ rowVersion: 9 }),
    };

    applyUserEdits(api, 42, [
      { kind: 'rename', name: 'Zoe Q' },
      { kind: 'admin', isAdmin: true },
    ], 4).subscribe({
      error: (err: Error) => {
        // Better to lose the request than to feed a wrong expectedVersion into the NEXT link of this chain,
        // where it would either 409 spuriously or silently overwrite another user.
        expect(err.message).toContain('rowVersion');
        done();
      },
    });
  });
});
