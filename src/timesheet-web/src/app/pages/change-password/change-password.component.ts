import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { ValidationBody } from '../../api/models';

/**
 * Go-live blocker fix. Self-service password change -- `POST /api/auth/set-password` (AuthEndpoints.cs).
 * Reachable by ANY signed-in user, not just admins: an account an admin created, or the seeded
 * `admin`/`admin` bootstrap login (AdminBootstrap.cs), has no other in-app way to leave a password someone
 * else set. See sidebar.component.ts -- the nav entry for this screen is deliberately NOT behind
 * `isAdmin()`, unlike Users/Settings.
 *
 * Confirm-mismatch is caught CLIENT-side, before any request: `AuthSetPasswordRequest` on the wire is
 * `(currentPassword, newPassword)` and carries no confirm field for the server to check for us.
 */
@Component({
  selector: 'app-change-password',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './change-password.component.html',
  styleUrl: './change-password.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChangePasswordComponent {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly form = this.fb.nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', Validators.required],
    confirmPassword: ['', Validators.required],
  });

  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.submitting()) return;

    const { currentPassword, newPassword, confirmPassword } = this.form.getRawValue();

    // Client-side, before any request -- the server has nothing to check this against.
    if (newPassword !== confirmPassword) {
      this.error.set('New password and confirmation do not match.');
      return;
    }

    this.error.set(null);
    this.submitting.set(true);

    this.api.setPassword(currentPassword, newPassword).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: () => {
        this.submitting.set(false);
        this.form.reset();
        this.toast.show('Password changed.');
      },
      error: (err: unknown) => {
        this.submitting.set(false);
        this.error.set(changePasswordErrorMessage(err));
      },
    });
  }
}

/**
 * The 400 is shown VERBATIM. AuthEndpoints.cs owns both sentences ("Current password is incorrect." and
 * the no-password-set one) -- they are copy a user can act on, and inventing our own paraphrase for a rule
 * the server owns would only make it worse.
 */
function changePasswordErrorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 400) {
      return (err.error as ValidationBody | null)?.error ?? 'That change was rejected.';
    }
    if (err.status === 0) return 'Cannot reach the server. Check your connection and try again.';
  }
  return 'Something went wrong. Please try again.';
}
