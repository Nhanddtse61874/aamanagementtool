import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';

import { BacklogDto, SavedBody, TimeLogDto, WeekBacklogGroup } from '../../api/models';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { LogWorkComponent } from './log-work.component';

/**
 * The week the component boots into is derived from `new Date()`, so these tests never hard-code a Monday:
 * they read the axis the component itself derived. (Hard-coding one would make the suite go red on a date
 * change -- a test that fails for the wrong reason.)
 */

const MONDAY_TASK = 7;      // has 4h on Monday, rowVersion 11  -> a POPULATED cell
const EMPTY_TASK = 8;       // has nothing at all               -> an EMPTY cell

function week(monHours: number | null, monVersion: number | null): WeekBacklogGroup[] {
  return [{
    backlogId: 2, backlogCode: 'ARCS-1001', project: 'ARCS', type: 'Implement', assigneeName: 'Nhan',
    tasks: [
      {
        taskId: MONDAY_TASK, taskName: 'Design schema', orderIndex: 0,
        mon: { hours: monHours, rowVersion: monVersion },
        tue: { hours: null, rowVersion: null },
        wed: { hours: null, rowVersion: null },
        thu: { hours: null, rowVersion: null },
        fri: { hours: null, rowVersion: null },
      },
      {
        taskId: EMPTY_TASK, taskName: 'Write tests', orderIndex: 1,
        mon: { hours: null, rowVersion: null },
        tue: { hours: null, rowVersion: null },
        wed: { hours: null, rowVersion: null },
        thu: { hours: null, rowVersion: null },
        fri: { hours: null, rowVersion: null },
      },
    ],
  }];
}

/** The same week, plus the hidden DEFAULT backlog. It holds the recurring default tasks, so it must appear in
 *  EVERY month — which is exactly why it must never BELONG to one, and must never be movable. */
function weekWithDefault(): WeekBacklogGroup[] {
  return [
    ...week(4, 11),
    {
      backlogId: 99, backlogCode: 'DEFAULT', project: 'Recurring',
      tasks: [{
        taskId: 90, taskName: 'Daily standup', orderIndex: 0,
        mon: { hours: null, rowVersion: null },
        tue: { hours: null, rowVersion: null },
        wed: { hours: null, rowVersion: null },
        thu: { hours: null, rowVersion: null },
        fri: { hours: null, rowVersion: null },
      }],
    },
  ];
}

/**
 * The full record as `GET /api/backlogs/{id}` returns it — the backlog behind `week()`'s group.
 *
 * It HAS a `periodMonth`, so the move is computed from that and not from the displayed Monday. These tests
 * therefore do not depend on today's date, in keeping with the note at the top of this file.
 */
const BACKLOG: BacklogDto = {
  id: 2, backlogCode: 'ARCS-1001', project: 'ARCS', note: 'keep me',
  progressPercent: 40, periodMonth: '2026-07', rowVersion: 3,
};

function httpError(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body, url: '/api/timesheet/cell' });
}

/** Every "Move to next month" button currently RENDERED. Asserted against the DOM, not against the guard
 *  function — the template guard is a separate guard, and a unit test of the pure rule cannot see it. */
function moveButtons(f: ComponentFixture<LogWorkComponent>): HTMLButtonElement[] {
  const all: HTMLButtonElement[] = Array.from(f.nativeElement.querySelectorAll('button'));
  return all.filter(b => (b.textContent ?? '').includes('Move to next month'));
}

describe('LogWorkComponent', () => {
  let fixture: ComponentFixture<LogWorkComponent>;
  let component: LogWorkComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let toast: jasmine.SpyObj<ToastService>;
  let dataChanged: Subject<void>;

  /** The ISO date of the Monday the component actually derived for itself. */
  let mon: string;

  function setUp(initial: WeekBacklogGroup[] = week(4, 11)): void {
    dataChanged = new Subject<void>();

    api = jasmine.createSpyObj<WorklogService>(
      'WorklogService',
      ['getWeek', 'saveHours', 'clearHours', 'smartFillApply', 'typeColor', 'avatarColor',
       'getBacklog', 'updateBacklog'],
    );
    api.getWeek.and.returnValue(of(initial));
    api.typeColor.and.returnValue({ bg: '#fff', c: '#000' });
    api.avatarColor.and.returnValue('#000');

    toast = jasmine.createSpyObj<ToastService>('ToastService', ['show']);

    const realtime: Partial<RealtimeService> = {
      start: () => undefined,
      dataChanged: dataChanged.asObservable() as Observable<void>,
    };

    TestBed.configureTestingModule({
      imports: [LogWorkComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: RealtimeService, useValue: realtime },
      ],
    });

    fixture = TestBed.createComponent(LogWorkComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    mon = component.days()[0].iso;
  }

  // ---- the read path ---------------------------------------------------------------------------------

  it('loads the current week on init and shows its hours', () => {
    setUp();

    expect(api.getWeek).toHaveBeenCalledWith(mon);
    expect(component.groups().length).toBe(1);
    expect(component.cellText(MONDAY_TASK, mon)).toBe('4');
  });

  it('derives a five-day axis from the Monday it is showing -- not a hard-coded literal', () => {
    setUp();

    expect(component.days().length).toBe(5);
    expect(component.days().map(d => d.dow)).toEqual(['MON', 'TUE', 'WED', 'THU', 'FRI']);
  });

  // Before W4 these were `toast.show('Loaded previous week')` and re-fetched nothing at all.
  it('week navigation actually RE-FETCHES, and for the right Monday', () => {
    setUp();
    api.getWeek.calls.reset();

    component.prevWeek();
    fixture.detectChanges();

    const previousMonday = component.days()[0].iso;
    expect(previousMonday).not.toBe(mon);
    expect(api.getWeek).toHaveBeenCalledWith(previousMonday);
  });

  // ---- 🔴 expectedVersion: the claim, not a hint ------------------------------------------------------

  it('sends the cell\'s REAL rowVersion when it already has hours -- never 0, never null', () => {
    setUp(week(4, 11));
    api.saveHours.and.returnValue(of(12));

    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);

    // This is the edit that was IMPOSSIBLE before W0: expectedVersion had to be null, which asserts
    // "I believe this cell is empty", so every edit of a pre-existing cell 409'd.
    expect(api.saveHours).toHaveBeenCalledWith(MONDAY_TASK, mon, 6, 11);
  });

  it('sends NULL for a cell that genuinely has no hours -- the claim "I believe this is empty"', () => {
    setUp();
    api.saveHours.and.returnValue(of(1));

    component.editCell(EMPTY_TASK, mon, '3');
    component.commitCell(EMPTY_TASK, mon);

    expect(api.saveHours).toHaveBeenCalledWith(EMPTY_TASK, mon, 3, null);

    const sent = api.saveHours.calls.mostRecent().args[3];
    expect(sent).toBeNull();
    expect(sent).not.toBe(0);      // 0 is a DIFFERENT assertion under the five-case table
  });

  // ---- 🔴 store the version the WRITE returns; never re-read it ---------------------------------------

  it('stores the rowVersion the WRITE returned, and uses it on the next save', () => {
    setUp(week(4, 11));
    api.saveHours.and.returnValue(of(12));      // the server bumps 11 -> 12 and tells us so

    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);

    // No re-read: the week is NOT fetched again after a successful write (a read-back is racy -- another
    // client could write in between and we would then hold THEIR version with OUR data).
    expect(api.getWeek).toHaveBeenCalledTimes(1);

    api.saveHours.and.returnValue(of(13));
    component.editCell(MONDAY_TASK, mon, '7');
    component.commitCell(MONDAY_TASK, mon);

    expect(api.saveHours).toHaveBeenCalledWith(MONDAY_TASK, mon, 7, 12);   // 12, from the WRITE
  });

  // ---- writes happen on commit, not per keystroke ----------------------------------------------------

  it('does not write on every keystroke -- "4", "4.", "4.5" would be three PUTs at the same version', () => {
    setUp();

    component.editCell(EMPTY_TASK, mon, '4');
    component.editCell(EMPTY_TASK, mon, '4.');
    component.editCell(EMPTY_TASK, mon, '4.5');

    expect(api.saveHours).not.toHaveBeenCalled();   // nothing written yet...

    api.saveHours.and.returnValue(of(1));
    component.commitCell(EMPTY_TASK, mon);

    expect(api.saveHours).toHaveBeenCalledTimes(1); // ...and exactly one write on commit
    expect(api.saveHours).toHaveBeenCalledWith(EMPTY_TASK, mon, 4.5, null);
  });

  it('writes nothing when the value was typed back to what it already was', () => {
    setUp(week(4, 11));

    component.editCell(MONDAY_TASK, mon, '4');
    component.commitCell(MONDAY_TASK, mon);

    expect(api.saveHours).not.toHaveBeenCalled();
    expect(api.clearHours).not.toHaveBeenCalled();
  });

  // ---- clearing a cell is a DELETE, not a save of 0 ---------------------------------------------------

  it('clears an emptied cell with DELETE and its real version -- the API rejects hours <= 0', () => {
    setUp(week(4, 11));
    api.clearHours.and.returnValue(of(void 0));

    component.editCell(MONDAY_TASK, mon, '');
    component.commitCell(MONDAY_TASK, mon);

    expect(api.clearHours).toHaveBeenCalledWith(MONDAY_TASK, mon, 11);
    expect(api.saveHours).not.toHaveBeenCalled();          // a save of 0 would 400
    expect(component.cellText(MONDAY_TASK, mon)).toBe('');
  });

  it('does not call the server to clear a cell that is already empty', () => {
    setUp();

    component.editCell(EMPTY_TASK, mon, '');
    component.commitCell(EMPTY_TASK, mon);

    expect(api.clearHours).not.toHaveBeenCalled();
  });

  // ---- 🔴 the three error channels --------------------------------------------------------------------

  it('400: shows the business rule\'s OWN message and reverts the cell', () => {
    setUp();
    api.saveHours.and.returnValue(
      throwError(() => httpError(400, { error: 'A single cell cannot exceed 8h.' })));

    component.editCell(EMPTY_TASK, mon, '40');
    component.commitCell(EMPTY_TASK, mon);

    expect(toast.show).toHaveBeenCalledWith('A single cell cannot exceed 8h.');
    expect(component.cellText(EMPTY_TASK, mon)).toBe('');    // nothing was written, so show the truth
    expect(component.conflict()).toBeNull();                 // a 400 is NOT a conflict
  });

  it('404: says the task is gone and re-fetches', () => {
    setUp();
    api.saveHours.and.returnValue(throwError(() => httpError(404, null)));
    api.getWeek.calls.reset();

    component.editCell(EMPTY_TASK, mon, '3');
    component.commitCell(EMPTY_TASK, mon);

    expect(toast.show).toHaveBeenCalledWith('This task is no longer available.');
    expect(api.getWeek).toHaveBeenCalled();
  });

  it('409: raises a conflict dialog carrying DETAIL -- the only field that says which cell', () => {
    setUp(week(4, 11));

    // The real body, verified on the wire. NOTE `id: 0`: a timesheet cell has no id (its key is the natural
    // triple), so `detail` is the ONLY thing identifying it.
    api.saveHours.and.returnValue(throwError(() => httpError(409, {
      table: 'TimeLogs',
      id: 0,
      deleted: false,
      detail: 'user 1, task 7, 2026-07-13',
      message: 'TimeLogs (user 1, task 7, 2026-07-13) was changed by someone else (you last saw row_version 11).',
    })));
    // the re-fetch that follows a 409 shows what the other person actually saved
    api.getWeek.and.returnValue(of(week(3, 12)));

    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);

    const conflict = component.conflict();
    expect(conflict).not.toBeNull();
    expect(conflict!.detail).toBe('user 1, task 7, 2026-07-13');
    expect(conflict!.yours).toBe(6);        // what the user typed
    expect(conflict!.theirs).toBe(3);       // what the re-fetch found on the server
    expect(conflict!.taskName).toBe('Design schema');
  });

  it('409 -> "keep theirs": the other person\'s work survives and nothing is re-sent', () => {
    setUp(week(4, 11));
    api.saveHours.and.returnValue(throwError(() => httpError(409, { detail: 'd', message: 'm' })));
    api.getWeek.and.returnValue(of(week(3, 12)));

    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);
    api.saveHours.calls.reset();

    component.keepTheirs();

    expect(component.conflict()).toBeNull();
    expect(api.saveHours).not.toHaveBeenCalled();
    expect(component.cellText(MONDAY_TASK, mon)).toBe('3');   // theirs, from the re-fetch
  });

  it('409 -> "overwrite": re-sends with the FRESH version, so the retry is a checked write, not a force', () => {
    setUp(week(4, 11));
    api.saveHours.and.returnValue(throwError(() => httpError(409, { detail: 'd', message: 'm' })));
    api.getWeek.and.returnValue(of(week(3, 12)));      // someone else's save bumped it to 12

    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);

    api.saveHours.calls.reset();
    api.saveHours.and.returnValue(of(13));
    component.overwriteTheirs();

    // 12 -- the version the RE-FETCH returned. Not 11 (stale), and emphatically not null or 0.
    expect(api.saveHours).toHaveBeenCalledWith(MONDAY_TASK, mon, 6, 12);
    expect(component.cellText(MONDAY_TASK, mon)).toBe('6');
  });

  // ---- 🔴 Smart Fill is MERGED into the grid ----------------------------------------------------------

  it('Smart Fill MERGES: the days it did not touch are still on screen, with their versions', () => {
    // Monday has 4h @ v11 and must survive a fill of Wed/Thu.
    setUp(week(4, 11));
    const days = component.days();
    const [, , wed, thu] = days.map(d => d.iso);

    const filled: TimeLogDto[] = [
      { id: 1, userId: 1, taskId: MONDAY_TASK, workDate: wed, hours: 3, rowVersion: 1 },
      { id: 2, userId: 1, taskId: MONDAY_TASK, workDate: thu, hours: 2, rowVersion: 1 },
    ];
    api.smartFillApply.and.returnValue(of(filled));

    component.openSmartFill();
    component.sfTaskId.set(MONDAY_TASK);
    component.applySmartFill();

    expect(component.cellText(MONDAY_TASK, wed)).toBe('3');
    expect(component.cellText(MONDAY_TASK, thu)).toBe('2');
    expect(component.cellText(MONDAY_TASK, mon)).toBe('4');   // NOT wiped -- a `replace` would have
  });

  it('Smart Fill: the very next edit of a filled cell uses the version the FILL returned, so it does not 409', () => {
    setUp(week(4, 11));
    const wed = component.days()[2].iso;

    api.smartFillApply.and.returnValue(of([
      { id: 1, userId: 1, taskId: MONDAY_TASK, workDate: wed, hours: 3, rowVersion: 5 },
    ]));

    component.openSmartFill();
    component.sfTaskId.set(MONDAY_TASK);
    component.applySmartFill();

    api.saveHours.and.returnValue(of(6));
    component.editCell(MONDAY_TASK, wed, '4');
    component.commitCell(MONDAY_TASK, wed);

    // 5 -- learned from the Smart Fill RESPONSE. We are excluded from our own SignalR echo, so that response
    // is the ONLY place this version exists. Sending null (or a stale value) here would 409 against our own
    // fill, on the happy path, every single time.
    expect(api.saveHours).toHaveBeenCalledWith(MONDAY_TASK, wed, 4, 5);
  });

  it('Smart Fill 400: shows the rule\'s message and writes nothing', () => {
    setUp();
    api.smartFillApply.and.returnValue(
      throwError(() => httpError(400, { error: '2026-07-15 is a holiday.' })));

    component.openSmartFill();
    component.sfTaskId.set(MONDAY_TASK);
    component.applySmartFill();

    expect(toast.show).toHaveBeenCalledWith('2026-07-15 is a holiday.');
  });

  // ---- live sync ---------------------------------------------------------------------------------------

  it('re-fetches the week when SignalR says someone else changed the data', () => {
    setUp();
    api.getWeek.calls.reset();

    dataChanged.next();

    // The server already excluded us from its own broadcast (that is what X-Connection-Id buys), so anything
    // arriving here is genuinely somebody else's change and the week must be re-read.
    expect(api.getWeek).toHaveBeenCalledWith(mon);
  });

  // ---- failure of the READ path -----------------------------------------------------------------------

  it('a failed week read shows an error and leaves navigation ALIVE', () => {
    setUp();
    api.getWeek.and.returnValue(throwError(() => httpError(500, null)));

    component.nextWeek();
    fixture.detectChanges();

    expect(component.loadError()).toBeTruthy();

    // The pipeline must not have died with the error: navigating again still fetches.
    api.getWeek.and.returnValue(of(week(4, 11)));
    api.getWeek.calls.reset();
    component.prevWeek();
    fixture.detectChanges();

    expect(api.getWeek).toHaveBeenCalled();
    expect(component.loadError()).toBeNull();
  });

  // ---- 🔴 move to next month --------------------------------------------------------------------------

  it('GETs the backlog, then PUTs the WHOLE record back with only periodMonth changed', async () => {
    setUp();
    api.getBacklog.and.returnValue(of(BACKLOG));
    api.updateBacklog.and.returnValue(of({ rowVersion: 4 }));
    api.getWeek.calls.reset();

    await component.onMoveMonth(component.groups()[0]);

    // The GET is MANDATORY, not an optimisation. The week read gives a `Group` -- id, code, project, type,
    // assignee -- and NEITHER the backlog's other fields NOR its rowVersion. There is nowhere else to get them.
    expect(api.getBacklog).toHaveBeenCalledWith(2);

    const [id, body] = api.updateBacklog.calls.mostRecent().args;
    expect(id).toBe(2);
    expect(body.periodMonth).toBe('2026-08');      // 2026-07, bumped. NOT derived from the displayed week.
    expect(body.expectedVersion).toBe(3);          // the version the GET returned -- never `!`, never 0.
    expect(body.note).toBe('keep me');             // carried across: an omitted field is written as NULL.

    expect(api.getWeek).toHaveBeenCalled();        // ...and the ticket leaves this month's view.
  });

  it('🔴 the hidden DEFAULT backlog shows NO Move button -- it must appear in EVERY month, so it belongs to none',
    () => {
      setUp(weekWithDefault());

      // 🔴 A SECOND pass, and it is not ceremony. `setUp`'s single `detectChanges()` is the pass during which
      // `toObservable(monday)`'s effect first fires -- so the week arrives and `groups` is set WHILE that pass
      // is already rendering, and the template goes out with `groups()` still empty (verified: `.grp` count 0,
      // toolbar reading "Expand all"). Component STATE is right after one pass; the DOM needs the next one.
      // Every existing test in this file asserts on state alone, which is why nothing has needed this before.
      fixture.detectChanges();

      // Both groups are on screen; only the normal ticket may be moved.
      expect(component.groups().length).toBe(2);
      expect(fixture.nativeElement.querySelectorAll('.grp').length).toBe(2);
      expect(moveButtons(fixture).length).toBe(1);

      expect(component.canMoveMonth('ARCS-1001')).toBeTrue();
      expect(component.canMoveMonth('DEFAULT')).toBeFalse();
    });

  it('🔴 ...and the HANDLER refuses it too, not only the template -- WPF guards it twice and so do we',
    async () => {
      setUp(weekWithDefault());

      const dflt = component.groups().find(g => g.code === 'DEFAULT')!;
      await component.onMoveMonth(dflt);

      // Not even read. Defence in depth here is deliberate: a future template edit that drops the @if must
      // not be able to move this backlog.
      expect(api.getBacklog).not.toHaveBeenCalled();
      expect(api.updateBacklog).not.toHaveBeenCalled();
    });

  it('🔴 409: says so, re-reads, and does NOT retry -- their change may ITSELF have been a Move', async () => {
    setUp();
    api.getBacklog.and.returnValue(of(BACKLOG));
    api.updateBacklog.and.returnValue(throwError(() => httpError(409, { detail: 'd', message: 'm' })));
    api.getWeek.calls.reset();

    await component.onMoveMonth(component.groups()[0]);

    expect(toast.show).toHaveBeenCalledWith(
      'Someone else just changed this ticket. Reloaded — try again if you still want to move it.');
    expect(api.getWeek).toHaveBeenCalled();

    // Exactly ONE write. A blind retry would re-read the ALREADY-BUMPED month and push the ticket TWO months
    // forward -- the corruption this whole path is careful about.
    expect(api.updateBacklog).toHaveBeenCalledTimes(1);

    // And this is NOT the cell conflict dialog: that one is a MERGE between two numbers (keep theirs /
    // overwrite with mine), and a moved backlog has nothing to merge.
    expect(component.conflict()).toBeNull();
  });

  it('404 on the GET: surfaces it instead of dying as an unhandled rejection the user never sees', async () => {
    setUp();
    // Reachable: the backlog can be deleted, or the user removed from its team, while this screen is open.
    api.getBacklog.and.returnValue(throwError(() => httpError(404, null)));
    api.getWeek.calls.reset();

    await component.onMoveMonth(component.groups()[0]);

    expect(toast.show).toHaveBeenCalledWith('This ticket is no longer available.');
    expect(api.updateBacklog).not.toHaveBeenCalled();
    expect(api.getWeek).toHaveBeenCalled();
    expect(component.moving()).toBeFalse();        // the button is released again
  });

  it('🔴 a second click while the first is in flight starts NO second chain -- it would move it TWICE',
    async () => {
      setUp();
      api.getBacklog.and.returnValue(of(BACKLOG));
      api.updateBacklog.and.returnValue(new Subject<SavedBody>());   // never settles: still in flight

      const group = component.groups()[0];
      void component.onMoveMonth(group);      // chain 1 -- its PUT hangs
      await component.onMoveMonth(group);     // chain 2 -- must be refused outright

      // Chain 2 never even READ the backlog. Had it done so, its GET could have landed AFTER chain 1's PUT
      // committed, read the already-bumped periodMonth, and moved the ticket a SECOND month forward. That is
      // reachable precisely when a user is most likely to click twice: a slow link, where "nothing happened".
      expect(api.getBacklog).toHaveBeenCalledTimes(1);
      expect(api.updateBacklog).toHaveBeenCalledTimes(1);
      expect(component.moving()).toBeTrue();        // chain 1 still in flight -> the button stays disabled
    });
});
