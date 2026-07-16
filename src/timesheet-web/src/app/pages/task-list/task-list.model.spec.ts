import { HttpErrorResponse } from '@angular/common/http';

import { BacklogDto, TaskItemDto, TaskListRowDto } from '../../api/models';
import {
  SCHEDULE_LATE, SCHEDULE_NORMAL, SCHEDULE_WARNING,
  buildChips, groupRows, isDone, messageOf, nextPeriod, parseProgress, pickOptions, tagIdsOf,
  toPickOptions, toTaskExtended, toUpdateRequest,
} from './task-list.model';

/** A backlog with EVERY wire field populated and DISTINCT, so a dropped one cannot hide behind a default. */
function backlog(overrides: Partial<BacklogDto> = {}): BacklogDto {
  return {
    id: 7,
    backlogCode: 'ARCS-101',
    project: 'ARCS',
    periodMonth: '2026-07',
    type: 'Implement',
    assigneeUserId: 42,
    pcaContactId: 9,
    note: 'the original note',
    roughEstimateHours: 12,
    officialEstimateHours: 20,
    startDate: '2026-07-01',
    endDate: '2026-07-31',
    deadlineInternal: '2026-07-20',
    deadlineExternal: '2026-07-25',
    progressPercent: 30,
    teamId: 3,
    createdAt: '2026-07-01T00:00:00Z',
    rowVersion: 5,
    ...overrides,
  };
}

function row(overrides: Partial<TaskListRowDto> = {}): TaskListRowDto {
  return { backlogId: 1, backlogCode: 'ARCS-1', project: 'ARCS', scheduleState: SCHEDULE_NORMAL, ...overrides };
}

function task(overrides: Partial<TaskItemDto> = {}): TaskItemDto {
  return { id: 3, backlogId: 1, taskName: 'Do it', status: 'Todo', rowVersion: 11, ...overrides };
}

// =====================================================================================================
// 🔴 H1 — THE WHOLE-RECORD TRAP. This is the load-bearing test on this screen.
//
// `PUT /api/backlogs/{id}` replaces the record: a field absent from the body is written NULL. The generated
// DTOs are all-optional, so a dropped field COMPILES. Only this test stands between a progress edit and a
// silently-nulled `pcaContactId`.
// =====================================================================================================
describe('toUpdateRequest — the WHOLE-RECORD trap', () => {
  it('🔴 changing ONLY the progress still round-trips type, assigneeUserId AND pcaContactId', () => {
    const dto = backlog();

    const body = toUpdateRequest(dto, { progressPercent: 55 });

    // The edit itself.
    expect(body.progressPercent).toBe(55);

    // 🔴 THE THREE THE BRIEF NAMES. Delete any ONE of these three lines from `toUpdateRequest` and this
    //    assertion goes red. (Mutation-checked — see the report.)
    expect(body.type).toBe('Implement');
    expect(body.assigneeUserId).toBe(42);
    expect(body.pcaContactId).toBe(9);
  });

  // ---- The NEW backlog edits this fix adds: Type / PCT (assignee) / PCA. Same trap, same defence. --------

  it('🔴 changing ONLY the TYPE still carries startDate AND note — the sparse row cannot supply them (R7)', () => {
    const dto = backlog();   // startDate '2026-07-01', note 'the original note'

    const body = toUpdateRequest(dto, { type: 'Investigate' });

    expect(body.type).toBe('Investigate');           // the edit itself
    // 🔴 THE R7 ASSERTION. Delete `startDate: dto.startDate` (or `note: dto.note`) from `toUpdateRequest` and
    //    exactly these two lines go red — the whole reason the write is built from the GET'd DTO, not the row.
    expect(body.startDate).toBe('2026-07-01');
    expect(body.note).toBe('the original note');
    expect(body.expectedVersion).toBe(5);
    expect(body.auditNote).toBeNull();               // Type carries no reason note; only deadlines do
  });

  it('🔴 changing ONLY the PCT assignee round-trips everything else (incl. the OTHER id, pcaContactId)', () => {
    const body = toUpdateRequest(backlog(), { assigneeUserId: 99 });

    expect(body.assigneeUserId).toBe(99);
    expect(body.pcaContactId).toBe(9);               // 🔴 would be nulled by a body built from the assignee alone
    expect(body.type).toBe('Implement');
    expect(body.startDate).toBe('2026-07-01');
  });

  it('🔴 changing ONLY the PCA contact round-trips everything else (incl. assigneeUserId)', () => {
    const body = toUpdateRequest(backlog(), { pcaContactId: 77 });

    expect(body.pcaContactId).toBe(77);
    expect(body.assigneeUserId).toBe(42);            // 🔴 the mirror-image bug
    expect(body.note).toBe('the original note');
  });

  it('CLEARING the PCT assignee sends null (a real edit) without disturbing the PCA contact', () => {
    const body = toUpdateRequest(backlog(), { assigneeUserId: null });

    expect(body.assigneeUserId).toBeNull();
    expect(body.pcaContactId).toBe(9);
  });

  it('🔴 round-trips EVERY OTHER wire field too — not just the three that got a name', () => {
    const dto = backlog();

    const body = toUpdateRequest(dto, { progressPercent: 55 });

    expect(body.backlogCode).toBe('ARCS-101');
    expect(body.project).toBe('ARCS');
    expect(body.periodMonth).toBe('2026-07');
    expect(body.note).toBe('the original note');
    expect(body.roughEstimateHours).toBe(12);
    expect(body.officialEstimateHours).toBe(20);
    expect(body.startDate).toBe('2026-07-01');
    expect(body.endDate).toBe('2026-07-31');
    expect(body.deadlineInternal).toBe('2026-07-20');
    expect(body.deadlineExternal).toBe('2026-07-25');
  });

  it('sends the LOADED rowVersion as expectedVersion — the checked write has nowhere else to get it', () => {
    expect(toUpdateRequest(backlog({ rowVersion: 5 }), {}).expectedVersion).toBe(5);
  });

  it('REFUSES to build a body when the DTO carried no rowVersion, rather than assert version 0', () => {
    // `requireRowVersion` throws. A silent `undefined` would serialise to an absent key and assert a version
    // we do not hold — which either 409s spuriously or overwrites somebody.
    expect(() => toUpdateRequest(backlog({ rowVersion: undefined }), {})).toThrowError(/rowVersion/);
  });

  it('a patch CANNOT clobber expectedVersion or auditNote — they are applied after the spread', () => {
    const body = toUpdateRequest(backlog({ rowVersion: 5 }), { progressPercent: 1 }, 'because');
    expect(body.expectedVersion).toBe(5);
    expect(body.auditNote).toBe('because');
  });

  it('a deadline edit carries the reason note; every other edit carries null (H3)', () => {
    expect(toUpdateRequest(backlog(), { deadlineInternal: '2026-08-01' }, 'slipped').auditNote).toBe('slipped');
    expect(toUpdateRequest(backlog(), { progressPercent: 10 }).auditNote).toBeNull();
  });

  it('CLEARING a date sends null — and does not disturb the other date', () => {
    const body = toUpdateRequest(backlog(), { deadlineInternal: null }, 'no longer needed');
    expect(body.deadlineInternal).toBeNull();
    expect(body.deadlineExternal).toBe('2026-07-25');   // untouched
  });

  it('never sends teamId — the wire has no such field, which is what stops a backlog going invisible', () => {
    expect('teamId' in toUpdateRequest(backlog(), {})).toBe(false);
  });
});

// =====================================================================================================
// The task's two-field write — the same trap, one level down.
// =====================================================================================================
describe('toTaskExtended', () => {
  it('🔴 changing ONLY the type still round-trips the assignee (both ride one write, both written verbatim)', () => {
    const body = toTaskExtended(task({ type: 'IT', assigneeUserId: 8 }), { type: 'Implement' });

    expect(body.type).toBe('Implement');
    expect(body.assigneeUserId).toBe(8);          // 🔴 would be nulled by a body built from the type alone
    expect(body.expectedVersion).toBe(11);
  });

  it('🔴 changing ONLY the assignee still round-trips the type', () => {
    const body = toTaskExtended(task({ type: 'IT', assigneeUserId: 8 }), { assigneeUserId: 4 });

    expect(body.assigneeUserId).toBe(4);
    expect(body.type).toBe('IT');                 // 🔴 the mirror-image bug
  });

  it('an explicit null CLEARS — it is a real value, not "leave alone"', () => {
    expect(toTaskExtended(task({ type: 'IT' }), { type: null }).type).toBeNull();
  });
});

// =====================================================================================================
// The inline PCT / PCA dropdown options — the deactivated-value fallback (WPF bug #6)
// =====================================================================================================
describe('toPickOptions', () => {
  it('maps an active {id,name} list to options and DROPS any row with no id', () => {
    expect(toPickOptions([{ id: 1, name: 'An' }, { id: undefined, name: 'ghost' }, { id: 2, name: 'Binh' }]))
      .toEqual([{ id: 1, label: 'An' }, { id: 2, label: 'Binh' }]);
  });

  it('tolerates a null/absent name', () => {
    expect(toPickOptions([{ id: 5, name: null }, { id: 6 }])).toEqual([{ id: 5, label: '' }, { id: 6, label: '' }]);
  });
});

describe('pickOptions', () => {
  const active = [{ id: 1, label: 'An' }, { id: 2, label: 'Binh' }];
  const names = [{ id: 1, name: 'An' }, { id: 2, name: 'Binh' }, { id: 9, name: 'Gone' }];

  it('returns just the active options when the current value is null/undefined', () => {
    expect(pickOptions(active, names, null)).toEqual(active);
    expect(pickOptions(active, names, undefined)).toEqual(active);
  });

  it('returns just the active options when the current value IS active (no duplicate)', () => {
    expect(pickOptions(active, names, 1)).toEqual(active);
  });

  it('🔴 appends a DEACTIVATED current as "Name (inactive)" so the <select> is not a blank box', () => {
    expect(pickOptions(active, names, 9)).toEqual([...active, { id: 9, label: 'Gone (inactive)' }]);
  });

  it('falls back to the id when even /names cannot name the deactivated current', () => {
    expect(pickOptions(active, names, 404)).toEqual([...active, { id: 404, label: '#404 (inactive)' }]);
  });
});

// =====================================================================================================
// H5 — isDone
// =====================================================================================================
describe('isDone', () => {
  it('🔴 ZERO TASKS IS NOT DONE — `[].every()` is true, and inverting this hides the Late chip', () => {
    expect(isDone(row({ tasks: [] }))).toBe(false);
    expect(isDone(row({ tasks: undefined }))).toBe(false);
  });

  it('every active task Done -> done', () => {
    expect(isDone(row({ tasks: [task({ status: 'Done' }), task({ status: 'Done' })] }))).toBe(true);
  });

  it('one task not Done -> not done', () => {
    expect(isDone(row({ tasks: [task({ status: 'Done' }), task({ status: 'Todo' })] }))).toBe(false);
  });
});

// =====================================================================================================
// Chips
// =====================================================================================================
describe('buildChips', () => {
  it('🔴 Late and Warning are MUTUALLY EXCLUSIVE — at most one system chip, never two', () => {
    const late = buildChips(row({ scheduleState: SCHEDULE_LATE }));
    expect(late.length).toBe(1);
    expect(late[0]).toEqual(jasmine.objectContaining({ kind: 'late', text: '⚠ Late' }));

    const warn = buildChips(row({ scheduleState: SCHEDULE_WARNING }));
    expect(warn.length).toBe(1);
    expect(warn[0]).toEqual(jasmine.objectContaining({ kind: 'warning', text: '⚠ At risk' }));
  });

  it('Normal shows no system chip at all', () => {
    expect(buildChips(row({ scheduleState: SCHEDULE_NORMAL }))).toEqual([]);
  });

  it('the system chip leads, then the custom tags ORDERED BY TAG ID', () => {
    const chips = buildChips(row({
      scheduleState: SCHEDULE_LATE,
      tags: [
        { id: 9, text: 'nine', color: '#111', icon: '9' },
        { id: 2, text: 'two', color: '#222', icon: '2' },
        { id: 5, text: 'five', color: '#333', icon: '5' },
      ],
    }));

    expect(chips.map(c => c.text)).toEqual(['⚠ Late', 'two', 'five', 'nine']);
  });

  it('carries each custom tag colour and icon through', () => {
    const chips = buildChips(row({ tags: [{ id: 1, text: 'blocked', color: '#C0362C', icon: '🚧' }] }));
    expect(chips[0]).toEqual({ kind: 'custom', text: 'blocked', color: '#C0362C', icon: '🚧' });
  });
});

// =====================================================================================================
// Grouping
// =====================================================================================================
describe('groupRows', () => {
  it('bands by project in the FIXED order, unknown last', () => {
    const bands = groupRows([
      row({ backlogId: 1, project: 'Other' }),
      row({ backlogId: 2, project: 'Nonesuch' }),
      row({ backlogId: 3, project: 'ARCS' }),
      row({ backlogId: 4, project: 'ARMS' }),
      row({ backlogId: 5, project: 'PlusArcs' }),
    ], false, new Map());

    expect(bands.map(b => b.key)).toEqual(['ARCS', 'PlusArcs', 'ARMS', 'Other', 'Nonesuch']);
  });

  it('preserves the SERVER row order within a band (it already sorted by backlogCode)', () => {
    const bands = groupRows([
      row({ backlogId: 1, backlogCode: 'A-1', project: 'ARCS' }),
      row({ backlogId: 2, backlogCode: 'A-2', project: 'ARCS' }),
      row({ backlogId: 3, backlogCode: 'A-3', project: 'ARCS' }),
    ], false, new Map());

    expect(bands[0].rows.map(r => r.backlogCode)).toEqual(['A-1', 'A-2', 'A-3']);
  });

  it('a null project falls into its own band and sorts last', () => {
    const bands = groupRows([row({ backlogId: 1, project: null }), row({ backlogId: 2, project: 'ARCS' })], false, new Map());
    expect(bands.map(b => b.key)).toEqual(['ARCS', '—']);
  });
});

// =====================================================================================================
// Grouping — adaptive team banding (TL-12)
// =====================================================================================================
describe('groupRows — adaptive team banding (TL-12)', () => {
  const names = new Map<number, string>([[1, 'Alpha'], [2, 'Beta']]);

  it('bands by TEAM when byTeam is true — team name, alpha order, server row order within a band', () => {
    const bands = groupRows([
      row({ backlogId: 1, teamId: 2, project: 'ARCS' }),
      row({ backlogId: 2, teamId: 1, project: 'Other' }),
      row({ backlogId: 3, teamId: 2, project: 'ARMS' }),
    ], true, names);

    expect(bands.map(b => b.key)).toEqual(['Alpha', 'Beta']);
    expect(bands.find(b => b.key === 'Beta')!.rows.map(r => r.backlogId)).toEqual([1, 3]);
  });

  it('a NULL teamId falls into a "—" band, which sorts LAST', () => {
    const bands = groupRows([
      row({ backlogId: 1, teamId: null }),
      row({ backlogId: 2, teamId: 2 }),
    ], true, names);
    expect(bands.map(b => b.key)).toEqual(['Beta', '—']);
  });

  it('falls back to "#<id>" when the team name is unknown (a deactivated team not in the map)', () => {
    expect(groupRows([row({ backlogId: 1, teamId: 99 })], true, names).map(b => b.key)).toEqual(['#99']);
  });

  it('bands by PROJECT (not team) when byTeam is false — even with teamIds present', () => {
    const bands = groupRows([
      row({ backlogId: 1, teamId: 2, project: 'Other' }),
      row({ backlogId: 2, teamId: 1, project: 'ARCS' }),
    ], false, names);
    expect(bands.map(b => b.key)).toEqual(['ARCS', 'Other']);
  });
});

// =====================================================================================================
// The progress box
// =====================================================================================================
describe('parseProgress', () => {
  it('accepts a whole number 0..100', () => {
    expect(parseProgress('0')).toEqual({ ok: true, value: 0 });
    expect(parseProgress('55')).toEqual({ ok: true, value: 55 });
    expect(parseProgress('100')).toEqual({ ok: true, value: 100 });
    expect(parseProgress(' 42 ')).toEqual({ ok: true, value: 42 });
  });

  it('an EMPTY box clears the percent', () => {
    expect(parseProgress('')).toEqual({ ok: true, value: null });
    expect(parseProgress('   ')).toEqual({ ok: true, value: null });
  });

  it('🔴 NEVER commits a non-integer or an out-of-range value — not as 0, not as null', () => {
    for (const bad of ['5.5', '-1', '101', '999', 'abc', '1e2', '0x10', '+5', '1 2', '½']) {
      expect(parseProgress(bad))
        .withContext(`"${bad}" must not commit`)
        .toEqual({ ok: false });
    }
  });
});

// =====================================================================================================
// Continue
// =====================================================================================================
describe('nextPeriod', () => {
  it('is the selected month + 1', () => {
    expect(nextPeriod(2026, 7)).toBe('2026-08');
    expect(nextPeriod(2026, 1)).toBe('2026-02');
  });

  it('🔴 ROLLS THE YEAR at December', () => {
    expect(nextPeriod(2026, 12)).toBe('2027-01');
  });

  it('pads to yyyy-MM', () => {
    expect(nextPeriod(2026, 8)).toBe('2026-09');
    expect(nextPeriod(2026, 9)).toBe('2026-10');
  });
});

// =====================================================================================================
// Errors
// =====================================================================================================
describe('messageOf', () => {
  it("shows the SERVER'S OWN sentence on a 400 — the duplicate-Continue message is the whole point", () => {
    const err = new HttpErrorResponse({
      status: 400,
      error: { error: "'ARCS-101' already exists in 2026-08." },
    });

    expect(messageOf(err, 'fallback')).toBe("'ARCS-101' already exists in 2026-08.");
  });

  it('explains a 409 in the user\'s terms', () => {
    const err = new HttpErrorResponse({ status: 409, error: null });
    expect(messageOf(err, 'fallback')).toContain('Someone else changed this');
  });

  it('falls back when the server said nothing useful', () => {
    expect(messageOf(new HttpErrorResponse({ status: 500 }), 'fallback')).toBe('fallback');
    expect(messageOf(new Error('boom'), 'fallback')).toBe('fallback');
  });
});

describe('tagIdsOf', () => {
  it('drops a tag with no id — it could not be written back anyway', () => {
    expect(tagIdsOf([{ id: 3 }, { id: undefined }, { id: 1 }])).toEqual([3, 1]);
    expect(tagIdsOf(null)).toEqual([]);
  });
});
