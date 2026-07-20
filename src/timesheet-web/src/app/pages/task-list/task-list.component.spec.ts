import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import {
  BacklogDto, MeResponse, PcaContactDto, SavedBody, TagDto, TaskItemDto, TaskListRowDto, TaskListScreenDto,
  TeamDto, UserDto,
} from '../../api/models';
import { DataChange, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { TaskListComponent } from './task-list.component';

/**
 * The Task List screen.
 *
 * The pure logic (the whole-record copy, the chips, the grouping, the progress parser) is pinned in
 * `task-list.model.spec.ts`. What is pinned HERE is everything that only exists once the component is wired
 * to the service: which calls it makes, WHICH IT DOES NOT MAKE, and what it does when they fail.
 */

const ME: MeResponse = { id: 1, name: 'Nhan', isAdmin: false, activeTeamId: 2, memberTeamIds: [1, 2] };
const TEAMS: TeamDto[] = [{ id: 1, name: 'Alpha', isActive: true }, { id: 2, name: 'Beta', isActive: true }];
const USERS: UserDto[] = [{ id: 42, name: 'An', isActive: true }, { id: 43, name: 'Binh', isActive: true }];
const PCA: PcaContactDto[] = [{ id: 9, name: 'Kim', isActive: true }, { id: 10, name: 'Lan', isActive: true }];
const TAGS: TagDto[] = [{ id: 1, text: 'blocked', color: '#C0362C', icon: '🚧' }];

/** 🔴 The loaded backlog: every field distinct, so a dropped one cannot hide behind a default. */
const BACKLOG: BacklogDto = {
  id: 100, backlogCode: 'ARCS-101', project: 'ARCS', periodMonth: '2026-07',
  type: 'Implement', assigneeUserId: 42, pcaContactId: 9, note: 'keep me',
  roughEstimateHours: 12, officialEstimateHours: 20,
  startDate: '2026-07-01', endDate: '2026-07-31',
  deadlineInternal: '2026-07-20', deadlineExternal: '2026-07-25',
  progressPercent: 30, teamId: 3, rowVersion: 5,
};

const TASK: TaskItemDto = {
  id: 500, backlogId: 100, taskName: 'Build it', status: 'Todo',
  type: 'IT', assigneeUserId: 43, rowVersion: 11, orderIndex: 0, isActive: true,
};

const ROW: TaskListRowDto = {
  backlogId: 100, backlogCode: 'ARCS-101', project: 'ARCS', type: 'Implement',
  pctAssigneeName: 'An', pcaContactName: 'Kim', assigneeUserId: 42, pcaContactId: 9,
  deadlineInternal: '2026-07-20', deadlineExternal: '2026-07-25',
  startDate: '2026-07-01', endDate: '2026-07-31',
  progressPercent: 30, loggedHours: 8, estimateHours: 20,
  scheduleState: 2, tags: [{ id: 1, text: 'blocked', color: '#C0362C', icon: '🚧' }], tasks: [TASK],
};

const SCREEN: TaskListScreenDto = {
  rows: [ROW],
  gantt: {
    axis: ['2026-07-01', '2026-07-02'],
    bars: [{
      backlogId: 100, backlogCode: 'ARCS-101', startDayIndex: 0, spanWorkingDays: 2,
      hasStart: true, scheduleState: 2, externalMarkerIndex: 1,
    }],
  },
};

const SAVED: SavedBody = { rowVersion: 6 };

describe('TaskListComponent', () => {
  let fixture: ComponentFixture<TaskListComponent>;
  let component: TaskListComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let toast: jasmine.SpyObj<ToastService>;

  function arrange(): void {
    api = jasmine.createSpyObj<WorklogService>('WorklogService', [
      'me', 'getTeamsActive', 'getTagList', 'getUsersActive', 'getUserNames',
      'getPcaContactsActive', 'getPcaContactNames',
      'getTaskListScreen', 'getBacklog', 'updateBacklog', 'setBacklogTags',
      'getTaskTags', 'setTaskTags', 'setTaskStatus', 'setTaskExtended',
      'continueBacklog', 'exportTaskListMarkdown',
    ]);

    api.me.and.returnValue(of(ME));
    api.getTeamsActive.and.returnValue(of(TEAMS));
    api.getTagList.and.returnValue(of(TAGS));
    api.getUsersActive.and.returnValue(of(USERS));
    api.getUserNames.and.returnValue(of([{ id: 42, name: 'An' }, { id: 43, name: 'Binh' }, { id: 77, name: 'Departed' }]));
    api.getPcaContactsActive.and.returnValue(of(PCA));
    api.getPcaContactNames.and.returnValue(of([{ id: 9, name: 'Kim' }, { id: 10, name: 'Lan' }, { id: 88, name: 'Gone' }]));
    api.getTaskListScreen.and.returnValue(of(SCREEN));
    api.getBacklog.and.returnValue(of(BACKLOG));
    api.updateBacklog.and.returnValue(of(SAVED));
    api.setBacklogTags.and.returnValue(of(SAVED));
    api.getTaskTags.and.returnValue(of([1]));
    api.setTaskTags.and.returnValue(of(SAVED));
    api.setTaskStatus.and.returnValue(of(SAVED));
    api.setTaskExtended.and.returnValue(of(SAVED));
    api.continueBacklog.and.returnValue(of(BACKLOG));
    api.exportTaskListMarkdown.and.returnValue(of('# markdown'));

    toast = jasmine.createSpyObj<ToastService>('ToastService', ['show']);

    TestBed.configureTestingModule({
      imports: [TaskListComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: ToastService, useValue: toast },
        {
          provide: RealtimeService,
          useValue: { start: () => undefined, dataChanged: new Subject<DataChange>().asObservable() },
        },
      ],
    });
  }

  /** Mount, and drain the constructor's reads + the team filter's seeded emission. */
  async function mount(): Promise<void> {
    fixture = TestBed.createComponent(TaskListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function setUp(): Promise<void> {
    arrange();
    await mount();
  }

  // ===================================================================================================
  // LOAD
  // ===================================================================================================

  it('reads the WHOLE screen in ONE call — rows and Gantt together', async () => {
    await setUp();

    expect(component.rows().length).toBe(1);
    expect(component.gantt()?.bars?.length).toBe(1);
    // One round-trip for both. Two calls could put the chart and the grid into visible disagreement.
    expect(api.getTaskListScreen).toHaveBeenCalled();
  });

  it('narrows to the team filter’s seeded selection once it arrives', async () => {
    await setUp();

    // The filter defaults to the ACTIVE team (2) and the screen re-reads scoped to it.
    expect(api.getTaskListScreen).toHaveBeenCalledWith(component.year(), component.month(), [2]);
  });

  // ===================================================================================================
  // 🔴 H4 — THE EMPTY TEAM SELECTION
  // ===================================================================================================

  it('🔴 makes NO API CALL when the user unchecks every team — `teamIds: []` means ALL MY TEAMS', async () => {
    await setUp();
    api.getTaskListScreen.calls.reset();

    component.onTeams([]);                       // the user just unchecked the last team
    await settle();

    // 🔴 THE ASSERTION THE WHOLE HAZARD EXISTS FOR. `[]` appends no query key, so the server would have
    //    read it as "every team the caller belongs to" and rendered EVERYTHING.
    expect(api.getTaskListScreen).not.toHaveBeenCalled();
    expect(component.noTeams()).toBe(true);
    expect(component.rows()).toEqual([]);
  });

  it('an UNLOADED filter (its read failed) is NOT an empty selection — it still calls, unscoped', async () => {
    await setUp();
    api.getTaskListScreen.calls.reset();

    component.teamIds.set(undefined);            // what a failed filter leaves behind: no emission at all
    await component.load();
    await settle();

    // `undefined` is the correct degradation: the server reads an absent key as "all my teams". A network
    // error is not a user's choice, and must not blank their screen.
    expect(api.getTaskListScreen).toHaveBeenCalledWith(component.year(), component.month(), undefined);
    expect(component.noTeams()).toBe(false);
  });

  // ===================================================================================================
  // 🔴 H1 — THE WHOLE-RECORD TRAP, ON THE WIRE
  // ===================================================================================================

  it('🔴 a progress edit LOADS THE BACKLOG FIRST and round-trips type, assigneeUserId AND pcaContactId', async () => {
    await setUp();

    component.startProgress(ROW);
    component.progressDraft.set('55');
    component.commitProgress(ROW);
    await settle();

    // The GET is not optional: it is the only source of both the untouched fields and the rowVersion.
    expect(api.getBacklog).toHaveBeenCalledWith(100);

    const [id, body] = api.updateBacklog.calls.mostRecent().args;
    expect(id).toBe(100);

    // The edit itself.
    expect(body.progressPercent).toBe(55);

    // 🔴 THE THREE THE BRIEF NAMES — each would be written NULL by a body built from the form alone.
    expect(body.type).toBe('Implement');
    expect(body.assigneeUserId).toBe(42);
    expect(body.pcaContactId).toBe(9);

    // ...and the rest of the record with them.
    expect(body.backlogCode).toBe('ARCS-101');
    expect(body.note).toBe('keep me');
    expect(body.officialEstimateHours).toBe(20);
    expect(body.expectedVersion).toBe(5);
  });

  // ===================================================================================================
  // INLINE PROGRESS
  // ===================================================================================================

  it('🔴 ESCAPE reverts WITHOUT saving — and disarms the blur that follows it', async () => {
    await setUp();

    component.startProgress(ROW);
    component.progressDraft.set('99');
    component.cancelProgress();

    // The blur fires as the input leaves the DOM. It must NOT commit the edit Escape just cancelled.
    component.commitProgress(ROW);
    await settle();

    expect(api.updateBacklog).not.toHaveBeenCalled();
    expect(component.editingProgress()).toBeNull();
  });

  it('🔴 an invalid progress NEVER commits — not as 0, not as null', async () => {
    await setUp();

    for (const bad of ['5.5', '101', 'abc', '-1']) {
      component.startProgress(ROW);
      component.progressDraft.set(bad);
      component.commitProgress(ROW);
      await settle();
    }

    expect(api.updateBacklog).not.toHaveBeenCalled();
    expect(toast.show).toHaveBeenCalledWith(jasmine.stringMatching(/whole number from 0 to 100/));
  });

  it('an EMPTY box CLEARS the percent (a real edit, unlike an invalid one)', async () => {
    await setUp();

    component.startProgress(ROW);
    component.progressDraft.set('');
    component.commitProgress(ROW);
    await settle();

    expect(api.updateBacklog.calls.mostRecent().args[1].progressPercent).toBeNull();
  });

  it('an UNCHANGED percent writes nothing', async () => {
    await setUp();

    component.startProgress(ROW);
    component.progressDraft.set('30');           // the row is already at 30
    component.commitProgress(ROW);
    await settle();

    expect(api.updateBacklog).not.toHaveBeenCalled();
  });

  // ===================================================================================================
  // 🔴 H3 — A DEADLINE CHANGE REQUIRES A REASON NOTE
  // ===================================================================================================

  function dateEvent(value: string): Event {
    const el = document.createElement('input');
    el.type = 'date';
    el.value = value;
    return { target: el } as unknown as Event;
  }

  it('🔴 a deadline change WRITES NOTHING until the reason note is confirmed', async () => {
    await setUp();

    component.onDeadline(ROW, 'internal', dateEvent('2026-08-15'));
    await settle();

    expect(component.pendingDeadline()).not.toBeNull();
    expect(api.updateBacklog).not.toHaveBeenCalled();      // 🔴 parked, not saved
  });

  it('confirming sends the note as `auditNote`, on the deadline field only', async () => {
    await setUp();

    component.onDeadline(ROW, 'internal', dateEvent('2026-08-15'));
    component.deadlineNote.set('client pushed the date');
    component.confirmDeadline();
    await settle();

    const body = api.updateBacklog.calls.mostRecent().args[1];
    expect(body.deadlineInternal).toBe('2026-08-15');
    expect(body.auditNote).toBe('client pushed the date');
    expect(body.deadlineExternal).toBe('2026-07-25');      // the OTHER deadline is untouched
    expect(body.pcaContactId).toBe(9);                     // ...and so is the rest of the record
  });

  it('an EMPTY note is allowed — the desktop’s OK button is always enabled', async () => {
    await setUp();

    component.onDeadline(ROW, 'external', dateEvent('2026-09-01'));
    component.confirmDeadline();
    await settle();

    const body = api.updateBacklog.calls.mostRecent().args[1];
    expect(body.deadlineExternal).toBe('2026-09-01');
    expect(body.auditNote).toBeNull();
  });

  it('🔴 CANCEL writes nothing AND puts the date input back', async () => {
    await setUp();

    const el = document.createElement('input');
    el.type = 'date';
    el.value = '2026-08-15';                               // what the user just picked
    component.onDeadline(ROW, 'internal', { target: el } as unknown as Event);

    component.cancelDeadline();
    await settle();

    expect(api.updateBacklog).not.toHaveBeenCalled();
    expect(el.value).toBe('2026-07-20');                   // reverted to the committed value
    expect(component.pendingDeadline()).toBeNull();
  });

  it('a START/END date needs NO note — it saves straight through', async () => {
    await setUp();

    component.onDate(ROW, 'start', dateEvent('2026-07-05'));
    await settle();

    expect(component.pendingDeadline()).toBeNull();        // no prompt
    const body = api.updateBacklog.calls.mostRecent().args[1];
    expect(body.startDate).toBe('2026-07-05');
    expect(body.auditNote).toBeNull();
    expect(body.endDate).toBe('2026-07-31');               // the other date survives
  });

  // ===================================================================================================
  // 🔴 H2 — A TAG WRITE IS CHECKED AGAINST THE PARENT'S VERSION
  // ===================================================================================================

  it('🔴 a backlog tag write RE-READS the backlog for a CURRENT version — it never reuses a cached one', async () => {
    await setUp();

    component.commitBacklogTags(ROW, [1, 2]);
    await settle();

    // The fresh GET is the point: `setBacklogTags` BUMPS the backlog's rowVersion, so a screen that cached
    // the version it loaded the page with would 409 against its own previous tag write.
    expect(api.getBacklog).toHaveBeenCalledWith(100);
    expect(api.setBacklogTags).toHaveBeenCalledWith(100, [1, 2], 5);
  });

  it('🔴 two tag writes in a row each re-read — the second uses the version the FIRST one bumped to', async () => {
    await setUp();

    // The server has moved on after the first write: the next GET reports the bumped version.
    api.getBacklog.and.returnValue(of({ ...BACKLOG, rowVersion: 5 }));
    component.commitBacklogTags(ROW, [1]);
    await settle();

    api.getBacklog.and.returnValue(of({ ...BACKLOG, rowVersion: 6 }));   // bumped by the write above
    component.commitBacklogTags(ROW, [1, 2]);
    await settle();

    expect(api.setBacklogTags.calls.first().args).toEqual([100, [1], 5]);
    expect(api.setBacklogTags.calls.mostRecent().args).toEqual([100, [1, 2], 6]);
  });

  it('a task tag write is checked against the TASK’s own version', async () => {
    await setUp();

    component.commitTaskTags(TASK, [1]);
    await settle();

    expect(api.setTaskTags).toHaveBeenCalledWith(500, [1], 11);
  });

  it('a task’s tags are read LAZILY, only when its picker opens', async () => {
    await setUp();

    expect(api.getTaskTags).not.toHaveBeenCalled();        // not on load — that would be an N+1

    await component.toggleTaskPicker(TASK);
    await settle();

    expect(api.getTaskTags).toHaveBeenCalledWith(500);
    expect(component.pickerSelected()).toEqual([1]);
  });

  // ===================================================================================================
  // TASK ROWS
  // ===================================================================================================

  it('a status change uses the NARROW route, with the task’s version', async () => {
    await setUp();

    component.onTaskStatus(TASK, selectEvent('Done'));
    await settle();

    // The narrow route touches status and nothing else. `updateTask` would replace the NAME too.
    expect(api.setTaskStatus).toHaveBeenCalledWith(500, 'Done', 11);
  });

  it('🔴 changing a task’s TYPE round-trips its assignee (both ride one write)', async () => {
    await setUp();

    component.onTaskType(TASK, selectEvent('Implement'));
    await settle();

    const [id, body] = api.setTaskExtended.calls.mostRecent().args;
    expect(id).toBe(500);
    expect(body.type).toBe('Implement');
    expect(body.assigneeUserId).toBe(43);                  // 🔴 would be CLEARED by a type-only body
    expect(body.expectedVersion).toBe(11);
  });

  it('🔴 changing a task’s ASSIGNEE round-trips its type', async () => {
    await setUp();

    component.onTaskAssignee(TASK, selectEvent('42'));
    await settle();

    const body = api.setTaskExtended.calls.mostRecent().args[1];
    expect(body.assigneeUserId).toBe(42);
    expect(body.type).toBe('IT');                          // 🔴 the mirror-image bug
  });

  // ===================================================================================================
  // 🔴 BACKLOG TYPE / PCT (assignee) / PCA — the inline dropdowns this fix restores. R7 all the way down.
  // ===================================================================================================

  it('🔴 a TYPE change LOADS THE BACKLOG FIRST and round-trips startDate + note (the row has NEITHER)', async () => {
    await setUp();

    component.onBacklogType(ROW, selectEvent('Investigate'));
    await settle();

    // The GET is the only source of the untouched fields AND the rowVersion — the sparse row has neither.
    expect(api.getBacklog).toHaveBeenCalledWith(100);
    const [id, body] = api.updateBacklog.calls.mostRecent().args;
    expect(id).toBe(100);
    expect(body.type).toBe('Investigate');                 // the edit
    expect(body.startDate).toBe('2026-07-01');             // 🔴 survives — copied from the DTO, not the row
    expect(body.note).toBe('keep me');                     // 🔴 survives
    expect(body.expectedVersion).toBe(5);
    expect(body.auditNote).toBeNull();                     // Type carries no reason note; only deadlines do
  });

  it('a PCT (assignee) change round-trips the record, overriding only assigneeUserId', async () => {
    await setUp();

    component.onBacklogAssignee(ROW, selectEvent('43'));
    await settle();

    const body = api.updateBacklog.calls.mostRecent().args[1];
    expect(body.assigneeUserId).toBe(43);
    expect(body.pcaContactId).toBe(9);                     // 🔴 the OTHER id survives
    expect(body.startDate).toBe('2026-07-01');
  });

  it('a PCA change round-trips the record, overriding only pcaContactId', async () => {
    await setUp();

    component.onBacklogPca(ROW, selectEvent('10'));
    await settle();

    const body = api.updateBacklog.calls.mostRecent().args[1];
    expect(body.pcaContactId).toBe(10);
    expect(body.assigneeUserId).toBe(42);                  // untouched
    expect(body.note).toBe('keep me');
  });

  it('clearing the PCT assignee ("—") sends null', async () => {
    await setUp();

    component.onBacklogAssignee(ROW, selectEvent(''));
    await settle();

    expect(api.updateBacklog.calls.mostRecent().args[1].assigneeUserId).toBeNull();
  });

  it('an UNCHANGED type writes nothing', async () => {
    await setUp();

    component.onBacklogType(ROW, selectEvent('Implement'));   // ROW.type is already 'Implement'
    await settle();

    expect(api.updateBacklog).not.toHaveBeenCalled();
  });

  it('🔴 a DEACTIVATED assignee / PCA is still OFFERED as "(inactive)" — not a blank box', async () => {
    await setUp();

    // 77 ('Departed') and 88 ('Gone') are in the /names lists but NOT in the active lists.
    const assignee = component.assigneeOptionsFor({ ...ROW, assigneeUserId: 77 });
    expect(assignee.some(o => o.id === 77 && o.label === 'Departed (inactive)')).toBe(true);

    const pca = component.pcaOptionsFor({ ...ROW, pcaContactId: 88 });
    expect(pca.some(o => o.id === 88 && o.label === 'Gone (inactive)')).toBe(true);
  });

  function selectEvent(value: string): Event {
    const el = document.createElement('select');
    const option = document.createElement('option');
    option.value = value;
    el.appendChild(option);
    el.value = value;
    return { target: el } as unknown as Event;
  }

  // ===================================================================================================
  // CONTINUE
  // ===================================================================================================

  it('continues into the NEXT month, rolling the year', async () => {
    await setUp();
    component.year.set(2026);
    component.month.set(12);

    component.continueRow(ROW);
    await settle();

    expect(api.continueBacklog).toHaveBeenCalledWith(100, '2027-01');
  });

  it('🔴 a duplicate shows the SERVER’S OWN sentence, not ours', async () => {
    await setUp();
    component.month.set(7);
    api.continueBacklog.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400, error: { error: "'ARCS-101' already exists in 2026-08." },
    })));

    component.continueRow(ROW);
    await settle();

    expect(toast.show).toHaveBeenCalledWith("'ARCS-101' already exists in 2026-08.");
  });

  it('cannot continue in "All months" — there is no next month to roll into', async () => {
    await setUp();
    component.month.set(0);

    component.continueRow(ROW);
    await settle();

    expect(api.continueBacklog).not.toHaveBeenCalled();
    expect(component.concreteMonth()).toBe(false);
  });

  // ===================================================================================================
  // EXPORT
  // ===================================================================================================

  it('does not export in "All months" — the markdown overview is month-scoped', async () => {
    await setUp();
    component.month.set(0);

    await component.exportMonth();
    await settle();

    expect(api.exportTaskListMarkdown).not.toHaveBeenCalled();
  });

  it('surfaces the server’s "no data" sentence rather than downloading an empty file', async () => {
    await setUp();
    component.month.set(7);
    api.exportTaskListMarkdown.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400, error: { error: 'No task list data for this month to export.' },
    })));

    await component.exportMonth();
    await settle();

    expect(toast.show).toHaveBeenCalledWith('No task list data for this month to export.');
  });

  // ===================================================================================================
  // 🔴 H6 — THE CATCH, AND THE RE-ENTRANCY GUARD
  // ===================================================================================================

  it('🔴 a failed write TOASTS and RELEASES the guard — it never escapes as an unhandled rejection', async () => {
    await setUp();
    api.updateBacklog.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));

    component.startProgress(ROW);
    component.progressDraft.set('55');
    component.commitProgress(ROW);                          // returns void — nothing to await, nothing to catch
    await settle();

    expect(toast.show).toHaveBeenCalledWith('The progress could not be saved.');
    expect(component.saving()).toBe(false);                 // the guard did not jam shut
  });

  it('🔴 a 409 is explained, and the screen resyncs', async () => {
    await setUp();
    api.updateBacklog.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    api.getTaskListScreen.calls.reset();

    component.startProgress(ROW);
    component.progressDraft.set('55');
    component.commitProgress(ROW);
    await settle();

    expect(toast.show).toHaveBeenCalledWith(jasmine.stringMatching(/Someone else changed this/));
    expect(api.getTaskListScreen).toHaveBeenCalled();       // reloaded, so the user sees the other write
  });

  /**
   * 🔴 THE RE-ENTRANCY GUARD — probed through the TAG PICKER, and that choice is the whole point.
   *
   * The obvious probe is the progress box, and it is a TRAP: `startProgress()` has a `saving()` check OF ITS
   * OWN, so it refuses to re-open the editor mid-save and the second click dies there — BEFORE `mutate()`'s
   * guard is ever reached. A test written that way passes with `mutate()`'s guard DELETED. (It did. The
   * mutation check caught it, which is what the mutation check is for.)
   *
   * `commitBacklogTags` has no such upstream check — the picker is merely `[disabled]` in the template — so
   * `mutate()`'s guard is the ONLY thing that can stop the second call. That makes this assertion load-bearing.
   */
  it('🔴 THE RE-ENTRANCY GUARD: a second mutation while one is in flight is DROPPED, not applied twice', async () => {
    await setUp();

    // Hold chain 1 open at its GET.
    const gate = new Subject<BacklogDto>();
    api.getBacklog.and.returnValue(gate);

    component.commitBacklogTags(ROW, [1]);                  // chain 1 — parked on getBacklog
    await settle();

    expect(component.saving()).toBe(true);

    // The second click. Without the guard, THIS chain's GET lands AFTER chain 1's write, reads the version
    // that write already bumped, and applies the mutation a SECOND time — with no 409 to stop it, because
    // the version it holds is genuinely current.
    component.commitBacklogTags(ROW, [1, 2]);
    await settle();

    expect(api.getBacklog).toHaveBeenCalledTimes(1);        // 🔴 the second chain never even started

    gate.next(BACKLOG);
    gate.complete();
    await settle();

    expect(api.setBacklogTags).toHaveBeenCalledTimes(1);    // 🔴 written ONCE
    expect(component.saving()).toBe(false);                 // and the guard released
  });

  it('the progress editor additionally refuses to re-open mid-save', async () => {
    await setUp();
    const gate = new Subject<BacklogDto>();
    api.getBacklog.and.returnValue(gate);

    component.commitBacklogTags(ROW, [1]);                  // something else is saving
    await settle();

    component.startProgress(ROW);
    expect(component.editingProgress()).toBeNull();         // the box does not open over an in-flight write

    gate.next(BACKLOG);
    gate.complete();
    await settle();
  });

  it('a failed screen read shows an error instead of a silently empty grid', async () => {
    arrange();
    api.getTaskListScreen.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    await mount();

    expect(component.error()).toBe('The task list could not be loaded.');
    expect(component.rows()).toEqual([]);
  });

  // ===================================================================================================
  // VIEW
  // ===================================================================================================

  it('groups into project bands and hides the project from the row (the band already shows it)', async () => {
    await setUp();

    expect(component.bands().map(b => b.key)).toEqual(['ARCS']);
    expect(fixture.nativeElement.textContent).toContain('ARCS-101');
  });

  it('🔴 bands by TEAM when >1 team is checked, by PROJECT otherwise (TL-12)', async () => {
    await setUp();

    const twoTeamScreen: TaskListScreenDto = {
      gantt: SCREEN.gantt,
      rows: [
        { ...ROW, backlogId: 100, teamId: 2, project: 'ARCS' },
        { ...ROW, backlogId: 200, teamId: 1, project: 'ARMS' },
      ],
    };
    api.getTaskListScreen.and.returnValue(of(twoTeamScreen));

    component.onTeams([1, 2]);                       // >1 team -> team mode; names from getTeamsActive()
    await settle();
    expect(component.bands().map(b => b.key)).toEqual(['Alpha', 'Beta']);

    component.onTeams([2]);                          // one team -> project mode
    await settle();
    expect(component.bands().map(b => b.key)).toEqual(['ARCS', 'ARMS']);
  });

  // ===================================================================================================
  // 🔴 P8 — the PROJECT label, lost entirely in team-banded mode (A4 audit #30)
  // ===================================================================================================

  it('🔴 P8: surfaces PROJECT on the row when team-banded — the band key no longer carries it (>1 team)', async () => {
    await setUp();

    const twoTeamScreen: TaskListScreenDto = {
      gantt: SCREEN.gantt,
      rows: [
        { ...ROW, backlogId: 100, teamId: 2, project: 'ARCS' },
        { ...ROW, backlogId: 200, teamId: 1, project: 'ARMS' },
      ],
    };
    api.getTaskListScreen.and.returnValue(of(twoTeamScreen));

    component.onTeams([1, 2]);                       // >1 team -> team mode; band keys become team names
    await settle();

    expect(component.bands().map(b => b.key)).toEqual(['Alpha', 'Beta']);   // team names, not projects

    const headTexts = Array.from(fixture.nativeElement.querySelectorAll('.tl-head') as NodeListOf<HTMLElement>)
      .map(h => h.textContent ?? '');
    expect(headTexts.some(t => t.includes('ARCS'))).toBe(true);
    expect(headTexts.some(t => t.includes('ARMS'))).toBe(true);
  });

  it('does NOT add a project cell to the row when exactly one team is selected (unchanged)', async () => {
    await setUp();                                   // seeded to team [2] -> project-banded mode

    expect(component.teamMode()).toBe(false);
    const head = fixture.nativeElement.querySelector('.tl-head') as HTMLElement;
    expect(head.textContent).not.toContain('Project');
  });

  it('shows exactly ONE system chip — Late, never Late AND At risk', async () => {
    await setUp();

    const chips = component.chips(ROW);
    expect(chips.filter(c => c.kind === 'late' || c.kind === 'warning').length).toBe(1);
    expect(chips[0].text).toBe('⚠ Late');
  });

  it('renders the SERVER’s Gantt model — it computes no geometry of its own', async () => {
    await setUp();
    component.view.set('gantt');
    fixture.detectChanges();

    const bar = component.gantt()?.bars?.[0];
    expect(component.barLeft(bar?.startDayIndex)).toBe(0);
    expect(component.barWidth(bar?.spanWorkingDays)).toBe(2 * component.COL);
    expect(component.barVisible(bar?.spanWorkingDays)).toBe(true);
  });

  it('a backlog with no dates at all (span 0) draws no bar', async () => {
    await setUp();
    expect(component.barVisible(0)).toBe(false);
  });

  // ===================================================================================================
  // 🔴 P8 — the Gantt empty-scope caption (A4 audit #36): "no data" must read differently from "broken"
  // ===================================================================================================

  it('🔴 P8: shows a caption when nothing in scope has a date, instead of a blank chart', async () => {
    await setUp();

    api.getTaskListScreen.and.returnValue(of({ rows: [ROW], gantt: { axis: [], bars: [] } }));
    await component.load();
    await settle();

    component.view.set('gantt');
    fixture.detectChanges();

    expect(component.ganttEmpty()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('No dated backlogs to chart for this month.');
    expect(fixture.nativeElement.querySelector('.tl-gantt')).toBeNull();   // the chart card itself is not drawn
  });

  it('does not show the empty caption when the Gantt has an axis to draw', async () => {
    await setUp();
    component.view.set('gantt');
    fixture.detectChanges();

    expect(component.ganttEmpty()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain('No dated backlogs to chart for this month.');
  });
});
