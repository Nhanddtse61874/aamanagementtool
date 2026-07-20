import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';

import {
  BacklogDto, BacklogListItemDto, MeResponse, NamedRefDto, PcaContactDto, TeamDto, UserDto,
} from '../../api/models';
import { DataChange, DataKind, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { BacklogEditorComponent } from './backlog-editor.component';
import { BacklogComponent } from './backlog.component';

/**
 * The Backlog grid. Until M8.6/T6 this screen was a mockup: the list was `of([])`, the four dropdowns were
 * hard-coded literals, and both buttons raised a TOAST and did nothing. So these tests are mostly about the
 * things that were previously FAKE, and each one is a bug someone would otherwise have shipped:
 *
 *   W1  the editor is REALLY MOUNTED -- asserted with `By.directive`, never by watching a handler run. In
 *       M8.5 six of seven tests stayed green against a completely dead feature, because they all asserted on
 *       the component's own state and none of them looked at the DOM.
 *   W2  `DEFAULT` never reaches the grid -- and the API still returns it, because Log Work needs it
 *   W3  a DEPARTED assignee's name still renders -- the entire reason `getUserNames()` exists
 *   W4  ONE fetch per load, not two -- the `refresh`-as-a-Subject rule
 *   W5  a failed read does NOT kill the pipeline -- `catchError` lives INSIDE the `switchMap`, so Retry and
 *       SignalR still work after an error. Move it outside and the screen is dead until an F5.
 *   W6  a filter whose value has VANISHED from the reloaded data resets to All (`coerceFilters`)
 *
 * P6 (M10 PORT): the team filter + TEAM column. `ME`/`TEAMS_ACTIVE` below seed a SINGLE-team world on
 * purpose -- `<app-team-filter>` then hides itself and its seeded default (the active team) narrows to
 * exactly what every pre-existing test already expects, so W1-W6 above needed no fixture change beyond
 * stamping a `teamId` on every item. The multi-team behaviour (TEAM column, narrowing, "no teams selected")
 * gets its own describe block, each test re-arranging with a second team.
 */

/** Assignee of ARMS-2001. She is in `NAMES` and NOT in the active users -- she has left. See W3. */
const DEPARTED = 3;

/**
 * What `GET /api/backlogs` returns -- INCLUDING the hidden `DEFAULT` backlog, because it really does return
 * it and Log Work really does need it. Excluding it is this screen's job, on the client. See W2.
 */
const ITEMS: BacklogListItemDto[] = [
  {
    id: 1, backlogCode: 'ARCS-1042', project: 'ARCS', periodMonth: '2026-07',
    type: 'Implement', assigneeUserId: 1, taskCount: 3, teamId: 1,
  },
  {
    id: 2, backlogCode: 'ARMS-2001', project: 'ARMS', periodMonth: '2026-06',
    type: 'Investigate', assigneeUserId: DEPARTED, taskCount: 1, teamId: 1,
  },
  {
    id: 9, backlogCode: 'DEFAULT', project: '', periodMonth: null,
    type: null, assigneeUserId: null, taskCount: 5, teamId: 1,
  },
];

/** `GET /api/users/names` -- id + name for EVERY user, deactivated included. Dana (3) has left. */
const NAMES: NamedRefDto[] = [
  { id: 1, name: 'Nhan' },
  { id: DEPARTED, name: 'Dana' },
];

/**
 * P6. `GET /api/me` + `GET /api/teams` — the reads `<app-team-filter>` makes on its own. A SINGLE-team
 * world: the filter hides itself (nothing to choose between) and its seeded default -- the active team --
 * is the one team every fixture item already carries, so it narrows nothing. See the class doc.
 */
const ME: MeResponse = { id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 1, memberTeamIds: [1] };
const TEAMS_ACTIVE: TeamDto[] = [{ id: 1, name: 'Alpha', isActive: true }];

/** The ACTIVE users -- what the EDITOR's assignee dropdown offers. Dana is not here; that is the point. */
const USERS: UserDto[] = [{ id: 1, name: 'Nhan', isActive: true }];
const CONTACTS: PcaContactDto[] = [{ id: 5, name: 'Yuki', isActive: true }];
const BACKLOG: BacklogDto = {
  id: 2, backlogCode: 'ARMS-2001', project: 'ARMS', periodMonth: '2026-06', rowVersion: 4,
};

/**
 * M9/P6d. `RealtimeService.dataChanged` used to be `Observable<void>` -- the hub handler took the server's
 * `(kind, teamId)` and threw BOTH away, so no screen could tell what had changed. It now carries a real
 * `DataChange`, and these stubs move with it: a `Subject<void>` here is a TS2322 against the widened type.
 *
 * This screen still re-reads on ANY change -- `.subscribe(() => this.refresh.next())` ignores the payload, and
 * a zero-arg callback stays assignable to `(v: DataChange) => void`, which is why `backlog.component.ts` itself
 * needed no edit. The payload is now merely AVAILABLE to it. Whether it filters on `kind` is its own call, and
 * it does not, yet.
 */
const CHANGED: DataChange = { kind: DataKind.Backlogs, teamId: 1 };

describe('BacklogComponent', () => {
  let fixture: ComponentFixture<BacklogComponent>;
  let component: BacklogComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let dataChanged: Subject<DataChange>;

  /** The spies and their happy-path answers. Split from `mount` so a test can bend one BEFORE the load runs. */
  function arrange(): void {
    dataChanged = new Subject<DataChange>();

    api = jasmine.createSpyObj<WorklogService>(
      'WorklogService',
      // The grid's THREE reads (P6 added `getTeamsActive`), then everything the EDITOR touches -- it is
      // really mounted in these tests (W1), so its own load path runs for real and every method it calls
      // must answer. `me`/`getTeamsActive` are ALSO what the real, really-mounted `<app-team-filter>` calls.
      ['getBacklogList', 'getUserNames', 'me', 'getTeamsActive', 'typeColor', 'avatarColor',
       'getBacklog', 'getTasks', 'getBacklogAudit', 'getUsersActive', 'getPcaContactsActive',
       'createBacklog', 'updateBacklog', 'addTask', 'updateTask', 'setTaskActive'],
    );

    api.getBacklogList.and.returnValue(of(ITEMS));
    api.getUserNames.and.returnValue(of(NAMES));
    api.me.and.returnValue(of(ME));
    api.getTeamsActive.and.returnValue(of(TEAMS_ACTIVE));

    api.getBacklog.and.returnValue(of(BACKLOG));
    api.getTasks.and.returnValue(of([]));
    api.getBacklogAudit.and.returnValue(of([]));
    api.getUsersActive.and.returnValue(of(USERS));
    api.getPcaContactsActive.and.returnValue(of(CONTACTS));

    // The two presentation helpers, kept honest rather than stubbed to undefined -- the template really calls
    // them for every row.
    api.typeColor.and.callFake((t: string | null) => (t ? { bg: '#E7F2EC', c: '#1B7A4B' } : null));
    api.avatarColor.and.callFake((n: string | null) => (n ? '#0E7C66' : ''));

    const realtime: Partial<RealtimeService> = {
      start: () => undefined,
      dataChanged: dataChanged.asObservable(),
    };

    TestBed.configureTestingModule({
      imports: [BacklogComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: ToastService, useValue: jasmine.createSpyObj<ToastService>('ToastService', ['show']) },
        { provide: RealtimeService, useValue: realtime },
      ],
    });
  }

  function mount(): void {
    fixture = TestBed.createComponent(BacklogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function setUp(): void {
    arrange();
    mount();
  }

  // ---- reading the query helpers as one place, so a class rename cannot quietly hollow out a test ----
  function rowCodes(): string[] {
    return fixture.debugElement.query(By.css('.tbl')) === null ? [] : fixture.debugElement
      .queryAll(By.css('.tbl__row .code'))
      .map(d => (d.nativeElement as HTMLElement).textContent?.trim() ?? '');
  }

  function editor(): BacklogEditorComponent | null {
    const found = fixture.debugElement.query(By.directive(BacklogEditorComponent));
    return found === null ? null : (found.componentInstance as BacklogEditorComponent);
  }

  function screenText(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  // ---- the read path ---------------------------------------------------------------------------------

  it('renders the REAL backlogs, joined to their assignee names', () => {
    setUp();

    expect(api.getBacklogList).toHaveBeenCalled();
    expect(rowCodes()).toEqual(['ARCS-1042', 'ARMS-2001']);
    expect(screenText()).toContain('Nhan');
  });

  /**
   * W3. `GET /api/users/all` is admin-only and would 403 an ordinary user -- taking the whole forkJoin, and
   * therefore the WHOLE LIST, down with it. `/names` is the route that names a departed user, and this is
   * what it buys: her row still says who owns it.
   */
  it('names a DEPARTED assignee -- the reason getUserNames() is the route, not getUsersAll()', () => {
    setUp();

    expect(api.getUserNames).toHaveBeenCalled();
    expect(screenText()).toContain('Dana');
  });

  /**
   * W2. The API returns DEFAULT (Log Work needs it -- ReadModels.cs:68) and this screen must drop it. The
   * assertion is on the RENDERED TEXT, not on `rows()`: a filter that works in the model and not in the
   * template is exactly the bug this is here to catch.
   */
  it('never shows the DEFAULT backlog, even though the API returns it', () => {
    setUp();

    expect(ITEMS.some(i => i.backlogCode === 'DEFAULT')).toBeTrue();   // the API really did send it
    expect(screenText()).not.toContain('DEFAULT');
    expect(component.rows().some(r => r.code === 'DEFAULT')).toBeFalse();
  });

  /** W4. A `refresh` SIGNAL would replay through `toObservable()` and fetch the list twice on every load. */
  it('fetches the list exactly ONCE on load', () => {
    setUp();

    expect(api.getBacklogList).toHaveBeenCalledTimes(1);
    expect(api.getUserNames).toHaveBeenCalledTimes(1);
  });

  // ---- the filters -----------------------------------------------------------------------------------

  it('narrows the list by a filter', () => {
    setUp();

    component.patch('project', 'ARMS');
    fixture.detectChanges();

    expect(rowCodes()).toEqual(['ARMS-2001']);
    expect(screenText()).toContain('1 backlogs');
  });

  it('narrows the list by the search term, against the code OR the project', () => {
    setUp();

    component.patch('term', 'arcs');       // lower case: the match is case-insensitive
    fixture.detectChanges();

    expect(rowCodes()).toEqual(['ARCS-1042']);
  });

  /**
   * The four dropdowns are built FROM THE DATA. They used to be literals -- `['All','2026-06','2026-07',
   * '2026-08']` -- so 2026-08 was an offered filter that could only ever return nothing, and any month
   * outside the hand-written three was simply unreachable.
   */
  it('drives every dropdown from the loaded rows, not from a hard-coded literal', () => {
    setUp();

    expect(component.options().months).toEqual(['All', '2026-06', '2026-07']);
    expect(component.options().projects).toEqual(['All', 'ARCS', 'ARMS']);
    expect(component.options().assignees).toEqual(['All', 'Dana', 'Nhan']);
    expect(component.options().types).toEqual(['All', 'Implement', 'Investigate']);
  });

  /** W6. `rebuildOptions` cannot do this: it never sees the filters. `coerceFilters` is the other half. */
  it('resets a filter whose value has VANISHED from the reloaded data', () => {
    setUp();
    component.patch('project', 'ARMS');
    fixture.detectChanges();
    expect(rowCodes()).toEqual(['ARMS-2001']);

    // ARMS-2001 is gone -- someone else deleted it. Without `coerceFilters` the user would now be staring at
    // an empty grid whose Project dropdown reads "ARMS", a value no longer in its own option list.
    api.getBacklogList.and.returnValue(of([ITEMS[0]]));
    dataChanged.next(CHANGED);
    fixture.detectChanges();

    expect(component.filters().project).toBe('All');
    expect(rowCodes()).toEqual(['ARCS-1042']);
  });

  // ---- the empty states ------------------------------------------------------------------------------

  it('shows the FILTERED empty state when the filters exclude everything', () => {
    setUp();

    component.patch('project', 'Nothing matches this');
    fixture.detectChanges();

    expect(rowCodes()).toEqual([]);
    expect(fixture.debugElement.query(By.css('.empty'))).not.toBeNull();
    expect(screenText()).toContain('No backlogs match your filters');
  });

  it('shows the NOTHING-HERE empty state when there are no backlogs at all', () => {
    arrange();
    api.getBacklogList.and.returnValue(of([]));
    mount();

    expect(screenText()).toContain('No backlogs yet');
    expect(screenText()).not.toContain('No backlogs match your filters');
    // The stub's copy, which told the USER to go and call a TypeScript method, is gone.
    expect(screenText()).not.toContain('getBacklogs');
  });

  // ---- the editor: W1, the whole point of the task ----------------------------------------------------

  it('does NOT render the editor until it is asked to', () => {
    setUp();

    expect(editor()).toBeNull();
  });

  /**
   * 🔴 THE test. `+ New backlog` used to call `notify('New backlog created')` -- a toast announcing a record
   * that was never written. It must now MOUNT THE EDITOR, and this asserts the editor is really in the DOM
   * (`By.directive`), not merely that a handler ran.
   */
  it('mounts the editor in CREATE mode when + New backlog is clicked', () => {
    setUp();

    fixture.debugElement.query(By.css('.bl-head .btn-primary'))
      .triggerEventHandler('click', null);
    fixture.detectChanges();

    const open = editor();
    expect(open).not.toBeNull();
    expect(open?.backlogId()).toBeNull();       // null = CREATE
  });

  /** `Edit` used to call `notify('Editing ' + b.code)` and open nothing. */
  it('mounts the editor on THAT backlog when its Edit button is clicked', () => {
    setUp();

    // The second row is ARMS-2001, id 2 -- so the id must be 2, not the row's index and not the first row's.
    fixture.debugElement.queryAll(By.css('.tbl__row .right .btn'))[1]
      .triggerEventHandler('click', null);
    fixture.detectChanges();

    expect(editor()?.backlogId()).toBe(2);
  });

  it('closes the editor on its (cancel), and does NOT re-fetch', () => {
    setUp();
    component.openCreate();
    fixture.detectChanges();

    // 🔴 Asserted BEFORE the emit, and not as ceremony: `editor()?.cancel.emit()` is a silent no-op when the
    // editor is not mounted, so without this line the `toBeNull()` below would pass against a template that
    // never renders the editor AT ALL -- a test that is green precisely when the feature is dead.
    const open = editor();
    expect(open).not.toBeNull();

    open?.cancel.emit();
    fixture.detectChanges();

    expect(editor()).toBeNull();
    expect(api.getBacklogList).toHaveBeenCalledTimes(1);   // nothing was written; nothing to re-read
  });

  /**
   * A save can add a row, rename a code, reassign an owner or change a task count -- four of this grid's own
   * columns -- so the list must be re-read from the server, not patched from a guess.
   */
  it('RE-FETCHES the list and closes the editor when the editor emits (saved)', () => {
    setUp();
    component.openCreate();
    fixture.detectChanges();
    expect(api.getBacklogList).toHaveBeenCalledTimes(1);

    editor()?.saved.emit();
    fixture.detectChanges();

    expect(api.getBacklogList).toHaveBeenCalledTimes(2);
    expect(editor()).toBeNull();
  });

  // ---- SignalR + the failure path --------------------------------------------------------------------

  it('re-reads the list when SignalR says someone else changed the data', () => {
    setUp();

    dataChanged.next(CHANGED);

    expect(api.getBacklogList).toHaveBeenCalledTimes(2);
  });

  it('shows an error, and no rows, when the list cannot be read', () => {
    arrange();
    api.getBacklogList.and.returnValue(throwError(() => new Error('offline')));
    mount();

    expect(component.loadError()).not.toBeNull();
    expect(component.loading()).toBeFalse();
    expect(fixture.debugElement.query(By.css('.bl-error'))).not.toBeNull();
    // NOT the empty state: "we could not read the list" and "the list is empty" are different sentences, and
    // telling someone their backlogs do not exist because the network blinked is the wrong one.
    expect(screenText()).not.toContain('No backlogs yet');
  });

  /**
   * 🔴 W5. THE `catchError`-PLACEMENT TEST, and the reason this one is worth its weight: with `catchError`
   * OUTSIDE the `switchMap` the failure completes the OUTER stream, and the component never fetches again --
   * Retry does nothing, SignalR does nothing, and the screen is dead until the user reloads the page. Every
   * other test here would still pass.
   */
  it('SURVIVES a failed read: Retry re-fetches and the rows arrive', () => {
    arrange();
    api.getBacklogList.and.returnValue(throwError(() => new Error('offline')));
    mount();
    expect(component.loadError()).not.toBeNull();

    api.getBacklogList.and.returnValue(of(ITEMS));
    fixture.debugElement.query(By.css('.bl-error .btn')).triggerEventHandler('click', null);
    fixture.detectChanges();

    expect(component.loadError()).toBeNull();
    expect(rowCodes()).toEqual(['ARCS-1042', 'ARMS-2001']);
  });

  it('SURVIVES a failed read: a later SignalR push still re-reads', () => {
    arrange();
    api.getBacklogList.and.returnValue(throwError(() => new Error('offline')));
    mount();

    api.getBacklogList.and.returnValue(of(ITEMS));
    dataChanged.next(CHANGED);
    fixture.detectChanges();

    expect(rowCodes()).toEqual(['ARCS-1042', 'ARMS-2001']);
  });

  // ---- P6: the team filter + TEAM column ---------------------------------------------------------------
  //
  // A second, MULTI-team world: two teams the user belongs to, one backlog on each. `<app-team-filter>`'s
  // own seeded default is the ACTIVE team ONLY (its own doc, "not all, and not none") -- so every test below
  // that wants BOTH teams in scope calls `component.onTeams([1, 2])` itself, exactly as
  // `task-list.component.spec.ts` does for the identical shared component.

  const ME_MULTI: MeResponse = { id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 1, memberTeamIds: [1, 2] };
  const TEAMS_MULTI: TeamDto[] = [
    { id: 1, name: 'Alpha', isActive: true },
    { id: 2, name: 'Beta', isActive: true },
  ];
  const ITEMS_MULTI: BacklogListItemDto[] = [
    {
      id: 1, backlogCode: 'ARCS-1042', project: 'ARCS', periodMonth: '2026-07',
      type: 'Implement', assigneeUserId: 1, taskCount: 3, teamId: 1,
    },
    {
      id: 2, backlogCode: 'ARMS-2001', project: 'ARMS', periodMonth: '2026-06',
      type: 'Investigate', assigneeUserId: DEPARTED, taskCount: 1, teamId: 2,
    },
  ];

  function arrangeMultiTeam(): void {
    arrange();
    api.me.and.returnValue(of(ME_MULTI));
    api.getTeamsActive.and.returnValue(of(TEAMS_MULTI));
    api.getBacklogList.and.returnValue(of(ITEMS_MULTI));
  }

  function teamPills(): string[] {
    return fixture.debugElement
      .queryAll(By.css('.badge--team'))
      .map(d => (d.nativeElement as HTMLElement).textContent?.trim() ?? '');
  }

  it('hides the TEAM column for a single-team user — nothing to disambiguate', () => {
    setUp();      // the default, single-team fixture

    expect(component.showTeam()).toBeFalse();
    expect(fixture.debugElement.query(By.css('.tbl--team'))).toBeNull();
    expect(screenText()).not.toContain('TEAM');
  });

  it('shows the TEAM column, with each row’s resolved name, once >1 team is checked', () => {
    arrangeMultiTeam();
    mount();

    component.onTeams([1, 2]);
    fixture.detectChanges();

    expect(component.showTeam()).toBeTrue();
    expect(fixture.debugElement.query(By.css('.tbl--team'))).not.toBeNull();
    expect(teamPills()).toEqual(['Alpha', 'Beta']);
  });

  it('narrows the grid to the checked teams — CLIENT-SIDE, with no re-fetch', () => {
    arrangeMultiTeam();
    mount();
    component.onTeams([1, 2]);
    fixture.detectChanges();
    expect(rowCodes()).toEqual(['ARCS-1042', 'ARMS-2001']);
    const callsBefore = api.getBacklogList.calls.count();

    component.onTeams([1]);
    fixture.detectChanges();

    expect(rowCodes()).toEqual(['ARCS-1042']);
    expect(api.getBacklogList).toHaveBeenCalledTimes(callsBefore);    // still no re-fetch
  });

  it('shows "No teams selected" and hides the grid when every team is unchecked', () => {
    arrangeMultiTeam();
    mount();
    component.onTeams([1, 2]);
    fixture.detectChanges();

    component.onTeams([]);
    fixture.detectChanges();

    expect(component.noTeams()).toBeTrue();
    expect(screenText()).toContain('No teams selected');
    // Not the GENERIC "no rows match your filters" state -- its Clear-filters button would not help here.
    expect(screenText()).not.toContain('No backlogs match your filters');
    expect(fixture.debugElement.query(By.css('.tbl'))).toBeNull();
  });

  it('re-checking a team recovers the grid', () => {
    arrangeMultiTeam();
    mount();
    component.onTeams([]);
    fixture.detectChanges();
    expect(component.noTeams()).toBeTrue();

    component.onTeams([2]);
    fixture.detectChanges();

    expect(component.noTeams()).toBeFalse();
    expect(rowCodes()).toEqual(['ARMS-2001']);
  });
});
