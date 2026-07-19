import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Subject, of, throwError } from 'rxjs';

import { BacklogDto, SavedBody, TimeLogDto, WeekBacklogGroup } from '../../api/models';
import { ConfirmDialogComponent } from '../../core/confirm-dialog/confirm-dialog.component';
import { DataChange, DataKind, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { TaskRow } from './grid-state';
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

/**
 * A `dropped` event, fabricated. CDK builds the real one from a live pointer gesture, which a headless unit
 * test has no way to perform — so the SHAPE is what is exercised here, and the DOM-level tests below are what
 * prove the directives that would emit it are actually attached.
 *
 * `container` / `previousContainer` are compared by REFERENCE in `onDrop`, so the identity of these two stubs
 * is the whole point: same object = a reorder within one group; different objects = it came from elsewhere.
 */
function dropEvent(
  previousIndex: number,
  currentIndex: number,
  opts: { fromAnotherList?: boolean } = {},
): CdkDragDrop<readonly TaskRow[]> {
  const container = { id: 'grp-rows' } as CdkDropList<readonly TaskRow[]>;
  const previousContainer = opts.fromAnotherList
    ? ({ id: 'trash' } as CdkDropList<readonly TaskRow[]>)
    : container;

  return {
    previousIndex, currentIndex, container, previousContainer,
    item: {} as CdkDrag<TaskRow>,
    isPointerOverContainer: true,
    distance: { x: 0, y: 0 },
    dropPoint: { x: 0, y: 0 },
    event: new MouseEvent('mouseup'),
  };
}

/**
 * A drop onto the TRASH. Two things distinguish it from `dropEvent`, and `onTrash` reads exactly those two:
 *
 *   - `previousContainer !== container` — it ARRIVED from somewhere else. (`dropped` fires only on the
 *     DESTINATION list, so this is what every real trash drop looks like, and a reorder never does.)
 *   - `item.data` is POPULATED. It is the only thing that says WHICH row was dropped, and it comes from
 *     `[cdkDragData]` on the row — Task 6's binding, asserted live further down. Without it `data` would be
 *     `undefined` and `row.taskId` would throw a TypeError, at the moment a user first tries to delete.
 */
function trashEvent(
  row: TaskRow,
  opts: { ontoItself?: boolean } = {},
): CdkDragDrop<readonly TaskRow[], readonly TaskRow[], TaskRow> {
  const container = { id: 'trash' } as CdkDropList<readonly TaskRow[]>;
  const previousContainer = opts.ontoItself
    ? container
    : ({ id: 'grp-rows' } as CdkDropList<readonly TaskRow[]>);

  return {
    previousIndex: 0, currentIndex: 0, container, previousContainer,
    item: { data: row } as CdkDrag<TaskRow>,
    isPointerOverContainer: true,
    distance: { x: 0, y: 0 },
    dropPoint: { x: 0, y: 0 },
    event: new MouseEvent('mouseup'),
  };
}

/**
 * M9/P6d. `RealtimeService.dataChanged` used to be `Observable<void>` -- the hub handler took the server's
 * `(kind, teamId)` and threw BOTH away, so no screen could tell what had changed. It now carries a real
 * `DataChange`, and this stub moves with it: a `Subject<void>` here is a TS2322 against the widened type.
 *
 * This screen still re-reads on ANY change -- `.subscribe(() => this.refresh.next())` ignores the payload, and
 * a zero-arg callback stays assignable to `(v: DataChange) => void`, which is why `log-work.component.ts`
 * itself needed no edit. `Logs` is simply the kind a cell write really announces.
 */
const CHANGED: DataChange = { kind: DataKind.Logs, teamId: 1 };

describe('LogWorkComponent', () => {
  let fixture: ComponentFixture<LogWorkComponent>;
  let component: LogWorkComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let toast: jasmine.SpyObj<ToastService>;
  let dataChanged: Subject<DataChange>;

  /** The ISO date of the Monday the component actually derived for itself. */
  let mon: string;

  function setUp(initial: WeekBacklogGroup[] = week(4, 11)): void {
    dataChanged = new Subject<DataChange>();

    api = jasmine.createSpyObj<WorklogService>(
      'WorklogService',
      ['getWeek', 'saveHours', 'clearHours', 'smartFillApply', 'typeColor', 'avatarColor',
       'getBacklog', 'updateBacklog', 'setTaskOrder', 'setTaskActive'],
    );
    api.getWeek.and.returnValue(of(initial));
    api.typeColor.and.returnValue({ bg: '#fff', c: '#000' });
    api.avatarColor.and.returnValue('#000');

    toast = jasmine.createSpyObj<ToastService>('ToastService', ['show']);

    const realtime: Partial<RealtimeService> = {
      start: () => undefined,
      dataChanged: dataChanged.asObservable(),
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

  // ---- 🔴 BUG-1 (spec 2026-07-19-m9.2): an invalid draft is kept, marked, and NEVER written --------------
  // `readCell('abc')` used to collapse onto the SAME null a genuine clear produces, and `commitCell` turned
  // every null into `clearCell()` -- a DELETE the user never asked for. These are the two regression tests
  // that would have caught it.

  it('typing gibberish over a filled cell issues NO request and leaves the stored value untouched', () => {
    setUp(week(4, 11));

    component.editCell(MONDAY_TASK, mon, 'abc');
    component.commitCell(MONDAY_TASK, mon);

    expect(api.saveHours).not.toHaveBeenCalled();
    expect(api.clearHours).not.toHaveBeenCalled();
    expect(component.isInvalidCell(MONDAY_TASK, mon)).toBe(true);

    // Prove the server-truth cell itself was never touched, not merely that no request fired: a legitimate
    // edit right after this one must still carry the ORIGINAL rowVersion (11) as its expectedVersion. If the
    // invalid commit had reached clearCell/saveCell and mutated `cells`, this would carry a version that was
    // never actually returned by a write.
    api.saveHours.and.returnValue(of(12));
    component.editCell(MONDAY_TASK, mon, '6');
    component.commitCell(MONDAY_TASK, mon);

    expect(api.saveHours).toHaveBeenCalledWith(MONDAY_TASK, mon, 6, 11);
    expect(component.isInvalidCell(MONDAY_TASK, mon)).toBe(false);   // the fix clears the mark
  });

  it('an invalid cell contributes its last committed value to dayTotal, never 0', () => {
    setUp(week(4, 11));
    expect(component.dayTotal(mon)).toBe(4);        // sanity: the day starts at the stored 4h

    component.editCell(MONDAY_TASK, mon, 'abc');    // typing gibberish over it, live -- no commit yet

    expect(component.dayTotal(mon)).toBe(4);        // ...must not drop the total to 0
    expect(component.rowTotal(MONDAY_TASK)).toBe(4);
  });

  // ---- 🔴 the three error channels --------------------------------------------------------------------

  // 🔴 M9.2: the input used to be '40' -- an over-8h value the SERVER rejected with 400. `readCell` (A1) now
  // enforces the same per-cell cap CLIENT-SIDE, so '40' never reaches `saveHours` at all any more (it is
  // 'invalid', not a request). This test still needs a genuine server-side 400, so it exercises a rule the
  // client legitimately cannot check itself -- a client-valid value the server refuses for its own reason.
  it('400: shows the business rule\'s OWN message and reverts the cell', () => {
    setUp();
    api.saveHours.and.returnValue(
      throwError(() => httpError(400, { error: 'Hours cannot be logged on a public holiday.' })));

    component.editCell(EMPTY_TASK, mon, '6');
    component.commitCell(EMPTY_TASK, mon);

    expect(toast.show).toHaveBeenCalledWith('Hours cannot be logged on a public holiday.');
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

    dataChanged.next(CHANGED);

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

  // ---- 🔴 drag to reorder ------------------------------------------------------------------------------
  //
  // These assert on directive INSTANCES (`By.directive`), not on markup, because `By.directive` can only find
  // a directive Angular actually INSTANTIATED — which is the one thing that distinguishes a live `cdkDropList`
  // from an inert attribute of the same name sitting on a <div>.
  //
  // 🔴 MEASURED, not assumed — and the received wisdom here is only half right. With `DragDropModule` removed
  // from `LogWorkComponent.imports`:
  //
  //   - the build FAILS, with four errors: `NG8002: Can't bind to 'cdkDropListData' | 'cdkDropListConnectedTo'
  //     | 'cdkDragData' since it isn't a known property of 'div'`, plus `NG5: Argument of type 'Event' is not
  //     assignable to parameter of type 'CdkDragDrop<…>'` on `(cdkDropListDropped)`, because `$event` falls
  //     back to the native `Event`. A PROPERTY BINDING and a TYPED OUTPUT are both checked. So the oft-repeated
  //     "the build passes and nothing drags" is FALSE for this template — losing the module here is loud.
  //   - but rewrite those same directives as BARE ATTRIBUTES and the identical module-less build reports
  //     "Application bundle generation complete" — CLEAN. Angular errors on an unknown ELEMENT (NG8001) and
  //     never on an unknown ATTRIBUTE. That is the real mechanism, and it is genuinely silent.
  //
  // 🔴 So the one directive here that CAN vanish silently is `cdkDragHandle`, the only bare attribute of the
  // three — nothing is bound to it, so nothing type-checks it. Drop it and CDK falls back to dragging the whole
  // row: the grid still works, and the bug is invisible. The handle assertion below is the only guard on it.

  function rows(f: ComponentFixture<LogWorkComponent>): HTMLElement[] {
    return Array.from(f.nativeElement.querySelectorAll('.row'));
  }

  /** Every `CdkDropList` Angular actually INSTANTIATED, by its CDK id. Since Task 7 that is one per rendered
   *  group PLUS the trash — so nothing here may index by position. */
  function dropLists(f: ComponentFixture<LogWorkComponent>): CdkDropList<readonly TaskRow[]>[] {
    return f.debugElement.queryAll(By.directive(CdkDropList))
      .map(d => d.injector.get(CdkDropList) as CdkDropList<readonly TaskRow[]>);
  }

  it('🔴 the drop-list directive is ACTUALLY APPLIED — an unknown attribute on a <div> would not be', () => {
    setUp();
    fixture.detectChanges();   // the DOM is one pass behind — see the DEFAULT-backlog test above

    // 🔴 TWO, not one: one drop list per rendered group, PLUS the trash (Task 7). This count was `1` when Task
    // 6 wrote it, because the trash did not exist yet — it is updated here rather than loosened, because the
    // exact number is the point: a THIRD list would mean a group had sprouted one, and groups connect only to
    // the trash and never to each other (a task must not be draggable into a different backlog).
    const lists = dropLists(fixture);
    expect(lists.length).toBe(2);

    const list = lists.find(l => l.id !== 'trash')!;

    // The rows really are the list's data — not a stale array captured somewhere else.
    expect(list.data.map(t => t.taskId)).toEqual([MONDAY_TASK, EMPTY_TASK]);

    // 🔴 Connected to the trash BY STRING ID, and pinned here because nothing else can pin it: the plan's
    // `[cdkDropListConnectedTo]="[trash]"` (a template ref to an element Task 7 creates) does NOT compile —
    // `NG9: Property 'trash' does not exist on type 'LogWorkComponent'`. CDK resolves a STRING against its
    // static registry lazily, on each drag start, so the target may not exist yet. The cost of that is that a
    // typo'd id is no longer a compile error — it is a console warning. This assertion is what buys it back.
    // TASK 7'S TRASH MUST THEREFORE CARRY `id="trash"`, NOT `#trash="cdkDropList"`.
    expect(list.connectedTo).toEqual(['trash']);
  });

  it('🔴 every row carries [cdkDragData] — without it the trash gets `undefined` and throws on row.taskId',
    () => {
      setUp();
      fixture.detectChanges();

      const drags = fixture.debugElement.queryAll(By.directive(CdkDrag));
      expect(drags.length).toBe(2);                  // one per task row

      // Task 7 reads `event.item.data`. This is the only place that value comes from.
      const dragged = drags.map(d => (d.injector.get(CdkDrag) as CdkDrag<TaskRow>).data);
      expect(dragged.map(t => t.taskId)).toEqual([MONDAY_TASK, EMPTY_TASK]);
      expect(dragged.every(t => t !== undefined)).toBeTrue();
    });

  it('🔴 the handle is the six-dot SVG INSIDE .c-task, and .row still has exactly SEVEN children', () => {
    setUp();
    fixture.detectChanges();

    const handles = fixture.debugElement.queryAll(By.directive(CdkDragHandle));
    expect(handles.length).toBe(2);                  // one per row, and it resolves — so the module is loaded

    // 🔴 `.row` is a CSS grid of exactly seven columns
    // (`--gcols: minmax(230px,1.8fr) repeat(5,1fr) 92px`) and has exactly seven children: `.c-task`, five
    // `.c-day`, `.c-total`. A `<span class="grip">` added as an EIGHTH child — the obvious way to build a drag
    // handle, and the way the first draft of this milestone did it — shifts every column by one and wraps
    // `.c-total` onto a second implicit row, on every task row in the grid. So the handle goes on the SVG that
    // was already nested inside `.c-task`, and this assertion is what stops anyone undoing that.
    const all = rows(fixture);
    expect(all.length).toBe(2);
    all.forEach(r => expect(r.children.length).toBe(7));

    handles.forEach(h => {
      const svg = h.nativeElement as SVGElement;
      expect(svg.tagName.toLowerCase()).toBe('svg');
      expect(svg.closest('.c-task')).not.toBeNull();      // nested INSIDE the first column, not beside it
    });
  });

  it('🔴 a drag rewrites the orderIndex of EVERY row — a windowed write would TIE after a soft delete',
    async () => {
      setUp();
      api.setTaskOrder.and.returnValue(of(void 0));
      api.getWeek.calls.reset();

      // Drag the first row (position 0) down to position 1.
      await component.onDrop(component.groups()[0], dropEvent(0, 1));

      // BOTH rows are rewritten, not just the one that moved. `SetActiveAsync` soft-deletes by setting
      // `is_active = 0` and LEAVES `order_index` alone, so a delete leaves a GAP in the survivors — and a
      // write of only the displaced rows, at absolute index `lo + i`, then produces a TIE that
      // `ORDER BY order_index` resolves arbitrarily. Rewriting every row renormalises the gap on every drag.
      expect(api.setTaskOrder).toHaveBeenCalledTimes(2);
      expect(api.setTaskOrder.calls.allArgs()).toEqual([[EMPTY_TASK, 0], [MONDAY_TASK, 1]]);

      expect(api.getWeek).toHaveBeenCalled();       // ...and the new order is re-read from the server
      expect(component.reordering()).toBeFalse();   // released again
    });

  it('writes nothing when a row is dropped back where it started', async () => {
    setUp();

    await component.onDrop(component.groups()[0], dropEvent(1, 1));

    expect(api.setTaskOrder).not.toHaveBeenCalled();
  });

  it('🔴 a drop that came from ANOTHER list is not a reorder — that is the TRASH, and it writes no order',
    async () => {
      setUp();

      await component.onDrop(component.groups()[0], dropEvent(0, 1, { fromAnotherList: true }));

      // `dropped` fires only on the DESTINATION list, so a drag to the trash fires the TRASH's handler, never
      // this one. The guard asserts that: if this method ever DOES see a cross-list index, something is wired
      // wrong and writing an order would be the wrong answer.
      expect(api.setTaskOrder).not.toHaveBeenCalled();
    });

  it('🔴 a failed order write TOASTS and re-reads — it must not die as an unhandled rejection', async () => {
    setUp();
    // Reachable: another user can delete one of these tasks (M8.5 ships exactly that) mid-drag. And the writes
    // are sequential, so a failure part-way leaves the group HALF-renormalised on the server — re-reading is
    // mandatory, not tidy.
    api.setTaskOrder.and.returnValue(throwError(() => httpError(404, null)));
    api.getWeek.calls.reset();

    await component.onDrop(component.groups()[0], dropEvent(0, 1));

    expect(toast.show).toHaveBeenCalledWith('One of those tasks is no longer available. Reloaded.');
    expect(api.getWeek).toHaveBeenCalled();
    expect(component.reordering()).toBeFalse();     // released, so the next drag is not dead
  });

  it('🔴 a second drag while the first batch is in flight is refused — two batches would interleave',
    async () => {
      setUp();
      // Never settles: batch 1's first write hangs.
      api.setTaskOrder.and.returnValue(new Subject<void>());

      const group = component.groups()[0];
      void component.onDrop(group, dropEvent(0, 1));    // batch 1 — in flight
      await component.onDrop(group, dropEvent(1, 0));   // batch 2 — must be refused outright

      // CDK does not move the row for us (we never call `moveItemInArray`; the re-fetch is what re-renders
      // it), so between the drop and the refresh landing the row SNAPS BACK. On a slow link that reads as "the
      // drag didn't work" — exactly when a user drags again. A second batch computed from the still-stale
      // `group.tasks` would interleave with the first and leave an order neither drag asked for.
      expect(api.setTaskOrder).toHaveBeenCalledTimes(1);
      expect(component.reordering()).toBeTrue();        // batch 1 still in flight
    });

  // ---- 🔴 drag to trash (a SOFT delete) ----------------------------------------------------------------
  //
  // 🔴 THE HAND-OFF. Everything below the first test is ordinary handler testing; the FIRST test is the one
  // that matters, and it is the only thing standing between this milestone and a feature that is silently,
  // completely dead.
  //
  // Groups are connected to the trash BY STRING ID (`[cdkDropListConnectedTo]="['trash']"`) because the
  // template-ref form the plan specified does not compile. CDK resolves that string against a STATIC REGISTRY,
  // lazily, on every drag start, and FILTERS OUT what it cannot find:
  //
  //     const correspondingDropList = CdkDropList._dropLists.find(list => list.id === drop);
  //     if (!correspondingDropList && ngDevMode) console.warn(`CdkDropList could not find connected drop
  //                                                            list with id "${drop}"`);
  //     …
  //     ref.connectedTo(siblings.filter(drop => drop && drop !== this).map(list => list._dropListRef))
  //                                                            (@angular/cdk 17.3.10, drag-drop.mjs:3510-3546)
  //
  // So an id that does not resolve is not an error — it is a CONSOLE WARNING and an empty sibling list. The
  // row then snaps back, `container === previousContainer`, and `onTrash` NEVER FIRES. Every handler test
  // below would still pass. A green build would still be green. That is why the first test drives CDK's real
  // `beforeStarted` and captures what its resolution actually produces.

  it('🔴 THE HAND-OFF: the group\'s connectedTo RESOLVES to the trash — proved through CDK\'s OWN resolution',
    () => {
      setUp();
      fixture.detectChanges();   // the DOM is one pass behind

      const trash = dropLists(fixture).find(l => l.id === 'trash');
      const group = dropLists(fixture).find(l => l.id !== 'trash');

      // HALF ONE — the trash is findable by the exact string CDK searches the registry by. A static
      // `id="trash"` really does set `CdkDropList`'s `id` INPUT (it is declared as one: `inputs: { …, id:
      // "id", … }`), and CDK host-binds it back out as `[attr.id]`. A `#trash="cdkDropList"` template ref
      // would have left it on the auto-generated `cdk-drop-list-N` and BOTH of these would fail.
      expect(trash).toBeDefined();
      expect(trash!.id).toBe('trash');
      expect(document.getElementById('trash')).not.toBeNull();

      // HALF TWO — the group still asks for it by that same string (Task 6's binding).
      expect(group!.connectedTo).toEqual(['trash']);

      // 🔴 AND THE TWO HALVES ACTUALLY MEET. This is not an inference from the two above — it RUNS CDK's
      // resolution. `_setupInputSyncSubscription` subscribes to `ref.beforeStarted`, and firing it is exactly
      // what a real drag start does. Both `beforeStarted` and `connectedTo()` are public on `DropListRef`.
      const warn = spyOn(console, 'warn');
      const connectedTo = spyOn(group!._dropListRef, 'connectedTo').and.callThrough();

      group!._dropListRef.beforeStarted.next();

      // The TRASH'S OWN `DropListRef` came out the other end of `_dropLists.find(...)`. Had the id not
      // resolved, `siblings` would be `[undefined]`, the `.filter(drop => drop && …)` would empty it, and this
      // would be `[]` — the exact state in which a dragged row cannot enter the trash and `onTrash` is dead.
      expect(connectedTo).toHaveBeenCalledTimes(1);
      expect(connectedTo.calls.mostRecent().args[0]).toEqual([trash!._dropListRef]);

      // ...and CDK's own miss-warning did NOT fire. Between Task 6 and Task 7 it fired on EVERY drag:
      //   "CdkDropList could not find connected drop list with id "trash""
      // Its disappearance is the fastest confirmation a human has that this is wired; this pins it.
      const misses = warn.calls.allArgs()
        .filter(args => String(args[0]).includes('could not find connected drop list'));
      expect(misses).toEqual([]);
    });

  it('🔴 the trash survives an EMPTY week — it sits OUTSIDE the @if that guards the day-totals footer', () => {
    setUp([]);
    fixture.detectChanges();

    // It is a direct child of `.grid`, at the root of the template. Tucked inside `@if (groups().length)` it
    // would vanish exactly when the grid is empty — and, being a SIBLING view of the `@for`, a `#trash`
    // declared there would not even be visible to the groups' bindings.
    expect(component.groups().length).toBe(0);
    expect(dropLists(fixture).map(l => l.id)).toEqual(['trash']);
    expect(document.getElementById('trash')).not.toBeNull();
  });

  // ---- 🔴 the drop ARMS the delete; the DIALOG commits it -----------------------------------------------
  //
  // The drop used to delete INSTANTLY. The delete is soft — `setTaskActive(id, true)` restores the task, and
  // that path is even tested — but NO SCREEN CALLS THE RESTORE, so a mis-drop was unrecoverable from the UI.
  // A drag is the easiest gesture in the app to perform by accident. So the drop now only ARMS the delete, and
  // `confirmDelete` is the ONLY thing that writes.

  it('does NOT delete on drop — it asks first', () => {
    setUp();

    const row = component.groups()[0].tasks[0];
    component.onTrash(trashEvent(row));

    expect(api.setTaskActive).not.toHaveBeenCalled();          // nothing written yet
    expect(component.pendingDelete()).toEqual({ taskId: row.taskId, taskName: 'Design schema' });
  });

  it('deletes only after the dialog is confirmed', async () => {
    setUp();
    api.setTaskActive.and.returnValue(of(void 0));

    const row = component.groups()[0].tasks[0];
    component.onTrash(trashEvent(row));
    await component.confirmDelete();

    expect(api.setTaskActive).toHaveBeenCalledWith(row.taskId, false);
    expect(component.pendingDelete()).toBeNull();
  });

  it('writes nothing when the dialog is cancelled', () => {
    setUp();

    component.onTrash(trashEvent(component.groups()[0].tasks[0]));
    component.cancelDelete();

    expect(api.setTaskActive).not.toHaveBeenCalled();
    expect(component.pendingDelete()).toBeNull();
  });

  it('🔴 renders the dialog in the DOM after a drop — a handler test cannot prove the handler is REACHABLE',
    () => {
      setUp();

      component.onTrash(trashEvent(component.groups()[0].tasks[0]));
      fixture.detectChanges();

      // 🔴 THE WIRING, and it is the only test here that can see it. Every other test in this section calls
      // `confirmDelete` / `cancelDelete` DIRECTLY — so delete the `<app-confirm-dialog>` line from the template
      // and they ALL still pass, against a dialog the user can never reach and a delete that can never happen.
      // VERIFIED by doing exactly that: with the line removed this test, and only this test, goes red.
      expect(fixture.debugElement.query(By.directive(ConfirmDialogComponent))).toBeTruthy();
    });

  // ---- 🔴 the write itself (now reached through the dialog, not through the drop) -----------------------

  it('🔴 a confirmed delete SOFT-deletes the dropped row — is_active = false, nothing is destroyed',
    async () => {
      setUp();
      api.setTaskActive.and.returnValue(of(void 0));
      api.getWeek.calls.reset();

      const row = component.groups()[0].tasks[0];
      component.onTrash(trashEvent(row));
      await component.confirmDelete();

      // `false`, and the row read from `event.item.data` — NOT from an index into `groups()`, which a
      // concurrent refresh could have re-pointed at a different task between the pick-up and the drop.
      expect(api.setTaskActive).toHaveBeenCalledOnceWith(MONDAY_TASK, false);

      // CDK does not remove the row for us — we never call `transferArrayItem`. The re-fetch is the ONLY thing
      // that makes it leave the grid.
      expect(api.getWeek).toHaveBeenCalled();
      expect(component.deleting()).toBeFalse();       // released again
    });

  it('writes nothing — and asks nothing — when the drop\'s source and destination are the same list', () => {
    setUp();

    component.onTrash(trashEvent(component.groups()[0].tasks[0], { ontoItself: true }));

    expect(api.setTaskActive).not.toHaveBeenCalled();
    expect(component.pendingDelete()).toBeNull();   // not even a dialog: nothing was dropped on the bin
  });

  it('🔴 a second CONFIRM while the first delete is in flight is refused — the row SNAPS BACK and invites it',
    async () => {
      setUp();
      api.setTaskActive.and.returnValue(new Subject<void>());   // never settles: still in flight

      const row = component.groups()[0].tasks[0];
      component.onTrash(trashEvent(row));
      void component.confirmDelete();               // delete 1 — hangs, and the dialog closes

      // The row is still on screen (CDK snapped it back; only the refresh removes it), so on a slow link the
      // user sees nothing happen and drags it to the bin AGAIN — which re-opens the dialog over an in-flight
      // delete. The dialog's `[busy]="deleting()"` disables the button, so this is not normally reachable from
      // the UI; the handler refuses it anyway, as `canMoveMonth` is refused in both the template and the
      // handler. Without this, a second write and a second re-fetch fire for a task already being deleted.
      component.onTrash(trashEvent(row));
      await component.confirmDelete();              // delete 2 — must be refused outright

      expect(api.setTaskActive).toHaveBeenCalledTimes(1);
      expect(component.deleting()).toBeTrue();      // delete 1 still in flight

      // 🔴 And the refused confirm did NOT close the dialog. Clearing it first would have shown the user a
      // dialog vanishing on a delete that never happened — they would read that as success.
      expect(component.pendingDelete()).not.toBeNull();
    });

  it('🔴 404: someone else already deleted it — say so and RE-READ, do not die as an unhandled rejection',
    async () => {
      setUp();
      // Reachable: another user can delete this task, or remove us from its team, while the screen is open.
      api.setTaskActive.and.returnValue(throwError(() => httpError(404, null)));
      api.getWeek.calls.reset();

      component.onTrash(trashEvent(component.groups()[0].tasks[0]));
      await component.confirmDelete();

      // `confirmDelete` is an async method bound to the dialog's (confirm) output: an escaping error would be
      // an UNHANDLED PROMISE REJECTION — console-only, and NOWHERE THE USER CAN SEE. They would confirm, watch
      // the dialog close and the row stay put, be told nothing, and try again.
      expect(toast.show).toHaveBeenCalledWith('This task is no longer available.');
      expect(api.getWeek).toHaveBeenCalled();      // the server is right and our screen is stale
      expect(component.deleting()).toBeFalse();    // released, so the next delete is not dead
    });

  it('🔴 a failed delete toasts but does NOT re-read — unlike a reorder, one write has no HALF-done state',
    async () => {
      setUp();
      api.setTaskActive.and.returnValue(throwError(() => httpError(500, null)));
      api.getWeek.calls.reset();

      component.onTrash(trashEvent(component.groups()[0].tasks[0]));
      await component.confirmDelete();

      expect(toast.show).toHaveBeenCalledWith('Could not delete this task. Please try again.');

      // 🔴 The considered difference from `onReorderError`, which re-reads on EVERY error. A reorder is a
      // SEQUENTIAL BATCH of N writes, so a failure on write 3 of 5 leaves the group half-renormalised on the
      // server and the screen showing an order that does not exist. A delete is ONE write: it failed, so the
      // server is unchanged, and CDK has already snapped the row back — the screen ALREADY matches the server.
      expect(api.getWeek).not.toHaveBeenCalled();
      expect(component.deleting()).toBeFalse();
    });
});
