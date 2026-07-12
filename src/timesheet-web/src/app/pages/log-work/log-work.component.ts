import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subject, catchError, map, merge, switchMap } from 'rxjs';

import { ConflictBody, TimeLogDto, ValidationBody, WeekBacklogGroup } from '../../api/models';
import { cellKey } from '../../core/cell-key';
import { CellConflict, ConflictDialogComponent } from '../../core/conflict-dialog/conflict-dialog.component';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import {
  CellMap, Group, buildCellMap, buildGroups, expectedVersionFor, formatHours, mergeSmartFill, parseHours,
  patchCell,
} from './grid-state';
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
  imports: [CommonModule, FormsModule, ConflictDialogComponent],
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
  /** What the user is TYPING, before it is committed. Cleared per cell once the write resolves. */
  private readonly drafts = signal<Record<string, string>>({});
  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly collapsed = signal<Record<number, boolean>>({});
  readonly conflict = signal<CellConflict | null>(null);
  private pendingWrite: PendingWrite | null = null;

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
  }

  private applyWeek(monday: string, groups: WeekBacklogGroup[]): void {
    this.loading.set(false);
    this.loadError.set(null);
    this.groups.set(buildGroups(groups));
    // The axis MUST be the axis of the week that was actually fetched, not whatever `monday()` says by the
    // time the response lands — otherwise a slow response for last week keys its cells onto this week.
    this.cells.set(buildCellMap(groups, weekDays(monday)));
    this.drafts.set({});
  }

  // ---- reading the grid ----------------------------------------------------------------------------

  /** What goes in the input box: the draft if the user is typing, otherwise the server's value. */
  cellText(taskId: number, iso: string): string {
    const key = cellKey(taskId, iso);
    return this.drafts()[key] ?? formatHours(this.cells()[key]?.hours ?? null);
  }

  private cellHours(taskId: number, iso: string): number {
    const text = this.cellText(taskId, iso);
    return parseHours(text) ?? 0;
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

    const wanted = parseHours(draft);
    const current = this.cells()[key]?.hours ?? null;

    if (wanted === current) { this.dropDraft(key); return; }  // typed it back to what it already was

    if (wanted === null) this.clearCell(taskId, iso);
    else this.saveCell(taskId, iso, wanted);
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
