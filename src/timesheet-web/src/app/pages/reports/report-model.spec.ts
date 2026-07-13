import { TeamNode, TimesheetWeeklyReportResponse } from '../../api/models';
import {
  PROJECTS, WHOLE_TEAM_TARGET, branchIds, buildTree, exportFileName, flattenTree, formatDayShort,
  formatLeafDate, hoursText, mondayOf, n1, parseIsoDate, parseMonth, statCards, toIsoDate,
} from './report-model';

/**
 * The Reports screen's arithmetic and shaping, tested without a TestBed.
 *
 * Three of the rules below encode a bug the product has already paid for once, so each of those fixtures is
 * built to DISCRIMINATE: the "obvious" wrong implementation produces a DIFFERENT number, not the same one.
 * A fixture where right and wrong agree is a test that cannot fail.
 */
describe('report-model', () => {

  // =====================================================================================================
  // THE PROJECT LIST
  // =====================================================================================================

  it('🔴 offers PlusArcs — the old mockup dropped it, so it could not be filtered by at all', () => {
    expect(PROJECTS).toEqual(['All', 'ARCS', 'PlusArcs', 'ARMS', 'Other']);
    // The source of truth: BacklogProjects.All = ARCS · PlusArcs · ARMS · Other, with "All" prepended.
    expect(PROJECTS).toContain('PlusArcs');
  });

  // =====================================================================================================
  // FORMATTING
  // =====================================================================================================

  it('formats N1 with the thousands separator, as .NET does', () => {
    expect(n1(8)).toBe('8.0');
    expect(n1(6.666)).toBe('6.7');
    expect(n1(0)).toBe('0.0');
    // 🔴 `toFixed(1)` alone would render "1234.5" — every other surface in the product says "1,234.5".
    expect(n1(1234.5)).toBe('1,234.5');
  });

  it('appends the unit for the stat cards and the tree (but NOT the grids)', () => {
    expect(hoursText(12.5)).toBe('12.5h');
    expect(hoursText(null)).toBe('0.0h');
    expect(hoursText(undefined)).toBe('0.0h');
  });

  /**
   * 🔴 THE TIMEZONE TRAP. `new Date("2026-07-06")` is parsed as UTC MIDNIGHT, so anywhere west of Greenwich
   * `getDate()` answers 5, not 6 — every date in the grid and every leaf of the tree renders ONE DAY EARLY,
   * for half the planet, while a UTC-based CI stays green. This must build a LOCAL date.
   */
  it('🔴 parses a wire date as LOCAL midnight, never UTC', () => {
    const date = parseIsoDate('2026-07-06')!;

    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(6);          // July, 0-based
    expect(date.getDate()).toBe(6);           // 🔴 NOT 5
    expect(date.getHours()).toBe(0);          // local midnight, so no drift in either direction

    // And it round-trips: the thing we parsed is the thing we can send back.
    expect(toIsoDate(date)).toBe('2026-07-06');
  });

  it('rejects junk rather than inventing a date', () => {
    expect(parseIsoDate('')).toBeNull();
    expect(parseIsoDate(null)).toBeNull();
    expect(parseIsoDate(undefined)).toBeNull();
    expect(parseIsoDate('not-a-date')).toBeNull();
  });

  it('formats the weekly grid date as `ddd dd/MM` (WPF StringFormat)', () => {
    expect(formatDayShort('2026-07-06')).toBe('Mon 06/07');   // 6 July 2026 is a Monday
    expect(formatDayShort('2026-07-10')).toBe('Fri 10/07');
    expect(formatDayShort(null)).toBe('');
  });

  it('formats the tree LEAF date as `ddd, yyyy-MM-dd` (WPF StringFormat)', () => {
    expect(formatLeafDate('2026-07-06')).toBe('Mon, 2026-07-06');
    expect(formatLeafDate(undefined)).toBe('');
  });

  // =====================================================================================================
  // THE WEEK
  // =====================================================================================================

  /**
   * The field says "Week (Mon)" and the API takes a `monday`. It reads rows over `monday..monday+4`, while
   * `DaysLogged` independently normalises to the REAL Monday for its Mon–Fri denominator. Send a Wednesday
   * and the two disagree: a Wed–Sun row set measured against a Mon–Fri denominator.
   */
  it('🔴 snaps any picked day to that week\'s Monday', () => {
    expect(mondayOf('2026-07-08')).toBe('2026-07-06');   // a Wednesday -> back to Monday
    expect(mondayOf('2026-07-06')).toBe('2026-07-06');   // already Monday -> unchanged (idempotent)
    expect(mondayOf('2026-07-12')).toBe('2026-07-06');   // 🔴 SUNDAY belongs to the week that STARTED Mon 6
    expect(mondayOf('2026-07-13')).toBe('2026-07-13');   // ...and the next Monday starts the next week
  });

  it('parses the month input, and refuses a bad one rather than querying month 0 or 13', () => {
    expect(parseMonth('2026-07')).toEqual([2026, 7]);    // 1-based, as the API wants
    expect(parseMonth('2026-13')).toBeNull();
    expect(parseMonth('2026-00')).toBeNull();
    expect(parseMonth('')).toBeNull();
    expect(parseMonth('2026-07-06')).toBeNull();
  });

  it('names the export file as WPF does — and whole-team is "team", never "0"', () => {
    expect(exportFileName('2026-07', { userId: 4, display: 'Nhan' })).toBe('Worklog-2026-07-Nhan.xlsx');
    expect(exportFileName('2026-07', WHOLE_TEAM_TARGET)).toBe('Worklog-2026-07-team.xlsx');
    // A display name is user data; it must not be able to escape into a path.
    expect(exportFileName('2026-07', { userId: 4, display: 'a/b:c*d' })).toBe('Worklog-2026-07-abcd.xlsx');
  });

  // =====================================================================================================
  // THE FOUR STAT CARDS
  //
  // 🔴 THE FIXTURE IS DELIBERATELY SELF-INCONSISTENT, and that is the whole point.
  //
  //   - `dayTotals` sums to 20, but `detailRows` sums to 13. In production they agree; here they must NOT,
  //     because that is the ONLY way to prove WEEK TOTAL reads `dayTotals` and not the detail grid.
  //   - `dayTotals` has FOUR entries but `daysLogged.logged` is THREE (one day carries 0h). In production
  //     the server derives exactly that; here the gap is what proves AVG / DAY divides by the SERVER's
  //     count and does not re-derive `dayTotals.length`.
  //
  //     20 / 3 = 6.7   <- correct
  //     20 / 4 = 5.0   <- what re-deriving the denominator would print
  // =====================================================================================================

  const WEEKLY: TimesheetWeeklyReportResponse = {
    dayTotals: [
      { date: '2026-07-06', totalHours: 8 },
      { date: '2026-07-07', totalHours: 7 },
      { date: '2026-07-08', totalHours: 0 },   // present, but nothing logged -> NOT a "day logged"
      { date: '2026-07-09', totalHours: 5 },
    ],
    daysLogged: { logged: 3, workingDays: 5 },
    detailRows: [
      { date: '2026-07-06', backlogCode: 'ARCS-1001', project: 'ARCS', taskName: 'Design', totalHours: 8 },
      { date: '2026-07-07', backlogCode: 'ARCS-1001', project: 'ARCS', taskName: 'Build', totalHours: 5 },
    ],
  };

  function card(label: string, weekly = WEEKLY, missing = [{ userName: 'Zoe' }, { userName: 'Pat' }]) {
    return statCards(weekly, missing, '2026-07-06').find(c => c.label === label)!;
  }

  it('🔴 WEEK TOTAL sums dayTotals — NOT the detail rows', () => {
    expect(card('WEEK TOTAL').value).toBe('20.0h');
    // The detail rows sum to 13. If WEEK TOTAL ever reads them, it says 13.0h and this goes red.
    expect(card('WEEK TOTAL').value).not.toBe('13.0h');
  });

  it('🔴 AVG / DAY divides by the SERVER\'s daysLogged.logged — never by dayTotals.length', () => {
    // 20 / 3 = 6.666… -> N1 -> 6.7
    expect(card('AVG / DAY').value).toBe('6.7h');
    // 🔴 THE DISCRIMINATOR. dayTotals has FOUR entries (one holds 0h), so re-deriving the denominator gives
    // 20 / 4 = 5.0. The fixture exists so that the wrong implementation prints a DIFFERENT number.
    expect(card('AVG / DAY').value).not.toBe('5.0h');
  });

  it('🔴 DAYS LOGGED is the server\'s stat verbatim — the denominator is NOT the numerator', () => {
    // The old bug: the denominator was `rows.Count`, which only holds days that HAVE logs — so it moved with
    // the numerator and the card could only EVER read N/N. It must be able to read 3 / 5.
    expect(card('DAYS LOGGED').value).toBe('3 / 5');
    expect(card('DAYS LOGGED').value).not.toBe('3 / 3');
    expect(card('DAYS LOGGED').value).not.toBe('4 / 5');   // dayTotals.length would say 4
  });

  it('🔴 AVG / DAY guards logged === 0 — an empty week must not render "NaN" or "∞"', () => {
    const empty: TimesheetWeeklyReportResponse = {
      dayTotals: [],
      daysLogged: { logged: 0, workingDays: 5 },
      detailRows: [],
    };

    const value = card('AVG / DAY', empty).value;
    expect(value).toBe('0.0h');
    expect(value).not.toContain('NaN');
    expect(value).not.toContain('Infinity');
    expect(card('DAYS LOGGED', empty).value).toBe('0 / 5');
  });

  it('NOT LOGGED is the length of the missing-logs list, and says which scope it means', () => {
    expect(card('NOT LOGGED').value).toBe('2');
    // 🔴 It is scoped to the ACTIVE TEAM, not the team filter — the label says so, so nobody has to guess.
    expect(card('NOT LOGGED').sub).toBe('in your active team');
    expect(card('NOT LOGGED', WEEKLY, []).value).toBe('0');
  });

  it('survives a wholly absent weekly response without throwing', () => {
    const cards = statCards(null, [], '2026-07-06');

    expect(cards.length).toBe(4);
    expect(cards[0].value).toBe('0.0h');    // WEEK TOTAL
    expect(cards[1].value).toBe('0.0h');    // AVG / DAY  — and NOT "NaN"
    expect(cards[2].value).toBe('0 / 0');   // DAYS LOGGED
  });

  it('appends the unit to the two hour cards and shows the week range', () => {
    expect(card('WEEK TOTAL').value.endsWith('h')).toBeTrue();
    expect(card('WEEK TOTAL').sub).toBe('06/07 – 10/07');   // Mon..Fri of the selected week
  });

  // =====================================================================================================
  // THE DRILL-DOWN TREE — an ARRAY of roots, five heterogeneous levels, no ids of its own
  // =====================================================================================================

  const TREE: TeamNode[] = [
    {
      teamName: 'Alpha', totalHours: 20,
      projects: [{
        project: 'ARCS', totalHours: 20,
        backlogs: [{
          backlogCode: 'ARCS-1001', project: 'ARCS', totalHours: 20,
          tasks: [{
            taskId: 7, taskName: 'Design schema', totalHours: 20,
            dates: [{ date: '2026-07-06', totalHours: 8 }, { date: '2026-07-07', totalHours: 12 }],
          }],
        }],
      }],
    },
    // 🔴 A SECOND ROOT. The old mock's TreeNode was single-root; the API returns TeamNode[].
    {
      teamName: 'Beta', totalHours: 5,
      projects: [{
        project: 'ARMS', totalHours: 5,
        backlogs: [{
          backlogCode: 'ARMS-2002', project: 'ARMS', totalHours: 5,
          tasks: [{ taskId: 9, taskName: 'Triage', totalHours: 5, dates: [{ date: '2026-07-08', totalHours: 5 }] }],
        }],
      }],
    },
  ];

  function expandAll(roots = buildTree(TREE)): Record<string, boolean> {
    const map: Record<string, boolean> = {};
    branchIds(roots).forEach(id => (map[id] = true));
    return map;
  }

  it('🔴 builds an ARRAY of roots — one per TEAM, not a single root', () => {
    const roots = buildTree(TREE);

    expect(roots.length).toBe(2);
    expect(roots.map(r => r.label)).toEqual(['Alpha', 'Beta']);
    expect(roots[0].hours).toBe('20.0h');
  });

  it('🔴 walks all FIVE heterogeneous levels — Team, Project, Backlog, Task, Date', () => {
    const roots = buildTree(TREE);
    const rows = flattenTree(roots, expandAll(roots));

    // Alpha's spine, top to bottom. Each level reads a DIFFERENT field of a DIFFERENT record type.
    const alpha = rows.filter(r => r.id.startsWith(roots[0].id));
    expect(alpha.map(r => [r.depth, r.label])).toEqual([
      [0, 'Alpha'],                  // TeamNode.teamName
      [1, 'ARCS'],                   // ProjectNode.project
      [2, 'ARCS-1001'],              // BacklogNode.backlogCode
      [3, 'Design schema'],          // TaskNode.taskName
      [4, 'Mon, 2026-07-06'],        // DateEntry.date  -> the LEAF, formatted
      [4, 'Tue, 2026-07-07'],
    ]);

    // Only the DateEntry level is a leaf; every level above it has children.
    expect(alpha.filter(r => r.isLeaf).map(r => r.depth)).toEqual([4, 4]);
    expect(alpha[4].hours).toBe('8.0h');
  });

  it('shows only the roots when nothing is expanded, and expands one branch at a time', () => {
    const roots = buildTree(TREE);

    expect(flattenTree(roots, {}).map(r => r.label)).toEqual(['Alpha', 'Beta']);

    // Open Alpha only: its project appears, Beta stays shut.
    const rows = flattenTree(roots, { [roots[0].id]: true });
    expect(rows.map(r => r.label)).toEqual(['Alpha', 'ARCS', 'Beta']);
    expect(rows[0].expanded).toBeTrue();
    expect(rows[2].expanded).toBeFalse();
  });

  /**
   * 🔴 THE IDS MUST SURVIVE A RELOAD. Every filter change re-fetches the tree and rebuilds it from scratch;
   * the expand/collapse map is keyed by id. Index-based ids ("root 0") would re-point the user's open
   * branches at whatever team happens to sort first in the NEW result.
   */
  it('🔴 synthesises ids that are STABLE across a rebuild, and unique across all five levels', () => {
    const first = buildTree(TREE);
    const second = buildTree(TREE);      // the same data, fetched again

    expect(second[0].id).toBe(first[0].id);
    expect(second[0].children[0].id).toBe(first[0].children[0].id);

    // Unique: no two nodes anywhere in the tree share an id.
    const all = flattenTree(first, expandAll(first)).map(r => r.id);
    expect(new Set(all).size).toBe(all.length);

    // And the two roots do not collide with each other.
    expect(first[0].id).not.toBe(first[1].id);
  });

  it('branchIds lists every EXPANDABLE node and no leaves — it is what Expand-all drives', () => {
    const roots = buildTree(TREE);
    const ids = branchIds(roots);

    // 2 teams x (team + project + backlog + task) = 8 branches. The 3 date leaves are NOT branches.
    expect(ids.length).toBe(8);

    const leafIds = flattenTree(roots, expandAll(roots)).filter(r => r.isLeaf).map(r => r.id);
    expect(leafIds.length).toBe(3);
    leafIds.forEach(id => expect(ids).not.toContain(id));
  });

  it('tolerates an empty, null or undefined tree', () => {
    expect(buildTree([])).toEqual([]);
    expect(buildTree(null)).toEqual([]);
    expect(buildTree(undefined)).toEqual([]);
    expect(flattenTree([], {})).toEqual([]);
    expect(branchIds([])).toEqual([]);
  });

  it('tolerates a node whose optional fields the wire left off', () => {
    const sparse: TeamNode[] = [{ teamName: null, totalHours: undefined, projects: null }];
    const roots = buildTree(sparse);

    expect(roots.length).toBe(1);
    expect(roots[0].label).toBe('(no team)');
    expect(roots[0].hours).toBe('0.0h');
    expect(roots[0].children).toEqual([]);
  });
});
