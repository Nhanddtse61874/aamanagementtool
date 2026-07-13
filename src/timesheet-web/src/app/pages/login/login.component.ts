import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';

/**
 * `LoginRequest` on the wire is `(username, password)` and NOTHING else -- there is no `rememberMe`
 * field, because `IsPersistent = true` is unconditional server-side (AuthSetup.cs). This form only ever
 * collects the two fields the API accepts.
 */
@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly form = this.fb.nonNullable.group({
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.submitting()) return;

    this.error.set(null);
    this.submitting.set(true);
    const { username, password } = this.form.getRawValue();

    this.auth.login(username, password).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      // Leave `submitting` true on success: we are navigating away immediately, and re-enabling the
      // button for one frame before that happens is a worse look than a form that stays disabled.
      next: () => this.router.navigateByUrl('/log'),
      error: (err: unknown) => {
        this.submitting.set(false);
        this.error.set(loginErrorMessage(err));
      },
    });
  }
}

/** Maps a failed login attempt to copy a user can act on -- never the raw HttpErrorResponse. */
function loginErrorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 401) return 'Incorrect username or password.';
    if (err.status === 0) return 'Cannot reach the server. Check your connection and try again.';
  }
  return 'Something went wrong. Please try again.';
}
