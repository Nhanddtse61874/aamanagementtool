import { HttpErrorResponse } from '@angular/common/http';
import {
  AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, OnInit, ViewChild, computed, inject, input,
  output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { forkJoin, firstValueFrom } from 'rxjs';

import {
  BacklogAuditDto, BacklogDto, NamedRefDto, PcaContactDto, TaskItemDto, UserDto, ValidationBody,
} from '../../api/models';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { EditForm, EditRow, periodMonth, toCreateRequest, toUpdateRequest, validate } from './backlog-form';
import { TaskWritePlan, planTaskWrites } from './task-edit';

/** One entry of a pick list. `null` is the "(unassigned)" choice, and it is a real, selectable value. */
export interface PickOption {
  id: number | null;
  label: string;
}

/** One rendered line of the change history. */
export interface AuditLine {
  change: string;
  who: string;
}

/** The type and project pick lists, from the SERVER's own enums -- BacklogType.All / BacklogProjects.All
 *  (Entities.cs:54-65). Not the literals the old stub carried, which had drifted (it listed 'DEFAULT' as a
 *  project a user could pick, and DEFAULT is the hidden recurring-tasks backlog, not a project). */
const TYPES = ['Continue', 'Implement', 'Investigate', 'IT', 'Estimate'] as const;
const PROJECTS = ['ARCS', 'PlusArcs', 'ARMS', 'Other'] as const;
const MONTHS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

/** An em dash for a null. The history says "Project: — → ARCS" for a field that had no value before. */
function dash(value: string | null | undefined): string {
  // '' is dashed too: an empty cell and a null one are the same thing to a reader, and rendering nothing at
  // all beside an arrow looks like the screen is broken.
  return value === null || value === undefined || value === '' ? '—' : value;
}

/** 'dd/MM/yyyy HH:mm', in the reader's LOCAL time (the server stamps UTC). */
function stamp(iso: string | undefined): string {
  if (iso === undefined) return '—';

  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';

  const pad = (n: number): string => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * 🔴 `TaskItemDto.id` is `id?: number` -- Swashbuckle emits no `required` for a C# record, so every generated
 * field is optional. A task whose id came back undefined would land in `EditRow.existingTaskId` as 0 via the
 * obvious `?? 0`, and `planTaskWrites` reads 0 as "brand new" and turns it into an INSERT. The original row
 * would stay exactly where it was, and the user would silently get a DUPLICATE task.
 *
 * So: refuse the LOAD rather than default the id. Same stance as `requireRowVersion`, for the same reason --
 * a write keyed on a defaulted identifier is worse than no write at all.
 */
function requireTaskId(task: TaskItemDto): number {
  if (typeof task.id !== 'number' || task.id <= 0) {
    throw new Error('The API returned a task with no id. Refusing to load an editor that could duplicate it.');
  }
  return task.id;
}

/** The same argument as `requireTaskId`, one step later: every task insert below is keyed on this id. */
function requireBacklogId(created: BacklogDto): number {
  if (typeof created.id !== 'number' || created.id <= 0) {
    throw new Error('The API created the backlog but returned no id. Its tasks cannot be attached.');
  }
  return created.id;
}

/** A blank form, defaulted to the CURRENT month -- the one a user creating a backlog today almost always wants. */
function blankForm(): EditForm {
  const now = new Date();
  return {
    code: '', project: PROJECTS[0], year: now.getFullYear(), month: now.getMonth() + 1,
    type: null, assigneeUserId: null, roughEstimateText: '', officialEstimateText: '', note: null,
    startDate: null, endDate: null, deadlineInternal: null, deadlineExternal: null,
    progressText: '', pcaContactId: null,
  };
}

/** 'yyyy-MM' -> year + month. Falls back to the current month, as WPF's ParsePeriodMonth does. */
function splitPeriod(yyyymm: string | null | undefined): { year: number; month: number } {
  const now = new Date();
  const m = /^(\d{4})-(\d{2})$/.exec(yyyymm ?? '');
  if (m === null) return { year: now.getFullYear(), month: now.getMonth() + 1 };

  const month = Number(m[2]);
  if (month < 1 || month > 12) return { year: now.getFullYear(), month: now.getMonth() + 1 };

  return { year: Number(m[1]), month };
}

/**
 * The backlog editor -- CREATE and EDIT in one modal, as WPF's RequestEditor is.
 *
 * Unlike `AddTaskDialogComponent` (which collects one string and emits it) this dialog cannot be
 * presentational: it owns a five-way parallel READ and a four-phase WRITE, and both are the substance of the
 * screen. It is still `OnPush` -- every piece of mutable state is a signal, so a write marks it dirty without
 * a `ChangeDetectorRef`, which is the idiom `LogWorkComponent` already proves under async/await.
 *
 * WHICH FIELDS SHOW, and why it is not symmetric (WPF's rule, verbatim):
 *
 *   create AND edit : code · project · assignee · month/year · type · rough est · official est · note · tasks
 *   CREATE only     : start date · end date · internal deadline · external deadline · progress · PCA contact
 *   EDIT only       : change history
 *
 * The six create-only fields are edited INLINE on the Task List screen, so the editor does not offer a second
 * place to change them. 🔴 They are still WRITTEN on every edit -- `PUT /api/backlogs/{id}` replaces the whole
 * record and an omitted field is written as NULL, not left alone. `toUpdateRequest` round-trips them FROM THE
 * LOADED DTO. This component's whole job on that front is to call it and not undermine it: never hand it a
 * form value for a hidden field. `form.startDate` compiles -- `EditForm` carries it because CREATE genuinely
 * owns it -- and passing it would null the record.
 */
@Component({
  selector: 'app-backlog-editor',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './backlog-editor.component.html',
  styleUrl: './backlog-editor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BacklogEditorComponent implements OnInit, AfterViewInit {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);

  /** The backlog to edit. `null` (the default) means CREATE. */
  readonly backlogId = input<number | null>(null);

  /** A record was written. The parent re-reads its list and closes this dialog. */
  readonly saved = output<void>();
  readonly cancel = output<void>();

  /**
   * 🔴 The id this dialog is ACTUALLY working on -- initialised from `backlogId`, but NOT the same thing.
   *
   * A create whose backlog lands and whose tasks then fail has produced a REAL record, and the dialog must
   * stop being a create dialog at that instant. Otherwise the user's next Save posts a SECOND backlog. See
   * `runCreate`.
   */
  private readonly currentId = signal<number | null>(null);
  readonly isEdit = computed(() => this.currentId() !== null);

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = signal<EditForm>(blankForm());
  readonly rows = signal<EditRow[]>([]);
  readonly newTaskName = signal('');

  /** The server's truth, as last read. `dto` feeds `toUpdateRequest`; `loaded` feeds `planTaskWrites`. */
  private readonly dto = signal<BacklogDto | null>(null);
  private readonly loaded = signal<TaskItemDto[]>([]);

  private readonly users = signal<UserDto[]>([]);
  private readonly userNames = signal<NamedRefDto[]>([]);
  private readonly pcaContacts = signal<PcaContactDto[]>([]);
  private readonly audit = signal<BacklogAuditDto[]>([]);

  readonly types = TYPES;
  readonly projects = PROJECTS;
  readonly months = MONTHS;
  /** current-2 .. current+3, exactly the span WPF offers. */
  readonly years = Array.from({ length: 6 }, (_, i) => new Date().getFullYear() - 2 + i);

  /** The grid shows the SURVIVORS, in array order. Array order IS the order that ships -- see `move`. */
  readonly visibleRows = computed(() => this.rows().filter(r => !r.removed));

  /**
   * 🔴 THE ASSIGNEE LIST NEEDS TWO SOURCES, AND USING ONE IS A SILENT DATA-LOSS BUG.
   *
   * The CHOICES are the ACTIVE users -- you may only assign work to someone who is still here. But the
   * CURRENT VALUE may be someone who has since LEFT, and `GET /api/users/active` does not return her. Bind a
   * `<select>` to an id that is not among its options and the browser shows a blank box; save without
   * touching it and the id is written as null. Open a backlog whose assignee has left, press Save, and THE
   * ASSIGNEE IS SILENTLY CLEARED.
   *
   * That is WPF bug #6 -- `vm.SelectedAssignee = vm.Users.FirstOrDefault(u => u.Id == backlog.AssigneeUserId)
   * ?? Unassigned` (RequestEditorViewModel.cs:194): the `?? Unassigned` IS the data loss. We are fixing it,
   * not porting it. `GET /api/users/names` exists precisely to name a departed user, so an untouched save
   * ROUND-TRIPS her.
   *
   * (`/api/users/all` could also name her, but it is ADMIN-ONLY: it would 403 an ordinary user and take the
   * whole forkJoin down with it.)
   */
  readonly assigneeOptions = computed<PickOption[]>(() => {
    const active: PickOption[] = this.users()
      .filter(u => typeof u.id === 'number' && u.id > 0)
      .map(u => ({ id: u.id ?? null, label: u.name ?? '' }));

    const unassigned: PickOption = { id: null, label: '(unassigned)' };
    const current = this.form().assigneeUserId;
    if (current === null || active.some(o => o.id === current)) return [unassigned, ...active];

    // She is not in the active list -- she has been deactivated. Render her anyway, and say so, so the user
    // understands why she is there and cannot be picked for a NEW assignment. The id is what matters: it is
    // still bound, so it still round-trips.
    const name = this.userNames().find(n => n.id === current)?.name ?? null;
    const label = name === null ? `User ${current} (inactive)` : `${name} (inactive)`;
    return [unassigned, { id: current, label }, ...active];
  });

  /**
   * The PCA contact list -- CREATE ONLY, and so it needs only the ACTIVE half.
   *
   * There is no `getPcaContactNames()` counterpart here on purpose. The deactivated-value problem the
   * assignee list has cannot arise for this field: a CREATE has no saved contact to preserve, and an EDIT
   * does not render this dropdown at all -- its `pcaContactId` round-trips from the DTO via
   * `toUpdateRequest`, untouched by any pick list. See the report on this task.
   */
  readonly pcaOptions = computed<PickOption[]>(() =>
    this.pcaContacts()
      .filter(c => typeof c.id === 'number' && c.id > 0)
      .map(c => ({ id: c.id ?? null, label: c.name ?? '' })));

  /**
   * The API already returns the history NEWEST FIRST (`BacklogRepository.cs:342` -- `ORDER BY id DESC`), so
   * this does not re-sort. A second opinion about an order the server is authoritative on is not caution, it
   * is a place for the two to disagree.
   */
  readonly auditLines = computed<AuditLine[]>(() =>
    this.audit().map(e => ({
      change: `${dash(e.field)}: ${dash(e.oldValue)} → ${dash(e.newValue)}`,
      who: `${e.changedByName ?? '(?)'} · ${stamp(e.changedAt)}`,
    })));

  /**
   * `validate` (T4) is the SHARED gate, and it deliberately knows nothing about this grid's inputs: it checks
   * that at least one task SURVIVES, not that each survivor is NAMED. This screen adds the name inputs, so
   * this screen owns that rule.
   *
   * 🔴 It is not cosmetic. An empty name is answered `400 "TaskName is required."` -- but on the EDIT path
   * only AFTER the backlog PUT has already landed, which leaves a HALF-WRITTEN save and forces a reload that
   * throws away everything else the user typed. Refuse it before the first request instead.
   * (`AddTaskDialogComponent` refuses a blank name one screen over, for the same reason.)
   */
  readonly blankTask = computed(() => this.visibleRows().some(r => r.name.trim() === ''));

  /** Barred while a load or a save is in flight, and barred if an EDIT never loaded (there is no DTO to
   *  round-trip the hidden fields from, so a save could only null them). */
  readonly canSave = computed(() =>
    !this.saving() && !this.loading() && !(this.isEdit() && this.dto() === null));

  @ViewChild('shell') private shell!: ElementRef<HTMLElement>;

  ngOnInit(): void {
    this.currentId.set(this.backlogId());
    // `void`: `load` handles every failure it can have and NEVER rejects -- see its catch. An `async`
    // ngOnInit would hand Angular a promise it ignores, and a rejection would vanish into the console.
    void this.load();
  }

  /**
   * Move focus INTO the dialog on open -- the correct behaviour for a modal, and the thing that makes the
   * `(keydown.escape)` binding work from the first keystroke. (Keydown bubbles, so it fires from any field
   * inside; without this it would do nothing at all until the user clicked something.) The shell is focused
   * rather than the first field, so opening an EDIT does not drop a caret into a code the user did not come
   * here to retype.
   */
  ngAfterViewInit(): void {
    this.shell.nativeElement.focus();
  }

  // ---- the read path -------------------------------------------------------------------------------

  /**
   * Everything the open form needs, in ONE parallel round trip.
   *
   * The two paths read DIFFERENT things, and deliberately not a superset:
   *
   *   CREATE reads the two pick lists it renders. There is no record, no tasks and no history to read.
   *   EDIT reads the record, its tasks, its history, and BOTH halves of the assignee list (see
   *   `assigneeOptions`). It does NOT read the PCA lists -- the PCA contact field is CREATE-ONLY, so there is
   *   no dropdown for them to fill, and the saved `pcaContactId` round-trips from the DTO.
   */
  private async load(): Promise<void> {
    const id = this.currentId();
    this.loading.set(true);

    try {
      if (id === null) {
        const [users, pcaContacts] = await firstValueFrom(forkJoin([
          this.api.getUsersActive(),
          this.api.getPcaContactsActive(),
        ]));

        this.users.set(users);
        this.pcaContacts.set(pcaContacts);
        this.form.set(blankForm());
        this.rows.set([]);
        return;
      }

      const [dto, tasks, audit, users, userNames] = await firstValueFrom(forkJoin([
        this.api.getBacklog(id),
        this.api.getTasks(id),
        this.api.getBacklogAudit(id),
        this.api.getUsersActive(),
        this.api.getUserNames(),
      ]));

      this.dto.set(dto);
      this.loaded.set(tasks);
      this.audit.set(audit);
      this.users.set(users);
      this.userNames.set(userNames);
      this.form.set(this.hydrate(dto));
      this.rows.set(this.toRows(tasks));
    } catch (err: unknown) {
      this.onLoadError(err);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * The DTO -> the form.
   *
   * The six create-only fields are copied across even though nothing renders them: `toUpdateRequest` reads
   * them from the DTO, not from here, so these values are inert on the edit path -- but leaving them blank
   * would put a form in memory that says "this backlog has no start date" when it has one, and the next
   * person to reach for `form.startDate` would find a lie rather than an absence.
   */
  private hydrate(dto: BacklogDto): EditForm {
    const { year, month } = splitPeriod(dto.periodMonth);

    return {
      code: dto.backlogCode ?? '',
      project: dto.project ?? '',
      year,
      month,
      type: dto.type ?? null,
      assigneeUserId: dto.assigneeUserId ?? null,
      roughEstimateText: dto.roughEstimateHours?.toString() ?? '',
      officialEstimateText: dto.officialEstimateHours?.toString() ?? '',
      note: dto.note ?? null,

      startDate: dto.startDate ?? null,
      endDate: dto.endDate ?? null,
      deadlineInternal: dto.deadlineInternal ?? null,
      deadlineExternal: dto.deadlineExternal ?? null,
      progressText: dto.progressPercent?.toString() ?? '',
      pcaContactId: dto.pcaContactId ?? null,
    };
  }

  /**
   * The tasks -> the grid rows.
   *
   * `GET /api/tasks` is `WHERE is_active = 1 ORDER BY order_index`, so the ARRAY ORDER already IS the display
   * order. It is not re-sorted here for the same reason the audit is not: the server is authoritative.
   */
  private toRows(tasks: TaskItemDto[]): EditRow[] {
    return tasks.map(t => ({
      existingTaskId: requireTaskId(t),
      name: t.taskName ?? '',
      // 🔴 The SERVER's last-known value, and nothing more. `planTaskWrites` reads it only to decide WHETHER a
      // write is needed; the order it SHIPS is this array's POSITION. See `move`.
      orderIndex: t.orderIndex ?? 0,
      removed: false,
    }));
  }

  /** Four GETs. Between them they declare `200` and `404` -- and nothing else. A 400 or 409 branch here would
   *  be dead code, exactly as `onTrashError` has none. */
  private onLoadError(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 404) {
      this.error.set('This backlog is no longer available.');
      return;
    }

    this.error.set('Could not load this backlog. Close it and try again.');
  }

  // ---- the form ------------------------------------------------------------------------------------

  patch<K extends keyof EditForm>(key: K, value: EditForm[K]): void {
    this.form.update(f => ({ ...f, [key]: value }));
    this.error.set(null);   // they are fixing something; stop shouting the last problem at them
  }

  // ---- the task grid -------------------------------------------------------------------------------

  addRow(): void {
    const name = this.newTaskName().trim();
    if (name === '') return;   // a whitespace-only name is not a task, and `POST /api/tasks` 400s it

    // `orderIndex: 0` is not a position -- it is "the server has never seen this row". `planTaskWrites` never
    // reads it for an insert; it reindexes from the array. APPENDING is what puts the task last.
    this.rows.update(all => [...all, { existingTaskId: 0, name, orderIndex: 0, removed: false }]);
    this.newTaskName.set('');
    this.error.set(null);
  }

  rename(index: number, name: string): void {
    const target = this.visibleRows()[index];
    if (target === undefined) return;

    this.rows.update(all => all.map(r => (r === target ? { ...r, name } : r)));
  }

  /**
   * A saved row is SOFT-deleted on save; a row that was never saved is simply dropped -- there is nothing on
   * the server to delete, and `planTaskWrites` would refuse to soft-delete id 0 anyway.
   */
  remove(index: number): void {
    const target = this.visibleRows()[index];
    if (target === undefined) return;

    this.rows.update(all => target.existingTaskId === 0
      ? all.filter(r => r !== target)
      : all.map(r => (r === target ? { ...r, removed: true } : r)));
    this.error.set(null);
  }

  moveUp(index: number): void { this.move(index, index - 1); }
  moveDown(index: number): void { this.move(index, index + 1); }

  /**
   * 🔴 A REORDER IS EXPRESSED BY MOVING THE ARRAY ELEMENT. NOTHING ELSE EXPRESSES IT.
   *
   * `EditRow.orderIndex` is pinned "display order", and that comment is misleading: `planTaskWrites`
   * REINDEXES every survivor from its POSITION in the array (`rows.filter(...).forEach((row, orderIndex)`),
   * and reads the FIELD only to decide whether the row needs a write at all. Mutate `row.orderIndex` and
   * leave the array alone and the reorder is SILENTLY IGNORED -- no error, no write, and the user's change
   * vanishes on the next reload. (WPF does exactly this too: `Tasks.Move(i, i - 1)` moves the element, and
   * `ActiveTasks` then reindexes 0..n from position.)
   *
   * The move is computed on the VISIBLE rows and the removed ones are parked at the tail. That is not
   * tidiness: WPF moves within the FULL list, so moving a row past a hidden soft-deleted one there is a
   * silent no-op -- the user clicks Up and nothing happens. Their positions carry no meaning (`deletes` is
   * derived from the flag, never the index), so the tail is exactly where they belong.
   */
  private move(from: number, to: number): void {
    const visible = this.visibleRows();
    if (to < 0 || to >= visible.length || from < 0 || from >= visible.length) return;

    const next = [...visible];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);

    this.rows.set([...next, ...this.rows().filter(r => r.removed)]);
  }

  // ---- the write path ------------------------------------------------------------------------------

  /**
   * 🔴 The re-entrancy guard is a CORRUPTION guard, not politeness.
   *
   * A second click starts a second chain, and on a slow link -- which is exactly when a user clicks again
   * because "nothing happened" -- chain 2's read can land AFTER chain 1's PUT committed. It would then read
   * the ALREADY-BUMPED rowVersion and apply the mutation a second time. (`onMoveMonth` guards the identical
   * shape for the identical reason.)
   *
   * 🔴 And `validate` runs BEFORE the guard is even taken: a refused save sends NO REQUEST AT ALL. That is
   * the entire point of it -- WPF sets an error message that blocks nothing, so typing "150" into Progress
   * and saving writes a null. We do not.
   */
  async save(): Promise<void> {
    if (this.saving()) return;

    const problem = validate(this.form(), this.rows());
    if (problem !== null || this.blankTask()) {
      this.error.set(problem ?? 'Every task needs a name.');
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    try {
      const id = this.currentId();
      if (id === null) await this.runCreate();
      else await this.runEdit(id);
    } catch {
      // 🔴 `save` is bound to a (click). ANYTHING that escapes it is an UNHANDLED PROMISE REJECTION: it lands
      // in the console and NOWHERE THE USER CAN SEE. They would press Save, watch the dialog sit there, and
      // press it again. `runCreate` and `runEdit` each handle every failure their own routes DECLARE; this is
      // the net under everything else (a `requireRowVersion` throw, a dropped connection).
      this.error.set('Could not save this backlog. Please try again.');
    } finally {
      this.saving.set(false);
    }
  }

  /**
   * 🔴 CREATE IS NOT TRANSACTIONAL, AND THIS MUST NOT HIDE THAT.
   *
   * The tasks need the id that `POST /api/backlogs` returns, so they CANNOT ride in it. A failure in between
   * leaves a REAL, ZERO-TASK backlog -- a state `validate` itself forbids anyone to create on purpose.
   *
   * So on a task failure the dialog does NOT close and does NOT report success. It becomes an EDIT dialog on
   * the record that now exists, re-reads, and says plainly what happened. Two things follow from that, and
   * both matter:
   *
   *   - A second Save now UPDATEs the record. It cannot post a TWIN, which is what would happen if the dialog
   *     stayed in create mode.
   *   - The grid is rebuilt FROM THE SERVER, so it shows exactly the tasks that DID land. Keeping the user's
   *     typed rows instead would look kinder and would be wrong: after a partial insert (2 of 3 landed) every
   *     kept row still carries `existingTaskId: 0`, so the next save would insert all three -- DUPLICATING
   *     the two that succeeded.
   */
  private async runCreate(): Promise<void> {
    let id: number;
    try {
      const created = await firstValueFrom(this.api.createBacklog(toCreateRequest(this.form())));
      id = requireBacklogId(created);
    } catch (err: unknown) {
      this.onCreateError(err);   // nothing was written: no re-read, no mode change, their form is untouched
      return;
    }

    try {
      await this.writeTasks(planTaskWrites([], this.rows(), id), id);
    } catch {
      // The status is deliberately not inspected. Every way this can fail has the same remedy and the same
      // honest sentence -- and the re-read below shows the truth far better than a status name could.
      this.currentId.set(id);
      await this.load();
      this.error.set(
        'The backlog was created, but its tasks could not all be saved. ' +
        'Add the missing ones below and save again.');
      this.toast.show('The backlog was created, but its tasks could not all be saved.');
      return;
    }

    this.toast.show('Backlog created.');
    this.saved.emit();
  }

  /** The four-phase write, in the only order that works. See `writeTasks` for phases 2-4. */
  private async runEdit(id: number): Promise<void> {
    const dto = this.dto();
    // Unreachable from the UI -- `canSave` bars a save on an edit that never loaded -- but it is also the
    // narrowing TypeScript needs, and a save built on a null DTO could only null the six hidden fields.
    if (dto === null) throw new Error('This backlog never loaded. Refusing to save over it.');

    try {
      // 1. The record itself -- CHECKED, against its OWN rowVersion. This is the call that can 409.
      //    🔴 `toUpdateRequest(dto, form)` -- the DTO first. It is what round-trips the six hidden fields.
      //    Building this body by hand, or "helpfully" passing `form.startDate`, nulls them.
      await firstValueFrom(this.api.updateBacklog(id, toUpdateRequest(dto, this.form())));

      await this.writeTasks(planTaskWrites(this.loaded(), this.rows(), id), id);
    } catch (err: unknown) {
      await this.onSaveError(err);
      return;
    }

    this.toast.show('Backlog saved.');
    this.saved.emit();
  }

  /**
   * Phases 2-4, in order. They touch DISJOINT rows, so no phase can invalidate another's `expectedVersion` --
   * there is no ordering hazard between them, only between them and the backlog PUT above.
   */
  private async writeTasks(plan: TaskWritePlan, backlogId: number): Promise<void> {
    // 2. DELETES -- 🔴 BUMP-ONLY. `PUT /api/tasks/{id}/active` takes `{ isActive }` and `TaskActiveRequest`
    //    has no version field at all. Sending an expectedVersion would make every delete fail.
    for (const taskId of plan.deletes) {
      await firstValueFrom(this.api.setTaskActive(taskId, false));
    }

    // 3. INSERTS -- no status; the server defaults it to 'Todo'.
    for (const insert of plan.inserts) {
      const { taskName, orderIndex } = insert;
      // `planTaskWrites` sets both, but the GENERATED `TaskCreateRequest` types them optional and `addTask`
      // takes them positionally. Narrow rather than `?? ''`: a defaulted empty name would be 400'd anyway,
      // and a defaulted orderIndex 0 would TIE with a real row -- the exact scramble `planTaskWrites`
      // reindexes 0..n to prevent.
      if (typeof taskName !== 'string' || typeof orderIndex !== 'number') {
        throw new Error('A planned task insert is missing its name or its order. Refusing to send it.');
      }
      await firstValueFrom(this.api.addTask(backlogId, taskName, orderIndex));
    }

    // 4. UPDATES -- CHECKED, so these can 409 too. `status` is round-tripped by `planTaskWrites`; the editor
    //    never shows it, and `PUT /api/tasks/{id}` writes it unconditionally.
    for (const update of plan.updates) {
      await firstValueFrom(this.api.updateTask(update.id, update.body));
    }
  }

  /**
   * `POST /api/backlogs` declares exactly TWO outcomes: `200 BacklogDto` and `400 ValidationBody`
   * (BacklogEndpoints.cs:123-124). No 404 -- there is no id to miss. No 409 -- there is no row yet to hold a
   * version. Branches for either would be DEAD CODE, so there are none.
   *
   * 🔴 The 400 is shown VERBATIM, and that is the whole reason this handler exists. One of the two the route
   * can return is "You are not a member of any team, so this backlog would be invisible to everyone. Ask an
   * admin to add you to a team." -- the only sentence in the exchange the user can actually ACT on.
   * Collapsing it into "could not create" would throw it away.
   */
  private onCreateError(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 400) {
      this.error.set((err.error as ValidationBody | null)?.error ?? 'That backlog was rejected.');
      return;
    }

    this.error.set('Could not create this backlog. Please try again.');
  }

  /**
   * The EDIT write. Four routes, and between them 400, 404 and 409 are all reachable.
   *
   * 🔴 A 409 gets a MESSAGE and a RE-READ. It does NOT get the cell conflict dialog. That dialog reconciles
   * TWO NUMBERS in a timesheet cell -- "keep theirs, or overwrite with mine". A backlog edit has fifteen
   * fields and a task list; there is no single number to merge and nothing to offer. Say so, re-read, stop.
   * (`onMoveError` refuses the same dialog for the same reason.)
   *
   * 🔴 AND IT DOES NOT SILENTLY RETRY. The other person's change may ITSELF have been a Continue or a Move,
   * and a blind retry would apply this edit ON TOP of it.
   *
   * It re-reads on EVERY status, not just 409, and that is deliberate: phase 1 can succeed and phase 3 fail,
   * which leaves the record written and its tasks not. The screen must never keep showing a state the server
   * does not have -- even though the re-read costs the user their unsaved edits, which is the honest price of
   * not knowing which half landed.
   */
  private async onSaveError(err: unknown): Promise<void> {
    if (err instanceof HttpErrorResponse && err.status === 409) {
      this.toast.show('Someone else just changed this backlog.');
    }

    const message = this.saveErrorMessage(err);

    await this.load();          // `load` never sets `error` except on ITS OWN failure...
    this.error.set(message);    // ...so the save's message is set after it, and wins.
  }

  private saveErrorMessage(err: unknown): string {
    if (!(err instanceof HttpErrorResponse)) return 'Could not reach the server. Reloaded.';

    switch (err.status) {
      case 400:
        return (err.error as ValidationBody | null)?.error ?? 'That change was rejected.';
      case 404:
        return 'This backlog, or one of its tasks, is no longer available.';
      case 409:
        return 'Someone else changed this backlog while you were editing it. ' +
          'Reloaded — reapply your changes if you still want them.';
      default:
        return 'Could not save this backlog. Please try again.';
    }
  }

  // ---- closing -------------------------------------------------------------------------------------

  /**
   * 🔴 Never while a save is in flight: closing would leave writes running against a dialog that is gone, and
   * the user would never learn whether they landed.
   */
  close(): void {
    if (this.saving()) return;
    this.cancel.emit();
  }
}
