import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';

import { MeResponse, TeamDto } from '../../api/models';
import { DataChange, DataKind, RealtimeService } from '../../core/realtime.service';
import { WorklogService } from '../../services/worklog.service';
import { TeamFilterComponent } from './team-filter.component';

/**
 * The shared multi-team filter (M9/P6b). Four screens will mount it, so both of its traps are pinned here
 * rather than four times over:
 *
 *   TRAP 1  `availableTeams` is `getTeamsActive() ∩ me.memberTeamIds` -- NOT `memberTeamIds`, which is the
 *           WIDER set (every UserTeams row, with NO is_active filter). Bind to it directly and the filter
 *           lists a DEACTIVATED team that WPF hides.
 *   TRAP 2  an EMPTY selection cannot be expressed on the wire: `teamIds: []` appends NO query key, and the
 *           server reads an absent key as "ALL MY TEAMS". So the component exposes `empty()` and the SCREENS
 *           must render locally instead of calling.
 */

/** `GET /api/teams` -- ACTIVE teams only, by construction. Gamma is active but NOT one of the user's. */
const TEAMS_ACTIVE: TeamDto[] = [
  { id: 1, name: 'Alpha', isActive: true },
  { id: 2, name: 'Beta', isActive: true },
  { id: 3, name: 'Gamma', isActive: true },
];

/**
 * 🔴 `GET /api/teams/all` -- EVERY team, DEACTIVATED INCLUDED, and the wrong source for this control. Delta
 * (id 4) is deactivated, and the user is still a member of it.
 *
 * This fixture exists so the TRAP-1 tests can actually FAIL. Stub only `getTeamsActive()` and "does not offer
 * a deactivated team" is green BY CONSTRUCTION -- team 4 could never appear whatever the component did with
 * the intersection, and the test would be pure theatre. Making the wrong source AVAILABLE and asserting it is
 * NOT REACHED for is what gives the assertion teeth.
 *
 * (It is also [ADMIN]-gated and would 403 the very users this filter is for -- so reaching for it is two bugs,
 * not one.)
 */
const TEAMS_ALL: TeamDto[] = [
  ...TEAMS_ACTIVE,
  { id: 4, name: 'Delta', isActive: false },
];

/**
 * `GET /api/me`. 🔴 `memberTeamIds` carries FOUR -- a team the user still has a `UserTeams` row for but which
 * an admin has DEACTIVATED. It is therefore absent from `TEAMS_ACTIVE`, and it is the whole of TRAP 1: a
 * filter built from `memberTeamIds` would offer it.
 */
const ME: MeResponse = {
  id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 2, memberTeamIds: [1, 2, 4],
};

describe('TeamFilterComponent', () => {
  let fixture: ComponentFixture<TeamFilterComponent>;
  let component: TeamFilterComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let dataChanged: Subject<DataChange>;
  let emitted: number[][];

  function arrange(me: MeResponse = ME, teams: TeamDto[] = TEAMS_ACTIVE): void {
    dataChanged = new Subject<DataChange>();
    emitted = [];

    // 🔴 `getTeamsAll` is stubbed AND answers -- it is the WRONG source, and it must be REACHABLE for the
    // TRAP-1 tests to be able to fail. A component that calls it gets a list containing the deactivated Delta.
    api = jasmine.createSpyObj<WorklogService>('WorklogService', ['me', 'getTeamsActive', 'getTeamsAll']);
    api.me.and.returnValue(of(me));
    api.getTeamsActive.and.returnValue(of(teams));
    api.getTeamsAll.and.returnValue(of(TEAMS_ALL));

    TestBed.configureTestingModule({
      imports: [TeamFilterComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        {
          provide: RealtimeService,
          useValue: { start: () => undefined, dataChanged: dataChanged.asObservable() },
        },
      ],
    });
  }

  function mount(): void {
    fixture = TestBed.createComponent(TeamFilterComponent);
    component = fixture.componentInstance;
    component.selectionChange.subscribe(ids => emitted.push(ids));
    fixture.detectChanges();
  }

  function setUp(me?: MeResponse, teams?: TeamDto[]): void {
    arrange(me, teams);
    mount();
  }

  /** The team names actually RENDERED as checkboxes -- read off the DOM, never off `availableTeams()`. A
   *  filter that is right in the model and wrong in the template is a filter that ships broken. */
  function renderedNames(): string[] {
    return fixture.debugElement
      .queryAll(By.css('.tf__row .tf__name'))
      .map(d => (d.nativeElement as HTMLElement).textContent?.trim() ?? '');
  }

  function checkboxes(): HTMLInputElement[] {
    return fixture.debugElement
      .queryAll(By.css('.tf__row input'))
      .map(d => d.nativeElement as HTMLInputElement);
  }

  function lastEmit(): number[] | undefined {
    return emitted[emitted.length - 1];
  }

  // ---- 🔴 TRAP 1: AvailableTeams, not memberTeamIds ----------------------------------------------------

  /**
   * 🔴 THE TRAP-1 TEST. Delta (4) is in `me.memberTeamIds` and is DEACTIVATED -- the user still has a
   * `UserTeams` row for a team an admin has since switched off. `memberTeamIds` has NO `is_active` filter, so
   * it carries her; `AvailableTeams` (active ∩ member) does not.
   *
   * 🔴 AND THE SOURCE IS PINNED, which is what makes this a real test rather than a tautology. `getTeamsAll()`
   * is stubbed and WOULD hand back Delta. Point the component at it -- the obvious way to "get every team the
   * user is in" -- and Delta appears and this goes red.
   */
  it('🔴 does NOT offer a DEACTIVATED team the user is still a member of', () => {
    setUp();

    expect(ME.memberTeamIds).toContain(4);                       // the server really did send it
    expect(TEAMS_ALL.some(t => t.id === 4)).toBeTrue();          // ...and the WRONG source would name it
    expect(TEAMS_ACTIVE.some(t => t.id === 4)).toBeFalse();      // ...but it is not active

    expect(component.availableTeams().map(t => t.id)).not.toContain(4);
    expect(renderedNames()).toEqual(['Alpha', 'Beta']);
    expect(renderedNames()).not.toContain('Delta');
  });

  /**
   * 🔴 The team names come from `GET /api/teams` (OPEN, active-only) and NEVER from `GET /api/teams/all`,
   * which is [ADMIN]-gated. Reaching for `/all` would both leak deactivated teams AND 403 every ordinary user
   * this control exists for -- taking the whole screen down, since a 403 inside a forkJoin kills all of it.
   */
  it('🔴 sources the team names from the OPEN active list, never the ADMIN-gated /all', () => {
    setUp();

    expect(api.getTeamsActive).toHaveBeenCalled();
    expect(api.getTeamsAll).not.toHaveBeenCalled();
  });

  /** The other half of the intersection: an ACTIVE team the user is not a member of is not theirs to filter. */
  it('🔴 does NOT offer an ACTIVE team the user is not a member of', () => {
    setUp();

    expect(component.availableTeams().map(t => t.id)).not.toContain(3);
    expect(renderedNames()).not.toContain('Gamma');
  });

  it('offers exactly the intersection -- active ∩ member', () => {
    setUp();

    expect(component.availableTeams().map(t => t.id)).toEqual([1, 2]);
  });

  // ---- the seeded default ------------------------------------------------------------------------------

  it('defaults to the ACTIVE TEAM ONLY -- not all of them, and not none', () => {
    setUp();

    expect(component.selectedIds()).toEqual([2]);               // ME.activeTeamId
    expect(component.empty()).toBeFalse();

    const boxes = checkboxes();
    expect(boxes.map(b => b.checked)).toEqual([false, true]);   // Alpha off, Beta on
  });

  it('emits the seeded default, so a screen never has to guess it', () => {
    setUp();

    expect(lastEmit()).toEqual([2]);
  });

  it('renders the WPF header text', () => {
    setUp();

    expect(component.header()).toBe('Teams (1)');

    component.toggle(1);
    fixture.detectChanges();

    expect(component.header()).toBe('Teams (2)');
  });

  // ---- the hide rule -----------------------------------------------------------------------------------

  it('is HIDDEN for a single-team user -- there is nothing to filter', () => {
    setUp(
      { ...ME, activeTeamId: 1, memberTeamIds: [1] },
      [{ id: 1, name: 'Alpha', isActive: true }],
    );

    expect(component.availableTeams().length).toBe(1);
    expect(component.visible()).toBeFalse();
    // ...and it is really gone from the DOM, not merely `visible() === false`.
    expect(fixture.debugElement.query(By.css('.tf'))).toBeNull();
  });

  it('is VISIBLE once there are two teams to choose between', () => {
    setUp();

    expect(component.visible()).toBeTrue();
    expect(fixture.debugElement.query(By.css('.tf'))).not.toBeNull();
  });

  // ---- toggling ----------------------------------------------------------------------------------------

  it('emits the full checked set on every toggle', () => {
    setUp();

    component.toggle(1);
    expect(lastEmit()).toEqual([1, 2]);

    component.toggle(2);
    expect(lastEmit()).toEqual([1]);
  });

  it('a real checkbox CLICK toggles it -- the (change) binding is really attached', () => {
    setUp();

    checkboxes()[0].click();     // Alpha
    fixture.detectChanges();

    expect(component.selectedIds()).toEqual([1, 2]);
    expect(lastEmit()).toEqual([1, 2]);
  });

  // ---- 🔴 TRAP 2: the empty selection ------------------------------------------------------------------

  /**
   * 🔴 THE TRAP-2 TEST, and the one the whole component exists for.
   *
   * `teamIds: []` CANNOT be sent: the generated `RequestBuilder` explodes an array into one query entry per
   * element, so an empty array appends NOTHING and the key is ABSENT -- and the server reads an absent
   * `teamIds` as "every team the caller belongs to". A screen that passed `[]` would render EVERYTHING.
   *
   * So the component must SAY it is empty, loudly, and the four screens must branch on it. Make `empty()`
   * return false when nothing is checked -- or make `selectedIds()` "helpfully" fall back to every team --
   * and this goes red.
   */
  it('🔴 unchecking EVERY team is a legal state: empty() is TRUE and the selection is []', () => {
    setUp();

    component.toggle(2);         // the only checked one
    fixture.detectChanges();

    expect(component.selectedIds()).toEqual([]);
    expect(component.empty()).toBeTrue();

    // 🔴 It must EMIT the empty set, not swallow it. A screen that never hears about the change would keep
    // showing the previous team's data.
    expect(lastEmit()).toEqual([]);

    // 🔴 And it must NOT have silently re-expanded to "all my teams" -- the exact bug the wire format invites.
    expect(lastEmit()).not.toEqual([1, 2]);
  });

  /** `empty()` must be a real computation, not a constant. Without this, an `empty = computed(() => true)`
   *  would pass the test above and break every screen. */
  it('🔴 empty() is FALSE whenever anything is checked', () => {
    setUp();

    expect(component.empty()).toBeFalse();       // seeded with the active team

    component.toggle(1);
    expect(component.empty()).toBeFalse();       // two checked

    component.toggle(1);
    component.toggle(2);
    expect(component.empty()).toBeTrue();        // ...and only now

    component.toggle(1);
    expect(component.empty()).toBeFalse();       // re-checking recovers
  });

  it('tells the user what an empty selection means, rather than looking broken', () => {
    setUp();

    component.toggle(2);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('No teams selected');
  });

  // ---- reset on active-team change ---------------------------------------------------------------------

  /**
   * WPF: `OnActiveTeamChanged -> Reload()` resets the selection to `{new active team}` (F-Q3). `reload()` is
   * public because `PUT /api/me/active-team` notifies NOBODY -- the one mutating route in the API that
   * deliberately broadcasts nothing -- so whoever wires the switcher must call this themselves.
   */
  it('reload() re-seeds from the server and RESETS the selection to the new active team', () => {
    setUp();
    component.toggle(1);
    expect(component.selectedIds()).toEqual([1, 2]);

    // The user switched their active team to Alpha.
    api.me.and.returnValue(of({ ...ME, activeTeamId: 1 }));
    component.reload();
    fixture.detectChanges();

    expect(component.selectedIds()).toEqual([1]);      // reset to {new active}, NOT the old two
    expect(lastEmit()).toEqual([1]);
  });

  // ---- live sync ---------------------------------------------------------------------------------------

  /**
   * 🔴 The first real consumer of the M9/P6d payload. Before it, `dataChanged` was `Observable<void>` and this
   * filter was IMPOSSIBLE -- every change of any kind would have re-read the team list.
   */
  it('🔴 re-reads when SignalR says the TEAMS changed', () => {
    setUp();
    expect(api.getTeamsActive).toHaveBeenCalledTimes(1);

    dataChanged.next({ kind: DataKind.Teams, teamId: 0 });

    expect(api.getTeamsActive).toHaveBeenCalledTimes(2);
  });

  it('🔴 IGNORES a change of any other kind -- a timesheet write must not re-read the team list', () => {
    setUp();

    dataChanged.next({ kind: DataKind.Logs, teamId: 1 });
    dataChanged.next({ kind: DataKind.Backlogs, teamId: 1 });
    dataChanged.next({ kind: DataKind.Tags, teamId: 0 });

    expect(api.getTeamsActive).toHaveBeenCalledTimes(1);   // still just the initial load
  });

  // ---- the failure path --------------------------------------------------------------------------------

  /** A team list that will not load must not take the screen down with it. The filter hides, and the screens'
   *  own `teamIds: undefined` default already means "all my teams" -- the correct fallback. */
  it('stays hidden, and does not throw, when the reads fail', () => {
    arrange();
    api.getTeamsActive.and.returnValue(throwError(() => new Error('offline')));
    mount();

    expect(component.visible()).toBeFalse();
    expect(component.availableTeams()).toEqual([]);
    expect(fixture.debugElement.query(By.css('.tf'))).toBeNull();
  });

  /**
   * 🔴 A FAILED READ IS NOT AN EMPTY SELECTION, and conflating the two is a data leak wearing a bug's clothes.
   *
   * `empty()` means "the user unchecked every team -- render nothing and call nothing". If a network error
   * reported `empty()`, every one of the four screens would show a permanent, unexplained "no data" view on a
   * blip. And the fix someone would reach for -- "if empty, just send `[]`" -- sends the key ABSENT and shows
   * them EVERY team's data instead. Both directions are wrong; the filter must simply stay quiet and let the
   * screen's own `teamIds: undefined` default stand.
   */
  it('🔴 a FAILED read does NOT report empty() -- the screen falls back to "all my teams", not to nothing', () => {
    arrange();
    api.getTeamsActive.and.returnValue(throwError(() => new Error('offline')));
    mount();

    expect(component.empty()).toBeFalse();     // NOT an empty selection
    expect(component.loaded()).toBeFalse();    // it never resolved
    expect(emitted).toEqual([]);               // ...and it told the screen nothing, so `undefined` stands
  });
});
