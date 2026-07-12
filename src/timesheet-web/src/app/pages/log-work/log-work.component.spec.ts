import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';

import { TimeLogDto, WeekBacklogGroup } from '../../api/models';
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

function httpError(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body, url: '/api/timesheet/cell' });
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
      ['getWeek', 'saveHours', 'clearHours', 'smartFillApply', 'typeColor', 'avatarColor'],
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
});
