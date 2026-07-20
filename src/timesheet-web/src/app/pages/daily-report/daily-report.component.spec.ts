import { CdkDrag, CdkDragDrop, CdkDropList } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';

import {
  BacklogListItemDto, MeResponse, SettingsStandupEntryView, SettingsUserStandup, StandupIssueDto, TeamDto,
} from '../../api/models';
import { DataChange, DataKind, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { DailyReportComponent, DropZone } from './daily-report.component';
import { StandupSection, addDays, todayIso } from './standup-day';

/**
 * A CDK drop event, hand-built. Only the four fields `onDrop` actually reads are meaningful; the rest exist
 * to satisfy the type. `item.data` is the dragged entry (bound via `[cdkDragData]`), `container.data` is the
 * zone (bound via `[cdkDropListData]`).
 */
function dropOnto(
  zone: DropZone, currentIndex: number, dragged: SettingsStandupEntryView,
): CdkDragDrop<DropZone, DropZone, SettingsStandupEntryView> {
  return {
    previousIndex: 0,
    currentIndex,
    item: { data: dragged } as CdkDrag<SettingsStandupEntryView>,
    container: { data: zone } as CdkDropList<DropZone>,
    previousContainer: { data: zone } as CdkDropList<DropZone>,
    isPointerOverContainer: true,
    distance: { x: 0, y: 0 },
    dropPoint: { x: 0, y: 0 },
    event: new MouseEvent('mouseup'),
  };
}

/**
 * 🔴 NO DATE IS HARD-CODED. The component seeds itself from `new Date()`, so the suite derives its
 * expectations from the same helper the component uses. A hard-coded '2026-07-12' would make this file go red
 * on 2026-07-13 — a test failing for the wrong reason.
 */
const TODAY = todayIso();
const YESTERDAY = addDays(TODAY, -1);
const TWO_DAYS_AGO = addDays(TODAY, -2);

const ME: MeResponse = { id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 2, memberTeamIds: [1, 2] };
const ADMIN: MeResponse = { ...ME, isAdmin: true };

const TEAMS: TeamDto[] = [
  { id: 1, name: 'Alpha', isActive: true },
  { id: 2, name: 'Beta', isActive: true },
];

/**
 * 🔴 `GET /api/backlogs` RETURNS `DEFAULT` — Log Work needs it, so the route cannot drop it, and the standup
 * picker must. It is in this fixture precisely so the "excludes DEFAULT" test can actually FAIL: a fixture
 * without it would pass by construction and assert nothing.
 */
const BACKLOGS: BacklogListItemDto[] = [
  { id: 1, backlogCode: 'ARCS-1001', project: 'ARCS', teamId: 2 },       // ME.activeTeamId === 2 -> kept
  { id: 99, backlogCode: 'DEFAULT', project: 'Recurring', teamId: 2 },   // hidden -> dropped by code
  { id: 2, backlogCode: 'BETA-2002', project: 'ARMS', teamId: 3 },       // another team -> dropped by DR-11
];

function issue(over: Partial<StandupIssueDto> = {}): StandupIssueDto {
  return {
    id: 50, entryId: 10, issueText: 'DB deadlock', solutionText: null, status: 'open',
    orderIndex: 0, rowVersion: 7, ...over,
  };
}

function view(
  id: number, section: StandupSection, editable = true, issues: StandupIssueDto[] = [],
): SettingsStandupEntryView {
  return {
    editable,
    issues,
    entry: {
      id, userId: 1, workDate: TODAY, section, backlogId: 1, backlogCode: `ARCS-${id}`,
      taskText: `Task ${id}`, description: '', deadline: null, status: 'Todo', orderIndex: 0, teamId: 2,
    },
  };
}

function myDay(over: Partial<SettingsUserStandup> = {}): SettingsUserStandup {
  return { userId: 1, userName: 'Nhan', yesterday: [], today: [view(10, 'today')], ...over };
}

describe('DailyReportComponent', () => {
  let fixture: ComponentFixture<DailyReportComponent>;
  let component: DailyReportComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let toast: ToastService;
  let dataChanged: Subject<DataChange>;

  function arrange(me: MeResponse = ME, day: SettingsUserStandup = myDay()): void {
    dataChanged = new Subject<DataChange>();

    api = jasmine.createSpyObj<WorklogService>('WorklogService', [
      'me', 'getStandupMyDay', 'getStandupBoard', 'getBacklogList', 'getTasks',
      'createStandupEntry', 'updateStandupEntry', 'deleteStandupEntry', 'reorderStandupEntry',
      'quickImportStandup', 'createStandupIssue', 'updateStandupIssue', 'deleteStandupIssue',
      'archiveStandupWeek', 'getTeamsActive', 'avatarColor',
    ]);

    api.me.and.returnValue(of(me));
    api.getStandupMyDay.and.returnValue(of(day));
    api.getStandupBoard.and.returnValue(of([]));
    api.getBacklogList.and.returnValue(of(BACKLOGS));
    api.getTasks.and.returnValue(of([]));
    api.getTeamsActive.and.returnValue(of(TEAMS));
    api.createStandupEntry.and.returnValue(of(11));
    api.updateStandupEntry.and.returnValue(of(void 0));
    api.deleteStandupEntry.and.returnValue(of(void 0));
    api.reorderStandupEntry.and.returnValue(of(void 0));
    api.quickImportStandup.and.returnValue(of(2));
    api.createStandupIssue.and.returnValue(of(51));
    api.updateStandupIssue.and.returnValue(of({ rowVersion: 8 }));
    api.deleteStandupIssue.and.returnValue(of(void 0));
    api.archiveStandupWeek.and.returnValue(of({ path: '\\\\share\\week.md' }));
    api.avatarColor.and.returnValue('#0E7C66');

    TestBed.configureTestingModule({
      imports: [DailyReportComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        {
          provide: RealtimeService,
          useValue: { start: () => undefined, dataChanged: dataChanged.asObservable() },
        },
      ],
    });

    fixture = TestBed.createComponent(DailyReportComponent);
    component = fixture.componentInstance;
    toast = TestBed.inject(ToastService);
  }

  /**
   * Flush the component's async work, then render.
   *
   * 🔴 `whenStable()` ALONE IS NOT ENOUGH HERE, and the failure it produces is a liar. A load is a CHAIN of
   * awaited promises (`me` -> `getStandupMyDay` -> `getStandupBoard` -> `getBacklogList`), and the zone can
   * report stable between links. Render at that moment and the DOM still carries `[disabled]="loading()"`
   * from the previous tick — so a `.click()` on a nav button silently does NOTHING, and the test fails as
   * though the handler were unwired. (It cost one debugging round here: `loading()` read `false` while the
   * button's `disabled` attribute was still `true`.)
   *
   * `setTimeout(0)` is a MACROTASK: it runs strictly after the entire microtask queue drains, so each pass
   * observes a fully-settled component. Three passes cover the longest chain with room to spare.
   */
  async function settle(): Promise<void> {
    fixture.detectChanges();
    for (let i = 0; i < 3; i++) {
      await new Promise<void>(resolve => setTimeout(resolve, 0));
      await fixture.whenStable();
      fixture.detectChanges();
    }
  }

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  function buttonWithText(label: string): HTMLButtonElement | null {
    return fixture.debugElement.queryAll(By.css('button'))
      .map(d => d.nativeElement as HTMLButtonElement)
      .find(b => (b.textContent ?? '').trim().includes(label)) ?? null;
  }

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // 🔴 THE RULE THAT DEFINES THIS SCREEN
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('🔴 ADD + DELETE ONLY — there is no edit-entry affordance, and there must not be', () => {
    /**
     * `StandupEntries` has NO `row_version` column — not on the table, the record, the DTO, or the update
     * request. It is deliberately unversioned, last-write-wins, and the API's DTO contract says the reason out
     * loud: "owner-gated ... Two users cannot reach the same row, so there is no race to guard."
     *
     * 🔴 That argument holds for two USERS. It does not hold for TWO BROWSER TABS, which are the same user —
     * and WPF, being one process, never had them. An edit button here would manufacture a lost-update race
     * that the desktop could not have, on the one standup table with no version to catch it.
     *
     * `PUT /api/standup/entries/{id}` exists and WPF NEVER CALLS IT: `StandupEntryRowVm` exposes a single
     * `DeleteAsync` command and every field is a getter. This test is the guard on that, and it is why
     * `updateStandupEntry` is stubbed above — it must be REACHABLE for this to be able to fail.
     */
    /**
     * 🔴 THIS TEST EXERCISES EVERY MUTATING PATH ON THE SCREEN, AND THAT IS THE WHOLE POINT.
     *
     * The first version of it drove only a load and two tab switches, then asserted
     * `updateStandupEntry` was never called. It PASSED — and it passed just as happily against a planted
     * `updateStandupEntry(...)` call inside `deleteEntry`, because it never deleted anything. A green test
     * against a broken feature: exactly the M8.5 failure mode.
     *
     * "Never called" is only a real assertion if the test first DOES everything that could have called it.
     * So: add, delete, quick-import, reorder, add-issue, save-issue, delete-issue, archive, day-nav, both
     * tabs — and only then, the assertion.
     */
    it('NEVER calls updateStandupEntry — through every mutating path this screen has', async () => {
      arrange(ADMIN, myDay({
        yesterday: [view(9, 'yesterday')],
        today: [view(10, 'today', true, [issue()]), view(11, 'today')],
      }));
      await settle();

      // every write the screen can make
      component.openAdd('today');
      component.patch('backlogCode', 'ARCS-1');
      component.patch('taskText', 'Do it');
      await component.submitEntry();

      await component.quickImport();
      await component.addIssue(10, 'Something broke');
      await component.saveIssue(10, issue(), 'Fixed it', 'resolved');
      await component.deleteIssue(10, 50);
      await component.onDrop(dropOnto('today', 0, view(11, 'today')));
      await component.deleteEntry(view(10, 'today'));
      await component.archiveWeek();

      // and every read/navigation
      component.prevDay();
      await settle();
      component.nextDay();
      await settle();
      component.tab.set('team');
      await settle();
      component.tab.set('input');
      await settle();

      // Proof the paths above were really taken — otherwise "never called" is vacuous.
      expect(api.createStandupEntry).toHaveBeenCalled();
      expect(api.deleteStandupEntry).toHaveBeenCalled();
      expect(api.reorderStandupEntry).toHaveBeenCalled();
      expect(api.quickImportStandup).toHaveBeenCalled();
      expect(api.createStandupIssue).toHaveBeenCalled();
      expect(api.updateStandupIssue).toHaveBeenCalled();
      expect(api.deleteStandupIssue).toHaveBeenCalled();

      // 🔴 THE ASSERTION.
      expect(api.updateStandupEntry).not.toHaveBeenCalled();
    });

    it('renders no edit/save control on an entry — only delete', async () => {
      arrange();
      await settle();

      expect(buttonWithText('Delete entry')).not.toBeNull();
      expect(buttonWithText('Edit')).toBeNull();
      expect(buttonWithText('Save entry')).toBeNull();
    });

    it('does not make an entry\'s fields editable', async () => {
      arrange();
      await settle();

      // The only inputs on the Input tab belong to the ISSUE editors (collaborative, by design). No input is
      // bound to an entry's code / task / status / deadline.
      const entryCard = fixture.debugElement.query(By.css('.entry'));
      const inputs = entryCard.queryAll(By.css('input, textarea'));
      expect(inputs.length).toBe(0);
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // H1 — the edit-lock
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('H1 — the edit-lock is the SERVER\'s; the client mirror only decides what to OFFER', () => {
    it('offers Add + Quick import on today', async () => {
      arrange();
      await settle();

      expect(buttonWithText('+ Add entry')).not.toBeNull();
      expect(buttonWithText('⇕ Quick import')).not.toBeNull();
    });

    it('offers them on YESTERDAY too — the lock is two days, not one', async () => {
      arrange();
      await settle();

      component.prevDay();
      await settle();

      expect(component.date()).toBe(YESTERDAY);
      expect(buttonWithText('+ Add entry')).not.toBeNull();
    });

    it('HIDES both add-buttons once the day is locked, and says why', async () => {
      arrange();
      await settle();

      component.prevDay();
      await settle();
      component.prevDay();
      await settle();

      expect(component.date()).toBe(TWO_DAYS_AGO);
      expect(component.canEdit()).toBeFalse();
      expect(buttonWithText('+ Add entry')).toBeNull();
      expect(buttonWithText('⇕ Quick import')).toBeNull();
      expect(text()).toContain('This day is locked');
    });

    /**
     * 🔴 The mirror must never PERMIT what the server refuses. When the server disagrees, the server wins and
     * the user is TOLD — its 400 body carries the real reason.
     */
    it('surfaces the server\'s 400 verbatim rather than trusting its own guess', async () => {
      arrange();
      await settle();

      api.createStandupEntry.and.returnValue(throwError(() => new HttpErrorResponse({
        status: 400,
        error: { error: 'Cannot add: the day is locked (editable only today or yesterday).' },
      })));

      component.openAdd('today');
      component.patch('backlogCode', 'ARCS-1');
      component.patch('taskText', 'Do it');
      await component.submitEntry();

      expect(toast.message()).toBe('Cannot add: the day is locked (editable only today or yesterday).');
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // H2 — issues are exempt from the lock AND the owner gate
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('H2 — issues are exempt from the lock, by design (DR-04: anyone, anytime)', () => {
    it('still offers "+ Issue" on a LOCKED day — AddIssueAsync has no CanEditDay', async () => {
      arrange();
      await settle();

      component.prevDay();
      await settle();
      component.prevDay();
      await settle();

      expect(component.canEdit()).toBeFalse();
      // The add-entry button is gone; the issue button is NOT.
      expect(buttonWithText('+ Add entry')).toBeNull();
      expect(buttonWithText('+ Issue')).not.toBeNull();
    });

    /**
     * 🔴 The whole-record-overwrite trap, at the component level. `toIssueUpdateBody` round-trips `issueText`
     * and `status` off the loaded issue; a body built from the solution box alone compiles clean and 400s.
     */
    it('round-trips issueText, status and rowVersion when saving a solution', async () => {
      arrange(ME, myDay({ today: [view(10, 'today', true, [issue()])] }));
      await settle();

      await component.saveIssue(10, issue(), 'Added an index', 'resolved');

      expect(api.updateStandupIssue).toHaveBeenCalledWith(10, 50, {
        issueText: 'DB deadlock',        // 🔴 round-tripped — omit it and the server writes null -> 400
        status: 'resolved',              // 🔴 round-tripped — a null status is likewise a 400
        solutionText: 'Added an index',
        expectedVersion: 7,              // the CHECKED write's token
      });
    });

    /**
     * 🔴 A 409 on an issue is NOT a merge. `ConflictDialogComponent` resolves a TIMESHEET CELL — two numbers,
     * pick one. An issue's solution is free text; there is nothing to merge. Say so, re-read, stop.
     */
    it('answers a 409 with a message and a RE-READ — never a merge dialog', async () => {
      arrange(ME, myDay({ today: [view(10, 'today', true, [issue()])] }));
      await settle();

      api.getStandupMyDay.calls.reset();
      api.updateStandupIssue.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));

      await component.saveIssue(10, issue(), 'Added an index', 'resolved');

      expect(toast.message()).toContain('changed by someone else');
      expect(api.getStandupMyDay).toHaveBeenCalled();                       // re-read
      expect(fixture.debugElement.query(By.css('app-conflict-dialog'))).toBeNull();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // H3 — the backlog picker
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('H3 — the picker excludes DEFAULT, client-side', () => {
    it('drops DEFAULT from the picker while GET /api/backlogs still returns it', async () => {
      arrange();
      await settle();

      expect(api.getBacklogList).toHaveBeenCalled();
      expect(component.backlogs().map(b => b.backlogCode)).toEqual(['ARCS-1001']);
      expect(component.backlogs().map(b => b.backlogCode)).not.toContain('DEFAULT');
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // 🔴 DR-11 — the picker is scoped to the ACTIVE team, matching WPF
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('🔴 DR-11 — the picker shows only the active team', () => {
    it('excludes another team\'s backlogs — only activeTeamId (2) survives', async () => {
      arrange();                 // ME.activeTeamId === 2
      await settle();

      const codes = component.backlogs().map(b => b.backlogCode);
      expect(codes).toContain('ARCS-1001');       // active team
      expect(codes).not.toContain('BETA-2002');   // 🔴 another team — DR-11 drops it
      expect(codes).not.toContain('DEFAULT');     // hidden
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // 🔴 H4 — the team filter's empty state. The highest-stakes test in this file.
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('🔴 H4 — an EMPTY team selection makes NO board call', () => {
    /**
     * `teamIds: []` appends NO query key (the RequestBuilder explodes an array one entry per element, and an
     * empty array iterates zero times). The key is therefore ABSENT from the URL — byte-identical to
     * `undefined` — and the server reads an absent key as "EVERY TEAM THE CALLER BELONGS TO".
     *
     * So a screen that "filters to nothing" by sending `[]` renders EVERYTHING. The worst possible direction
     * for the bug to fail in, and it fails silently. The only correct answer is not to call at all.
     */
    it('does NOT call getStandupBoard when the user has unchecked every team', async () => {
      arrange();
      await settle();

      component.tab.set('team');
      await settle();

      api.getStandupBoard.calls.reset();

      component.onTeamSelection([]);          // the user unchecked everything
      await settle();

      expect(api.getStandupBoard).not.toHaveBeenCalled();
      expect(component.board()).toEqual([]);
      expect(text()).toContain('No teams selected');
    });

    it('DOES call it, with the ids, when a team is checked', async () => {
      arrange();
      await settle();

      component.tab.set('team');
      await settle();

      api.getStandupBoard.calls.reset();

      component.onTeamSelection([2]);
      await settle();

      expect(api.getStandupBoard).toHaveBeenCalledWith(TODAY, [2]);
    });

    /**
     * A filter whose reads FAILED never emits, so the screen keeps `undefined` — which the server reads as
     * "all my teams". That is the documented correct degradation: a broken team list narrows nobody's view.
     * `undefined` and `[]` must not be conflated.
     */
    it('passes undefined (not []) when the filter has never emitted', async () => {
      arrange();
      await settle();

      expect(api.getStandupBoard).toHaveBeenCalledWith(TODAY, undefined);
      expect(component.boardBlocked()).toBeFalse();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // The board
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('the team board', () => {
    /** 🔴 The mock's `TeamMember` had NO `yesterday` array at all — the band was decorative and always read
     *  "— nothing logged". It is a real band now. */
    it('renders BOTH yesterday and today per member', async () => {
      arrange();
      api.getStandupBoard.and.returnValue(of([{
        userId: 2, userName: 'Binh',
        yesterday: [view(20, 'yesterday')],
        today: [view(21, 'today')],
      }]));
      await settle();

      component.tab.set('team');
      await settle();

      expect(text()).toContain('ARCS-20');     // yesterday — was IMPOSSIBLE to render before
      expect(text()).toContain('ARCS-21');     // today
    });

    it('shows "nothing logged" only on a band that is genuinely empty', async () => {
      arrange();
      api.getStandupBoard.and.returnValue(of([{
        userId: 2, userName: 'Binh', yesterday: [], today: [view(21, 'today')],
      }]));
      await settle();

      component.tab.set('team');
      await settle();

      const bands = fixture.debugElement.queryAll(By.css('.band.sm'))
        .map(d => (d.nativeElement as HTMLElement).textContent ?? '');

      expect(bands[0]).toContain('nothing logged');       // YESTERDAY — empty
      expect(bands[1]).not.toContain('nothing logged');   // TODAY — has a row
    });

    /**
     * 🔴 P9 (M10 audit A5) — the board showed THAT an issue resolved, never HOW. `solutionText` was already on
     * the wire (`StandupIssueDto`, used by the Input tab's own solution editor); this is a template-only fix.
     */
    it('P9 — shows a resolved issue\'s solution text on the board', async () => {
      arrange();
      api.getStandupBoard.and.returnValue(of([{
        userId: 2, userName: 'Binh', yesterday: [],
        today: [view(21, 'today', true, [issue({ status: 'resolved', solutionText: 'Restarted the pool' })])],
      }]));
      await settle();

      component.tab.set('team');
      await settle();

      expect(text()).toContain('Restarted the pool');
    });

    /**
     * 🔴 A resolved issue with a BLANK solution is "pending" (WPF's own rule — `hasSolution` gates on
     * `solutionText`, not `status`), and must not render an empty container.
     */
    it('P9 — renders no empty solution container when solutionText is blank, even if status says resolved', async () => {
      arrange();
      api.getStandupBoard.and.returnValue(of([{
        userId: 2, userName: 'Binh', yesterday: [],
        today: [view(21, 'today', true, [issue({ status: 'resolved', solutionText: null })])],
      }]));
      await settle();

      component.tab.set('team');
      await settle();

      const issueEl = fixture.debugElement.query(By.css('.issue.sm'));
      expect(issueEl).not.toBeNull();
      expect((issueEl.nativeElement as HTMLElement).textContent ?? '').not.toContain('—');
      expect(fixture.debugElement.query(By.css('.issue.sm .faint'))).toBeNull();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // Day nav — the two buttons that had NO handler at all
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('day nav', () => {
    it('boots on TODAY, derived — never a hard-coded string', async () => {
      arrange();
      await settle();

      expect(component.date()).toBe(TODAY);
      expect(api.getStandupMyDay).toHaveBeenCalledWith(TODAY);
    });

    it('◀ steps back a day and RE-READS', async () => {
      arrange();
      await settle();
      api.getStandupMyDay.calls.reset();

      buttonWithText('◀')!.click();
      await settle();

      expect(component.date()).toBe(YESTERDAY);
      expect(api.getStandupMyDay).toHaveBeenCalledWith(YESTERDAY);
    });

    it('▶ steps forward a day and RE-READS', async () => {
      arrange();
      await settle();

      component.prevDay();
      await settle();
      api.getStandupMyDay.calls.reset();

      buttonWithText('▶')!.click();
      await settle();

      expect(component.date()).toBe(TODAY);
      expect(api.getStandupMyDay).toHaveBeenCalledWith(TODAY);
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // Quick import
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('quick import', () => {
    /** 🔴 `quickImportStandup` takes BOTH dates — source AND target. It is not a one-argument call. */
    it('sends the source AND the target day', async () => {
      arrange();
      await settle();

      await component.quickImport();

      expect(api.quickImportStandup).toHaveBeenCalledWith(YESTERDAY, TODAY);
      expect(toast.message()).toContain('Imported 2 entries');
    });

    /** A 0 that reaches the client means "the source day was empty" — a legitimate no-op, not an error.
     *  (The locked-target rejection is a 400, and is reported as such.) */
    it('reports an empty source as a no-op, not a failure', async () => {
      arrange();
      await settle();
      api.quickImportStandup.and.returnValue(of(0));

      await component.quickImport();

      expect(toast.message()).toContain('Nothing to import');
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // Drag: reorder onto another entry, or onto the trash to delete
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('drag', () => {
    function twoRows(): SettingsUserStandup {
      return myDay({ today: [view(10, 'today'), view(11, 'today')] });
    }

    /** 🔴 The API is a PAIR — `(draggedId, targetId)` — not an index list. The server inserts the dragged row
     *  at the TARGET's slot, so the drop index must be resolved back to the entry occupying it. */
    it('reorders by resolving the drop index to the TARGET ENTRY\'s id', async () => {
      arrange(ME, twoRows());
      await settle();

      // Drag entry 11 onto the slot held by entry 10.
      await component.onDrop(dropOnto('today', 0, view(11, 'today')));

      expect(api.reorderStandupEntry).toHaveBeenCalledWith(11, 10);
    });

    it('deletes when dropped on the trash', async () => {
      arrange(ME, twoRows());
      await settle();

      await component.onDrop(dropOnto('trash', 0, view(11, 'today')));

      expect(api.deleteStandupEntry).toHaveBeenCalledWith(11);
      expect(api.reorderStandupEntry).not.toHaveBeenCalled();
    });

    /** `editable` is the SERVER's per-entry verdict (owner gate + edit-lock). It is not ours to override. */
    it('refuses to move an entry the server marked NOT editable', async () => {
      arrange(ME, myDay({ today: [view(10, 'today'), view(11, 'today', false)] }));
      await settle();

      await component.onDrop(dropOnto('today', 0, view(11, 'today', false)));
      await component.onDrop(dropOnto('trash', 0, view(11, 'today', false)));

      expect(api.reorderStandupEntry).not.toHaveBeenCalled();
      expect(api.deleteStandupEntry).not.toHaveBeenCalled();
    });

    /** The pair API cannot express "drop into an empty section" — there is no entry to target. No-op, not a
     *  crash and not a bogus call. */
    it('no-ops when the destination section has no row to target', async () => {
      arrange(ME, myDay({ yesterday: [], today: [view(10, 'today')] }));
      await settle();

      await component.onDrop(dropOnto('yesterday', 0, view(10, 'today')));

      expect(api.reorderStandupEntry).not.toHaveBeenCalled();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // H5 — re-entrancy + no unhandled rejections
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('H5 — every mutation is guarded and every failure is surfaced', () => {
    /**
     * 🔴 `StandupEntry` has no version, so a double-submitted add creates the row TWICE and nothing downstream
     * would ever catch it. The guard is the only thing standing between a double-click and a duplicate row.
     */
    it('drops a re-entrant mutation — a double-click adds ONE entry, not two', async () => {
      arrange();
      await settle();

      // A write that never completes: the first click stays in flight while the second one arrives.
      const inFlight = new Subject<number>();
      api.createStandupEntry.and.returnValue(inFlight.asObservable());

      component.openAdd('today');
      component.patch('backlogCode', 'ARCS-1');
      component.patch('taskText', 'Do it');

      const first = component.submitEntry();
      const second = component.submitEntry();     // the double-click

      expect(api.createStandupEntry).toHaveBeenCalledTimes(1);   // 🔴 not 2

      inFlight.next(11);
      inFlight.complete();
      await first;
      await second;
    });

    /**
     * 🔴 These are `async` methods bound to template outputs. Anything that escapes is an UNHANDLED PROMISE
     * REJECTION: console only, nowhere the user can see it. The write fails, the screen keeps showing stale
     * rows, and nobody is told.
     */
    it('never re-throws out of a template-bound handler — it toasts', async () => {
      arrange();
      await settle();

      api.deleteStandupEntry.and.returnValue(throwError(() => new HttpErrorResponse({
        status: 400, error: { error: 'Not the entry\'s owner, or the day is no longer editable.' },
      })));

      // Must RESOLVE, not reject.
      await expectAsync(component.deleteEntry(view(10, 'today'))).toBeResolved();
      expect(toast.message()).toBe('Not the entry\'s owner, or the day is no longer editable.');
    });

    it('clears the guard after a failure, so the screen is not wedged', async () => {
      arrange();
      await settle();

      api.deleteStandupEntry.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
      await component.deleteEntry(view(10, 'today'));

      expect(component.busy()).toBeFalse();
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // THE ADMIN CONTRACT
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('archive week is [ADMIN]', () => {
    /**
     * 🔴 `POST /api/standup/archive-week` is `.RequireAuthorization(AuthSetup.AdminPolicy)` and 403s a
     * non-admin. This screen is reachable by EVERY user, so per THE ADMIN CONTRACT the button must not be
     * offered to one — the mock's `toast.show('Week archived')` was a lie that would have become a 403.
     */
    it('hides the button from a non-admin', async () => {
      arrange(ME);
      await settle();

      expect(component.isAdmin()).toBeFalse();
      expect(buttonWithText('Archive week')).toBeNull();
    });

    it('offers it to an admin, and shows the SERVER-SIDE path it returns', async () => {
      arrange(ADMIN);
      await settle();

      expect(buttonWithText('Archive week')).not.toBeNull();

      await component.archiveWeek();

      expect(api.archiveStandupWeek).toHaveBeenCalledWith(TODAY);
      expect(toast.message()).toContain('\\\\share\\week.md');
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // The add-entry modal
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('the add-entry modal', () => {
    it('sends an AD-HOC code with a null backlogId', async () => {
      arrange();
      await settle();

      component.openAdd('yesterday');
      component.patch('backlogCode', 'ADHOC-1');
      component.patch('taskText', 'Investigate the outage');
      await component.submitEntry();

      expect(api.createStandupEntry).toHaveBeenCalledWith(jasmine.objectContaining({
        workDate: TODAY,
        section: 'yesterday',
        backlogId: null,          // 🔴 DR-03 — ad-hoc carries no id
        backlogCode: 'ADHOC-1',
        taskText: 'Investigate the outage',
        deadline: null,           // optional
        status: 'Todo',
      }));
    });

    it('picking a backlog fills the code, loads its tasks, and sends the id', async () => {
      arrange();
      await settle();

      component.openAdd('today');
      await component.pickBacklog('1');

      expect(component.draft().backlogId).toBe(1);
      expect(component.draft().backlogCode).toBe('ARCS-1001');
      expect(api.getTasks).toHaveBeenCalledWith(1);
    });

    it('choosing the ad-hoc option CLEARS the backlog id', async () => {
      arrange();
      await settle();

      component.openAdd('today');
      await component.pickBacklog('1');
      expect(component.draft().backlogId).toBe(1);

      await component.pickBacklog('');

      expect(component.draft().backlogId).toBeNull();
    });

    it('blocks a blank task client-side, without a round-trip', async () => {
      arrange();
      await settle();

      component.openAdd('today');
      component.patch('backlogCode', 'ARCS-1');
      component.patch('taskText', '   ');
      await component.submitEntry();

      expect(api.createStandupEntry).not.toHaveBeenCalled();
      expect(component.draftError()).toBe('Task text is required.');
    });
  });

  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
  // Realtime
  // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

  describe('realtime', () => {
    it('re-reads on a Standup change', async () => {
      arrange();
      await settle();
      api.getStandupMyDay.calls.reset();

      dataChanged.next({ kind: DataKind.Standup, teamId: 2 });
      await settle();

      expect(api.getStandupMyDay).toHaveBeenCalled();
    });

    it('ignores a kind this screen does not show', async () => {
      arrange();
      await settle();
      api.getStandupMyDay.calls.reset();

      dataChanged.next({ kind: DataKind.Tags, teamId: 0 });
      await settle();

      expect(api.getStandupMyDay).not.toHaveBeenCalled();
    });
  });
});
