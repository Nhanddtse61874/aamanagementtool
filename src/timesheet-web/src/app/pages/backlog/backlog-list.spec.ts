import { BacklogListItemDto, NamedRefDto } from '../../api/models';
import { ALL, buildRows, coerceFilters, Filters, filterRows, rebuildOptions } from './backlog-list';

/**
 * 🔴 `names` comes from GET /api/users/names, which returns users.GetAllAsync() -- INCLUDING DEACTIVATED
 * users. That is the entire reason the route exists (SettingsEndpoints.cs:337-348): a backlog whose
 * assignee has since LEFT must still render her name, or opening it and saving without touching anything
 * silently clears the assignee. GET /api/users omits her by construction; GET /api/users/all can name her
 * but is AdminPolicy-gated, so an ordinary user reading it gets a 403 and the whole screen's forkJoin dies.
 */
const NAMES: NamedRefDto[] = [
  { id: 5, name: 'Nhan' },
  { id: 6, name: 'Binh Tran' },        // present in the directory, but assigned to nothing below
  { id: 9, name: 'Departed Dana' },    // DEACTIVATED -- only /api/users/names can name her
];

/** 🔴 The API still returns DEFAULT, and MUST keep doing so. The exclusion is this screen's job, not its. */
const ITEMS: BacklogListItemDto[] = [
  { id: 1, backlogCode: 'ARCS-1', project: 'ARCS', taskCount: 3, periodMonth: '2026-07', type: 'Implement', assigneeUserId: 5 },
  { id: 2, backlogCode: 'ARMS-9', project: 'ARMS', taskCount: 0, periodMonth: '2026-06', type: 'Investigate', assigneeUserId: 9 },
  { id: 3, backlogCode: 'DEFAULT', project: 'DEFAULT', taskCount: 7, periodMonth: null, type: null, assigneeUserId: null },
  { id: 4, backlogCode: 'ARCS-2', project: 'ARCS', taskCount: 1, periodMonth: '2026-07', type: null, assigneeUserId: null },
];

const NONE: Filters = { term: '', project: ALL, type: ALL, assignee: ALL, month: ALL };

describe('buildRows', () => {
  /**
   * 🔴 CLIENT-SIDE, NOT SERVER-SIDE. The API must keep returning DEFAULT because Log Work needs it --
   * ReadModels.cs:68: "EVERY backlog item (incl. DEFAULT and empty ones) becomes one collapsible group".
   * The filter belongs to the screen that does not want it, exactly as TaskListViewModel.cs:220 does it.
   */
  it('EXCLUDES the DEFAULT backlog -- which is still present in the input', () => {
    expect(ITEMS.some(i => i.backlogCode === 'DEFAULT')).toBe(true);   // the input really does carry it

    expect(buildRows(ITEMS, NAMES).map(r => r.code)).toEqual(['ARCS-1', 'ARMS-9', 'ARCS-2']);
  });

  it('resolves a DEACTIVATED assignee name -- the whole point of /api/users/names', () => {
    const rows = buildRows(ITEMS, NAMES);

    expect(rows.find(r => r.code === 'ARMS-9')!.assigneeName).toBe('Departed Dana');
    expect(rows.find(r => r.code === 'ARMS-9')!.assigneeUserId).toBe(9);
  });

  it('leaves an unassigned row null', () => {
    expect(buildRows(ITEMS, NAMES).find(r => r.code === 'ARCS-2')!.assigneeName).toBeNull();
  });

  it('projects the scalar columns', () => {
    expect(buildRows(ITEMS, NAMES)[0]).toEqual({
      id: 1, code: 'ARCS-1', project: 'ARCS', taskCount: 3,
      month: '2026-07', type: 'Implement', assigneeUserId: 5, assigneeName: 'Nhan',
    });
  });
});

describe('filterRows', () => {
  const rows = buildRows(ITEMS, NAMES);

  it('passes everything through when nothing is selected', () => {
    expect(filterRows(rows, NONE).length).toBe(3);
  });

  it('matches the term against code OR project, case-insensitively, as a CONTAINS', () => {
    expect(filterRows(rows, { ...NONE, term: 'arcs' }).map(r => r.code)).toEqual(['ARCS-1', 'ARCS-2']);
    expect(filterRows(rows, { ...NONE, term: '-9' }).map(r => r.code)).toEqual(['ARMS-9']);
    expect(filterRows(rows, { ...NONE, term: 'ARMS' }).map(r => r.code)).toEqual(['ARMS-9']);  // via project
  });

  it('ANDs all five filters', () => {
    expect(filterRows(rows, { ...NONE, project: 'ARCS', month: '2026-07', type: 'Implement' })
      .map(r => r.code)).toEqual(['ARCS-1']);

    // Same project, but a type only the OTHER project's row has -> the AND collapses to empty.
    expect(filterRows(rows, { ...NONE, project: 'ARCS', type: 'Investigate' })).toEqual([]);
  });

  it('filters by assignee NAME, including a departed one', () => {
    expect(filterRows(rows, { ...NONE, assignee: 'Departed Dana' }).map(r => r.code)).toEqual(['ARMS-9']);
  });
});

describe('rebuildOptions', () => {
  /** Today these are hard-coded literals -- `monthOpts = ['All','2026-06','2026-07','2026-08']`. */
  it('builds all four dropdowns from the LOADED data -- distinct, sorted, All first', () => {
    const o = rebuildOptions(buildRows(ITEMS, NAMES));

    expect(o.projects).toEqual([ALL, 'ARCS', 'ARMS']);            // ARCS appears twice -> distinct
    expect(o.types).toEqual([ALL, 'Implement', 'Investigate']);
    expect(o.assignees).toEqual([ALL, 'Departed Dana', 'Nhan']);
    expect(o.months).toEqual([ALL, '2026-06', '2026-07']);
  });

  it('never offers DEFAULT as a project -- buildRows already dropped it', () => {
    expect(rebuildOptions(buildRows(ITEMS, NAMES)).projects).not.toContain('DEFAULT');
  });

  // Binh Tran is in the DIRECTORY but assigned to nothing. The dropdowns come from the DATA, not the
  // directory -- offering a filter that can only ever return zero rows is a dead option.
  it('offers only assignees who actually appear in the rows', () => {
    expect(rebuildOptions(buildRows(ITEMS, NAMES)).assignees).not.toContain('Binh Tran');
  });

  it('degrades to All-only when there is no data', () => {
    expect(rebuildOptions([])).toEqual({ projects: [ALL], types: [ALL], assignees: [ALL], months: [ALL] });
  });
});

describe('coerceFilters', () => {
  it('resets a selection that has VANISHED from the data back to All, and leaves the rest alone', () => {
    const options = rebuildOptions(buildRows(ITEMS, NAMES));
    const stale: Filters = {
      term: 'keep me', project: 'GONE', type: 'Implement', assignee: ALL, month: '1999-01',
    };

    expect(coerceFilters(stale, options)).toEqual({
      term: 'keep me',        // the free-text term is not an option list -- never coerced
      project: ALL,           // 'GONE' is not in the data any more
      type: 'Implement',      // still there -> survives
      assignee: ALL,
      month: ALL,             // '1999-01' is not in the data any more
    });
  });
});
