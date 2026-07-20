import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subject, catchError, firstValueFrom, map, merge, of, switchMap } from 'rxjs';

import { ConflictBody, HolidayDto, TimeLogDto, ValidationBody, WeekBacklogGroup } from '../../api/models';
import { cellKey } from '../../core/cell-key';
import { ConfirmDialogComponent } from '../../core/confirm-dialog/confirm-dialog.component';
import { CellConflict, ConflictDialogComponent } from '../../core/conflict-dialog/conflict-dialog.component';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { AddTaskDialogComponent } from './add-task-dialog.component';
import {
  CellMap, Group, InvalidReason, TaskRow, buildCellMap, buildGroups, expectedVersionFor, formatHours,
  mergeSmartFill, nextOrderIndex, patchCell, readCell,
} from './grid-state';
import { canMoveMonth, nextMonthFrom, toUpdateRequest } from './move-month';
import { reorderPlan } from './reorder';
import { buildSmartFillRequest } from './smart-fill';
import { WeekDay, mondayOf, shiftWeeks, weekDays } from './week';

/** A write that 409'd and is waiting on the user's decision. */
interface PendingWrite {
  readonly taskId: number;
  readonly isoDate: string;
  /** `null` = they were trying to CLEAR the cell. */
  readonly hours: number | null;
}

/**
 * A task dragged to the bin and waiting on the user's confirmation. `taskName` is carried, not looked up: the
 * dialog names the task the user actually dropped, and a refresh landing while the dialog is open must not be
 * able to change WHICH name is on screen under their cursor.
 */
interface PendingDelete {
  readonly taskId: number;
  readonly taskName: string;
}

/**
 * Log Work — the first screen in this app that touches the real database.
 *
 * Four things here are load-bearing and none of them are visible on screen:
 *
 * 1. **Cells are keyed by `(taskId, isoDate)`, never by position.** The vendored grid keyed hours by
 *    `${groupIndex}-${taskIndex}-${dayIndex}`; one sort or filter re-pointed every key at a different task
 *    and hours landed on the wrong row with nothing to notice.
 * 2. **`expectedVersion` is a claim, not a hint.** See `expectedVersionFor` — `null` means "I believe this
 *    cell is empty", and there is no way to say "I don't know".
 * 3. **The version comes from the WRITE's response, never from a re-read.** A read-back is racy: another
 *    client can write in between, and we would hold THEIR version with OUR data and overwrite them next save.
 * 4. **Smart Fill is MERGED, never used to replace the grid** — and it is not ignored either. See
 *    `mergeSmartFill`.
 *
 * Writes are committed on BLUR, not on keystroke. `(ngModelChange)` fires per character, so a per-keystroke
 * save would fire "4", "4.", "4.5" as three concurrent PUTs carrying the SAME expectedVersion — the second
 * and third would 409 against the first. The user would watch a conflict dialog open over their own typing,
 * on the happy path, every time. The draft map holds what is being typed; the cell map holds what the server
 * has confirmed.
 */
@Component({
  selector: 'app-log-work',
  standalone: true,
  // 🔴 `DragDropModule` is LOAD-BEARING, and HOW it fails when absent is worth knowing exactly, because the
  // widely-repeated version of this warning is only half true. MEASURED, by removing it and building:
  //
  //   - A PROPERTY BINDING is checked: `[cdkDropListData]` / `[cdkDropListConnectedTo]` / `[cdkDragData]` each
  //     fail with `NG8002: Can't bind to '…' since it isn't a known property of 'div'`, and the output fails
  //     with `NG5: Argument of type 'Event' is not assignable to parameter of type 'CdkDragDrop<…>'` — because
  //     `$event` falls back to the native `Event`. So for THIS template, losing the module is LOUD: four errors.
  //   - A BARE ATTRIBUTE is NOT checked. With the same module missing and the same directives written as bare
  //     attributes, `ng build` reports "Application bundle generation complete" — CLEAN. Angular errors on an
  //     unknown ELEMENT (NG8001), never on an unknown ATTRIBUTE.
  //
  // 🔴 Which matters here, because `cdkDragHandle` on the SVG IS a bare attribute — the one directive in this
  // template with nothing bound to it. Typo it, or let it drift outside `cdkDrag`, and it is silently dropped;
  // CDK then falls back to making the WHOLE ROW draggable, so the grid still "works" and the bug is invisible.
  // `log-work.component.spec.ts` asserts `By.directive(CdkDragHandle)` finds it, and that assertion is the only
  // thing standing under it. A green build is not evidence for that one.
  imports: [
    CommonModule, FormsModule, ConflictDialogComponent, ConfirmDialogComponent, AddTaskDialogComponent,
    DragDropModule,
  ],
  templateUrl: './log-work.component.html',
  styleUrl: './log-work.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LogWorkComponent {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  // ---- the week ------------------------------------------------------------------------------------
  /** The Monday whose week is on screen. Changing it RE-FETCHES — see the pipeline in the constructor. */
  readonly monday = signal(mondayOf(new Date()));
  /** The day axis, DERIVED. Was a hard-coded 5-day literal in `WorklogService.WEEK_DAYS`. */
  readonly days = computed<WeekDay[]>(() => weekDays(this.monday()));
  readonly weekLabel = computed(() => {
    const d = this.days();
    return `${d[0].label} – ${d[4].label}`;
  });

  // ---- state ---------------------------------------------------------------------------------------
  readonly groups = signal<Group[]>([]);
  /** SERVER TRUTH: hours + rowVersion per cell. The source of every `expectedVersion`. */
  private readonly cells = signal<CellMap>({});
  /**
   * ISO date -> the holiday's description (or `null` if it has none). Fetched ONCE at load -- see the
   * constructor. M10/P7: "today a user discovers a holiday by typing into the cell and being refused" (the
   * server already 400s the write; this map only needs to exist for the grid to say so BEFORE that happens).
   * Holidays change rarely enough that tying a re-fetch to `refresh` -- which fires on nearly every write this
   * screen makes -- is not worth the extra round trip.
   */
  private readonly holidays = signal<ReadonlyMap<string, string | null>>(new Map());
  /** What the user is TYPING, before it is committed. Cleared per cell once the write resolves. */
  private readonly drafts = signal<Record<string, string>>({});
  /**
   * Cells whose draft `commitCell` refused to write, keyed by `cellKey(taskId, iso)` -- drives the red
   * border, `aria-invalid` and the status line. NOT touched per keystroke: WPF's own contract ("Persist a
   * single cell as soon as it commits") is commit-gated, so a cell reads red only after a real commit
   * attempt, never mid-type ("4." is not "4.5" gone wrong). Cleared whenever the draft it belongs to is
   * dropped -- see `dropDraft` -- so a fixed cell or a reload/Smart-Fill cannot leave a stale mark on screen.
   */
  private readonly invalidCells = signal<ReadonlyMap<string, InvalidReason>>(new Map());
  /** Drives the single `role="alert"` status line -- the idiom at `backlog-editor.component.html:24-27`. */
  readonly hasInvalidCell = computed(() => this.invalidCells().size > 0);
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly collapsed = signal<Record<number, boolean>>({});
  readonly conflict = signal<CellConflict | null>(null);
  private pendingWrite: PendingWrite | null = null;

  /** The group whose "Add task" dialog is open, or `null`. A signal plus an `@if` in the template — the same
   *  shape as `conflict`, because this app has no dialog service and does not need one. */
  readonly addingTo = signal<Group | null>(null);

  /** True while a Move's GET -> PUT is in flight; the button is disabled for its duration. The same shape as
   *  `sfBusy`. A second click is not merely wasteful here, it CORRUPTS — see `onMoveMonth`. */
  readonly moving = signal(false);

  /** True while a drag's batch of order writes is in flight. The same shape as `moving`, and for the same
   *  reason: a second drag started before the first batch lands would CORRUPT the order — see `onDrop`. */
  readonly reordering = signal(false);

  /** True while a soft delete is in flight. The same shape as `reordering`, and for a closely related reason:
   *  CDK does not remove the row for us, so it SNAPS BACK until the refresh lands — see `confirmDelete`. */
  readonly deleting = signal(false);

  /**
   * The task dropped on the bin and awaiting confirmation, or `null`. A signal plus an `@if` in the template —
   * the same shape as `conflict` and `addingTo`, because this app has no dialog service and does not need one.
   *
   * 🔴 This signal IS the undo. The drop used to delete instantly; the delete is soft, so `setTaskActive(id,
   * true)` would restore the task — but NO SCREEN CALLS THE RESTORE, and none is planned, so a mis-dropped row
   * was gone for good as far as any user could tell. Dragging is the easiest gesture in this app to perform by
   * accident. The cheapest fix is not to build an undo: it is to ask first.
   */
  readonly pendingDelete = signal<PendingDelete | null>(null);

  /** The dialog's question. Built here rather than concatenated in the template — the template is not where
   *  copy belongs, and this way the exact sentence the user reads is assertable. */
  readonly confirmDeleteTitle = computed(() => {
    const p = this.pendingDelete();
    return p ? `Delete task “${p.taskName}”?` : '';
  });

  /**
   * The trash's `[cdkDropListData]`. It holds no rows, and never will — but it is a TYPED empty array on a
   * field rather than a `[]` literal in the template, and that is a compiler requirement, not a style choice.
   *
   * 🔴 MEASURED. The plan's `[cdkDropListData]="[]"` infers `CdkDropList<never[]>`, and `T` reaches a
   * CONTRAVARIANT position through `container.dropped.emit(value)` — so `never[]` does NOT widen to
   * `readonly TaskRow[]` and the build fails:
   *
   *     NG5: Argument of type 'CdkDragDrop<never[], any, any>' is not assignable to parameter of type
   *          'CdkDragDrop<readonly TaskRow[], …>'. The type 'readonly TaskRow[]' is 'readonly' and cannot be
   *          assigned to the mutable type 'never[]'.
   *
   * Naming the type here fixes the drop list's `T` to the same `readonly TaskRow[]` the groups use, which is
   * what lets `onTrash` take a properly typed event and read `event.item.data` as a `TaskRow` with no `any`
   * and no cast. (It also stops a fresh `[]` being allocated on every change-detection pass.)
   */
  readonly noRows: readonly TaskRow[] = [];

  /**
   * The DEFAULT-backlog rule, exposed to the template so the Move button can be HIDDEN on that group — the
   * FIRST of its two guards. (`onMoveMonth` re-checks it: WPF guards it in both places and so do we.)
   *
   * A bound reference, not a wrapper method: the pure function in `move-month.ts` stays the single source of
   * truth for the rule, and a template can only call a member of its own component.
   */
  readonly canMoveMonth = canMoveMonth;

  // ---- smart fill ----------------------------------------------------------------------------------
  readonly sfOpen = signal(false);
  readonly sfTaskId = signal<number | null>(null);
  readonly sfDays = signal<readonly string[]>([]);
  readonly sfTotal = signal(8);
  readonly sfBusy = signal(false);

  /**
   * "Re-read the CURRENT week" (SignalR, a 404, a conflict).
   *
   * A Subject, NOT a signal — and that distinction is load-bearing, not stylistic. A refresh is an EVENT;
   * the week on screen is STATE. Modelling it as a signal broke it in two separate ways, both of which the
   * tests caught:
   *   - `toObservable()` REPLAYS a signal's current value on subscribe, so a `refresh` signal emitted at
   *     startup alongside `monday` and the page fetched the week TWICE on every single load.
   *   - `toObservable()` propagates through an effect, so a bump only reached the pipeline on the next
   *     change-detection pass — a 404's re-fetch silently did not happen until something else moved.
   */
  private readonly refresh = new Subject<void>();

  constructor() {
    // ONE fetch pipeline for both "the week changed" and "something says re-read it". switchMap so the
    // LATEST wins: clicking Prev twice quickly must not let the first response land on top of the second.
    merge(
      toObservable(this.monday),
      this.refresh.pipe(map(() => this.monday())),
    ).pipe(
      switchMap(monday => {
        this.loading.set(true);
        return this.api.getWeek(monday).pipe(
          map(groups => ({ monday, groups })),
          // catchError INSIDE the switchMap: a failed week read must not kill the pipeline and leave
          // navigation permanently dead.
          catchError((err: unknown) => {
            this.loading.set(false);
            this.loadError.set(readError(err, 'Could not load this week.'));
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(),
    ).subscribe(({ monday, groups }) => this.applyWeek(monday, groups));

    // Someone ELSE changed this team's data. The server already excluded us from its own broadcast (that is
    // what X-Connection-Id buys), so there is no echo of our own writes to filter out here.
    this.realtime.dataChanged.pipe(takeUntilDestroyed()).subscribe(() => this.refresh.next());
    this.realtime.start();

    // Holidays are OPEN, no year/month filter -- same call `settings.component.ts` makes, for the same
    // reason: this returns every holiday on file. Decorative only, so a failed fetch must not break the grid.
    this.api.getHolidayList().pipe(
      catchError(() => of<HolidayDto[]>([])),
      takeUntilDestroyed(),
    ).subscribe(list => {
      const map = new Map<string, string | null>();
      for (const h of list) {
        if (h.date) map.set(h.date, h.description ?? null);
      }
      this.holidays.set(map);
    });
  }

  private applyWeek(monday: string, groups: WeekBacklogGroup[]): void {
    this.loading.set(false);
    this.loadError.set(null);
    this.groups.set(buildGroups(groups));
    // The axis MUST be the axis of the week that was actually fetched, not whatever `monday()` says by the
    // time the response lands — otherwise a slow response for last week keys its cells onto this week.
    this.cells.set(buildCellMap(groups, weekDays(monday)));
    this.drafts.set({});
    this.invalidCells.set(new Map());   // a fresh week read replaces every draft; no stale mark can survive it
  }

  // ---- reading the grid ----------------------------------------------------------------------------

  /** What goes in the input box: the draft if the user is typing, otherwise the server's value. */
  cellText(taskId: number, iso: string): string {
    const key = cellKey(taskId, iso);
    return this.drafts()[key] ?? formatHours(this.cells()[key]?.hours ?? null);
  }

  /** Whether this cell's current draft was refused by `commitCell` -- the template's red border / aria-invalid. */
  isInvalidCell(taskId: number, iso: string): boolean {
    return this.invalidCells().has(cellKey(taskId, iso));
  }

  /**
   * WHY the cell was refused, as a sentence -- spec §5.6's "the message as the input's accessible description".
   *
   * Bound to `title`, which is not merely a tooltip: per the accessible-name-and-description spec, `title` IS
   * the accessible description when no `aria-describedby` is present. One attribute therefore serves both a
   * sighted user hovering a red cell and a screen reader, without inventing a visually-hidden utility class
   * this codebase does not have.
   *
   * The generic `role="alert"` line says only that SOMETHING was refused (WPF's own sentence, kept verbatim);
   * without this, a user who types `9` sees red and is never told the per-cell cap is 8. WPF surfaces the
   * specific reason per cell via `INotifyDataErrorInfo` -- strings below match its own wording where it has one.
   */
  invalidMessage(taskId: number, iso: string): string | null {
    switch (this.invalidCells().get(cellKey(taskId, iso))) {
      case 'not-a-number': return 'Not a number.';
      case 'not-positive': return 'Hours must be greater than 0.';   // the API's own sentence
      case 'over-cap':     return 'At most 8h in one cell.';         // XC-02
      case 'too-precise':  return 'At most 1 decimal place.';        // TimesheetRowVm.cs:54, verbatim
      default:             return null;
    }
  }

  /** Whether `iso` is a public holiday -- the grid's only way to show what the server already enforces
   *  (a holiday write 400s). Weekends are excluded from the grid entirely already; this is the other half. */
  isHoliday(iso: string): boolean {
    return this.holidays().has(iso);
  }

  /** The header's tooltip and the holiday half of a cell's `title` -- `null` for an ordinary day. */
  holidayLabel(iso: string): string | null {
    const description = this.holidays().get(iso);
    if (description === undefined) return null;
    return description ? `Holiday — ${description}` : 'Holiday';
  }

  /** A cell's `title`. An invalid commit wins over a holiday note -- the same priority the red border already
   *  implies: a refused edit is more pressing than a warning not to have typed at all. */
  cellTitle(taskId: number, iso: string): string | null {
    return this.invalidMessage(taskId, iso) ?? this.holidayLabel(iso);
  }

  /**
   * 🔴 An invalid draft must never lower the totals: the DB still holds whatever was last committed here, and
   * `readCell` -- not a live parse of gibberish -- is what tells the difference (spec §5.5, the totals-side
   * twin of BUG-1: typing "abc" over a 4h cell used to drop the row/day/week totals to 0 on screen while the
   * database still held the 4). Checked LIVE, per keystroke, via the draft text itself -- not via
   * `invalidCells`, which only updates on commit -- because the totals move as you type (see the class
   * comment) and the lie appeared before any blur ever happened.
   */
  private cellHours(taskId: number, iso: string): number {
    const key = cellKey(taskId, iso);
    const input = readCell(this.cellText(taskId, iso));
    if (input.kind === 'value') return input.hours;
    if (input.kind === 'clear') return 0;
    return this.cells()[key]?.hours ?? 0;                 // 'invalid' -- fall back to the LAST COMMITTED value
  }

  rowTotal(taskId: number): number {
    return round1(this.days().reduce((sum, d) => sum + this.cellHours(taskId, d.iso), 0));
  }

  groupTotal(group: Group): number {
    return round1(group.tasks.reduce((sum, t) => sum + this.rowTotal(t.taskId), 0));
  }

  dayTotal(iso: string): number {
    return round1(this.groups().reduce(
      (sum, g) => sum + g.tasks.reduce((s, t) => s + this.cellHours(t.taskId, iso), 0), 0));
  }

  readonly weekTotal = computed(() =>
    round1(this.days().reduce((sum, d) => sum + this.dayTotal(d.iso), 0)));

  fmt(n: number): string { return n.toFixed(1); }
  typeColor(t: string | null) { return this.api.typeColor(t); }
  avatar(name: string | null) { return this.api.avatarColor(name); }
  isOpen(backlogId: number): boolean { return !this.collapsed()[backlogId]; }

  // ---- week navigation (was `toast.show('Loaded previous week')` and nothing else) -------------------

  prevWeek(): void { this.monday.update(m => shiftWeeks(m, -1)); }
  nextWeek(): void { this.monday.update(m => shiftWeeks(m, 1)); }
  thisWeek(): void { this.monday.set(mondayOf(new Date())); }

  // ---- editing -------------------------------------------------------------------------------------

  /** Every keystroke. Updates the DRAFT only — the totals move, nothing is written. */
  editCell(taskId: number, iso: string, text: string): void {
    const key = cellKey(taskId, iso);
    this.drafts.update(d => ({ ...d, [key]: text }));
  }

  /** Blur/Enter. THIS is the write. */
  commitCell(taskId: number, iso: string): void {
    const key = cellKey(taskId, iso);
    const draft = this.drafts()[key];
    if (draft === undefined) return;                       // never touched — nothing to commit

    const input = readCell(draft);

    // 🔴 'invalid' is never "equal to current" and never reaches the API. It used to collapse onto
    // `parseHours(draft) === null` -- the SAME null a genuine clear produces -- and the branch below turned
    // every null into `clearCell()`: a DELETE of hours the user never asked to remove (BUG-1, spec §3). The
    // draft stays exactly as typed; only the invalid mark changes.
    if (input.kind === 'invalid') {
      this.markInvalid(key, input.reason);
      return;
    }

    const wanted = input.kind === 'clear' ? null : input.hours;
    const current = this.cells()[key]?.hours ?? null;

    this.clearInvalid(key);                                  // a legitimate clear/value -- any prior mark is gone

    if (wanted === current) { this.dropDraft(key); return; }  // typed it back to what it already was

    if (input.kind === 'clear') this.clearCell(taskId, iso);
    else this.saveCell(taskId, iso, input.hours);
  }

  private markInvalid(key: string, reason: InvalidReason): void {
    this.invalidCells.update(m => (m.get(key) === reason ? m : new Map(m).set(key, reason)));
  }

  private clearInvalid(key: string): void {
    this.invalidCells.update(m => {
      if (!m.has(key)) return m;
      const next = new Map(m);
      next.delete(key);
      return next;
    });
  }

  private saveCell(taskId: number, iso: string, hours: number): void {
    // The claim: the real version if this cell has one, `null` if it genuinely has none. Never 0.
    const expected = expectedVersionFor(this.cells(), taskId, iso);

    this.api.saveHours(taskId, iso, hours, expected).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: rowVersion => {
        // STORE WHAT THE WRITE RETURNED. Never re-read it — see the class comment.
        this.cells.update(c => patchCell(c, taskId, iso, { hours, rowVersion }));
        this.dropDraft(cellKey(taskId, iso));
      },
      error: (err: unknown) => {
        this.dropDraft(cellKey(taskId, iso));              // the write did not happen: show the truth again
        this.handleWriteError(err, taskId, iso, hours);
      },
    });
  }

  /**
   * An emptied box is a DELETE, not a save of zero: the API rejects `hours <= 0` with
   * `400 "Hours must be greater than 0."` (verified), so there is no way to empty a cell through the save
   * route. `DELETE /api/timesheet/cell` takes a NON-nullable `expectedVersion` — you cannot clear a cell you
   * do not already hold a version for, which is exactly right: no version means it is already empty.
   */
  private clearCell(taskId: number, iso: string): void {
    const key = cellKey(taskId, iso);
    const expected = expectedVersionFor(this.cells(), taskId, iso);

    if (expected === null) { this.dropDraft(key); return; }   // already empty — nothing to delete

    this.api.clearHours(taskId, iso, expected).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: () => {
        this.cells.update(c => patchCell(c, taskId, iso, { hours: null, rowVersion: null }));
        this.dropDraft(key);
      },
      error: (err: unknown) => {
        this.dropDraft(key);
        this.handleWriteError(err, taskId, iso, null);
      },
    });
  }

  private dropDraft(key: string): void {
    this.drafts.update(d => {
      const { [key]: _removed, ...rest } = d;
      return rest;
    });
    this.clearInvalid(key);   // the draft is gone -- an invalid mark with no draft behind it would be a lie
  }

  // ---- the three error channels --------------------------------------------------------------------

  /**
   * 400 · 409 · 404 are three DIFFERENT things and must not be collapsed into one "save failed".
   *
   *   400 `{ error }`  — a business rule said no (8h cap, holiday, >1 decimal, hours <= 0). A RETURN VALUE,
   *                      not an exception. Show the server's own sentence; it is better than anything we
   *                      could write here.
   *   409 `{ table, id, deleted, detail, message }` — someone else changed this cell. `id` is 0 (a cell has
   *                      no id — its key is the natural triple), so `detail` is the ONLY field that says
   *                      WHICH cell. Re-fetch, show both values, let the user choose.
   *   404              — the task was deleted, or is not in your team. Easy to forget, and reachable.
   */
  private handleWriteError(err: unknown, taskId: number, iso: string, attempted: number | null): void {
    if (!(err instanceof HttpErrorResponse)) {
      this.toast.show('Could not reach the server.');
      return;
    }

    switch (err.status) {
      case 400:
        this.toast.show((err.error as ValidationBody | null)?.error ?? 'That change was rejected.');
        break;

      case 404:
        this.toast.show('This task is no longer available.');
        this.refresh.next();
        break;

      case 409:
        this.openConflict(err.error as ConflictBody | null, taskId, iso, attempted);
        break;

      default:
        this.toast.show('Could not save. Please try again.');
        break;
    }
  }

  /**
   * A 409 carries no `current` value — deliberately. So re-read the week, which gives a coherent
   * (hours, version) pair, and show the user both sides.
   */
  private openConflict(
    body: ConflictBody | null, taskId: number, iso: string, attempted: number | null,
  ): void {
    this.api.getWeek(this.monday()).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: groups => {
        const monday = this.monday();
        this.applyWeek(monday, groups);

        const fresh = buildCellMap(groups, weekDays(monday));
        this.pendingWrite = { taskId, isoDate: iso, hours: attempted };
        this.conflict.set({
          detail: body?.detail ?? '(the server did not say which cell)',
          message: body?.message ?? 'Someone else changed this cell while you were editing it.',
          taskName: this.taskName(taskId),
          dayLabel: this.days().find(d => d.iso === iso)?.label ?? iso,
          yours: attempted,
          theirs: fresh[cellKey(taskId, iso)]?.hours ?? null,
        });
      },
      // We know there was a conflict but cannot show what it is. Say so rather than opening an empty dialog.
      error: () => this.toast.show(
        'Someone else changed this cell, and the week could not be reloaded to show you their value.'),
    });
  }

  /** Accept the server's value. The re-fetch already put it on screen, so this is just "close". */
  keepTheirs(): void {
    this.pendingWrite = null;
    this.conflict.set(null);
  }

  /**
   * Re-apply the user's value on top of the version the re-fetch returned.
   *
   * This works precisely BECAUSE `saveCell` reads `expectedVersionFor` from the CURRENT cell map, which the
   * re-fetch replaced — so the retry carries the fresh version and is a legitimate checked write, not a
   * check-bypassing force.
   */
  overwriteTheirs(): void {
    const pending = this.pendingWrite;
    this.pendingWrite = null;
    this.conflict.set(null);
    if (!pending) return;

    if (pending.hours === null) this.clearCell(pending.taskId, pending.isoDate);
    else this.saveCell(pending.taskId, pending.isoDate, pending.hours);
  }

  private taskName(taskId: number): string {
    for (const g of this.groups()) {
      const t = g.tasks.find(x => x.taskId === taskId);
      if (t) return t.taskName;
    }
    return `Task ${taskId}`;
  }

  // ---- smart fill ----------------------------------------------------------------------------------

  openSmartFill(): void {
    const firstTask = this.groups()[0]?.tasks[0]?.taskId ?? null;
    this.sfTaskId.set(firstTask);
    this.sfDays.set(this.days().map(d => d.iso));
    this.sfTotal.set(8);
    this.sfOpen.set(true);
  }

  closeSmartFill(): void { this.sfOpen.set(false); }

  /** Toggle a day, keeping the selection in MON..FRI order however the user clicked — `distributeHours`
   *  settles the rounding drift on the LAST day, so which day is last must be the calendar's choice, not the
   *  click order's. */
  toggleSfDay(iso: string): void {
    const order = this.days().map(d => d.iso);
    this.sfDays.update(days => {
      const next = days.includes(iso) ? days.filter(d => d !== iso) : [...days, iso];
      return order.filter(d => next.includes(d));
    });
  }

  isSfDay(iso: string): boolean { return this.sfDays().includes(iso); }

  readonly allTasks = computed(() =>
    this.groups().flatMap(g => g.tasks.map(t => ({ ...t, code: g.code }))));

  /**
   * Apply Smart Fill, then 🔴 MERGE the response into the grid.
   *
   * The response is a FLAT `TimeLogDto[]` spanning only the filled dates — NOT a week grid. Replacing state
   * from it wipes the days it did not touch off the screen; ignoring it means never learning the versions it
   * bumped (the server excludes us from our own echo, so this response is the only place they exist) and
   * 409ing against our own fill on the very next edit. `mergeSmartFill` does neither.
   */
  applySmartFill(): void {
    const taskId = this.sfTaskId();
    if (taskId == null) { this.toast.show('Pick a task to fill.'); return; }

    const tasks = buildSmartFillRequest(taskId, this.sfDays(), this.sfTotal());
    if (tasks.length === 0) { this.toast.show('Pick at least one day and enter some hours.'); return; }

    this.sfBusy.set(true);
    this.api.smartFillApply(tasks).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: (rows: TimeLogDto[]) => {
        this.sfBusy.set(false);
        this.sfOpen.set(false);
        this.cells.update(c => mergeSmartFill(c, rows));      // MERGE. Never replace.
        this.drafts.set({});
        this.invalidCells.set(new Map());   // every draft just went with it -- no stale mark can survive it
        this.toast.show(`Smart fill applied to ${rows.length} cell${rows.length === 1 ? '' : 's'}.`);
      },
      error: (err: unknown) => {
        this.sfBusy.set(false);
        if (err instanceof HttpErrorResponse && err.status === 400) {
          this.toast.show((err.error as ValidationBody | null)?.error ?? 'Smart fill was rejected.');
        } else if (err instanceof HttpErrorResponse && err.status === 404) {
          this.toast.show('One of those tasks is no longer available.');
          this.sfOpen.set(false);
          this.refresh.next();
        } else {
          this.toast.show('Smart fill failed.');
        }
      },
    });
  }

  // ---- adding a task -------------------------------------------------------------------------------

  /**
   * The dialog handed us a name. Append the task, then re-read the week so it appears as a row.
   *
   * 🔴 The group is re-read from the CURRENT `groups()`, not taken from the object captured when the dialog
   * opened. `addingTo` survives a refresh, and a refresh REPLACES every Group — so if someone else adds a task
   * to this same backlog while the dialog is open, the captured `tasks` is stale, `nextOrderIndex` computes a
   * stale maximum, and the new task TIES with theirs. That is the very tie `nextOrderIndex` exists to prevent,
   * reached through a second door. If the group has vanished entirely, fall through with the stale id and let
   * the server answer 404 — which `onAddTaskError` handles.
   */
  async onAddTaskConfirmed(name: string): Promise<void> {
    const opened = this.addingTo();
    if (!opened) return;
    this.addingTo.set(null);

    const group = this.groups().find(g => g.backlogId === opened.backlogId) ?? opened;

    try {
      await firstValueFrom(this.api.addTask(group.backlogId, name, nextOrderIndex(group.tasks)));
      this.refresh.next();                       // the new task must appear as a row
    } catch (err: unknown) {
      this.onAddTaskError(err);
    }
  }

  /**
   * 🔴 This method is why `onAddTaskConfirmed` has a `catch` at all, and it must never re-throw.
   *
   * `onAddTaskConfirmed` is an `async` method invoked from an output binding, so anything that escapes it is
   * an UNHANDLED PROMISE REJECTION — which surfaces in the console and NOWHERE THE USER CAN SEE. The row would
   * simply never appear, with no error, and they would sit there clicking Add again.
   *
   * 404 is genuinely reachable, not defensive padding: the backlog can be deleted, or the user removed from
   * its team, while this screen is open. (400 "TaskName is required." is already refused in the dialog, but
   * the server's own sentence is still better than anything invented here if it ever does arrive.)
   */
  private onAddTaskError(err: unknown): void {
    if (!(err instanceof HttpErrorResponse)) {
      this.toast.show('Could not reach the server.');
      return;
    }

    switch (err.status) {
      case 404:
        this.toast.show('That backlog is no longer available.');
        this.refresh.next();
        break;

      case 400:
        this.toast.show((err.error as ValidationBody | null)?.error ?? 'That task was rejected.');
        break;

      default:
        this.toast.show('Could not add the task. Please try again.');
        break;
    }
  }

  // ---- moving a ticket to next month ---------------------------------------------------------------

  /**
   * Push an untouched ticket into next month: a READ, then a CHECKED WRITE.
   *
   * 🔴 **The GET is mandatory, not an optimisation.** `PUT /api/backlogs/{id}` REPLACES THE WHOLE RECORD (an
   * omitted field is written as NULL, not left alone) and it demands an `expectedVersion`. All this screen
   * holds is a `Group`, built from the WEEK read — which carries the backlog's id, code, project, type and
   * assignee, and NEITHER its other fields NOR its rowVersion. There is nowhere else to get them.
   *
   * `group` is read ONLY for `backlogId` and `code`, and only BEFORE the first await — so the fact that a
   * SignalR refresh REPLACES every `Group` mid-flight cannot make this stale, and ids do not change.
   * (`onAddTaskConfirmed` has to re-look-up its group precisely because it needs `tasks`, which IS live data.
   * This one must not copy that: it needs no live data at all — the GET re-reads the authoritative record.)
   */
  async onMoveMonth(group: Group): Promise<void> {
    if (!canMoveMonth(group.code)) return;   // the SECOND of the two DEFAULT guards. Both, deliberately.

    // 🔴 Re-entrancy, and it is a CORRUPTION guard, not a politeness. A second click starts a second GET->PUT
    // chain, and on a slow link — which is exactly when a user clicks again because "nothing happened" —
    // chain 2's GET can land AFTER chain 1's PUT committed. It then reads the ALREADY-BUMPED periodMonth and
    // moves the ticket a SECOND month forward. That is the very outcome the 409 branch below refuses to reach
    // by retrying; leaving this door open while barring that one would be incoherent.
    if (this.moving()) return;
    this.moving.set(true);

    try {
      const b = await firstValueFrom(this.api.getBacklog(group.backlogId));
      const periodMonth = nextMonthFrom(b.periodMonth ?? null, this.monday());

      // toUpdateRequest carries every other field across untouched and THROWS rather than sending a version
      // it does not have (never `rowVersion!`). Its throw lands in the catch below, so it cannot escape.
      await firstValueFrom(this.api.updateBacklog(group.backlogId, toUpdateRequest(b, periodMonth)));
      this.refresh.next();                   // the ticket leaves this month's view
    } catch (err: unknown) {
      // 🔴 The GET is INSIDE the try, deliberately. Its 404 is reachable — the backlog can be deleted, or the
      // user removed from its team, while this screen is open — and an error escaping an `async` method
      // called from a (click) binding is an UNHANDLED PROMISE REJECTION: it lands in the console and NOWHERE
      // THE USER CAN SEE. They would click Move, watch nothing happen, and click again.
      this.onMoveError(err);
    } finally {
      this.moving.set(false);
    }
  }

  /**
   * All THREE declared failures, not just the conflict.
   *
   * The route declares `400 ValidationBody`, `404` and `409 ConflictBody`. Collapsing them into one "could
   * not move" throws away the only sentence the user can act on.
   *
   * 🔴 And a 409 must NOT reuse the cell conflict dialog. That dialog is a MERGE between two numbers — keep
   * theirs, or overwrite with mine. A moved backlog has nothing to merge: someone else changed the ticket,
   * and there is no reconciliation to offer. Say so, re-read, stop.
   *
   * 🔴 Above all, DO NOT SILENTLY RETRY. Their change may ITSELF have been a Move — a retry would then read
   * the already-bumped month and push the ticket TWO months forward, which is precisely the corruption this
   * whole path is careful about.
   */
  private onMoveError(err: unknown): void {
    if (!(err instanceof HttpErrorResponse)) {
      this.toast.show('Could not reach the server.');
      return;
    }

    switch (err.status) {
      case 400:
        this.toast.show((err.error as ValidationBody | null)?.error ?? 'That change was rejected.');
        break;

      case 404:
        this.toast.show('This ticket is no longer available.');
        this.refresh.next();
        break;

      case 409:
        this.toast.show(
          'Someone else just changed this ticket. Reloaded — try again if you still want to move it.');
        this.refresh.next();
        break;

      default:
        this.toast.show('Could not move this ticket. Please try again.');
        break;
    }
  }

  // ---- drag to reorder -----------------------------------------------------------------------------

  /**
   * A row was dropped. Rewrite the group's order on the server, then re-read the week.
   *
   * 🔴 **`reorderPlan` rewrites EVERY row, and that is a FIX, not caution.** `SetActiveAsync` soft-deletes by
   * setting `is_active = 0` and LEAVES `order_index` untouched, while the read is
   * `WHERE is_active = 1 ORDER BY order_index` — so one delete leaves the survivors at 1,2,3: a GAP. A
   * windowed write (only the rows between the two positions, at absolute index `lo + i`) then produces a TIE,
   * and `ORDER BY` with a tie is ARBITRARY: the order silently scrambles on the next reload. Rewriting every
   * row renormalises the gap on every drag, which is exactly what WPF does. Do not "optimise" it back.
   *
   * The writes are BUMP-ONLY — `setTaskOrder` sends `{ orderIndex }` and no version, deliberately. See it.
   *
   * 🔴 **The container guard, traced through CDK's source rather than guessed:** `dropped` fires ONLY on the
   * DESTINATION list. A drag *within* a group makes that group both source and destination, so
   * `previousContainer === container` and this proceeds. A drag *to the trash* fires only the TRASH's handler
   * — so this method can never see a cross-list index, and the guard below is the assertion of that, not a
   * branch that does real work. Groups connect only to the trash, never to each other: a task cannot be
   * dragged into a different backlog, which `PUT /api/tasks/{id}/order` could not express anyway (it takes an
   * `orderIndex` and no `backlogId`).
   */
  async onDrop(group: Group, event: CdkDragDrop<readonly TaskRow[]>): Promise<void> {
    if (event.previousContainer !== event.container) return;   // came from elsewhere — not a reorder

    // 🔴 Re-entrancy, and — as with `moving` — it is a CORRUPTION guard, not a politeness. CDK does NOT move
    // the row for us (we never call `moveItemInArray`; the re-fetch is what re-renders it), so between the
    // drop and the refresh landing the row SNAPS BACK to where it started. On a slow link that reads as "the
    // drag didn't work", which is precisely when a user drags again — and a second batch computed from the
    // still-stale `group.tasks` would interleave with the first, leaving the group's `order_index` values in
    // an order neither drag asked for.
    if (this.reordering()) return;

    const writes = reorderPlan(group.tasks, event.previousIndex, event.currentIndex);
    if (!writes.length) return;                                // dropped where it started

    this.reordering.set(true);
    try {
      for (const w of writes) await firstValueFrom(this.api.setTaskOrder(w.taskId, w.orderIndex));
      this.refresh.next();
    } catch (err: unknown) {
      this.onReorderError(err);
    } finally {
      this.reordering.set(false);
    }
  }

  /**
   * 🔴 Why `onDrop` has a `catch` at all, and why it must never re-throw.
   *
   * `onDrop` is an `async` method bound to `(cdkDropListDropped)`, so anything escaping it is an UNHANDLED
   * PROMISE REJECTION — console-only, and NOWHERE THE USER CAN SEE. They would drag a row, watch it snap back,
   * and have no idea the write failed. (`onAddTaskError` and `onMoveError` exist for the identical reason.)
   *
   * And a failure here is not merely a no-op: the writes are sequential, so a 404 on write 3 of 5 leaves the
   * group HALF-renormalised on the server. Re-reading is therefore mandatory, not tidy — it is the only way
   * the screen stops showing an order the server does not have. A 404 is genuinely reachable: another user can
   * delete one of these tasks (M8.5 ships exactly that) while the drag is in flight.
   */
  private onReorderError(err: unknown): void {
    if (!(err instanceof HttpErrorResponse)) {
      this.toast.show('Could not reach the server.');
      this.refresh.next();
      return;
    }

    this.toast.show(err.status === 404
      ? 'One of those tasks is no longer available. Reloaded.'
      : 'Could not save the new order. Reloaded — please try again.');
    this.refresh.next();
  }

  // ---- drag to trash (a CONFIRMED soft delete) -----------------------------------------------------

  /**
   * A row was dropped on the trash. ARM the delete and ask — `confirmDelete` is what actually writes.
   *
   * 🔴 **The trash is reached by STRING ID, not by a template ref**, and the whole feature is silently dead if
   * that is got wrong. Each group carries `[cdkDropListConnectedTo]="['trash']"`; CDK resolves that string
   * against its static registry on EVERY drag start (`CdkDropList._dropLists.find(l => l.id === drop)`,
   * drag-drop.mjs:3510-3546) and FILTERS OUT anything it cannot find. So the trash must carry `id="trash"` —
   * a plain attribute, which Angular maps onto `CdkDropList`'s `id` INPUT. Write `#trash="cdkDropList"`
   * instead and the drop list keeps its auto-generated `cdk-drop-list-N` id, the string never resolves, the
   * row snaps back, `container === previousContainer`, and THIS METHOD IS NEVER CALLED AT ALL. The component
   * spec drives CDK's own `beforeStarted` handler to prove the resolution really happens; a green build
   * proves nothing here.
   *
   * `dropped` fires ONLY on the DESTINATION list, so this handler sees only drags that ARRIVED at the trash —
   * and the guard below is the assertion of that, not a branch that does real work.
   */
  onTrash(event: CdkDragDrop<readonly TaskRow[], readonly TaskRow[], TaskRow>): void {
    if (event.previousContainer === event.container) return;   // dropped on itself — not a delete

    // `[cdkDragData]` on the row is the ONLY place this value comes from. Without it `data` is `undefined` and
    // `row.taskId` throws — and only at the moment a user first tries to delete something.
    const row = event.item.data;

    // 🔴 ARMS the delete. It does NOT write, and there is no `deleting` guard here — the guard belongs on the
    // WRITE, and re-opening a dialog is harmless where re-firing a PUT is not. Only the two fields the dialog
    // needs are kept: `pendingDelete` is a snapshot of the row the user actually dropped, so a refresh landing
    // while the dialog is open cannot re-point it at a different task.
    this.pendingDelete.set({ taskId: row.taskId, taskName: row.taskName });
  }

  /** They changed their mind. Nothing was ever written, so there is nothing to undo. */
  cancelDelete(): void {
    this.pendingDelete.set(null);
  }

  /**
   * They confirmed. THIS is the write — the only one on this path.
   *
   * The dialog closes FIRST: the decision is made, and leaving it up over an in-flight PUT would just be a
   * modal the user has to watch. (`onAddTaskConfirmed` clears `addingTo` the same way, for the same reason.)
   */
  async confirmDelete(): Promise<void> {
    const pending = this.pendingDelete();
    if (!pending) return;

    // 🔴 Re-entrancy, and — as with `moving` and `reordering` — the reason is that CDK does NOT remove the row
    // for us. We never call `transferArrayItem`; the re-fetch is what makes the row disappear. So between the
    // confirm and the refresh landing, THE ROW SNAPS BACK to where it was. On a slow link that reads as "it
    // didn't work", which is exactly when a user drags it to the bin again — and a second delete of the same
    // task would fire a second write and a second re-fetch for nothing.
    //
    // 🔴 The guard is checked BEFORE the dialog is cleared, deliberately. Clearing first would close the dialog
    // on a confirm that was then silently refused — the user would see the dialog vanish and the row stay, and
    // conclude the delete worked. The dialog's own `[busy]="deleting()"` disables the button so this is not
    // normally reachable from the UI at all; this is the second of the two guards, as with `canMoveMonth`.
    if (this.deleting()) return;

    this.pendingDelete.set(null);
    this.deleting.set(true);
    try {
      await firstValueFrom(this.api.setTaskActive(pending.taskId, false));  // SOFT — `is_active = 0`, nothing lost
      this.refresh.next();                                                  // the row leaves the grid
    } catch (err: unknown) {
      this.onTrashError(err);
    } finally {
      this.deleting.set(false);
    }
  }

  /**
   * 🔴 Why `confirmDelete` has a `catch` at all, and why it must never re-throw.
   *
   * `confirmDelete` is an `async` method bound to the dialog's `(confirm)` output, so anything escaping it is
   * an UNHANDLED PROMISE REJECTION — console-only, and NOWHERE THE USER CAN SEE. They would confirm the delete,
   * watch the dialog close and the row stay exactly where it was, be told nothing, and try again.
   * (`onAddTaskError`, `onMoveError` and `onReorderError` all exist for this identical reason.)
   *
   * 🔴 Deliberately NOT `onMoveError`'s three-way switch. `PUT /api/tasks/{id}/active` declares exactly two
   * outcomes — `204 NoContent` and `404 NotFound`. It is bump-only (no version -> no 409) and its body is a
   * lone bool (nothing to reject -> no 400). Handlers for those would be dead code.
   */
  private onTrashError(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 404) {
      // Reachable, and it means someone ELSE already deleted this task (or we lost access to its team). The
      // server is right and our screen is stale, so this one MUST re-read.
      this.toast.show('This task is no longer available.');
      this.refresh.next();
      return;
    }

    // 🔴 And here it does NOT re-read — a considered difference from `onReorderError`, which re-reads on every
    // error. A reorder is a SEQUENTIAL BATCH of N writes, so a failure on write 3 of 5 leaves the group
    // HALF-renormalised on the server and the screen showing an order that does not exist. A delete is ONE
    // write: if it failed, the server is unchanged, and CDK has already snapped the row back — so the screen
    // ALREADY matches the server. Re-reading would be a wasted round-trip that fixes nothing.
    this.toast.show('Could not delete this task. Please try again.');
  }

  // ---- misc ----------------------------------------------------------------------------------------

  toggleGroup(backlogId: number): void {
    this.collapsed.update(c => ({ ...c, [backlogId]: !c[backlogId] }));
  }

  anyOpen(): boolean { return this.groups().some(g => this.isOpen(g.backlogId)); }

  collapseAll(): void {
    if (this.anyOpen()) {
      const c: Record<number, boolean> = {};
      this.groups().forEach(g => (c[g.backlogId] = true));
      this.collapsed.set(c);
    } else {
      this.collapsed.set({});
    }
  }

  reload(): void { this.refresh.next(); }
}

function round1(n: number): number { return Math.round(n * 10) / 10; }

/** Pull a human sentence out of whatever came back. */
function readError(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as ValidationBody | null;
    if (body?.error) return body.error;
  }
  return fallback;
}
