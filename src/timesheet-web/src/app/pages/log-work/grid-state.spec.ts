import { TimeLogDto, WeekBacklogGroup } from '../../api/models';
import { cellKey } from '../../core/cell-key';
import {
  buildCellMap, buildGroups, CellMap, expectedVersionFor, formatHours, mergeSmartFill, nextOrderIndex,
  parseHours, patchCell,
} from './grid-state';
import { weekDays } from './week';

const MONDAY = '2026-07-13';
const DAYS = weekDays(MONDAY);          // 13th .. 17th July 2026
const [MON, TUE, WED, THU, FRI] = DAYS.map(d => d.iso);

/** The week read, as the API actually shapes it: five POSITIONAL slots, no dates, every slot a
 *  `{ hours, rowVersion }` object whose halves are BOTH null when the cell is empty. */
function weekRead(): WeekBacklogGroup[] {
  return [{
    backlogId: 2, backlogCode: 'ARCS-1001', project: 'ARCS', type: 'Implement', assigneeName: 'Nhan',
    tasks: [{
      taskId: 7, backlogCode: 'ARCS-1001', taskName: 'Design schema', orderIndex: 0,
      mon: { hours: 4, rowVersion: 11 },
      tue: { hours: 2, rowVersion: 12 },
      wed: { hours: null, rowVersion: null },
      thu: { hours: null, rowVersion: null },
      fri: { hours: 1, rowVersion: 13 },
    }],
  }];
}

describe('buildCellMap', () => {
  it('keys every cell by (taskId, isoDate) and carries the per-cell rowVersion', () => {
    const cells = buildCellMap(weekRead(), DAYS);

    expect(cells[cellKey(7, MON)]).toEqual({ hours: 4, rowVersion: 11 });
    expect(cells[cellKey(7, TUE)]).toEqual({ hours: 2, rowVersion: 12 });
    expect(cells[cellKey(7, FRI)]).toEqual({ hours: 1, rowVersion: 13 });
  });

  it('records an empty cell as BOTH halves null -- never 0', () => {
    const cells = buildCellMap(weekRead(), DAYS);

    expect(cells[cellKey(7, WED)]).toEqual({ hours: null, rowVersion: null });
    expect(cells[cellKey(7, WED)].rowVersion).not.toBe(0);
  });

  // Swashbuckle does not emit `required` for C# records, so the generated WeekCell is
  // `number | null | undefined` -- three TS states for two wire states. Both absent forms must collapse to
  // null, or a cell with an `undefined` version would send `undefined` as expectedVersion.
  it('collapses an ABSENT slot (undefined) to the same "empty" as an explicit null', () => {
    const groups: WeekBacklogGroup[] = [{
      backlogId: 1, backlogCode: 'B', project: 'P',
      tasks: [{ taskId: 9, taskName: 'T', mon: {} }],   // mon present but both halves undefined; tue..fri absent
    }];

    const cells = buildCellMap(groups, DAYS);

    expect(cells[cellKey(9, MON)]).toEqual({ hours: null, rowVersion: null });
    expect(cells[cellKey(9, TUE)]).toEqual({ hours: null, rowVersion: null });
  });
});

// 🔴 The rule the entire optimistic-concurrency mechanism rests on.
describe('expectedVersionFor', () => {
  it('is NULL for an empty cell -- the claim "I believe this cell is empty"', () => {
    const cells = buildCellMap(weekRead(), DAYS);
    expect(expectedVersionFor(cells, 7, WED)).toBeNull();
  });

  it('is the REAL rowVersion for a populated cell -- never 0, never forced', () => {
    const cells = buildCellMap(weekRead(), DAYS);

    expect(expectedVersionFor(cells, 7, MON)).toBe(11);
    expect(expectedVersionFor(cells, 7, TUE)).toBe(12);
  });

  it('never invents 0 for a cell it has never seen', () => {
    const cells = buildCellMap(weekRead(), DAYS);

    // A task/date the grid has no knowledge of at all: still null ("I believe it is empty"), NOT 0.
    // 0 is a different assertion under the five-case table and would either 409 spuriously or overwrite.
    expect(expectedVersionFor(cells, 999, MON)).toBeNull();
    expect(expectedVersionFor(cells, 999, MON)).not.toBe(0);
  });
});

// 🔴 THE seam most likely to lose a user's data silently. This is the test the plan says would have caught
// the "replace the grid" bug on its own.
describe('mergeSmartFill', () => {
  it('PATCHES only the returned (taskId, workDate) pairs and leaves every other cell untouched', () => {
    const before = buildCellMap(weekRead(), DAYS);

    // Smart Fill filled WED and THU only. Mon/Tue/Fri must survive -- hours AND versions.
    const filled: TimeLogDto[] = [
      { id: 86, userId: 1, taskId: 7, workDate: WED, hours: 3, rowVersion: 1 },
      { id: 87, userId: 1, taskId: 7, workDate: THU, hours: 2, rowVersion: 1 },
    ];

    const after = mergeSmartFill(before, filled);

    // the filled days are patched, with their NEW versions
    expect(after[cellKey(7, WED)]).toEqual({ hours: 3, rowVersion: 1 });
    expect(after[cellKey(7, THU)]).toEqual({ hours: 2, rowVersion: 1 });

    // ...and the untouched days are STILL THERE. A `replace` would have wiped these off the screen.
    expect(after[cellKey(7, MON)]).toEqual({ hours: 4, rowVersion: 11 });
    expect(after[cellKey(7, TUE)]).toEqual({ hours: 2, rowVersion: 12 });
    expect(after[cellKey(7, FRI)]).toEqual({ hours: 1, rowVersion: 13 });
  });

  it('learns the BUMPED rowVersion of a cell Smart Fill overwrote -- the next edit must not 409', () => {
    const before = buildCellMap(weekRead(), DAYS);
    expect(expectedVersionFor(before, 7, MON)).toBe(11);

    // Smart Fill rewrote Monday: the server bumped 11 -> 12 and told us so in the response body. This is the
    // ONLY place we can learn it, because the server excludes us from our own SignalR echo.
    const after = mergeSmartFill(before, [
      { id: 1, userId: 1, taskId: 7, workDate: MON, hours: 6, rowVersion: 12 },
    ]);

    expect(expectedVersionFor(after, 7, MON)).toBe(12);   // stale 11 would have 409'd against our own fill
  });

  // VERIFIED against the running API: asking Smart Fill to fill ONE task came back with FIVE rows, because
  // GetByUserAndRangeAsync returns every log the user has in that range -- not just the ones it wrote.
  it('merges rows for tasks it was never asked to fill (the response spans the whole date range)', () => {
    const before = buildCellMap(weekRead(), DAYS);

    const after = mergeSmartFill(before, [
      { id: 1, userId: 1, taskId: 7, workDate: WED, hours: 3, rowVersion: 1 },   // the task we filled
      { id: 2, userId: 1, taskId: 42, workDate: WED, hours: 0.5, rowVersion: 4 }, // a task we did NOT
    ]);

    expect(after[cellKey(42, WED)]).toEqual({ hours: 0.5, rowVersion: 4 });
    expect(after[cellKey(7, MON)]).toEqual({ hours: 4, rowVersion: 11 });        // still untouched
  });

  it('does not mutate the map it was given', () => {
    const before = buildCellMap(weekRead(), DAYS);

    mergeSmartFill(before, [{ id: 1, userId: 1, taskId: 7, workDate: WED, hours: 3, rowVersion: 1 }]);

    expect(before[cellKey(7, WED)]).toEqual({ hours: null, rowVersion: null });
  });

  it('drops a row it cannot key rather than guessing where it goes', () => {
    const before = buildCellMap(weekRead(), DAYS);
    const after = mergeSmartFill(before, [{ id: 1, userId: 1, hours: 3, rowVersion: 1 }]);   // no taskId/date

    expect(after).toEqual(before);
  });

  it('an empty response changes nothing', () => {
    const before = buildCellMap(weekRead(), DAYS);
    expect(mergeSmartFill(before, [])).toEqual(before);
  });
});

describe('patchCell', () => {
  it('stores the version the WRITE RETURNED, touching no other cell', () => {
    const before = buildCellMap(weekRead(), DAYS);

    const after = patchCell(before, 7, MON, { hours: 6, rowVersion: 12 });

    expect(expectedVersionFor(after, 7, MON)).toBe(12);
    expect(after[cellKey(7, TUE)]).toEqual({ hours: 2, rowVersion: 12 });   // untouched
  });

  it('clears a cell back to both-halves-null after a DELETE', () => {
    const before = buildCellMap(weekRead(), DAYS);

    const after = patchCell(before, 7, MON, { hours: null, rowVersion: null });

    expect(after[cellKey(7, MON)]).toEqual({ hours: null, rowVersion: null });
    expect(expectedVersionFor(after, 7, MON)).toBeNull();
  });
});

describe('parseHours', () => {
  // The API rejects hours <= 0 with 400 "Hours must be greater than 0." (VERIFIED on the wire), so an empty
  // box cannot mean "save 0" -- it means CLEAR, which is a DELETE.
  it('reads a blank box as null -- meaning CLEAR, not zero', () => {
    expect(parseHours('')).toBeNull();
    expect(parseHours('   ')).toBeNull();
  });

  it('reads a number', () => {
    expect(parseHours('4')).toBe(4);
    expect(parseHours('4.5')).toBe(4.5);
  });

  it('reads gibberish as null rather than sending it', () => {
    expect(parseHours('abc')).toBeNull();
  });

  // 0 is NOT silently swallowed into "clear": it is a real value the user typed, and the API has a specific
  // message for it. Sending it and showing that message is more honest than guessing they meant to delete.
  it('keeps an explicit 0 as 0, so the API can say what it thinks of it', () => {
    expect(parseHours('0')).toBe(0);
  });
});

describe('formatHours', () => {
  it('shows an empty cell as an empty box, never "0"', () => {
    expect(formatHours(null)).toBe('');
    expect(formatHours(4)).toBe('4');
    expect(formatHours(4.5)).toBe('4.5');
  });
});

describe('buildGroups', () => {
  it('keeps the backlog grouping the flat Smart Fill response could never reconstruct', () => {
    const groups = buildGroups(weekRead());

    expect(groups.length).toBe(1);
    expect(groups[0].code).toBe('ARCS-1001');
    expect(groups[0].project).toBe('ARCS');
    expect(groups[0].assignee).toBe('Nhan');
    // orderIndex is carried off the wire (WeekRow.orderIndex) -- see the buildGroups/nextOrderIndex test below.
    expect(groups[0].tasks).toEqual([{ taskId: 7, taskName: 'Design schema', orderIndex: 0 }]);
  });

  it('drops a task with no id -- it could never be written to anyway', () => {
    const groups = buildGroups([{ backlogId: 1, backlogCode: 'B', project: 'P', tasks: [{ taskName: 'ghost' }] }]);
    expect(groups[0].tasks).toEqual([]);
  });
});

// 🔴 Where "Add task" appends -- and the tie it must not create.
describe('nextOrderIndex', () => {
  it('appends PAST the highest index, not past the count -- a soft delete leaves a gap', () => {
    // Straight off the wire, so this pins buildGroups' carry-through TOO, and that is deliberate:
    // `orderIndex: 0` hardcoded in buildGroups (a plausible slip for `t.orderIndex ?? 0`) would leave every
    // other test in this file green while making nextOrderIndex return 1 forever -- reintroducing the very
    // tie it exists to prevent. Hand-made TaskRow literals could never catch that; the real wire shape can.
    //
    // A was soft-deleted. SetActiveAsync sets is_active = 0 and LEAVES order_index alone, so B, C, D survive
    // at order_index 1, 2, 3 -- while `tasks.length` is 3.
    const [group] = buildGroups([{
      backlogId: 1, backlogCode: 'B', project: 'P',
      tasks: [
        { taskId: 20, taskName: 'B', orderIndex: 1 },
        { taskId: 30, taskName: 'C', orderIndex: 2 },
        { taskId: 40, taskName: 'D', orderIndex: 3 },
      ],
    }]);

    expect(group.tasks.map(t => t.orderIndex)).toEqual([1, 2, 3]);   // CARRIED off the wire, not defaulted
    expect(nextOrderIndex(group.tasks)).toBe(4);                     // NOT 3 (== length), which would tie with D
    expect(nextOrderIndex([])).toBe(0);
  });
});

describe('the cell map survives a reorder (the bug the whole re-key exists to prevent)', () => {
  it('addresses the same cell after the tasks are re-sorted on screen', () => {
    const cells: CellMap = buildCellMap(weekRead(), DAYS);

    // The vendored grid keyed hours by `${groupIndex}-${taskIndex}-${dayIndex}`. Under a re-sort, index 0
    // becomes a DIFFERENT task and every value silently re-points. Identity keys cannot: task 7's Monday is
    // task 7's Monday no matter where it is drawn.
    expect(cells[cellKey(7, MON)].hours).toBe(4);
    expect(expectedVersionFor(cells, 7, MON)).toBe(11);
  });
});
