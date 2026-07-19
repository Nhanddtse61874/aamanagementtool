import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

import { UserDto } from '../../api/models';
import { ToastService } from '../../services/toast.service';
import { WorklogService, requireRowVersion } from '../../services/worklog.service';
import { UserCreateError, createUserFully, requireUserId } from './user-create';
import { applyUserEdits, planUserEdits } from './user-edit';

/** The row being edited, as the form holds it. */
interface EditDraft {
  name: string;
  username: string;
  isAdmin: boolean;
}

/**
 * The Users screen. ADMIN-ONLY — `adminGuard` gates `/users` (app.routes.ts) and the sidebar hides it from
 * everyone else, which is what makes it safe for this screen to call `getUsersAll()` and the six [ADMIN]
 * writes. A non-admin who reached here would 403 on the very first read and see a broken screen, not a
 * hidden one. Never call any of these methods from a screen an ordinary user can reach.
 *
 * ── WHY `getUsersAll()` AND NOT `getUsersActive()` ───────────────────────────────────────────────────────
 * `GET /api/users` is `GetActiveAsync` and can NEVER return a deactivated user. This screen's whole job
 * includes bringing one BACK, so it must be able to SEE one: only `/api/users/all` returns them. That is the
 * entire reason the route is in the generated client, and the entire reason this screen is admin-gated.
 *
 * ── THE THREE-STEP CREATE ────────────────────────────────────────────────────────────────────────────────
 * See `user-create.ts`. `POST /api/users` alone produces an account with no username and no password hash
 * that NOBODY CAN EVER LOG INTO, and returns a cheerful 200 while doing it. Adding a user is create → set
 * username → set password, and a failure part-way through is REPORTED, loudly and persistently, because the
 * account it leaves behind looks perfectly healthy in the list.
 *
 * ── THE VERSION CHAIN ────────────────────────────────────────────────────────────────────────────────────
 * See `user-edit.ts`. Rename / set-username / set-admin are three CHECKED writes against ONE row, so they
 * cannot share an `expectedVersion` — each one's returned version feeds the next.
 */
@Component({
  selector: 'app-users',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './users.component.html',
  styleUrl: './users.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsersComponent {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly users = signal<UserDto[]>([]);
  readonly search = signal('');
  readonly loading = signal(true);

  /** A read that failed. The screen has nothing to show, so it says so instead of rendering an empty table. */
  readonly loadError = signal<string | null>(null);

  /**
   * 🔴 THE RE-ENTRANCY GUARD, and it is not decoration. Every mutation on this screen is guarded by it and
   * every button is disabled while it is set. Without it a double-clicked "Add user" runs the three-step
   * create TWICE and produces two accounts — and the second one races the first's version.
   */
  readonly busy = signal(false);

  /**
   * A message that MUST NOT vanish: the half-created account, above all. `ToastService` clears itself after
   * two seconds, which is fine for "Saved" and completely wrong for "the account exists, nobody can log into
   * it, and here is how to finish it". That sentence has to stay on screen until it is read and dismissed.
   */
  readonly notice = signal<string | null>(null);

  // ---- the add form ----
  readonly adding = signal(false);
  readonly draft = signal<EditDraft & { password: string }>({
    name: '', username: '', isAdmin: false, password: '',
  });

  // ---- the inline row editor ----
  readonly editingId = signal<number | null>(null);
  readonly edit = signal<EditDraft>({ name: '', username: '', isAdmin: false });

  // ---- the set-password row ----
  readonly passwordForId = signal<number | null>(null);
  readonly newPassword = signal('');

  constructor() {
    this.load();
  }

  // 🔴 These exist because an Angular TEMPLATE EXPRESSION IS NOT JAVASCRIPT — its parser is a restricted
  // subset with NO SPREAD OPERATOR, so `draft.set({ ...draft(), name: $event })` does not compile (it fails
  // at JIT with "Unexpected token ."). The spread has to live in the class. One setter per field, which is
  // also what keeps the template free of state-shape knowledge.
  setDraftName(v: string): void { this.draft.update(d => ({ ...d, name: v })); }
  setDraftUsername(v: string): void { this.draft.update(d => ({ ...d, username: v })); }
  setDraftPassword(v: string): void { this.draft.update(d => ({ ...d, password: v })); }

  setEditName(v: string): void { this.edit.update(e => ({ ...e, name: v })); }
  setEditUsername(v: string): void { this.edit.update(e => ({ ...e, username: v })); }

  // 🔴 The two admin checkboxes bind `[checked]` + `(change)`, NOT `[ngModel]`, and that is deliberate.
  // `NgModel` writes its value to the DOM on a MICROTASK (`resolvedPromise.then()` inside `_updateValue`),
  // so immediately after the editor opens the checkbox is still visually UNCHECKED even for a user who IS an
  // admin. A click at that moment toggles it false->true and emits `true` — the value the signal ALREADY
  // held — so the change is a no-op and the admin grant is silently dropped. A plain boolean does not need a
  // value accessor; a direct property binding is synchronous, simpler, and cannot desynchronise.
  toggleDraftAdmin(): void { this.draft.update(d => ({ ...d, isAdmin: !d.isAdmin })); }
  toggleEditAdmin(): void { this.edit.update(e => ({ ...e, isAdmin: !e.isAdmin })); }

  readonly filtered = computed(() => {
    const term = this.search().trim().toLowerCase();
    if (term === '') return this.users();
    return this.users().filter(u =>
      (u.name ?? '').toLowerCase().includes(term) || (u.username ?? '').toLowerCase().includes(term));
  });

  /**
   * An account nobody can log into: no username, or no password ever set.
   *
   * 🔴 The second half was DESCRIBED here and never CHECKED. The old body was `(u.username ?? '') !== ''`
   * alone, so on any database holding accounts without passwords this screen reported every one of them
   * as able to log in — while none of them could. That is the screen an admin provisions accounts from,
   * and it was lying to them about the exact thing they were there to fix. `hasPassword` (M11) closes it.
   *
   * `=== true`, not a bare truthiness test: the generated field is `boolean | undefined`, and `undefined`
   * from an older client or a partial response must read as "cannot log in", never as "can".
   */
  readonly canLogIn = (u: UserDto): boolean =>
    (u.username ?? '') !== '' && u.hasPassword === true;

  avatar(name: string | null | undefined): string { return this.api.avatarColor(name ?? null); }
  initial(name: string | null | undefined): string { return (name ?? '?').charAt(0).toUpperCase(); }

  trackById = (_: number, u: UserDto): number => u.id ?? -1;

  // =======================================================================================================
  // READ
  // =======================================================================================================

  private load(): void {
    this.loading.set(true);
    this.api.getUsersAll()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.users.set(rows);
          this.loadError.set(null);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.users.set([]);
          this.loadError.set(describeError(err));
          this.loading.set(false);
        },
      });
  }

  reload(): void { this.load(); }

  dismissNotice(): void { this.notice.set(null); }

  // =======================================================================================================
  // CREATE — the three-step flow
  // =======================================================================================================

  openAdd(): void {
    this.draft.set({ name: '', username: '', isAdmin: false, password: '' });
    this.adding.set(true);
    this.notice.set(null);
  }

  cancelAdd(): void { this.adding.set(false); }

  readonly addReady = computed(() => {
    const d = this.draft();
    // All three are REQUIRED, and the requirement is the point: a user created without a username or a
    // password is an account nobody can log into. There is no "fill it in later" that does not ship a ghost.
    return d.name.trim() !== '' && d.username.trim() !== '' && d.password !== '';
  });

  addUser(): void {
    if (this.busy() || !this.addReady()) return;   // ← re-entrancy guard
    this.busy.set(true);

    const draft = this.draft();
    const wantsAdmin = draft.isAdmin;

    createUserFully(this.api, draft)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: id => {
          this.busy.set(false);
          this.adding.set(false);
          this.toast.show(`${draft.name.trim()} can now log in`);
          // Granting admin is a separate CHECKED write and we do not hold the row's current version here
          // (the password step returns nothing). Reload, then grant from the row we just re-read.
          if (wantsAdmin) this.grantAdminAfterCreate(id);
          else this.load();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.adding.set(false);
          // 🔴 A PERSISTENT banner, not a toast. If steps 1-2 landed and step 3 did not, an account that
          // cannot be logged into is now sitting in the list looking completely normal, and a message that
          // disappears in two seconds is how it stays there.
          this.notice.set(describeError(err));
          // Reload regardless: on a partial failure the half-made account IS there, and the admin needs to
          // see it to repair it.
          if (err instanceof UserCreateError && err.userId !== null) this.load();
        },
      });
  }

  /** The new row is at a known version only after a re-read, so this runs as its own step. */
  private grantAdminAfterCreate(id: number): void {
    this.api.getUsersAll()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.users.set(rows);
          this.loading.set(false);
          const created = rows.find(u => u.id === id);
          if (created === undefined) return;
          this.write(this.api.setUserAdmin(id, true, requireRowVersion(created.rowVersion)), 'Admin granted');
        },
        error: (err: unknown) => this.notice.set(describeError(err)),
      });
  }

  // =======================================================================================================
  // EDIT — name / username / admin, as ONE chained save
  // =======================================================================================================

  openEdit(u: UserDto): void {
    this.editingId.set(u.id ?? null);
    this.edit.set({ name: u.name ?? '', username: u.username ?? '', isAdmin: u.isAdmin === true });
    this.passwordForId.set(null);
  }

  cancelEdit(): void { this.editingId.set(null); }

  saveEdit(original: UserDto): void {
    if (this.busy()) return;   // ← re-entrancy guard

    const id = original.id;
    if (typeof id !== 'number') return;

    const edits = planUserEdits(original, this.edit());
    if (edits.length === 0) { this.editingId.set(null); return; }   // nothing changed — make no write at all

    this.busy.set(true);

    // 🔴 The version is threaded through the chain (user-edit.ts). Handing all three writes `original
    // .rowVersion` would land the rename and 409 the rest.
    applyUserEdits(this.api, id, edits, requireRowVersion(original.rowVersion))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.editingId.set(null);
          this.toast.show('Saved');
          this.load();
        },
        error: (err: unknown) => this.failWrite(err),
      });
  }

  // =======================================================================================================
  // THE BUMP-ONLY WRITES — no version, so no 409 is possible on either
  // =======================================================================================================

  openPassword(u: UserDto): void {
    this.passwordForId.set(u.id ?? null);
    this.newPassword.set('');
    this.editingId.set(null);
  }

  cancelPassword(): void { this.passwordForId.set(null); }

  savePassword(u: UserDto): void {
    if (this.busy() || this.newPassword() === '') return;   // ← re-entrancy guard
    const id = requireUserId(u.id);

    this.busy.set(true);
    this.api.adminSetPassword(id, this.newPassword())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.passwordForId.set(null);
          this.newPassword.set('');
          this.toast.show(`${u.name ?? 'The user'} can now log in`);
          this.load();
        },
        error: (err: unknown) => this.failWrite(err),
      });
  }

  /**
   * SOFT. `is_active = false`; the user's TimeLogs are untouched and every report they appear in still
   * resolves their name. `true` restores through the same route — which is why the flag is passed through
   * rather than hard-coded, and why this screen needs `getUsersAll()` to be able to show a row to restore.
   */
  toggleActive(u: UserDto): void {
    if (this.busy()) return;   // ← re-entrancy guard
    const id = requireUserId(u.id);
    const next = !(u.isActive === true);

    this.write(this.api.setUserActive(id, next), next ? 'Activated' : 'Deactivated');
  }

  // =======================================================================================================
  // The one write path everything bump-only funnels through.
  // =======================================================================================================

  private write(call: Observable<unknown>, done: string): void {
    this.busy.set(true);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.show(done);
        this.load();
      },
      error: (err: unknown) => this.failWrite(err),
    });
  }

  /**
   * Every `async` path on this screen ends here, and it NEVER re-throws — an unhandled rejection out of a
   * template-bound handler leaves `busy` stuck true and the whole screen dead behind disabled buttons.
   *
   * 🔴 A 409 means someone else moved the row while this form was open, so the version we hold is stale and
   * every further write from it would 409 too. Re-read, so the next attempt starts from the truth.
   */
  private failWrite(err: unknown): void {
    this.busy.set(false);
    this.editingId.set(null);
    this.passwordForId.set(null);
    this.toast.show(describeError(err));
    if (isConflict(err)) this.load();
  }
}

function isConflict(err: unknown): boolean {
  return err instanceof HttpErrorResponse && err.status === 409;
}

/**
 * Turn whatever came back into a sentence the admin can act on.
 *
 * `UserCreateError` first: it is the only one that knows the account EXISTS and is unusable, and that is the
 * single most important thing this screen can ever have to say.
 */
export function describeError(err: unknown): string {
  if (err instanceof UserCreateError) return err.message;

  if (err instanceof HttpErrorResponse) {
    if (err.status === 409) {
      return 'Someone else changed this user while you had it open. Reloading — please try again.';
    }
    if (err.status === 403) return 'You are not an administrator.';
    if (err.status === 400) {
      const body: unknown = err.error;
      const message = readMessage(body);
      return message ?? 'The server rejected that.';
    }
    if (err.status === 0) return 'Cannot reach the server.';
    return `The server returned ${err.status}.`;
  }

  if (err instanceof Error) return err.message;
  return 'Something went wrong.';
}

/** `ValidationBody` is `{ message }`. Read it defensively — this is the network boundary. */
function readMessage(body: unknown): string | null {
  if (typeof body === 'object' && body !== null && 'message' in body) {
    const message = (body as { message: unknown }).message;
    if (typeof message === 'string' && message !== '') return message;
  }
  return null;
}
