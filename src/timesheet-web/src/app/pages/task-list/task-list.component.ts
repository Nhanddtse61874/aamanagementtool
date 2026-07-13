import {
  ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, inject, signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { firstValueFrom } from 'rxjs';

import { NamedRefDto, TagDto, TaskItemDto, TaskListRowDto, TaskListScreenDto, UserDto } from '../../api/models';
import { TagPickerComponent } from '../../components/tag-picker/tag-picker.component';
import { TeamFilterComponent } from '../../components/team-filter/team-filter.component';
import { ToastService } from '../../services/toast.service';
import { WorklogService, requireRowVersion } from '../../services/worklog.service';
import {
  ALL_MONTHS, Band, Chip, STATUSES, TYPES,
  buildChips, groupRows, hoursText, isDone, messageOf, nextPeriod, parseProgress, progressText,
  tagIdsOf, toTaskExtended, toUpdateRequest,
} from './task-list.model';

/** Which entity's tag picker is open. Only ever one at a time. */
interface OpenPicker {
  readonly kind: 'backlog' | 'task';
  readonly id: number;
}

/**
 * A deadline edit waiting on its reason note.
 *
 * `el` is the date input itself, kept so CANCEL can put it back (H3 — the desktop's
 * `picker.SelectedDate = current`). A native `<input type="date">` holds its own value: not re-binding it
 * would leave the cancelled date sitting on screen as though it had been saved.
 */
interface PendingDeadline {
  readonly backlogId: number;
  readonly which: 'internal' | 'external';
  readonly iso: string | null;
  readonly previous: string | null;
  readonly el: HTMLInputElement;
}

/**
 * The Task List screen.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 * 🔴 ONE READ FOR THE WHOLE SCREEN, AND THAT IS NOT AN OPTIMISATION.
 *
 * `GET /api/tasklist` -> `{ rows, gantt }`. The grid's schedule chip and the Gantt bar's colour are THE
 * SAME `ScheduleState`, computed from ONE server-side snapshot. Fetch them separately and a write landing
 * between the two requests puts the chart and the grid into visible disagreement, with nothing to notice it.
 *
 * 🔴 AND THE STATE IS NOT RECOMPUTABLE HERE. `ScheduleStateService` needs logged-hours-PER-BACKLOG, which no
 * other route exposes, plus the holiday set and a working-day calculator. A client-side reimplementation
 * would not merely be duplicated work — it would silently DISAGREE with the server the first time a rule
 * changed. Render what arrives; never derive it.
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 *
 * 🔴 EVERY MUTATION GOES THROUGH `mutate()`. It holds the re-entrancy guard and the catch. See its doc.
 */
@Component({
  selector: 'app-task-list',
  standalone: true,
  imports: [CommonModule, TeamFilterComponent, TagPickerComponent],
  templateUrl: './task-list.component.html',
  styleUrl: './task-list.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskListComponent {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);

  readonly ALL_MONTHS = ALL_MONTHS;
  readonly TYPES = TYPES;
  readonly STATUSES = STATUSES;
  readonly MONTHS = Array.from({ length: 13 }, (_, i) => i);          // 0 = "All months"
  readonly YEARS = Array.from({ length: 6 }, (_, i) => new Date().getFullYear() - 2 + i);

  // ---- selection -------------------------------------------------------------------------------------
  readonly year = signal(new Date().getFullYear());
  readonly month = signal(new Date().getMonth() + 1);

  /**
   * 🔴 THE TEAM FILTER'S THREE-VALUED STATE, AND IT REALLY IS THREE-VALUED.
   *
   *   `undefined`  the filter has not emitted — it is still loading, OR ITS READ FAILED. Send `undefined`:
   *                the server reads an absent `teamIds` key as "every team the caller belongs to", which is
   *                the correct degradation (a broken team list narrows nobody's view).
   *   `[]`         the user UNCHECKED EVERYTHING. 🔴 MAKE NO CALL. `teamIds: []` serialises to NOTHING —
   *                `RequestBuilder` appends one entry per element, so an empty array leaves the key ABSENT
   *                and goes out BYTE-IDENTICAL to `undefined`, i.e. ALL MY TEAMS. The exact inverse. There
   *                is no sentinel that helps: a fake id is intersected away server-side into the same empty
   *                set that already means "all". Render the empty state locally. See `load()`.
   *   `[1, 2]`     a real selection.
   *
   * The filter never emits `[]` from a FAILED read — it emits nothing at all and stays unloaded — so an
   * empty array here can only ever mean the user's own choice. That is what makes this branch safe.
   */
  readonly teamIds = signal<number[] | undefined>(undefined);

  // ---- server state ----------------------------------------------------------------------------------
  private readonly screen = signal<TaskListScreenDto | null>(null);
  readonly tags = signal<readonly TagDto[]>([]);
  private readonly users = signal<readonly UserDto[]>([]);
  private readonly userNames = signal<readonly NamedRefDto[]>([]);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  /** 🔴 THE RE-ENTRANCY GUARD. See `mutate()`. */
  readonly saving = signal(false);

  // ---- view state ------------------------------------------------------------------------------------
  readonly view = signal<'grid' | 'gantt'>('grid');
  readonly chartCollapsed = signal(false);
  readonly expanded = signal<ReadonlySet<number>>(new Set());
  readonly editingProgress = signal<number | null>(null);
  readonly progressDraft = signal('');
  readonly openPicker = signal<OpenPicker | null>(null);
  readonly pickerSelected = signal<readonly number[]>([]);
  readonly pendingDeadline = signal<PendingDeadline | null>(null);
  readonly deadlineNote = signal('');

  // ---- derived ---------------------------------------------------------------------------------------
  readonly rows = computed<readonly TaskListRowDto[]>(() => this.screen()?.rows ?? []);
  readonly bands = computed<Band[]>(() => groupRows(this.rows()));
  readonly gantt = computed(() => this.screen()?.gantt ?? null);

  /** 🔴 True ONLY when the user unchecked every team — never when the filter merely failed. */
  readonly noTeams = computed(() => this.teamIds()?.length === 0);

  /** "Continue" and "Export" both need a concrete month; neither means anything across all of them. */
  readonly concreteMonth = computed(() => this.month() !== ALL_MONTHS);

  /** The assignee dropdown's options: the ACTIVE users. You do not assign new work to a departed person. */
  readonly assignees = computed(() => this.users());

  constructor() {
    void this.loadLookups();
    void this.load();
  }

  // =====================================================================================================
  // READS
  // =====================================================================================================

  /**
   * The catalogue reads, once. `getTagList()` feeds every `<app-tag-picker [tags]>` on the screen — the
   * picker takes it as an INPUT precisely so that N rows do not fire N `/api/tags` calls.
   *
   * `getUserNames()` is the RENDER half of the pair: a task assigned to a since-deactivated user must still
   * show her name, and `getUsersActive()` no longer contains her.
   *
   * Both are open reads. A failure here degrades the pickers, not the screen, so it does not touch
   * `error()` — the grid is still perfectly usable without a tag catalogue.
   */
  private async loadLookups(): Promise<void> {
    try {
      const [tags, users, names] = await Promise.all([
        firstValueFrom(this.api.getTagList()),
        firstValueFrom(this.api.getUsersActive()),
        firstValueFrom(this.api.getUserNames()),
      ]);
      this.tags.set(tags);
      this.users.set(users);
      this.userNames.set(names);
    } catch {
      this.toast.show('Tags and people could not be loaded. Editing them is unavailable.');
    }
  }

  /**
   * The screen, in one round-trip.
   *
   * 🔴 THE EMPTY-SELECTION BRANCH IS THE POINT OF THIS METHOD. It returns BEFORE the call. See `teamIds`.
   *
   * Never re-throws: `mutate()` awaits it in a `finally`, and an unhandled rejection there would be an
   * unhandled rejection in a template-bound handler — console-only, nowhere the user can see.
   */
  async load(): Promise<void> {
    const ids = this.teamIds();

    // 🔴 NO API CALL. `[]` on the wire means ALL MY TEAMS.
    if (ids !== undefined && ids.length === 0) {
      this.screen.set(null);
      this.error.set(null);
      this.loading.set(false);
      return;
    }

    this.loading.set(true);
    try {
      this.screen.set(await firstValueFrom(this.api.getTaskListScreen(this.year(), this.month(), ids)));
      this.error.set(null);
    } catch (err: unknown) {
      this.screen.set(null);
      this.error.set(messageOf(err, 'The task list could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  // =====================================================================================================
  // 🔴 EVERY MUTATION GOES THROUGH HERE.
  //
  // Two guards, and this project has paid for both:
  //
  //   1. THE CATCH. Every handler below is bound to a template output. An `async` handler that throws
  //      produces an UNHANDLED PROMISE REJECTION — visible in the console and NOWHERE THE USER CAN SEE. The
  //      write silently did not happen. `mutate` catches, toasts, and NEVER RE-THROWS.
  //
  //   2. THE RE-ENTRANCY GUARD. Every chain below is GET-then-WRITE. Without a guard, a second click's GET
  //      lands AFTER the first chain's PUT, reads the version that PUT already bumped, and applies the
  //      mutation A SECOND TIME — no 409, because the version it holds is genuinely current. The write is
  //      simply applied twice.
  //
  // The reload in `finally` runs on BOTH paths on purpose: on success it re-derives the chips, the hours and
  // the Gantt from the server (never client-side — see the class doc); on failure it resyncs a screen whose
  // 409 means someone else's write is already there.
  // =====================================================================================================
  private async mutate(run: () => Promise<void>, fallback: string): Promise<void> {
    if (this.saving()) return;
    this.saving.set(true);
    try {
      await run();
    } catch (err: unknown) {
      this.toast.show(messageOf(err, fallback));
    } finally {
      this.saving.set(false);
      await this.load();               // `load()` swallows its own errors — it cannot re-throw into here.
    }
  }

  // =====================================================================================================
  // TOOLBAR
  // =====================================================================================================

  onTeams(ids: number[]): void {
    this.teamIds.set(ids);
    void this.load();
  }

  onYear(event: Event): void {
    this.year.set(Number((event.target as HTMLSelectElement).value));
    void this.load();
  }

  onMonth(event: Event): void {
    this.month.set(Number((event.target as HTMLSelectElement).value));
    void this.load();
  }

  toggleView(): void {
    this.view.set(this.view() === 'grid' ? 'gantt' : 'grid');
  }

  toggleChart(): void {
    this.chartCollapsed.update(c => !c);
  }

  /**
   * The month's markdown, as a download.
   *
   * `exportTaskListMarkdown` returns the document as a STRING (the route answers `text/markdown`, and the
   * generator can type that) — so the Blob is built here rather than fetched. A 400 means "no data for this
   * month": the service refuses to build an empty document rather than hand back a zero-byte file the user
   * cannot tell from a broken one. Show the server's own sentence.
   */
  async exportMonth(): Promise<void> {
    if (!this.concreteMonth() || this.saving()) return;

    this.saving.set(true);
    try {
      const markdown = await firstValueFrom(
        this.api.exportTaskListMarkdown(this.year(), this.month(), this.teamIds()));

      const url = URL.createObjectURL(new Blob([markdown], { type: 'text/markdown' }));
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `tasklist-${this.year()}-${String(this.month()).padStart(2, '0')}.md`;
      anchor.click();
      URL.revokeObjectURL(url);

      this.toast.show('Exported.');
    } catch (err: unknown) {
      this.toast.show(messageOf(err, 'The export failed.'));
    } finally {
      this.saving.set(false);
    }
  }

  // =====================================================================================================
  // ROWS
  // =====================================================================================================

  chips(row: TaskListRowDto): Chip[] {
    return buildChips(row);
  }

  done(row: TaskListRowDto): boolean {
    return isDone(row);
  }

  hours(value: number | null | undefined): string {
    return hoursText(value);
  }

  percent(value: number | null | undefined): string {
    return progressText(value);
  }

  isExpanded(row: TaskListRowDto): boolean {
    return row.backlogId !== undefined && this.expanded().has(row.backlogId);
  }

  toggleExpand(row: TaskListRowDto): void {
    const id = row.backlogId;
    if (id === undefined) return;

    const next = new Set(this.expanded());
    if (!next.delete(id)) next.add(id);
    this.expanded.set(next);
  }

  /** The name to render for a task's assignee — from ALL users, so a departed one still shows. */
  assigneeName(task: TaskItemDto): string {
    if (task.assigneeUserId === undefined || task.assigneeUserId === null) return '—';
    return this.userNames().find(u => u.id === task.assigneeUserId)?.name ?? '—';
  }

  // =====================================================================================================
  // INLINE PROGRESS
  // =====================================================================================================

  /**
   * Focus the box the moment `@if` inserts it — a setter-style `@ViewChild` fires on every change to the
   * query result, which is exactly when the editor opens. (The desktop does the same: "IsVisibleChanged then
   * focuses the input".) An inline edit the user has to click twice to type into is not an inline edit.
   */
  @ViewChild('progressBox') set progressBox(box: ElementRef<HTMLInputElement> | undefined) {
    box?.nativeElement.focus();
  }

  startProgress(row: TaskListRowDto): void {
    if (row.backlogId === undefined || this.saving()) return;
    this.editingProgress.set(row.backlogId);
    this.progressDraft.set(row.progressPercent === null || row.progressPercent === undefined
      ? ''
      : String(row.progressPercent));
  }

  onProgressInput(event: Event): void {
    this.progressDraft.set((event.target as HTMLInputElement).value);
  }

  /**
   * ESCAPE — revert WITHOUT saving.
   *
   * 🔴 THIS ALSO DISARMS THE BLUR. Closing the editor removes the input from the DOM, and a blur handler
   * firing on the way out would commit the very edit Escape just cancelled. `commitProgress` therefore
   * checks that the row is still the one being edited, and this has already set that to null. Do not
   * "simplify" either half away — they are one mechanism.
   */
  cancelProgress(): void {
    this.editingProgress.set(null);
    this.progressDraft.set('');
  }

  /**
   * ENTER, or blur. Commits — unless the text is not a whole 0-100.
   *
   * 🔴 AN INVALID VALUE NEVER COMMITS. Not as 0, not as null. The desktop gets this right too
   * (TaskListRowVm:595, `return; // invalid input — leave EditProgress as-is and do not commit`), and it
   * matters: a fat-fingered `1000` that silently writes NULL is a lost percent with no error and no undo.
   *
   * An EMPTY box is not invalid — it CLEARS the percent, which is a real thing the user may want.
   */
  commitProgress(row: TaskListRowDto): void {
    const id = row.backlogId;
    if (id === undefined || this.editingProgress() !== id) return;   // Escape already closed it.

    const parsed = parseProgress(this.progressDraft());
    this.editingProgress.set(null);
    this.progressDraft.set('');

    if (!parsed.ok) {
      this.toast.show('Progress must be a whole number from 0 to 100. Nothing was saved.');
      return;                                                        // 🔴 NO WRITE.
    }
    if (parsed.value === (row.progressPercent ?? null)) return;      // unchanged — nothing to write

    void this.mutate(async () => {
      // 🔴 LOAD THE RECORD FIRST. The PUT replaces it; `toUpdateRequest` copies every field we are not
      //    changing back off this DTO. The GET is also where the `expectedVersion` comes from — and doing
      //    it at the head of EVERY chain is what keeps H2 (a tag write bumps this same version) from ever
      //    biting: we never hold a version across two writes.
      const dto = await firstValueFrom(this.api.getBacklog(id));
      await firstValueFrom(this.api.updateBacklog(id, toUpdateRequest(dto, { progressPercent: parsed.value })));
    }, 'The progress could not be saved.');
  }

  // =====================================================================================================
  // DATES
  // =====================================================================================================

  /** Start / End — a plain operational date. 🔴 NO reason note; only deadlines need one. */
  onDate(row: TaskListRowDto, which: 'start' | 'end', event: Event): void {
    const id = row.backlogId;
    if (id === undefined) return;

    const raw = (event.target as HTMLInputElement).value;
    const iso = raw === '' ? null : raw;
    const current = (which === 'start' ? row.startDate : row.endDate) ?? null;
    if (iso === current) return;

    void this.mutate(async () => {
      const dto = await firstValueFrom(this.api.getBacklog(id));
      const patch = which === 'start' ? { startDate: iso } : { endDate: iso };
      await firstValueFrom(this.api.updateBacklog(id, toUpdateRequest(dto, patch)));
    }, 'The date could not be saved.');
  }

  /**
   * A DEADLINE change — 🔴 STOPS AND ASKS FOR A REASON (H3).
   *
   * Nothing is written here. The edit is parked in `pendingDeadline` and the note prompt is rendered; only
   * `confirmDeadline` writes. `cancelDeadline` puts the input back.
   */
  onDeadline(row: TaskListRowDto, which: 'internal' | 'external', event: Event): void {
    const id = row.backlogId;
    if (id === undefined) return;

    const el = event.target as HTMLInputElement;
    const iso = el.value === '' ? null : el.value;
    const previous = (which === 'internal' ? row.deadlineInternal : row.deadlineExternal) ?? null;
    if (iso === previous) return;

    this.deadlineNote.set('');
    this.pendingDeadline.set({ backlogId: id, which, iso, previous, el });
  }

  /** 🔴 CANCEL REVERTS THE PICKER. A native date input keeps whatever the user picked until we put it back. */
  cancelDeadline(): void {
    const pending = this.pendingDeadline();
    if (pending) pending.el.value = pending.previous ?? '';
    this.pendingDeadline.set(null);
    this.deadlineNote.set('');
  }

  onNote(event: Event): void {
    this.deadlineNote.set((event.target as HTMLTextAreaElement).value);
  }

  /** The note may be EMPTY — the desktop's OK button is always enabled, and this mirrors it. */
  confirmDeadline(): void {
    const pending = this.pendingDeadline();
    if (pending === null) return;

    const note = this.deadlineNote().trim();
    this.pendingDeadline.set(null);
    this.deadlineNote.set('');

    void this.mutate(async () => {
      const dto = await firstValueFrom(this.api.getBacklog(pending.backlogId));
      const patch = pending.which === 'internal'
        ? { deadlineInternal: pending.iso }
        : { deadlineExternal: pending.iso };
      await firstValueFrom(
        this.api.updateBacklog(pending.backlogId, toUpdateRequest(dto, patch, note === '' ? null : note)));
    }, 'The deadline could not be saved.');
  }

  // =====================================================================================================
  // TAGS
  //
  // 🔴 `<app-tag-picker>` DOES NOT WRITE — it emits the COMPLETE new set and this component owns the write.
  //    That is deliberate: the write is CHECKED against the PARENT's rowVersion (BacklogTags has no version
  //    of its own), so it can 409, and a picker that owned the write would be perfectly placed to swallow it.
  // =====================================================================================================

  pickerOpen(kind: 'backlog' | 'task', id: number | undefined): boolean {
    const open = this.openPicker();
    return open !== null && open.kind === kind && open.id === id;
  }

  toggleBacklogPicker(row: TaskListRowDto): void {
    const id = row.backlogId;
    if (id === undefined) return;

    if (this.pickerOpen('backlog', id)) {
      this.openPicker.set(null);
      return;
    }
    // The row already carries its tags — no extra read to open the picker.
    this.openPicker.set({ kind: 'backlog', id });
    this.pickerSelected.set(tagIdsOf(row.tags));
  }

  async toggleTaskPicker(task: TaskItemDto): Promise<void> {
    const id = task.id;
    if (id === undefined) return;

    if (this.pickerOpen('task', id)) {
      this.openPicker.set(null);
      return;
    }

    // 🔴 A task's tags are NOT on `TaskItemDto` — they need their own read. Done lazily, on open, so the
    //    screen does not fire one `/api/tasks/{id}/tags` PER TASK on every load. (The desktop does exactly
    //    that N+1; it does not have to pay for it over a network.)
    try {
      const ids = await firstValueFrom(this.api.getTaskTags(id));
      this.openPicker.set({ kind: 'task', id });
      this.pickerSelected.set(ids);
    } catch (err: unknown) {
      this.toast.show(messageOf(err, "That task's tags could not be loaded."));
    }
  }

  commitBacklogTags(row: TaskListRowDto, ids: number[]): void {
    const id = row.backlogId;
    if (id === undefined) return;

    this.pickerSelected.set(ids);          // keep the open picker in step; the reload will confirm it

    void this.mutate(async () => {
      // 🔴 THE FRESH GET IS H2's ANSWER. `setBacklogTags` is a CHECKED write against the BACKLOG's version,
      //    and it RETURNS A NEW ONE. Because every chain re-reads, we never carry a version across two
      //    writes — so we cannot 409 against our own tag write, which is what happens to a screen that
      //    caches the version it loaded the page with.
      const dto = await firstValueFrom(this.api.getBacklog(id));
      await firstValueFrom(this.api.setBacklogTags(id, ids, requireRowVersion(dto.rowVersion)));
    }, 'The tags could not be saved.');
  }

  commitTaskTags(task: TaskItemDto, ids: number[]): void {
    const id = task.id;
    if (id === undefined) return;

    this.pickerSelected.set(ids);

    void this.mutate(async () => {
      // Same rule, the task's own version: a task tag write is checked against the TASK's rowVersion and
      // bumps it. The row we hold was loaded by the last `load()`, and `mutate` reloads after every write,
      // so this version is the one the server last gave us.
      await firstValueFrom(this.api.setTaskTags(id, ids, requireRowVersion(task.rowVersion)));
    }, 'The tags could not be saved.');
  }

  // =====================================================================================================
  // TASK ROWS
  // =====================================================================================================

  /**
   * 🔴 THE NARROW ROUTE, ON PURPOSE. `PUT /api/tasks/{id}/status` touches status and nothing else.
   * `updateTask` (`PUT /api/tasks/{id}`) would REPLACE name, order AND status — a body built from a status
   * dropdown alone compiles clean and blanks the task's name.
   */
  onTaskStatus(task: TaskItemDto, event: Event): void {
    const id = task.id;
    const status = (event.target as HTMLSelectElement).value;
    if (id === undefined || status === (task.status ?? '')) return;

    void this.mutate(
      async () => { await firstValueFrom(this.api.setTaskStatus(id, status, requireRowVersion(task.rowVersion))); },
      "The task's status could not be saved.");
  }

  onTaskType(task: TaskItemDto, event: Event): void {
    const raw = (event.target as HTMLSelectElement).value;
    const type = raw === '' ? null : raw;
    if (type === (task.type ?? null)) return;

    // 🔴 `toTaskExtended` round-trips the ASSIGNEE. Both fields ride this one write and both are written
    //    verbatim — a body carrying only `type` would clear the assignee.
    this.writeTaskExtended(task, { type });
  }

  onTaskAssignee(task: TaskItemDto, event: Event): void {
    const raw = (event.target as HTMLSelectElement).value;
    const assigneeUserId = raw === '' ? null : Number(raw);
    if (assigneeUserId === (task.assigneeUserId ?? null)) return;

    // 🔴 And here `toTaskExtended` round-trips the TYPE, for the same reason.
    this.writeTaskExtended(task, { assigneeUserId });
  }

  private writeTaskExtended(
    task: TaskItemDto,
    patch: { type?: string | null; assigneeUserId?: number | null },
  ): void {
    const id = task.id;
    if (id === undefined) return;

    void this.mutate(
      async () => { await firstValueFrom(this.api.setTaskExtended(id, toTaskExtended(task, patch))); },
      'The task could not be saved.');
  }

  // =====================================================================================================
  // CONTINUE
  // =====================================================================================================

  /**
   * Copy this backlog into NEXT month (the original stays). Hidden in "All months" — there is no "next"
   * month to roll into.
   *
   * A duplicate is a 400 carrying the server's own sentence ("'X' already exists in 2026-08"). `mutate`
   * surfaces it verbatim; there is nothing this screen could add to it.
   */
  continueRow(row: TaskListRowDto): void {
    const id = row.backlogId;
    if (id === undefined || !this.concreteMonth()) return;

    const target = nextPeriod(this.year(), this.month());
    void this.mutate(async () => {
      await firstValueFrom(this.api.continueBacklog(id, target));
      this.toast.show(`Continued to ${target}.`);
    }, 'The backlog could not be continued.');
  }

  // =====================================================================================================
  // GANTT — 🔴 RENDER THE SERVER'S MODEL. DO NOT COMPUTE ONE.
  //
  // The axis is already working days only (weekends AND holidays excluded server-side); the bars already
  // carry their start index, span and external-deadline marker as AXIS POSITIONS. There is no date maths to
  // do here, and any done here would be a second source of truth for the geometry.
  // =====================================================================================================

  readonly COL = 26;                    // px per working day

  barLeft(startDayIndex: number | undefined): number {
    return (startDayIndex ?? 0) * this.COL;
  }

  barWidth(spanWorkingDays: number | undefined): number {
    return (spanWorkingDays ?? 0) * this.COL;
  }

  /** `span === 0` is the server's "this backlog has no dates at all" — there is nothing to draw. */
  barVisible(spanWorkingDays: number | undefined): boolean {
    return (spanWorkingDays ?? 0) > 0;
  }

  axisLabel(iso: string): string {
    return iso.slice(8, 10);                      // day-of-month; the month rides the band header
  }
}
