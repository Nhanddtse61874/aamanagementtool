import { TimeLogDto, WeekBacklogGroup } from '../../api/models';
import { cellKey } from '../../core/cell-key';
import { WeekDay } from './week';

/**
 * The Log Work grid's SERVER TRUTH: what the API last told us each cell holds, and at what version.
 *
 * Everything correctness-critical in this wave is a pure function over this map, which is why it lives here
 * and not in the component: the merge below is the single seam most likely to lose a user's data silently,
 * and a pure function is the only kind you can actually pin down with a test.
 */

/** One cell, exactly as the server last reported it. */
export interface Cell {
  /** `null` = the cell is EMPTY. Never `0` -- the API rejects `hours <= 0` with a 400 ("Hours must be
   *  greater than 0"), so a zero is not a value this map can legitimately hold. VERIFIED against the running
   *  API, not assumed. */
  readonly hours: number | null;
  /** `null` = NO VERSION, which means (and only means) THE CELL IS EMPTY. See `expectedVersionFor`. */
  readonly rowVersion: number | null;
}

/** Keyed by `cellKey(taskId, isoDate)` -- by IDENTITY, never by screen position. */
export type CellMap = Readonly<Record<string, Cell>>;

/** A task row, flattened out of the week read so the template never indexes into nested arrays. */
export interface TaskRow {
  readonly taskId: number;
  readonly taskName: string;
  /**
   * The row's `order_index` ON THE SERVER — not its position on screen, and the two are NOT interchangeable.
   *
   * `SetActiveAsync` soft-deletes by setting `is_active = 0` and LEAVES `order_index` alone, while the read is
   * `WHERE is_active = 1 ORDER BY order_index`. So after one delete the survivors sit at 1,2,3 while rendering
   * at positions 0,1,2 — a GAP. Anything that appends must append past the highest INDEX, never past the
   * count. See `nextOrderIndex`.
   *
   * The wire has always carried it (`WeekRow.orderIndex`, and `TimeLogService` both populates it from
   * `t.OrderIndex` and sorts by it); `buildGroups` simply used to drop it on the floor.
   */
  readonly orderIndex: number;
}

/** A backlog group, with its task rows. */
export interface Group {
  readonly backlogId: number;
  readonly code: string;
  readonly project: string;
  readonly type: string | null;
  readonly assignee: string | null;
  readonly tasks: readonly TaskRow[];
}

/**
 * 🔴 THE `expectedVersion` RULE. The whole optimistic-concurrency mechanism rests on this one function.
 *
 * `expectedVersion` is `long?` on the wire and `null` is A CLAIM, not a placeholder:
 *
 *   - `null`          asserts "I believe this cell is EMPTY."   -> the server 409s if a row exists.
 *   - a real number   asserts "I last saw version N."           -> the server 409s if it has moved on.
 *
 * So there is no honest way to say "I don't know", and there must not be: every OTHER value is a lie that
 * either 409s spuriously or SILENTLY OVERWRITES another user. In particular:
 *
 *   - NEVER `0`. Under the five-case table zero is a DIFFERENT assertion, not a safe default.
 *   - NEVER `rowVersion!`. The generated `WeekCell.rowVersion` is `number | null | undefined` because
 *     Swashbuckle does not emit `required` for C# records -- three TypeScript states for two wire states.
 *     `?? null` collapses `undefined` and `null` onto the one thing they both actually mean here: NO
 *     VERSION, therefore THE CELL IS EMPTY. Narrowing with `!` instead would reintroduce, through
 *     TypeScript's front door, the exact bug M8.4/W0 was created to fix.
 */
export function expectedVersionFor(cells: CellMap, taskId: number, isoDate: string): number | null {
  return cells[cellKey(taskId, isoDate)]?.rowVersion ?? null;
}

/**
 * Flatten `GET /api/timesheet/week` into the cell map.
 *
 * The five slots are POSITIONAL on the wire (`WeekRow { mon, tue, wed, thu, fri }` -- no dates anywhere), so
 * the caller's day axis is what gives them meaning. `days` must be the five days of the very Monday that was
 * requested; pass a different week's axis and every cell is keyed to the wrong date.
 */
export function buildCellMap(groups: WeekBacklogGroup[], days: readonly WeekDay[]): CellMap {
  const cells: Record<string, Cell> = {};

  for (const group of groups) {
    for (const task of group.tasks ?? []) {
      // Bound to a const, not asserted with `!`: a `!` across the closure below would compile just as well
      // and mean nothing, and `!` is precisely the habit that lets an absent value through as if it were
      // present. If there is no id there is no cell to key.
      const taskId = task.taskId;
      if (taskId == null) continue;

      const slots = [task.mon, task.tue, task.wed, task.thu, task.fri];

      slots.forEach((slot, i) => {
        const day = days[i];
        if (!day) return;
        cells[cellKey(taskId, day.iso)] = {
          hours: slot?.hours ?? null,
          rowVersion: slot?.rowVersion ?? null,
        };
      });
    }
  }

  return cells;
}

/** The rendering shape of the week read. Kept separate from the cell map so hours and structure can move
 *  independently -- a Smart Fill patches cells without disturbing the grouping. */
export function buildGroups(groups: WeekBacklogGroup[]): Group[] {
  return groups.map(g => ({
    backlogId: g.backlogId ?? 0,
    code: g.backlogCode ?? '',
    project: g.project ?? '',
    type: g.type ?? null,
    assignee: g.assigneeName ?? null,
    // flatMap, not filter+map: `filter` cannot narrow the type for the `map` that follows it, so that spelling
    // needs a `!` to compile. A task with no id could never be written to anyway — drop it.
    tasks: (g.tasks ?? []).flatMap(t =>
      t.taskId == null
        ? []
        : [{ taskId: t.taskId, taskName: t.taskName ?? '', orderIndex: t.orderIndex ?? 0 }]),
  }));
}

/**
 * The next free `orderIndex` in a group — where an appended task must go.
 *
 * 🔴 NOT `tasks.length`. WPF appends at `Tasks.Count` (`RequestGroupVm.cs:63`) and that is a bug we cannot
 * ship, because a soft delete leaves a GAP: `SetActiveAsync` sets `is_active = 0` and leaves `order_index`
 * alone, so three survivors of a four-row group sit at 1,2,3 while `length` is 3. Appending at `length` would
 * write the new task at 3 — a TIE with the existing 3 — and `ORDER BY order_index` with a tie is arbitrary.
 * "Add task" would simply not append, on the wholly ordinary sequence *delete, then add*.
 *
 * Appending past the highest INDEX cannot tie. (A later drag renormalises the gap anyway — see
 * `reorderPlan` — but a user should not have to drag their way out of a misplaced row.)
 */
export function nextOrderIndex(tasks: readonly TaskRow[]): number {
  return tasks.length ? Math.max(...tasks.map(t => t.orderIndex)) + 1 : 0;
}

/**
 * 🔴 MERGE `POST /api/smartfill/apply` INTO THE GRID. NEVER REPLACE THE GRID WITH IT.
 *
 * The response is a FLAT `TimeLogDto[]`, not a week grid. It is
 * `GetByUserAndRangeAsync(userId, min(filledDates), max(filledDates))` -- so it spans only the dates that
 * were actually filled, it is ungrouped, and (VERIFIED against the running API) it contains EVERY log the
 * user has in that range, not just the rows Smart Fill wrote: filling one task came back with five, because
 * four other tasks already had hours on those days.
 *
 * Three ways to get this wrong, and all three are silent:
 *
 *   1. REPLACE the map with it, and the days the fill did not touch VANISH FROM THE SCREEN. Fill Wed-Thu and
 *      Mon/Tue/Fri are simply gone -- and a flat list cannot reconstruct the backlog grouping anyway.
 *   2. IGNORE it, and you never learn the new versions. Smart Fill bumps `row_version` on every cell it
 *      writes, and the server EXCLUDES YOU FROM YOUR OWN SignalR echo -- so this response is the one and only
 *      place you can learn them. Drop it and your next inline edit sends a stale version and gets "someone
 *      else changed this" -- about your OWN Smart Fill, on the happy path, every single time.
 *   3. Key it by anything but `(taskId, workDate)`. `workDate` arrives as a bare ISO date (`"2026-07-15"` --
 *      VERIFIED on the wire; a `DateOnly` does NOT serialise with a time component), so it keys directly
 *      against the day axis with no parsing and no timezone conversion.
 *
 * So: PATCH the returned `(taskId, workDate)` pairs, and leave every other cell EXACTLY as it was -- hours
 * and version both.
 */
export function mergeSmartFill(cells: CellMap, rows: readonly TimeLogDto[]): CellMap {
  const merged: Record<string, Cell> = { ...cells };

  for (const row of rows) {
    // A row we cannot key is a row we cannot place. Dropping it is the only safe option: guessing a position
    // is how hours land on the wrong task.
    if (row.taskId == null || row.workDate == null) continue;

    merged[cellKey(row.taskId, row.workDate)] = {
      hours: row.hours ?? null,
      rowVersion: row.rowVersion ?? null,
    };
  }

  return merged;
}

/** Patch ONE cell after a successful write, with the version THE WRITE RETURNED.
 *
 *  Never re-read the version instead. A read-back is racy: between the write committing and the re-read,
 *  another client can write -- you would then hold THEIR version with YOUR data, and your next save would
 *  pass the check and silently overwrite them. (The server deleted `GetRowVersionAsync` for this reason.) */
export function patchCell(cells: CellMap, taskId: number, isoDate: string, cell: Cell): CellMap {
  return { ...cells, [cellKey(taskId, isoDate)]: cell };
}

/** Why a cell's text was refused. */
export type InvalidReason = 'not-a-number' | 'not-positive' | 'over-cap' | 'too-precise';

/**
 * The total, three-way answer for what the user typed into a cell.
 *
 * 🔴 `parseHours` used to return `number | null`, collapsing "the box is empty, clear the cell" and
 * "this text is garbage" onto the SAME `null` -- and the caller turned every `null` into a DELETE. That is
 * BUG-1 (spec §3): typing `abc` over a cell holding 4 hours silently destroyed the 4. `clear` and `invalid`
 * are different intents and must stay different types all the way to the caller.
 */
export type CellInput =
  | { kind: 'clear' }
  | { kind: 'value'; hours: number }
  | { kind: 'invalid'; reason: InvalidReason };

/**
 * Read what the user typed. Rules, in order (spec §5.2):
 *
 *   blank/whitespace           -> clear                 (unchanged from the old `parseHours`)
 *   not a finite number        -> invalid 'not-a-number'
 *   <= 0 (incl. '0', '-1')     -> invalid 'not-positive'
 *   > 8                        -> invalid 'over-cap'
 *   more than 1 decimal place  -> invalid 'too-precise'
 *   otherwise                  -> value
 *
 * 🔴 `'0'` used to be sent to the server as a real value (spec §5.3 -- a documented decision, reversed on
 * purpose here). WPF's `TimesheetRowVm.Validate` rejects `hours <= 0` client-side; the web app now matches
 * it instead of round-tripping to the API to learn the same thing a beat later.
 *
 * Decimal-place count is read off the TRIMMED STRING, not the parsed `number` -- `Number` gives no reliable
 * way to recover "how many digits were typed after the point" once it has rounded/normalised the value.
 */
export function readCell(text: string): CellInput {
  const trimmed = text.trim();
  if (trimmed === '') return { kind: 'clear' };

  const n = Number(trimmed);
  if (!Number.isFinite(n)) return { kind: 'invalid', reason: 'not-a-number' };
  if (n <= 0) return { kind: 'invalid', reason: 'not-positive' };
  if (n > 8) return { kind: 'invalid', reason: 'over-cap' };

  const decimalPlaces = trimmed.includes('.') ? trimmed.split('.')[1].length : 0;
  if (decimalPlaces > 1) return { kind: 'invalid', reason: 'too-precise' };

  return { kind: 'value', hours: n };
}

/** How a cell's hours are shown in its input box. `null` -> empty string, not `"0"`. */
export function formatHours(hours: number | null): string {
  return hours == null ? '' : `${hours}`;
}
