import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';

import {
  MeResponse, MissingLogWarning, TeamDto, TimesheetMonthlyReportResponse, TimesheetWeeklyReportResponse,
  UserDto,
} from '../../api/models';
import { TeamFilterComponent } from '../../components/team-filter/team-filter.component';
import { DataChange, DataKind, RealtimeService } from '../../core/realtime.service';
import { ReportFilter, WorklogService } from '../../services/worklog.service';
import { ReportsComponent } from './reports.component';

/**
 * The Reports screen.
 *
 * 🔴 WHAT THIS SUITE EXISTS TO CATCH. Every bug below produces a 200 and a screen that LOOKS fine — none of
 * them would be caught by a smoke test, and two of them fail in the "shows MORE data than it should"
 * direction:
 *
 *   1. `userId: 0` on the wire. The C# is `userId is int uid ? GetReportRowsAsync(uid, …) : …` and 0 IS an
 *      int — so the default "Whole team (all)" selection would query USER #0 and render EMPTY, always.
 *   2. `project: 'All'` on the wire. `rows.Where(r => r.Project == "All")` matches nothing.
 *   3. `teamIds: []` on the wire. An empty array appends NO query key, and the server reads an ABSENT
 *      teamIds as "EVERY TEAM YOU BELONG TO" — so unchecking every team would show MORE, not less.
 *   4. Re-deriving `daysLogged` from `dayTotals` (see report-model.spec.ts).
 *   5. `kind === 'Logs'` on the realtime feed. It is a NUMBER; a string matches nothing, forever.
 */

/**
 * 🔴 DELIBERATELY SELF-INCONSISTENT — see report-model.spec.ts. `dayTotals` sums to 20 while `detailRows`
 * sums to 13, and `daysLogged.logged` is 3 while `dayTotals.length` is 4. In production they agree; here the
 * gaps are the only thing that can tell a right implementation from a plausible wrong one.
 */
const WEEKLY: TimesheetWeeklyReportResponse = {
  dayTotals: [
    { date: '2026-07-06', totalHours: 8 },
    { date: '2026-07-07', totalHours: 7 },
    { date: '2026-07-08', totalHours: 0 },
    { date: '2026-07-09', totalHours: 5 },
  ],
  daysLogged: { logged: 3, workingDays: 5 },
  detailRows: [
    { date: '2026-07-06', backlogCode: 'ARCS-1001', project: 'ARCS', taskName: 'Design schema', totalHours: 8 },
    { date: '2026-07-07', backlogCode: 'ARCS-1001', project: 'ARCS', taskName: 'Build', totalHours: 5 },
  ],
};

const MONTHLY: TimesheetMonthlyReportResponse = {
  monthlyTotals: [
    { backlogCode: 'ARCS-1001', project: 'ARCS', taskName: 'Design schema', totalHours: 20 },
    { backlogCode: 'ARMS-2002', project: 'ARMS', taskName: 'Triage', totalHours: 5 },
  ],
  // 🔴 TWO ROOTS. The wire is TeamNode[], not a single node.
  projectTree: [
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
  ],
};

const MISSING: MissingLogWarning[] = [{ userName: 'Zoe' }, { userName: 'Pat' }];

const USERS: UserDto[] = [
  { id: 4, name: 'Nhan', isActive: true },
  { id: 5, name: 'An', isActive: true },
];

/** The team-filter child's two reads. The user belongs to Alpha + Beta; Beta (2) is active. */
const ME: MeResponse = { id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 2, memberTeamIds: [1, 2] };
const TEAMS: TeamDto[] = [{ id: 1, name: 'Alpha', isActive: true }, { id: 2, name: 'Beta', isActive: true }];

describe('ReportsComponent', () => {
  let fixture: ComponentFixture<ReportsComponent>;
  let component: ReportsComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let dataChanged: Subject<DataChange>;

  function arrange(): void {
    dataChanged = new Subject<DataChange>();

    api = jasmine.createSpyObj<WorklogService>('WorklogService', [
      'getWeeklyReport', 'getMonthlyReport', 'getMissingLogs', 'getUsersActive', 'exportExcel',
      'me', 'getTeamsActive',
    ]);
    api.getWeeklyReport.and.returnValue(of(WEEKLY));
    api.getMonthlyReport.and.returnValue(of(MONTHLY));
    api.getMissingLogs.and.returnValue(of(MISSING));
    api.getUsersActive.and.returnValue(of(USERS));
    api.exportExcel.and.returnValue(of(new Blob(['xlsx'])));
    api.me.and.returnValue(of(ME));
    api.getTeamsActive.and.returnValue(of(TEAMS));

    TestBed.configureTestingModule({
      imports: [ReportsComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: RealtimeService, useValue: { start: () => undefined, dataChanged: dataChanged.asObservable() } },
      ],
    });
  }

  function mount(): void {
    fixture = TestBed.createComponent(ReportsComponent);
    component = fixture.componentInstance;
    // The export writes a file; never let the real anchor-click run in a real browser.
    spyOn(component, 'saveBlob');
    fixture.detectChanges();
  }

  function setUp(): void {
    arrange();
    mount();
  }

  /** The filter object handed to the last weekly read — i.e. what is actually about to go on the wire. */
  function lastWeeklyFilter(): ReportFilter | undefined {
    return api.getWeeklyReport.calls.mostRecent().args[1];
  }

  function lastMonthlyFilter(): ReportFilter | undefined {
    return api.getMonthlyReport.calls.mostRecent().args[2];
  }

  function text(selector: string): string {
    return (fixture.debugElement.query(By.css(selector))?.nativeElement as HTMLElement)?.textContent?.trim() ?? '';
  }

  function all(selector: string): HTMLElement[] {
    return fixture.debugElement.queryAll(By.css(selector)).map(d => d.nativeElement as HTMLElement);
  }

  /** The four stat cards, as `label -> value`, read off the DOM. */
  function cards(): Record<string, string> {
    const out: Record<string, string> = {};
    for (const card of all('.metric')) {
      const label = card.querySelector('.metric__lbl')?.textContent?.trim() ?? '';
      const value = card.querySelector('.metric__val')?.textContent?.trim() ?? '';
      out[label.replace(/^\S+\s/, '')] = value;    // strip the leading icon glyph
    }
    return out;
  }

  function teamFilter(): TeamFilterComponent {
    return fixture.debugElement.query(By.directive(TeamFilterComponent)).componentInstance;
  }

  // ===== the four stat cards ============================================================================

  it('🔴 derives all four stat cards CLIENT-SIDE — there is no /api/reports/metrics, and it never calls one', () => {
    setUp();

    expect(cards()).toEqual({
      'WEEK TOTAL': '20.0h',      // sum(dayTotals) — NOT sum(detailRows), which is 13
      'AVG / DAY': '6.7h',        // 20 / daysLogged.logged (3) — NOT / dayTotals.length (4), which is 5.0
      'DAYS LOGGED': '3 / 5',     // the server's stat, verbatim
      'NOT LOGGED': '2',          // missingLogs.length
    });
  });

  it('🔴 renders 0.0h, not NaN, for a week with nothing logged', () => {
    arrange();
    api.getWeeklyReport.and.returnValue(of({
      dayTotals: [], daysLogged: { logged: 0, workingDays: 5 }, detailRows: [],
    }));
    mount();

    expect(cards()['AVG / DAY']).toBe('0.0h');
    expect(cards()['AVG / DAY']).not.toContain('NaN');
    expect(cards()['DAYS LOGGED']).toBe('0 / 5');
  });

  // ===== 🔴 the three wire sentinels ====================================================================

  /**
   * 🔴 THE ONE THAT WOULD BREAK THE DEFAULT VIEW FOR EVERY USER.
   *
   * "Whole team (all)" is `userId === 0` in the DROPDOWN. On the wire it must be ABSENT. The C# reads
   * `userId is int uid` and **0 IS an int**, so sending it queries the report rows of user #0 — who does not
   * exist — and every grid on the default selection renders empty, silently, with a 200.
   */
  it('🔴 whole team sends NO userId — never 0', () => {
    setUp();

    expect(component.selectedUserId()).toBe(0);          // the UI sentinel is 0...
    expect(lastWeeklyFilter()?.userId).toBeUndefined();  // ...and the wire value is ABSENT
    expect(lastMonthlyFilter()?.userId).toBeUndefined();
  });

  it('sends a real userId once a real person is picked', () => {
    setUp();

    component.onTargetChange('4');
    fixture.detectChanges();

    expect(lastWeeklyFilter()?.userId).toBe(4);
    expect(lastMonthlyFilter()?.userId).toBe(4);
  });

  /** 🔴 `rows.Where(r => r.Project == "All")` matches nothing. "All" means NO filter, so: absent. */
  it('🔴 project "All" sends NO project param', () => {
    setUp();

    expect(component.selectedProject()).toBe('All');
    expect(lastWeeklyFilter()?.project).toBeUndefined();

    component.onProjectChange('ARCS');
    fixture.detectChanges();

    expect(lastWeeklyFilter()?.project).toBe('ARCS');
  });

  it('🔴 offers PlusArcs in the project dropdown — the old mockup dropped it', () => {
    setUp();

    const options = all('#rpt-project option').map(o => o.textContent?.trim());
    expect(options).toEqual(['All', 'ARCS', 'PlusArcs', 'ARMS', 'Other']);
  });

  // ===== 🔴 THE EMPTY TEAM SELECTION ====================================================================

  /**
   * 🔴 THE INVERSION, AND THE WORST DIRECTION FOR A BUG TO FAIL IN.
   *
   * `teamIds: []` appends NO query key, and the server reads an ABSENT key as "every team you belong to".
   * So a user who unchecks every team, expecting to see nothing, would be shown EVERYTHING. There is no
   * sentinel that fixes it. The only correct behaviour is to NOT CALL AT ALL and render locally.
   *
   * This drives the REAL child component (a real toggle on the real `(selectionChange)` binding), not a
   * hand-called method — a screen that is right in the model and unwired in the template ships broken.
   */
  it('🔴 an EMPTY team selection makes NO API CALL — and does not silently widen to "all my teams"', () => {
    setUp();

    const weeklyBefore = api.getWeeklyReport.calls.count();
    const monthlyBefore = api.getMonthlyReport.calls.count();

    teamFilter().toggle(2);          // uncheck the only checked team -> selection is now []
    fixture.detectChanges();

    expect(component.teamEmpty()).toBeTrue();

    // 🔴 NOT ONE further call. Not with `[]`, and not with `undefined`.
    expect(api.getWeeklyReport.calls.count()).toBe(weeklyBefore);
    expect(api.getMonthlyReport.calls.count()).toBe(monthlyBefore);

    // The screen says so locally, rather than showing stale rows or an unexplained blank.
    expect(fixture.nativeElement.textContent).toContain('No teams selected');
    expect(component.detailRows()).toEqual([]);
    expect(component.monthlyRows()).toEqual([]);
    expect(component.flat()).toEqual([]);
  });

  it('🔴 the EXPORT is gated on the empty selection too — it would otherwise export EVERY team', () => {
    setUp();

    teamFilter().toggle(2);
    fixture.detectChanges();

    const button = fixture.debugElement.query(By.css('.filters .btn-primary')).nativeElement as HTMLButtonElement;
    expect(button.disabled).toBeTrue();

    component.exportExcel();     // belt as well as braces: even called directly, it refuses
    expect(api.exportExcel).not.toHaveBeenCalled();
  });

  it('re-checking a team resumes the reads, scoped to it', () => {
    setUp();
    teamFilter().toggle(2);
    fixture.detectChanges();

    teamFilter().toggle(1);
    fixture.detectChanges();

    expect(component.teamEmpty()).toBeFalse();
    expect(lastWeeklyFilter()?.teamIds).toEqual([1]);
  });

  it('passes the checked teams through on the happy path', () => {
    setUp();

    expect(lastWeeklyFilter()?.teamIds).toEqual([2]);     // the filter seeds to the ACTIVE team
    expect(lastMonthlyFilter()?.teamIds).toEqual([2]);
  });

  // ===== auto-load: which filter reloads which grid =====================================================

  it('🔴 a WEEK change reloads the WEEKLY report only', () => {
    setUp();
    const monthlyBefore = api.getMonthlyReport.calls.count();
    const weeklyBefore = api.getWeeklyReport.calls.count();

    component.onWeekChange('2026-07-13');
    fixture.detectChanges();

    expect(api.getWeeklyReport.calls.count()).toBe(weeklyBefore + 1);
    expect(api.getMonthlyReport.calls.count()).toBe(monthlyBefore);      // untouched
    expect(api.getWeeklyReport.calls.mostRecent().args[0]).toBe('2026-07-13');
  });

  it('🔴 a MONTH change reloads the MONTHLY report only', () => {
    setUp();
    const monthlyBefore = api.getMonthlyReport.calls.count();
    const weeklyBefore = api.getWeeklyReport.calls.count();

    component.onMonthChange('2026-08');
    fixture.detectChanges();

    expect(api.getMonthlyReport.calls.count()).toBe(monthlyBefore + 1);
    expect(api.getWeeklyReport.calls.count()).toBe(weeklyBefore);        // untouched
    expect(api.getMonthlyReport.calls.mostRecent().args[0]).toBe(2026);
    expect(api.getMonthlyReport.calls.mostRecent().args[1]).toBe(8);     // 1-based, as the API wants
  });

  it('🔴 a TARGET or PROJECT change reloads BOTH', () => {
    setUp();
    let weekly = api.getWeeklyReport.calls.count();
    let monthly = api.getMonthlyReport.calls.count();

    component.onTargetChange('5');
    expect(api.getWeeklyReport.calls.count()).toBe(weekly + 1);
    expect(api.getMonthlyReport.calls.count()).toBe(monthly + 1);

    weekly = api.getWeeklyReport.calls.count();
    monthly = api.getMonthlyReport.calls.count();

    component.onProjectChange('ARMS');
    expect(api.getWeeklyReport.calls.count()).toBe(weekly + 1);
    expect(api.getMonthlyReport.calls.count()).toBe(monthly + 1);
  });

  it('🔴 snaps a mid-week pick to that week\'s Monday before querying', () => {
    setUp();

    component.onWeekChange('2026-07-08');       // a Wednesday

    expect(component.weekMonday()).toBe('2026-07-06');
    expect(api.getWeeklyReport.calls.mostRecent().args[0]).toBe('2026-07-06');
  });

  it('the target <select> is really wired — a DOM change event reloads', () => {
    setUp();
    const before = api.getWeeklyReport.calls.count();

    const select = fixture.debugElement.query(By.css('#rpt-target')).nativeElement as HTMLSelectElement;
    select.value = '4';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(component.selectedUserId()).toBe(4);
    expect(api.getWeeklyReport.calls.count()).toBe(before + 1);
    expect(lastWeeklyFilter()?.userId).toBe(4);
  });

  // ===== the report-target dropdown =====================================================================

  it('lists the whole team first, then every ACTIVE user', () => {
    setUp();

    expect(all('#rpt-target option').map(o => o.textContent?.trim()))
      .toEqual(['Whole team (all)', 'Nhan', 'An']);
    expect(component.targets()[0].userId).toBe(0);
  });

  it('preserves the selected person across a dropdown rebuild, and falls back to the team when they go', () => {
    setUp();
    component.onTargetChange('4');                 // Nhan
    expect(component.selectedUserId()).toBe(4);

    // Nhan is deactivated elsewhere; the Users broadcast rebuilds the list without her.
    api.getUsersActive.and.returnValue(of([{ id: 5, name: 'An', isActive: true }]));
    dataChanged.next({ kind: DataKind.Users, teamId: 0 });
    fixture.detectChanges();

    expect(component.selectedUserId()).toBe(0);    // fell back to the whole team, rather than querying a ghost

    // ...but a person who IS still there keeps the selection.
    component.onTargetChange('5');
    api.getUsersActive.and.returnValue(of(USERS));
    dataChanged.next({ kind: DataKind.Users, teamId: 0 });
    fixture.detectChanges();

    expect(component.selectedUserId()).toBe(5);
  });

  // ===== the banner + the NOT LOGGED card ===============================================================

  it('renders one chip per missing user', () => {
    setUp();

    expect(all('.mchip').map(c => c.textContent?.trim())).toEqual(['Zoe', 'Pat']);
  });

  /**
   * 🔴 THE BANNER AND THE "NOT LOGGED" CARD IGNORE THE TEAM FILTER, AND THAT IS DELIBERATE — the route takes
   * NO parameters at all (N is a server-side setting; the scope is the caller's ACTIVE team, applied inside
   * the service). WPF does exactly this. Do not "fix" it into a team-scoped call: there is nothing to scope
   * it WITH.
   */
  it('🔴 the missing-logs read takes NO arguments and does NOT re-run on a team change', () => {
    setUp();
    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);
    expect(api.getMissingLogs.calls.mostRecent().args.length).toBe(0);

    teamFilter().toggle(1);          // the team scope changed...
    fixture.detectChanges();

    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);     // ...and the banner did not move
    expect(cards()['NOT LOGGED']).toBe('2');
  });

  // ===== live refresh ===================================================================================

  it('🔴 reloads the banner on Logs, and the banner + the user list on Users', () => {
    setUp();
    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);
    expect(api.getUsersActive).toHaveBeenCalledTimes(1);
    const weeklyBefore = api.getWeeklyReport.calls.count();

    dataChanged.next({ kind: DataKind.Logs, teamId: 1 });
    expect(api.getMissingLogs).toHaveBeenCalledTimes(2);
    expect(api.getUsersActive).toHaveBeenCalledTimes(1);     // Logs does not rebuild the dropdown

    dataChanged.next({ kind: DataKind.Users, teamId: 0 });
    expect(api.getMissingLogs).toHaveBeenCalledTimes(3);
    expect(api.getUsersActive).toHaveBeenCalledTimes(2);

    // Neither reloads the two grids — WPF refreshes only the banner (and the dropdown) on these.
    expect(api.getWeeklyReport.calls.count()).toBe(weeklyBefore);
  });

  it('ignores a broadcast of any other kind', () => {
    setUp();

    dataChanged.next({ kind: DataKind.Backlogs, teamId: 1 });
    dataChanged.next({ kind: DataKind.Tags, teamId: 0 });
    dataChanged.next({ kind: DataKind.Standup, teamId: 1 });

    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);
  });

  /**
   * 🔴 `kind` IS AN INTEGER ORDINAL. SignalR serialises the bare C# enum as a number, so `kind === 'Logs'`
   * compiles clean under `strict` and matches NOTHING, forever — a filter that silently drops every event on
   * a feed whose entire purpose is filtering. If anyone swaps the DataKind constants for string literals,
   * this goes red.
   */
  it('🔴 matches the realtime kind as a NUMBER, not a string', () => {
    setUp();
    expect(DataKind.Logs).toBe(3);
    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);

    // A string-shaped event is not a Logs event and must change nothing.
    dataChanged.next({ kind: 'Logs' as unknown as DataKind, teamId: 1 });
    expect(api.getMissingLogs).toHaveBeenCalledTimes(1);

    // The real, numeric one does.
    dataChanged.next({ kind: DataKind.Logs, teamId: 1 });
    expect(api.getMissingLogs).toHaveBeenCalledTimes(2);
  });

  // ===== the two grids ==================================================================================

  it('renders the weekly DETAIL rows — date · ticket · task · N1 hours', () => {
    setUp();

    const cells = all('.wtbl-body .wtbl').map(r => Array.from(r.children).map(c => c.textContent?.trim()));
    expect(cells).toEqual([
      ['Mon 06/07', 'ARCS-1001', 'Design schema', '8.0'],   // ddd dd/MM, and a BARE N1 (no unit)
      ['Tue 07/07', 'ARCS-1001', 'Build', '5.0'],
    ]);
  });

  it('renders the monthly rows — backlog · project · task · N1 hours', () => {
    setUp();

    const cells = all('.mtbl:not(.mtbl--head)').map(r => Array.from(r.children).map(c => c.textContent?.trim()));
    expect(cells).toEqual([
      ['ARCS-1001', 'ARCS', 'Design schema', '20.0'],
      ['ARMS-2002', 'ARMS', 'Triage', '5.0'],
    ]);
  });

  // ===== the drill-down tree ============================================================================

  it('🔴 the header names all FIVE levels — the old label was stale in both apps', () => {
    setUp();

    expect(text('.tree-card__title .sub')).toBe('Team → Project → Backlog → Task → Date');
  });

  it('🔴 renders an ARRAY of team roots, not a single root', () => {
    setUp();

    // Both roots are present, and both start expanded (a bare list of team names explains nothing).
    const labels = all('.tree .node .lbl').map(n => n.textContent?.trim());
    expect(labels).toContain('Alpha');
    expect(labels).toContain('Beta');
    expect(component.flat().filter(n => n.depth === 0).length).toBe(2);
  });

  /**
   * The team roots are seeded OPEN, so the button must still read "Expand all" on first paint — there are
   * four unexplored levels below. 🔴 If the toggle were driven by `some(expanded)` instead of `every(...)`,
   * it would read "Collapse all" here and the user's FIRST click would collapse the tree in their face.
   */
  it('🔴 the toggle reads "Expand all" while ANY branch is still shut, and expands all five levels', () => {
    setUp();

    expect(component.allExpanded()).toBeFalse();
    expect(text('.tree-card__title .btn')).toBe('Expand all');

    component.toggleAll();
    fixture.detectChanges();

    expect(component.allExpanded()).toBeTrue();
    expect(Math.max(...component.flat().map(n => n.depth))).toBe(4);

    const labels = all('.tree .node .lbl').map(n => n.textContent?.trim());
    expect(labels).toContain('ARCS-1001');            // depth 2
    expect(labels).toContain('Design schema');        // depth 3
    expect(labels).toContain('Mon, 2026-07-06');      // depth 4 — the leaf, `ddd, yyyy-MM-dd`
    expect(text('.tree-card__title .btn')).toBe('Collapse all');

    component.toggleAll();
    fixture.detectChanges();

    expect(component.allExpanded()).toBeFalse();
    expect(component.flat().length).toBe(2);          // every branch shut — just the two team roots
    expect(text('.tree-card__title .btn')).toBe('Expand all');
  });

  it('a click on a branch toggles just that branch', () => {
    setUp();
    const alpha = component.flat().find(n => n.label === 'Alpha')!;
    expect(alpha.expanded).toBeTrue();

    component.toggleNode(alpha.id);
    fixture.detectChanges();

    expect(component.flat().find(n => n.label === 'Alpha')!.expanded).toBeFalse();
    expect(component.flat().find(n => n.label === 'Beta')).toBeTruthy();     // Beta untouched
  });

  /** The ids are synthesised from the node PATH, so a reload of the same data must not lose the user's place. */
  it('🔴 keeps the tree\'s open branches across a reload', () => {
    setUp();
    component.toggleAll();                       // everything open
    fixture.detectChanges();
    const openBefore = component.flat().length;

    component.onMonthChange('2026-08');          // re-fetch (the stub answers with the same tree)
    fixture.detectChanges();

    expect(component.flat().length).toBe(openBefore);
    expect(component.flat().some(n => n.label === 'Mon, 2026-07-06')).toBeTrue();
  });

  /** Collapse-all must not be undone by the next reload: it writes an explicit `false`, it does not just
   *  forget. (Clearing the map would make every root "never seen" and `seedRootExpansion` would helpfully
   *  re-open them all on the very next filter change, undoing the collapse in front of the user.) */
  it('🔴 a collapsed tree STAYS collapsed across a reload', () => {
    setUp();
    component.toggleAll();      // expand everything
    component.toggleAll();      // ...then collapse everything, roots included
    fixture.detectChanges();
    expect(component.flat().length).toBe(2);

    component.onMonthChange('2026-08');
    fixture.detectChanges();

    expect(component.flat().length).toBe(2);       // still just the roots
    expect(component.allExpanded()).toBeFalse();
  });

  // ===== export =========================================================================================

  /**
   * 🔴 THE EXPORT USES THE MONTH, NOT THE WEEK — `ExportFilter(userId, Year, Month, project, teamIds)`. The
   * week is not part of it and never was. This test moves the week into a DIFFERENT month and proves the
   * export does not follow it.
   */
  it('🔴 exports the MONTH + target + project + teams, and IGNORES the selected week', () => {
    setUp();

    component.onWeekChange('2026-09-07');      // September — and irrelevant
    component.onMonthChange('2026-07');
    component.onProjectChange('ARCS');
    component.onTargetChange('4');
    fixture.detectChanges();

    component.exportExcel();

    const [year, month, filter] = api.exportExcel.calls.mostRecent().args;
    expect(year).toBe(2026);
    expect(month).toBe(7);                     // 🔴 the MONTH, not the week's September
    expect(filter?.userId).toBe(4);
    expect(filter?.project).toBe('ARCS');
    expect(filter?.teamIds).toEqual([2]);
  });

  it('🔴 a whole-team export sends NO userId — not 0', () => {
    setUp();

    component.exportExcel();

    expect(api.exportExcel.calls.mostRecent().args[2]?.userId).toBeUndefined();
  });

  it('saves the blob under the WPF file name, and says so', () => {
    setUp();
    component.onTargetChange('4');
    component.onMonthChange('2026-07');
    fixture.detectChanges();

    component.exportExcel();

    expect(component.saveBlob).toHaveBeenCalledTimes(1);
    const [blob, name] = (component.saveBlob as jasmine.Spy).calls.mostRecent().args;
    expect(blob instanceof Blob).toBeTrue();
    expect(name).toBe('Worklog-2026-07-Nhan.xlsx');
  });

  it('a whole-team export is named for the team, not for user 0', () => {
    setUp();
    component.onMonthChange('2026-07');     // pinned, so this does not go red when the calendar turns over
    fixture.detectChanges();

    component.exportExcel();

    expect((component.saveBlob as jasmine.Spy).calls.mostRecent().args[1]).toBe('Worklog-2026-07-team.xlsx');
  });

  it('a failed export does not leave the button stuck in "Exporting…"', () => {
    arrange();
    api.exportExcel.and.returnValue(throwError(() => new Error('500')));
    mount();

    component.exportExcel();
    fixture.detectChanges();

    expect(component.exporting()).toBeFalse();
    expect(component.saveBlob).not.toHaveBeenCalled();
  });

  // ===== failure paths ==================================================================================

  /** A failed read must not kill the reload stream — an error inside a switchMap that is not caught would
   *  terminate the subscription and the screen would never load again, for the rest of the session. */
  it('🔴 survives a failed report read, and RELOADS again afterwards', () => {
    arrange();
    api.getWeeklyReport.and.returnValue(throwError(() => new Error('offline')));
    mount();

    expect(component.weekly()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Nothing logged this week');

    // The stream is still alive: the next filter change really does re-query.
    api.getWeeklyReport.and.returnValue(of(WEEKLY));
    const before = api.getWeeklyReport.calls.count();

    component.onWeekChange('2026-07-13');
    fixture.detectChanges();

    expect(api.getWeeklyReport.calls.count()).toBe(before + 1);
    expect(cards()['WEEK TOTAL']).toBe('20.0h');
  });

  it('a failed user list leaves the whole-team option standing', () => {
    arrange();
    api.getUsersActive.and.returnValue(throwError(() => new Error('offline')));
    mount();

    expect(component.targets().length).toBe(1);
    expect(component.targets()[0].display).toBe('Whole team (all)');
    expect(component.selectedUserId()).toBe(0);
  });
});
