import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/**
 * What the user needs to decide a 409, and nothing else.
 *
 * `ConflictBody` on the wire is `{ table, id, deleted, detail, message }` and — this is the part that catches
 * people — **`id` is `0`**. A timesheet cell has no id: its key is the natural triple (user, task, date), so
 * there is no integer to report and the server sends zero. **`detail` is the ONLY field that says WHICH
 * cell** ("user 1, task 1, 2026-07-13" — verified on the wire). Never key a conflict off `id`.
 *
 * There is deliberately NO `current` value in the body — the client re-fetches the week and reads the
 * server's value from that. One extra round-trip on a rare path, and it cannot go stale between the 409 and
 * the dialog the way an embedded value could.
 */
export interface CellConflict {
  /** Which cell, in the server's words. The only identifier there is. */
  readonly detail: string;
  /** The server's own sentence about what happened. */
  readonly message: string;
  /** Human context the grid knows and the wire does not. */
  readonly taskName: string;
  readonly dayLabel: string;
  /** What the user typed and tried to save. `null` = they tried to clear the cell. */
  readonly yours: number | null;
  /** What the cell holds NOW, from the re-fetch that followed the 409. `null` = someone deleted it. */
  readonly theirs: number | null;
}

/**
 * The 409 dialog. Presentational and OnPush: it renders a decision and emits which way the user went. It
 * does not know what a version is, and it must not — re-fetching, re-saving and version arithmetic all
 * belong to the grid.
 *
 * "Keep theirs" is the safe side and is styled as the primary action. Overwriting is the destructive one:
 * it throws away a colleague's work, so it must be the deliberate choice, never the easy one.
 */
@Component({
  selector: 'app-conflict-dialog',
  standalone: true,
  templateUrl: './conflict-dialog.component.html',
  styleUrl: './conflict-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ConflictDialogComponent {
  readonly conflict = input.required<CellConflict>();

  /** Accept the server's value and drop the edit. The grid already shows theirs after the re-fetch, so this
   *  is simply "close". */
  readonly keepTheirs = output<void>();

  /** Re-apply the user's value ON TOP of the version the re-fetch just returned. */
  readonly overwrite = output<void>();

  /** `null` hours means the cell is empty — show that as a word, not as a blank space the user has to
   *  interpret, and never as "0" (which the API rejects outright). */
  display(hours: number | null): string {
    return hours == null ? '(empty)' : `${hours}h`;
  }
}
