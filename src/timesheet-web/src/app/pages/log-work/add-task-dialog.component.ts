import {
  AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, ViewChild, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';

/**
 * "Add task" — a modal, matching WPF's `TaskInputDialog`. (The user's explicit choice over an inline input.)
 *
 * Presentational and OnPush, exactly like `ConflictDialogComponent`: it collects one string and emits it. It
 * does not know what a backlog is, what an `orderIndex` is, or that an API exists — WHERE the new task lands
 * is the grid's problem, and a deceptively sharp one (see `nextOrderIndex`). Keeping that out of here is what
 * makes both halves testable.
 *
 * The backdrop WRAPS the dialog and centres it, as `.sf-backdrop` and `.cd-backdrop` both do. Clicking the
 * backdrop cancels; clicking inside the dialog must not, hence the `stopPropagation`.
 */
@Component({
  selector: 'app-add-task-dialog',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="atd-backdrop" role="presentation" (click)="cancel.emit()">
      <div class="atd" role="dialog" aria-modal="true" aria-labelledby="atd-title"
           (click)="$event.stopPropagation()">
        <h2 class="atd__title" id="atd-title">Add task</h2>

        <label class="atd__lbl" for="atd-name">Task name</label>
        <input #box id="atd-name" class="atd__input" autocomplete="off" placeholder="What needs doing?"
               [ngModel]="name()" (ngModelChange)="name.set($event)"
               (keydown.enter)="submit()" (keydown.escape)="cancel.emit()" />

        <div class="atd__actions">
          <button type="button" class="btn btn-ghost" (click)="cancel.emit()">Cancel</button>
          <button type="button" class="btn btn-primary" [disabled]="!name().trim()" (click)="submit()">
            Add
          </button>
        </div>
      </div>
    </div>`,
  styleUrl: './add-task-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AddTaskDialogComponent implements AfterViewInit {
  /** The TRIMMED task name. Emitted only when it is non-empty — see `submit`. */
  readonly confirm = output<string>();
  readonly cancel = output<void>();

  /**
   * A signal driven by `[ngModel]` + `(ngModelChange)`, not `[(ngModel)]` on a plain field — the idiom
   * `LogWorkComponent` (itself OnPush) already uses for its Smart Fill inputs, so it is proven under OnPush
   * in this app rather than assumed to work.
   */
  readonly name = signal('');

  @ViewChild('box') private box!: ElementRef<HTMLInputElement>;

  /** Focus the box on open. The dialog exists to take one string; the user should not have to click into it. */
  ngAfterViewInit(): void {
    this.box.nativeElement.focus();
  }

  /**
   * Enter and the Add button both funnel here.
   *
   * A whitespace-only name is not a task, and the API says so — `POST /api/tasks` answers
   * `400 "TaskName is required."` on a blank one. Refuse it here rather than make a round trip to be told.
   * The name is trimmed, because the server trims it too (`req.TaskName.Trim()`): emitting the untrimmed
   * string would put a value on screen that differs from the one in the database.
   */
  submit(): void {
    const name = this.name().trim();
    if (name) this.confirm.emit(name);
  }
}
