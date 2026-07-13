import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import {
  BacklogAuditDto, BacklogDto, NamedRefDto, PcaContactDto, SavedBody, TaskItemDto, UserDto,
} from '../../api/models';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { BacklogEditorComponent } from './backlog-editor.component';

/**
 * The editor's job is to be BORING about six things that are each a real bug someone has already paid for:
 *
 *   H1  a reorder is expressed by MOVING THE ARRAY ELEMENT -- `planTaskWrites` reindexes from POSITION, and
 *       mutating `EditRow.orderIndex` instead is a silent no-op
 *   H2  the assignee dropdown needs the ACTIVE list for its choices and the ALL-NAMES list to render a
 *       CURRENT value that has since been deactivated -- or an untouched save clears her
 *   H3  six fields are HIDDEN on edit and must still be WRITTEN, from the DTO
 *   H4  a 409 gets a message and a re-read -- never the cell merge dialog, and NEVER a silent retry
 *   H5  create is NOT transactional, and a half-created backlog must not be reported as a success
 *   --  `validate` refuses a bad save BEFORE any request is sent
 */

/** The assignee (id 3) is NOT in the active list below -- she has left. That is deliberate; see H2. */
const DEPARTED = 3;

const BACKLOG: BacklogDto = {
  id: 7,
  backlogCode: 'ARCS-1042',
  project: 'ARCS',
  periodMonth: '2026-07',
  type: 'Implement',
  assigneeUserId: DEPARTED,
  roughEstimateHours: 8,
  officialEstimateHours: 12,
  note: 'first note',

  // 🔴 The six the EDIT form HIDES. `PUT /api/backlogs/{id}` replaces the whole record, so every one of them
  // must come back out of a save that never rendered a single input for them.
  startDate: '2026-07-01',
  endDate: '2026-07-31',
  deadlineInternal: '2026-07-20',
  deadlineExternal: '2026-07-25',
  progressPercent: 40,
  pcaContactId: 9,

  rowVersion: 5,
};

const TASKS: TaskItemDto[] = [
  { id: 11, backlogId: 7, taskName: 'Design', orderIndex: 0, status: 'Done', isActive: true, rowVersion: 2 },
  { id: 12, backlogId: 7, taskName: 'Build', orderIndex: 1, status: 'Todo', isActive: true, rowVersion: 3 },
];

/** ACTIVE users -- the CHOICES. Note that the backlog's own assignee (3) is not here. */
const USERS: UserDto[] = [
  { id: 1, name: 'Nhan', isActive: true },
  { id: 2, name: 'An', isActive: true },
];

/** EVERY user, deactivated included -- the only place the departed assignee's NAME can be found. */
const NAMES: NamedRefDto[] = [
  { id: 1, name: 'Nhan' },
  { id: 2, name: 'An' },
  { id: DEPARTED, name: 'Dana' },
];

const CONTACTS: PcaContactDto[] = [{ id: 9, name: 'Yuki', isActive: true }];

const AUDIT: BacklogAuditDto[] = [
  // Newest first -- the order the API itself returns (`ORDER BY id DESC`).
  { id: 2, field: 'Note', oldValue: null, newValue: 'first note', changedByName: 'Nhan', changedAt: '2026-07-13T14:22:00Z' },
  { id: 1, field: 'Project', oldValue: 'ARMS', newValue: 'ARCS', changedByName: null, changedAt: '2026-07-12T09:05:00Z' },
];

const CREATED: BacklogDto = { ...BACKLOG, id: 42, rowVersion: 1 };

function httpError(status: number, error: unknown = null): HttpErrorResponse {
  return new HttpErrorResponse({ status, error });
}

describe('BacklogEditorComponent', () => {
  let fixture: ComponentFixture<BacklogEditorComponent>;
  let component: BacklogEditorComponent;
  let api: jasmine.SpyObj<WorklogService>;
  let toast: jasmine.SpyObj<ToastService>;
  let saved: number;
  /** Every write, in the order it was actually issued -- the four-phase order is a contract, not a detail. */
  let calls: string[];

  /** The spies and their happy-path answers. Split from `mount` so a test can bend one BEFORE the load runs. */
  function arrange(): void {
    calls = [];

    api = jasmine.createSpyObj<WorklogService>(
      'WorklogService',
      ['getBacklog', 'getTasks', 'getBacklogAudit', 'getUsersActive', 'getUserNames', 'getPcaContactsActive',
       'createBacklog', 'updateBacklog', 'addTask', 'updateTask', 'setTaskActive'],
    );

    api.getBacklog.and.returnValue(of(BACKLOG));
    api.getTasks.and.returnValue(of(TASKS));
    api.getBacklogAudit.and.returnValue(of([]));
    api.getUsersActive.and.returnValue(of(USERS));
    api.getUserNames.and.returnValue(of(NAMES));
    api.getPcaContactsActive.and.returnValue(of(CONTACTS));

    api.createBacklog.and.callFake(() => { calls.push('create'); return of(CREATED); });
    api.updateBacklog.and.callFake(() => { calls.push('update-backlog'); return of<SavedBody>({ rowVersion: 6 }); });
    api.setTaskActive.and.callFake((id: number) => { calls.push(`delete-${id}`); return of(void 0); });
    api.addTask.and.callFake((_b: number, name: string) => { calls.push(`insert-${name}`); return of(TASKS[0]); });
    api.updateTask.and.callFake((id: number) => { calls.push(`update-task-${id}`); return of<SavedBody>({ rowVersion: 9 }); });

    toast = jasmine.createSpyObj<ToastService>('ToastService', ['show']);

    TestBed.configureTestingModule({
      imports: [BacklogEditorComponent],
      providers: [
        { provide: WorklogService, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    });
  }

  /** `backlogId: null` is CREATE. Any number is EDIT. */
  async function mount(backlogId: number | null = 7): Promise<void> {
    fixture = TestBed.createComponent(BacklogEditorComponent);
    component = fixture.componentInstance;

    saved = 0;
    component.saved.subscribe(() => saved++);

    fixture.componentRef.setInput('backlogId', backlogId);
    fixture.detectChanges();          // -> ngOnInit -> load()
    await settle();
  }

  async function setUp(backlogId: number | null = 7): Promise<void> {
    arrange();
    await mount(backlogId);
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** A minimally-valid CREATE form: `validate` wants a code, a project and at least one task. */
  function fillCreate(): void {
    component.patch('code', 'ARCS-9');
    component.patch('project', 'ARCS');
    component.newTaskName.set('Design');
    component.addRow();
  }

  function html(): string {
    return (fixture.nativeElement as HTMLElement).innerHTML;
  }

  // ---- the gate: a refused save sends NOTHING -------------------------------------------------------

  it('REFUSES an out-of-range progress and sends NO REQUEST AT ALL', async () => {
    await setUp(null);
    fillCreate();

    // 🔴 The bug we are fixing, not porting. WPF's ParseProgress sets an ErrorMessage that BLOCKS NOTHING and
    // then returns null, so typing 150 and saving writes a NULL progress. `validate` refuses the save.
    component.patch('progressText', '150');

    await component.save();

    expect(component.error()).toContain('Progress');
    expect(api.createBacklog).not.toHaveBeenCalled();
    expect(api.addTask).not.toHaveBeenCalled();
    expect(api.updateBacklog).not.toHaveBeenCalled();
    expect(saved).toBe(0);
  });

  it('refuses a save with an un-named task -- the server would 400 it, but only AFTER the backlog PUT landed',
    async () => {
      await setUp();
      component.rename(0, '   ');

      await component.save();

      expect(component.error()).toContain('name');
      expect(api.updateBacklog).not.toHaveBeenCalled();
      expect(saved).toBe(0);
    });

  // ---- H3: the hidden fields ------------------------------------------------------------------------

  it('WRITES the six fields the edit form hides -- from the DTO, on a save that only touched the note',
    async () => {
      await setUp();

      component.patch('note', 'second note');
      await component.save();

      const body = api.updateBacklog.calls.mostRecent().args[1];

      // 🔴 Not one of these has an input on the edit form. Every one of them is written by the PUT, and an
      // omitted field is written as NULL -- not "left alone".
      expect(body.startDate).toBe('2026-07-01');
      expect(body.endDate).toBe('2026-07-31');
      expect(body.deadlineInternal).toBe('2026-07-20');
      expect(body.deadlineExternal).toBe('2026-07-25');
      expect(body.progressPercent).toBe(40);
      expect(body.pcaContactId).toBe(9);

      expect(body.note).toBe('second note');            // ...and the one field they DID touch
      expect(body.expectedVersion).toBe(5);             // checked, against the backlog's own version
    });

  it('renders NO input for the create-only fields on EDIT', async () => {
    await setUp();
    const el = fixture.nativeElement as HTMLElement;

    // They are edited INLINE on the Task List screen. The editor does not offer a second place to change
    // them -- but it still WRITES them, from the DTO. See the test above.
    expect(el.querySelector('#bed-start')).toBeNull();
    expect(el.querySelector('#bed-end')).toBeNull();
    expect(el.querySelector('#bed-pct')).toBeNull();
    expect(el.querySelector('#bed-pca')).toBeNull();
    expect(el.querySelector('#bed-progress')).toBeNull();
    expect(el.querySelector('#bed-contact')).toBeNull();
  });

  it('DOES render them on CREATE -- that is the one path that genuinely owns them', async () => {
    await setUp(null);
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('#bed-start')).not.toBeNull();
    expect(el.querySelector('#bed-end')).not.toBeNull();
    expect(el.querySelector('#bed-pct')).not.toBeNull();
    expect(el.querySelector('#bed-pca')).not.toBeNull();
    expect(el.querySelector('#bed-progress')).not.toBeNull();
    expect(el.querySelector('#bed-contact')).not.toBeNull();
  });

  // ---- H2: the deactivated assignee -----------------------------------------------------------------

  it('ROUND-TRIPS a deactivated assignee through a save that never touched her', async () => {
    await setUp();

    // Nobody opens the assignee dropdown. They fix a typo in the note and press Save.
    component.patch('note', 'typo fixed');
    await component.save();

    // 🔴 WPF bug #6: `Users.FirstOrDefault(u => u.Id == backlog.AssigneeUserId) ?? Unassigned` -- she is not
    // in the ACTIVE list, so the `??` fires and the save writes null. The assignee is silently cleared.
    expect(api.updateBacklog.calls.mostRecent().args[1].assigneeUserId).toBe(DEPARTED);
  });

  it('RENDERS the deactivated assignee, from the all-names list, and says she is inactive', async () => {
    await setUp();

    const departed = component.assigneeOptions().find(o => o.id === DEPARTED);
    expect(departed).toBeDefined();
    expect(departed?.label).toBe('Dana (inactive)');   // the name comes from getUserNames(), not getUsersActive()

    // She is a choice the <select> can actually bind to -- which is the whole point: an unbindable value
    // renders as a blank box and saves as null.
    expect(html()).toContain('Dana (inactive)');
  });

  // ---- H1: the reorder ------------------------------------------------------------------------------

  it('MOVES THE ARRAY ELEMENT for a reorder, and that is what produces the writes', async () => {
    await setUp();
    expect(component.visibleRows().map(r => r.name)).toEqual(['Design', 'Build']);

    component.moveDown(0);            // 🔴 the element MOVES. Mutating row.orderIndex would write NOTHING.

    expect(component.visibleRows().map(r => r.name)).toEqual(['Build', 'Design']);
    await component.save();

    // Reindexed from ARRAY POSITION: Build 1->0, Design 0->1. Both moved, so both are written.
    const bodies = api.updateTask.calls.all().map(c => ({ id: c.args[0], body: c.args[1] }));
    expect(bodies.length).toBe(2);

    const build = bodies.find(b => b.id === 12);
    const design = bodies.find(b => b.id === 11);
    expect(build?.body.orderIndex).toBe(0);
    expect(design?.body.orderIndex).toBe(1);

    // ...and each carries its OWN status, round-tripped. The editor never shows a status, and
    // `PUT /api/tasks/{id}` writes it unconditionally: build the body from the form and it lands as null.
    expect(build?.body.status).toBe('Todo');
    expect(design?.body.status).toBe('Done');
    expect(build?.body.expectedVersion).toBe(3);
    expect(design?.body.expectedVersion).toBe(2);
  });

  it('emits ZERO task writes for a save that touched no task', async () => {
    await setUp();

    component.patch('note', 'only the note');
    await component.save();

    expect(api.updateBacklog).toHaveBeenCalledTimes(1);   // the record itself always goes
    expect(api.updateTask).not.toHaveBeenCalled();
    expect(api.addTask).not.toHaveBeenCalled();
    expect(api.setTaskActive).not.toHaveBeenCalled();
  });

  it('does not let a hidden removed row swallow a move -- WPF moves within the FULL list and no-ops', async () => {
    await setUp();
    component.newTaskName.set('Ship');
    component.addRow();               // -> Design, Build, Ship

    component.remove(1);              // Build is soft-deleted and leaves the view -> Design, Ship
    expect(component.visibleRows().map(r => r.name)).toEqual(['Design', 'Ship']);

    component.moveDown(0);            // the move is computed on what the user can SEE
    expect(component.visibleRows().map(r => r.name)).toEqual(['Ship', 'Design']);
  });

  // ---- the four-phase write -------------------------------------------------------------------------

  it('writes backlog -> deletes -> inserts -> updates, in that order', async () => {
    await setUp();

    component.remove(0);              // Design (id 11) -> soft delete
    component.newTaskName.set('Ship');
    component.addRow();               // -> a new row after Build

    await component.save();

    // Build survives at position 0 (it was 1), so it is rewritten; Ship is inserted at 1.
    expect(calls).toEqual(['update-backlog', 'delete-11', 'insert-Ship', 'update-task-12']);
  });

  it('soft-deletes with NO version -- PUT /api/tasks/{id}/active is bump-only by design', async () => {
    await setUp();

    component.remove(0);
    await component.save();

    // Two args, and neither is a version. TaskActiveRequest has no version field at all; sending one would
    // make every delete fail.
    expect(api.setTaskActive).toHaveBeenCalledWith(11, false);
    expect(api.setTaskActive.calls.mostRecent().args.length).toBe(2);
  });

  // ---- H4: the conflict -----------------------------------------------------------------------------

  it('on 409 says so, RE-READS, and does NOT open the cell merge dialog', async () => {
    await setUp();
    api.updateBacklog.and.returnValue(throwError(() => httpError(409, { message: 'changed by someone else' })));

    component.patch('note', 'mine');
    await component.save();
    await settle();

    expect(component.error()).toContain('Someone else changed this backlog');
    expect(toast.show).toHaveBeenCalledWith('Someone else just changed this backlog.');

    // 🔴 RE-READ. Without it every later save 409s forever against a version we already know is stale.
    expect(api.getBacklog).toHaveBeenCalledTimes(2);

    // 🔴 NOT the merge dialog. It reconciles TWO NUMBERS in a timesheet cell -- a backlog edit has fifteen
    // fields and a task list, and nothing to merge.
    expect(html()).not.toContain('conflict');
    expect((fixture.nativeElement as HTMLElement).querySelector('app-conflict-dialog')).toBeNull();

    // 🔴 AND IT DOES NOT SILENTLY RETRY. Their change may itself have been a Continue or a Move; a blind
    // retry would apply this edit on top of it.
    expect(api.updateBacklog).toHaveBeenCalledTimes(1);
    expect(saved).toBe(0);            // the dialog stays open
  });

  it('re-reads after a 409 raised by a TASK write too -- the backlog PUT already landed', async () => {
    await setUp();
    api.updateTask.and.returnValue(throwError(() => httpError(409, {})));

    component.moveDown(0);
    await component.save();
    await settle();

    expect(api.getBacklog).toHaveBeenCalledTimes(2);
    expect(api.getTasks).toHaveBeenCalledTimes(2);
    expect(saved).toBe(0);
  });

  // ---- H5: create is not transactional ---------------------------------------------------------------

  it('KEEPS THE DIALOG OPEN and tells the truth when the backlog is created but its tasks are not',
    async () => {
      await setUp(null);
      fillCreate();
      api.addTask.and.returnValue(throwError(() => httpError(500)));

      await component.save();
      await settle();

      // 🔴 The backlog EXISTS. Closing here and reporting success is how a half-created record becomes a
      // mystery nobody can reproduce.
      expect(saved).toBe(0);
      expect(component.error()).toContain('created, but its tasks could not all be saved');

      // 🔴 And the dialog has STOPPED being a create dialog: it now edits the record that exists, so a second
      // Save UPDATEs it. Left in create mode it would post a TWIN.
      expect(component.isEdit()).toBeTrue();
      expect(api.getBacklog).toHaveBeenCalledWith(42);

      api.addTask.and.callFake(() => of(TASKS[0]));
      await component.save();

      expect(api.createBacklog).toHaveBeenCalledTimes(1);   // NOT twice
      expect(api.updateBacklog).toHaveBeenCalledTimes(1);
    });

  it('creates the backlog, then its tasks in array order, then closes', async () => {
    await setUp(null);
    component.patch('code', 'ARCS-9');
    component.patch('project', 'ARMS');
    component.newTaskName.set('Design');
    component.addRow();
    component.newTaskName.set('Build');
    component.addRow();

    await component.save();

    const body = api.createBacklog.calls.mostRecent().args[0];
    expect(body.backlogCode).toBe('ARCS-9');
    expect(body.project).toBe('ARMS');

    expect(api.addTask).toHaveBeenCalledWith(42, 'Design', 0);
    expect(api.addTask).toHaveBeenCalledWith(42, 'Build', 1);
    expect(calls).toEqual(['create', 'insert-Design', 'insert-Build']);
    expect(saved).toBe(1);
  });

  it('shows a create 400 VERBATIM -- "you are in no team" is the only sentence the user can act on',
    async () => {
      await setUp(null);
      fillCreate();

      const message = 'You are not a member of any team, so this backlog would be invisible to everyone. ' +
        'Ask an admin to add you to a team.';
      api.createBacklog.and.returnValue(throwError(() => httpError(400, { error: message })));

      await component.save();

      expect(component.error()).toBe(message);
      expect(saved).toBe(0);
      expect(component.isEdit()).toBeFalse();   // nothing was written -- it is still a create dialog
    });

  // ---- re-entrancy ----------------------------------------------------------------------------------

  it('refuses a second save while the first is in flight -- a corruption guard, not politeness', async () => {
    await setUp();
    const gate = new Subject<SavedBody>();
    api.updateBacklog.and.returnValue(gate);

    component.patch('note', 'mine');
    const first = component.save();     // suspends on the PUT
    await component.save();             // 🔴 must be refused: its read could land AFTER the first PUT commits

    expect(api.updateBacklog).toHaveBeenCalledTimes(1);

    gate.next({ rowVersion: 6 });
    gate.complete();
    await first;

    expect(saved).toBe(1);
  });

  // ---- the change history ---------------------------------------------------------------------------

  it('renders the history newest first, dashes a null value and marks an unknown author', async () => {
    arrange();
    api.getBacklogAudit.and.returnValue(of(AUDIT));
    await mount();

    const lines = component.auditLines();
    expect(lines.length).toBe(2);

    expect(lines[0].change).toBe('Note: — → first note');      // newest first; a null old value is an em dash
    expect(lines[1].change).toBe('Project: ARMS → ARCS');
    expect(lines[1].who).toContain('(?)');                     // a null author is named, not blank

    // The stamp is rendered in LOCAL time, so its VALUE is machine-dependent -- assert the SHAPE, not a
    // literal. (A test that hard-codes a time zone fails for the wrong reason.)
    expect(lines[0].who).toMatch(/^Nhan · \d{2}\/\d{2}\/\d{4} \d{2}:\d{2}$/);
  });

  it('hides the history section entirely when there is none', async () => {
    await setUp();                      // getBacklogAudit returns [] by default

    expect(component.auditLines().length).toBe(0);
    expect(html()).not.toContain('Change history');
  });

  it('shows no history on CREATE -- there is no record to have one', async () => {
    await setUp(null);

    expect(api.getBacklogAudit).not.toHaveBeenCalled();
    expect(html()).not.toContain('Change history');
  });

  // ---- the load -------------------------------------------------------------------------------------

  it('loads the record, its tasks, its history and BOTH halves of the assignee list, in parallel', async () => {
    await setUp();

    expect(api.getBacklog).toHaveBeenCalledWith(7);
    expect(api.getTasks).toHaveBeenCalledWith(7);
    expect(api.getBacklogAudit).toHaveBeenCalledWith(7);
    expect(api.getUsersActive).toHaveBeenCalled();
    expect(api.getUserNames).toHaveBeenCalled();

    expect(component.form().code).toBe('ARCS-1042');
    expect(component.form().year).toBe(2026);
    expect(component.form().month).toBe(7);
    expect(component.form().roughEstimateText).toBe('8');
    expect(component.visibleRows().map(r => r.name)).toEqual(['Design', 'Build']);
  });

  it('says so, and bars the save, when the backlog is gone', async () => {
    arrange();
    api.getBacklog.and.returnValue(throwError(() => httpError(404)));
    await mount();

    expect(component.error()).toBe('This backlog is no longer available.');
    expect(component.canSave()).toBeFalse();   // there is no DTO -- a save could only null the hidden fields
  });
});
