import { Observable, catchError, concatMap, map, throwError } from 'rxjs';
import { SavedBody, UserDto } from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';

/**
 * 🔴 CREATING A USER TAKES THREE CALLS, AND TWO OF THEM ARE EASY TO FORGET.
 *
 * `POST /api/users` is `InsertAsync(new User(0, req.Name, null, true))` — the third argument is
 * `WindowsUsername`, and it is **null**. The row it writes has NO USERNAME and NO PASSWORD HASH
 * (`password_hash` is not even on the `User` record — it lives on `UserCredentials`, deliberately, so a hash
 * can never be one careless projection away from a DTO). Both halves of the login path then reject it:
 *
 *   - `GetCredentialsAsync(username)` looks the account up BY USERNAME. A null username matches nothing.
 *   - `if (string.IsNullOrEmpty(creds.PasswordHash)) return Results.Unauthorized();` (AuthSetup.cs:176) —
 *     with the C# comment "A NULL password_hash means 'has never had a password set' => CANNOT LOG IN. Never
 *     treat it as 'any password matches': that is an authentication bypass."
 *
 * So a screen that calls ONLY `createUser` has produced an account that **nobody can ever log into**, and
 * NOTHING TELLS IT SO: the POST returns 200 with a perfectly well-formed `UserDto`, the row appears in the
 * list, and it looks exactly like a working user. (The old mockup was worse still — `addUser()` was literally
 * `this.toast.show('User added')`; it did not even read the name.)
 *
 * Hence this module. The three calls are sequenced HERE, once, so no caller can do two of them and stop.
 *
 * ── VERSION THREADING ────────────────────────────────────────────────────────────────────────────────────
 * Step 2 is a CHECKED write. Its `expectedVersion` is the version step 1 RETURNED (`UserDto.rowVersion`,
 * which the endpoint hard-codes to `RowVersion: 1` for a fresh row) — never `!`, never `0`, and never a
 * re-read (a re-read is racy; see `SavedBody`'s C# doc). Step 3 is BUMP-ONLY and takes no version at all.
 *
 * ── WHY IT IS NOT TRANSACTIONAL, AND WHAT THAT COSTS ─────────────────────────────────────────────────────
 * Three HTTP calls cannot be atomic. If step 2 or step 3 fails, the account EXISTS and is UNUSABLE — and
 * pretending otherwise is precisely the ghost this module exists to prevent. So a mid-flight failure throws a
 * {@link UserCreateError} that names the step, carries the created id, and says in plain words what state the
 * account is in and how to finish it. The row is already in the list; the row's own actions are the repair
 * path. Failing SILENTLY here would recreate the exact bug we are defending against, one level down.
 */
export interface NewUserDraft {
  readonly name: string;
  readonly username: string;
  readonly password: string;
}

/**
 * The three writes this flow needs, and nothing else — so the tests can drive it with a fake and the
 * component can hand it the real `WorklogService` unchanged. (`WorklogService` satisfies this structurally.)
 *
 * 🔴 The password route is `adminSetPassword` -> `POST /api/auth/users/{id}/set-password`. NOT
 * `POST /api/auth/set-password`, which is the SELF-SERVICE change and demands the caller's CURRENT password —
 * which, for an account that has never had one, does not exist.
 */
export interface UserCreateApi {
  createUser(name: string): Observable<UserDto>;
  setUserUsername(id: number, username: string, expectedVersion: number): Observable<SavedBody>;
  adminSetPassword(userId: number, newPassword: string): Observable<void>;
}

/** Which of the three calls failed. The account's state — and the repair — differ per step. */
export type UserCreateStep = 'create' | 'username' | 'password';

/**
 * What the admin is actually told. Each sentence states (a) whether the account exists and (b) whether anyone
 * can log into it — because those are the two facts that decide what they must do next, and a bare "Failed to
 * create user" answers neither.
 */
const STEP_MESSAGE: Record<UserCreateStep, string> = {
  create:
    'The account was NOT created. Nothing was changed.',
  username:
    'The account WAS created, but setting its username failed — so it has no username and no password, and ' +
    'NOBODY CAN LOG INTO IT. It is in the list below: use Edit to give it a username, then Set password.',
  password:
    'The account WAS created and its username was set, but setting its password failed — and an account with ' +
    'no password CANNOT BE LOGGED INTO. It is in the list below: use Set password to finish it.',
};

/** A failure that names the step, so the caller can say what survived and what must still be done. */
export class UserCreateError extends Error {
  constructor(
    readonly step: UserCreateStep,
    /** The id of the account that WAS created, or `null` when step 1 itself failed and nothing exists. */
    readonly userId: number | null,
    override readonly cause: unknown,
  ) {
    super(STEP_MESSAGE[step]);
    this.name = 'UserCreateError';
  }
}

/**
 * Create a user that can ACTUALLY LOG IN: create -> set username -> set password, in that order.
 *
 * `concatMap`, not `mergeMap`: each step needs the previous step's result (the id, then the version), and
 * running them concurrently would send step 2 a version it does not have yet.
 *
 * Resolves to the new user's id.
 */
export function createUserFully(api: UserCreateApi, draft: NewUserDraft): Observable<number> {
  const name = draft.name.trim();
  const username = draft.username.trim();

  return api.createUser(name).pipe(
    // Catches step 1 ONLY — it sits BEFORE the concatMap, so nothing downstream reaches it.
    catchError((err: unknown) => throwError(() => new UserCreateError('create', null, err))),

    concatMap(created => {
      // Never `created.id!`. Every field of every generated model is optional (Swashbuckle emits no
      // `required` for a C# record), and a 200 whose body is not the body we asked for is not impossible.
      // Losing the id here means we hold a user we can no longer address — the ghost, again.
      const id = requireUserId(created.id);
      const version = requireRowVersion(created.rowVersion);

      return api.setUserUsername(id, username, version).pipe(
        catchError((err: unknown) => throwError(() => new UserCreateError('username', id, err))),

        concatMap(() => api.adminSetPassword(id, draft.password).pipe(
          catchError((err: unknown) => throwError(() => new UserCreateError('password', id, err))),
        )),

        map(() => id),
      );
    }),
  );
}

/**
 * Narrow `UserDto.id?: number` to the `number` the next two calls cannot proceed without.
 *
 * The sibling of `requireRowVersion`, and for the same reason: a silent fallback (`?? 0`) would address
 * `PUT /api/users/0/username` — a 404 at best, and at worst a write aimed at the wrong row. Failing loudly is
 * the whole point.
 */
export function requireUserId(id: number | undefined): number {
  if (typeof id !== 'number') {
    throw new Error(
      'The API created the user but returned no id. Refusing to continue: the username and password calls ' +
      'would have nothing to address, and the account would be left unable to log in.',
    );
  }
  return id;
}
