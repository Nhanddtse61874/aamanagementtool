import {
  AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, ViewChild, input, output,
} from '@angular/core';

/**
 * "Are you sure?" — one question, two buttons, no knowledge of what it is guarding.
 *
 * Presentational and OnPush, the same shape as `ConflictDialogComponent` and `AddTaskDialogComponent`: the
 * caller owns the decision and the write; this renders the question and emits which way the user went. It does
 * not know what a task is, what a soft delete is, or that an API exists.
 *
 * The confirming button is `btn-danger` and NOT configurable, deliberately — this exists to guard DESTRUCTIVE
 * actions, and a confirm dialog whose dangerous action can be styled as the inviting one is worse than none.
 * (`btn`/`btn-primary`/`btn-ghost`/`btn-soft`/`btn-danger`/`btn-sm` are the whole house set — styles.scss:91-102.)
 *
 * The backdrop WRAPS the dialog and centres it, as `.cd-backdrop` and `.atd-backdrop` both do — there is no
 * global modal class in this app, so each dialog ships its own scoped SCSS. Clicking the backdrop cancels;
 * clicking inside must not, hence the `stopPropagation`.
 */
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  // `role="alertdialog"`, not `role="dialog"`: this interrupts to confirm a consequence, which is exactly the
  // distinction ARIA draws between the two. `aria-describedby` points at the consequence itself — for a screen
  // reader that sentence is the whole reason the dialog exists, and a bare title would not carry it.
  template: `
    <div class="cfd-backdrop" role="presentation" (click)="cancel.emit()">
      <div class="cfd" role="alertdialog" aria-modal="true"
           aria-labelledby="cfd-title" aria-describedby="cfd-msg"
           (click)="$event.stopPropagation()" (keydown.escape)="cancel.emit()">
        <h2 class="cfd__title" id="cfd-title">{{ title() }}</h2>
        <p class="cfd__msg" id="cfd-msg">{{ message() }}</p>

        <div class="cfd__actions">
          <button #safe type="button" class="btn btn-ghost" (click)="cancel.emit()">Cancel</button>
          <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="confirm.emit()">
            {{ confirmLabel() }}
          </button>
        </div>
      </div>
    </div>`,
  styleUrl: './confirm-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConfirmDialogComponent implements AfterViewInit {
  /** The question. Owned by the caller, which is the only side that knows what is being destroyed. */
  readonly title = input.required<string>();
  /** What will and will not happen — the sentence that lets the user decide. */
  readonly message = input.required<string>();
  /** The word on the destructive button. "OK" tells the user nothing; the verb they are authorising does. */
  readonly confirmLabel = input.required<string>();

  /**
   * The caller's write is in flight, so the destructive button is disabled.
   *
   * Not decoration: the caller closes this dialog on confirm and the deleted row STAYS ON SCREEN until the
   * refresh lands (CDK snaps it back — nothing removes it locally), so on a slow link the user can drag it to
   * the bin again and re-open this dialog while the first write is still running. Its re-entrancy guard would
   * then refuse the second confirm SILENTLY — a button that does nothing, which is the exact "nothing happened"
   * trap the guard exists to avoid. Disabling it says so instead. Cancel stays live: the user is never trapped.
   */
  readonly busy = input(false);

  readonly confirm = output<void>();
  readonly cancel = output<void>();

  @ViewChild('safe') private safe!: ElementRef<HTMLButtonElement>;

  /**
   * Focus CANCEL, not the destructive button — so Enter and Space, the keys a user is most likely to hit
   * reflexively on a dialog they did not expect, take the SAFE branch. (`AddTaskDialogComponent` focuses its
   * input for the same reason: a modal that opens with focus still behind it is unusable from a keyboard.)
   *
   * It is also what makes the Escape binding above work at all: a `<div>` receives no keydown of its own, so
   * the event has to bubble from a focused element INSIDE the dialog.
   */
  ngAfterViewInit(): void {
    this.safe.nativeElement.focus();
  }
}
