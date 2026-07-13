import { Observable, concatMap, map, of } from 'rxjs';
import { SavedBody, UserDto } from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';

/**
 * 🔴 THREE CHECKED WRITES, ONE ROW, ONE VERSION COLUMN — THEY CANNOT SHARE AN `expectedVersion`.
 *
 * Rename, set-username and set-admin are all CHECKED writes against the SAME `Users` row, and every checked
 * write BUMPS `row_version`. So firing all three with the version the row was LOADED with means:
 *
 *      write 1 → passes, row_version 4 → 5
 *      write 2 → sends expectedVersion 4, row is now 5 → 409
 *      write 3 → sends expectedVersion 4, row is now 5 → 409
 *
 * ...and the user sees a rename land while "and make them an admin" silently 409s next to it. Worse, the
 * spelling that produces this bug is the OBVIOUS one — three independent calls, each handed `user.rowVersion`
 * — and it passes a test suite that only ever changes ONE field at a time.
 *
 * So the writes are CHAINED: each one's `SavedBody.rowVersion` is the next one's `expectedVersion`. That is
 * also why the version must come from what the WRITE RETURNED and never from a re-read — between the write
 * committing and the re-read another client can write, and you would then hold THEIR version with YOUR data
 * and silently overwrite them on the next save. (`SavedBody`'s own C# doc: "GetRowVersionAsync was deleted
 * for this reason.")
 *
 * ── WHAT IS *NOT* IN HERE, AND WHY ───────────────────────────────────────────────────────────────────────
 * `setUserActive` and `adminSetPassword` are BUMP-ONLY: neither route declares an `expectedVersion` (their
 * request records carry no such field), so neither can 409 and neither belongs in this chain. They are fired
 * on their own by the component. Putting them here would mean inventing a version to send, which is exactly
 * the dead code the service's comments warn against.
 */

/** The editable half of a user row. `isAdmin` is a plain bool — the DTO's `isAdmin?: boolean` is not. */
export interface UserEditDraft {
  readonly name: string;
  readonly username: string;
  readonly isAdmin: boolean;
}

/** One checked write. A discriminated union so `writeOne` cannot forget a case and TypeScript says so. */
export type UserEdit =
  | { readonly kind: 'rename'; readonly name: string }
  | { readonly kind: 'username'; readonly username: string }
  | { readonly kind: 'admin'; readonly isAdmin: boolean };

/** The three checked writes, as `WorklogService` already exposes them. */
export interface UserEditApi {
  renameUser(id: number, name: string, expectedVersion: number): Observable<SavedBody>;
  setUserUsername(id: number, username: string, expectedVersion: number): Observable<SavedBody>;
  setUserAdmin(id: number, isAdmin: boolean, expectedVersion: number): Observable<SavedBody>;
}

/**
 * The writes an edit turns into — ONLY for the fields that actually changed.
 *
 * An untouched form must emit ZERO writes (the same rule `planTaskWrites` holds itself to): re-sending an
 * unchanged name is not harmless, it bumps `row_version` and invalidates every OTHER client's held version
 * for no reason, 409-ing edits that had nothing to do with this one.
 *
 * 🔴 `original.isAdmin === true`, not `original.isAdmin`. The generated field is `boolean | undefined`, and
 * comparing a `boolean` draft against an `undefined` original with `!==` would report a change that is not
 * one — emitting a spurious admin write on every save of a user whose DTO happened to omit the field.
 */
export function planUserEdits(original: UserDto, draft: UserEditDraft): UserEdit[] {
  const edits: UserEdit[] = [];

  const name = draft.name.trim();
  const username = draft.username.trim();

  if (name !== (original.name ?? '')) edits.push({ kind: 'rename', name });
  if (username !== (original.username ?? '')) edits.push({ kind: 'username', username });
  if (draft.isAdmin !== (original.isAdmin === true)) edits.push({ kind: 'admin', isAdmin: draft.isAdmin });

  return edits;
}

/**
 * Run the planned writes IN ORDER, threading the version each one returns into the next.
 *
 * `reduce` over `concatMap` is what makes the threading structural rather than remembered: there is no
 * variable holding "the current version" that a later edit could forget to update — the version IS the value
 * flowing down the chain, and a write cannot run without receiving it.
 *
 * Resolves to the row's FINAL version, so the caller can keep editing without a re-read. An empty plan
 * resolves immediately to the version it started with, having made no call at all.
 */
export function applyUserEdits(
  api: UserEditApi,
  id: number,
  edits: readonly UserEdit[],
  startVersion: number,
): Observable<number> {
  return edits.reduce<Observable<number>>(
    (chain$, edit) => chain$.pipe(concatMap(version => writeOne(api, id, edit, version))),
    of(startVersion),
  );
}

/** One write, returning the version it produced — which is the next write's `expectedVersion`. */
function writeOne(api: UserEditApi, id: number, edit: UserEdit, expectedVersion: number): Observable<number> {
  const saved$: Observable<SavedBody> =
    edit.kind === 'rename'
      ? api.renameUser(id, edit.name, expectedVersion)
      : edit.kind === 'username'
        ? api.setUserUsername(id, edit.username, expectedVersion)
        : api.setUserAdmin(id, edit.isAdmin, expectedVersion);

  // Never `saved.rowVersion!`, never `?? 0`. A wrong expectedVersion on the NEXT link of this very chain
  // either 409s spuriously or silently overwrites another user. Losing the version is better than that.
  return saved$.pipe(map(saved => requireRowVersion(saved.rowVersion)));
}
