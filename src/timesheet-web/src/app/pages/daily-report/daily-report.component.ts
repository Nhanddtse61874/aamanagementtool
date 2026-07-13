import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, firstValueFrom } from 'rxjs';

import {
  BacklogListItemDto, SettingsStandupEntryView, SettingsUserStandup, StandupIssueDto, TaskItemDto,
} from '../../api/models';
import { TeamFilterComponent } from '../../components/team-filter/team-filter.component';
import { DataKind, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import {
  ISSUE_STATUSES, SECTIONS, STANDUP_STATUSES, StandupSection, StandupStatus,
  addDays, canEditDay, formatDay, todayIso,
} from './standup-day';
import {
  EntryDraft, apiError, emptyDraft, hasSolution, isConflict, pickableBacklogs, toCreateRequest,
  toIssueUpdateBody, validateDraft,
} from './standup-entry';

/** The drop targets on the Input tab: the two section lists, and the trash. */
export type DropZone = StandupSection | 'trash';

/**
 * The Daily Report (standup) screen. WPF parity: `DailyReportViewModel` + `StandupEntryRowVm` +
 * `StandupIssueRowVm` + `StandupDraftVm`.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 * 🔴 ADD + DELETE ONLY. THERE IS NO EDIT-ENTRY AFFORDANCE ON THIS SCREEN, AND ADDING ONE WOULD BE A BUG.
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 *
 * `StandupEntries` HAS NO `row_version` COLUMN — not on the table (DatabaseInitializer.cs), not on the record
 * (`StandupEntry`), not on the DTO (`StandupEntryDto`), and no `expectedVersion` on
 * `SettingsStandupEntryUpdateRequest`. It is DELIBERATELY unversioned. The API's own DTO contract says so:
 *
 *     DO NOT INVENT A VERSION FOR:
 *         StandupEntry — deliberately unversioned: owner-gated, last-write-wins BY DESIGN. Two users cannot
 *                        reach the same row, so there is no race to guard.
 *
 * That argument holds ONLY because the owner gate plus the edit-lock make a CROSS-USER race unreachable.
 * 🔴 BUT TWO BROWSER TABS ARE THE SAME USER, and WPF was a single process. An edit button here would
 * manufacture, in the browser, exactly the race the desktop could not have — on the one standup table with no
 * version to protect it. `PUT /api/standup/entries/{id}` exists; WPF NEVER CALLS IT (`StandupEntryRowVm` has a
 * single `DeleteAsync` command and its fields are all getters). That is not a gap in WPF. It is the design.
 * `WorklogService.updateStandupEntry` therefore has no caller here, on purpose.
 *
 * ── THE EDIT-LOCK IS THE SERVER'S, NOT OURS ────────────────────────────────────────────────────────────
 * `StandupService.CanEditDay` (today or yesterday) gates all five entry writes in Core, and the API re-checks
 * it so a silent no-op becomes an honest 400. We CANNOT bypass it and do not try. We mirror it in
 * `canEditDay()` for ONE reason: the lock reaches the client only as a PER-ENTRY `editable` bool, so a day
 * with ZERO entries carries no lock signal at all and "+ Add entry" would have nothing to gate on. So the
 * mirror decides what to OFFER; the server decides what HAPPENS, and every rejection is surfaced (`report`).
 *
 * ── ISSUES ARE EXEMPT FROM BOTH GATES, BY DESIGN ───────────────────────────────────────────────────────
 * `AddIssueAsync` / `UpdateIssueAsync` / `DeleteIssueAsync` contain NO `CanEditDay` and NO owner check — the
 * requirement is "issues are exempt (anyone, anytime)" (DR-04). So the issue controls below are NOT
 * lock-gated, and that is not an oversight to tidy up. Issues DO carry `row_version` (they are the one standup
 * table that can be raced, precisely because they are collaborative), so the solution/status write is CHECKED
 * and can 409 — see `saveIssue`.
 *
 * ── 🔴 THE BACKLOG PICKER'S TEAM SCOPE DIVERGES FROM WPF, AND CANNOT BE MADE TO MATCH FROM HERE ─────────
 * WPF's picker is ACTIVE-TEAM ONLY: `StandupService.SearchBacklogsAsync` passes `new[] { ActiveTeamId }`.
 * THE WEB CANNOT REPRODUCE THAT. `SearchBacklogsAsync` is a Core method with no HTTP surface of its own, and
 * the only backlog list on the wire is `GET /api/backlogs`, which scopes to `EffectiveTeamIds` — i.e. ALL MY
 * TEAMS. It cannot be narrowed:
 *
 *   - the generated `BacklogList$Params` is `{ term?: string }` — there is NO `teamIds` parameter (the C#
 *     hand-reads it off the raw query string, so ApiExplorer never saw it and none was generated); and
 *   - `BacklogListItemDto` carries NO `teamId`, so it cannot be filtered client-side either.
 *
 * So this picker offers the backlogs of every team the user belongs to, where WPF offers only the active
 * team's. It is a FIDELITY DIVERGENCE, not a leak — every backlog listed is one the server already authorises
 * this user to see, and the entry the server creates is stamped with the ACTIVE team regardless of which
 * backlog was picked (`AddEntryAsync`: `TeamId: _currentTeam.ActiveTeamId`). Closing it needs a new route or
 * a `teamId` on the DTO; both are API changes and neither is this screen's to make.
 *
 * `DEFAULT` IS excluded, client-side, exactly as WPF does — see `pickableBacklogs`.
 *
 * ── THE BOARD'S TEAM FILTER ────────────────────────────────────────────────────────────────────────────
 * 🔴 An empty selection CANNOT be sent: `teamIds: []` appends no query key, and the server reads an absent key
 * as "ALL MY TEAMS" — the exact inverse. So when the filter emits `[]` we render the empty state LOCALLY and
 * make NO CALL. See `boardBlocked`.
 */
@Component({
  selector: 'app-daily-report',
  standalone: true,
  imports: [CommonModule, CdkDrag, CdkDropList, CdkDropListGroup, TeamFilterComponent],
  templateUrl: './daily-report.component.html',
  styleUrl: './daily-report.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DailyReportComponent implements OnInit {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  readonly statuses = STANDUP_STATUSES;
  readonly issueStatuses = ISSUE_STATUSES;
  readonly sections = SECTIONS;

  /**
   * The drop zones, widened to `DropZone`.
   *
   * `CdkDropList<T>` infers `T` from `[cdkDropListData]`, and `(cdkDropListDropped)` is then typed
   * `CdkDragDrop<T>`. Binding a section list to a bare `StandupSection` and the trash to `'trash'` gives the
   * two lists DIFFERENT generics, and `onDrop` — which must accept both — matches neither under
   * `strictTemplates`. Widening both to `DropZone` at the binding is what lets one handler serve both.
   */
  readonly trashZone: DropZone = 'trash';
  zone(section: StandupSection): DropZone {
    return section;
  }

  /** Real today, captured once at construction — the anchor the edit-lock mirror compares against. */
  private readonly today = signal(todayIso());

  /** The day on screen. `◀ ▶` move it; every move re-reads. Seeded to today, never to a hard-coded string. */
  readonly date = signal(todayIso());
  readonly tab = signal<'input' | 'team'>('input');

  readonly myDay = signal<SettingsUserStandup | null>(null);
  readonly board = signal<readonly SettingsUserStandup[]>([]);
  readonly backlogs = signal<readonly BacklogListItemDto[]>([]);
  readonly tasks = signal<readonly TaskItemDto[]>([]);
  readonly isAdmin = signal(false);

  /** Re-entrancy guard. One mutation at a time; a double-click is dropped, not queued. */
  readonly busy = signal(false);
  readonly loading = signal(false);

  readonly dayLabel = computed(() => formatDay(this.date()));

  /**
   * The edit-lock mirror. Gates what we OFFER (the two add buttons, drag, the trash). The server is still the
   * authority and its 400 is always surfaced — see the class doc.
   */
  readonly canEdit = computed(() => canEditDay(this.date(), this.today()));

  /**
   * 🔴 `undefined` = the filter has not emitted (it is still loading, or its reads FAILED). `[]` = the user
   * unchecked every team, which is a real, meaningful selection.
   *
   * The distinction is the whole of H4. `undefined` is passed to the API as `undefined`, which the server
   * reads as "all my teams" — the correct degradation for a filter that failed to load, and the one the
   * TeamFilterComponent's own doc prescribes. `[]` is NEVER sent, because it goes on the wire BYTE-IDENTICALLY
   * to `undefined` and would therefore mean the exact opposite of what the user asked for.
   */
  private readonly teamIds = signal<number[] | undefined>(undefined);

  /** True only when the user has explicitly unchecked everything. Render locally; do not call. */
  readonly boardBlocked = computed(() => this.teamIds()?.length === 0);

  // ---- the Add-entry modal ----
  readonly modalOpen = signal(false);
  readonly draft = signal<EntryDraft>(emptyDraft('today'));
  readonly draftError = signal<string | null>(null);

  // ---- the inline issue editors, keyed by issue id ----
  /** Issue ids whose solution editor is open (WPF: `IsEditingSolution`). */
  readonly editingSolution = signal<ReadonlySet<number>>(new Set());
  /** Entry ids whose "add issue" box is open. */
  readonly addingIssue = signal<ReadonlySet<number>>(new Set());

  constructor() {
    /**
     * WPF's `DailyReportViewModel` reloads on `Standup`, `Users` or `Backlogs` and nothing else. Same three:
     * Standup is our entries, Users changes who is on the board, Backlogs changes the picker.
     *
     * 🔴 `takeUntilDestroyed` — the one long-lived subscription on this screen. Every other call below is a
     * `firstValueFrom` over a completing HttpClient observable, which needs no teardown.
     */
    this.realtime.dataChanged
      .pipe(
        filter(e => e.kind === DataKind.Standup || e.kind === DataKind.Users || e.kind === DataKind.Backlogs),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => void this.reload());
  }

  ngOnInit(): void {
    this.realtime.start();
    void this.boot();
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // READS
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  private async boot(): Promise<void> {
    // `me()` is read ONLY to decide whether to OFFER "Archive week". That route is
    // `.RequireAuthorization(AuthSetup.AdminPolicy)` and 403s a non-admin — and this screen is reachable by
    // every user, so per THE ADMIN CONTRACT it must not call an [ADMIN] method it has not first earned.
    // A failure here is not fatal: we simply stay non-admin and hide the button.
    try {
      const me = await firstValueFrom(this.api.me());
      this.isAdmin.set(me.isAdmin === true);
    } catch {
      this.isAdmin.set(false);
    }
    await this.reload();
  }

  /** Re-read everything the screen shows for the current day. Never throws — see `report`. */
  async reload(): Promise<void> {
    this.loading.set(true);
    try {
      const day = this.date();

      const myDay = await firstValueFrom(this.api.getStandupMyDay(day));
      this.myDay.set(myDay);

      // 🔴 H4: `[]` means "the user unchecked everything". It CANNOT be sent — do not call, render locally.
      if (this.boardBlocked()) {
        this.board.set([]);
      } else {
        this.board.set(await firstValueFrom(this.api.getStandupBoard(day, this.teamIds())));
      }

      // The picker. `DEFAULT` is dropped client-side; the team scope is all-my-teams and cannot be narrowed
      // from here — see the class doc.
      const backlogs = await firstValueFrom(this.api.getBacklogList());
      this.backlogs.set(pickableBacklogs(backlogs));
    } catch (err) {
      this.report(err, 'Could not load the daily report.');
    } finally {
      this.loading.set(false);
    }
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // DAY NAV — the two buttons that had no handler at all.
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  prevDay(): void {
    this.date.set(addDays(this.date(), -1));
    void this.reload();
  }

  nextDay(): void {
    this.date.set(addDays(this.date(), 1));
    void this.reload();
  }

  onTeamSelection(ids: number[]): void {
    this.teamIds.set(ids);
    void this.reload();
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // THE ADD-ENTRY MODAL
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  openAdd(section: StandupSection): void {
    this.draft.set(emptyDraft(section));
    this.draftError.set(null);
    this.tasks.set([]);
    this.modalOpen.set(true);
  }

  closeAdd(): void {
    this.modalOpen.set(false);
  }

  patch<K extends keyof EntryDraft>(key: K, value: EntryDraft[K]): void {
    this.draft.update(d => ({ ...d, [key]: value }));
  }

  /**
   * Picking a backlog fills the code box and loads its tasks (WPF: `OnSelectedBacklogChanged`).
   *
   * 🔴 The empty option is AD-HOC: it clears `backlogId` to null, which is what makes the typed code an ad-hoc
   * line rather than a claim about a backlog that exists. See `EntryDraft`.
   */
  async pickBacklog(rawId: string): Promise<void> {
    const id = rawId ? Number(rawId) : null;
    if (id === null) {
      this.draft.update(d => ({ ...d, backlogId: null }));
      this.tasks.set([]);
      return;
    }

    const backlog = this.backlogs().find(b => b.id === id);
    this.draft.update(d => ({ ...d, backlogId: id, backlogCode: backlog?.backlogCode ?? d.backlogCode }));
    this.tasks.set([]);

    try {
      this.tasks.set(await firstValueFrom(this.api.getTasks(id)));
    } catch (err) {
      // A picker that cannot list tasks is not a dead end — the task box is still free text.
      this.report(err, 'Could not load that backlog\'s tasks.');
    }
  }

  /** Picking a task fills the task-text box (WPF: `OnSelectedTaskChanged`). Free text still wins after. */
  pickTask(rawId: string): void {
    const id = rawId ? Number(rawId) : null;
    if (id === null) return;
    const task = this.tasks().find(t => t.id === id);
    if (task?.taskName) this.patch('taskText', task.taskName);
  }

  async submitEntry(): Promise<void> {
    const draft = this.draft();

    // The client mirror of the server's four validations — a courtesy, not a gate.
    const invalid = validateDraft(draft);
    if (invalid) {
      this.draftError.set(invalid);
      return;
    }
    this.draftError.set(null);

    await this.run('Could not add the entry.', async () => {
      await firstValueFrom(this.api.createStandupEntry(toCreateRequest(draft, this.date())));
      this.modalOpen.set(false);
      this.toast.show('Entry added');
      await this.reload();
    });
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // ENTRY WRITES — add, delete, reorder. NO UPDATE. See the class doc.
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  async deleteEntry(view: SettingsStandupEntryView): Promise<void> {
    const id = view.entry?.id;
    if (!id) return;

    await this.run('Could not delete the entry.', async () => {
      await firstValueFrom(this.api.deleteStandupEntry(id));
      this.toast.show('Entry deleted');
      await this.reload();
    });
  }

  /**
   * Quick import: clone a source day's entries (with their issues) onto the day on screen.
   *
   * 🔴 `quickImportStandup` takes BOTH dates — source AND target. The target is the day currently shown, and
   * the source defaults to the day before it, which is the "clone yesterday into today" case the button is for.
   * A locked TARGET is a 400; an empty SOURCE is a 200 with a count of 0, which is a legitimate no-op and not
   * an error — the two are deliberately distinguishable and are reported differently below.
   */
  async quickImport(): Promise<void> {
    const target = this.date();
    const source = addDays(target, -1);

    await this.run('Could not import.', async () => {
      const cloned = await firstValueFrom(this.api.quickImportStandup(source, target));
      if (cloned <= 0) {
        this.toast.show(`Nothing to import from ${source}.`);
        return;
      }
      this.toast.show(`Imported ${cloned} ${cloned === 1 ? 'entry' : 'entries'} from ${source}.`);
      await this.reload();
    });
  }

  /**
   * Drag: onto another entry to reorder (dropping onto the other section moves it there), or onto the trash
   * to delete. Both are lock-gated server-side; the zones are only offered when `canEdit()`.
   *
   * 🔴 `reorderStandupEntry(draggedId, targetId)` is a PAIR, not an index list: the server rebuilds the
   * destination section's order with the dragged row inserted AT THE TARGET'S SLOT. So we resolve the entry
   * currently occupying the slot that was dropped on. Insert-BEFORE is therefore the semantics, and "drop past
   * the last row" lands second-to-last rather than last — that is the API's contract (a target outside the
   * destination section appends, but the endpoint requires the target to be a real, same-day, team-visible
   * entry, so "append" is not reachable from within one section). A second drag settles it.
   */
  async onDrop(event: CdkDragDrop<DropZone, DropZone, SettingsStandupEntryView>): Promise<void> {
    const dragged = event.item.data;
    const draggedId = dragged.entry?.id;
    if (!draggedId || dragged.editable !== true) return;

    const zone = event.container.data;

    if (zone === 'trash') {
      await this.deleteEntry(dragged);
      return;
    }

    // The destination section as it stands, minus the row being dragged: those are the slots we can target.
    const destination = this.entriesOf(zone).filter(v => v.entry?.id !== draggedId);
    if (destination.length === 0) return;   // no slot to target — the pair API cannot express this drop

    const slot = Math.min(event.currentIndex, destination.length - 1);
    const targetId = destination[slot].entry?.id;
    if (!targetId || targetId === draggedId) return;

    await this.run('Could not reorder.', async () => {
      await firstValueFrom(this.api.reorderStandupEntry(draggedId, targetId));
      await this.reload();
    });
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // ISSUES — collaborative: NOT lock-gated, NOT owner-gated. By design (DR-04). See the class doc.
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  toggleAddIssue(entryId: number): void {
    this.addingIssue.update(s => toggle(s, entryId));
  }

  beginEditSolution(issueId: number): void {
    this.editingSolution.update(s => toggle(s, issueId));
  }

  isAddingIssue(entryId: number): boolean {
    return this.addingIssue().has(entryId);
  }

  isEditingSolution(issue: StandupIssueDto): boolean {
    // WPF: `ShowSolutionEditor => IsEditingSolution || HasSolution` — an editor once there is something to edit.
    return this.editingSolution().has(issue.id ?? 0) || hasSolution(issue);
  }

  hasSolution(issue: StandupIssueDto): boolean {
    return hasSolution(issue);
  }

  async addIssue(entryId: number, text: string): Promise<void> {
    if (!text.trim()) {
      this.toast.show('Issue text is required.');
      return;
    }

    await this.run('Could not add the issue.', async () => {
      // A new issue starts `open` with no solution — WPF's dialog default, and the column default.
      await firstValueFrom(
        this.api.createStandupIssue(entryId, { issueText: text.trim(), solutionText: null, status: 'open' }),
      );
      this.addingIssue.update(s => remove(s, entryId));
      this.toast.show('Issue added');
      await this.reload();
    });
  }

  /**
   * Save an issue's solution and/or status. A CHECKED write — it can 409.
   *
   * 🔴 `toIssueUpdateBody` ROUND-TRIPS `issueText` and `status` off the loaded issue. The PUT is a whole-record
   * overwrite and both fields are optional on the request type, so a body built from the solution box alone
   * compiles clean and 400s every time. See that function's doc.
   *
   * 🔴 On 409: toast + RE-READ. Not the ConflictDialogComponent — that merges a timesheet cell (two numbers,
   * pick one). An issue's solution is free text; there is nothing to merge.
   */
  async saveIssue(
    entryId: number, issue: StandupIssueDto, solutionText: string, status: string,
  ): Promise<void> {
    const issueId = issue.id;
    if (!issueId) return;

    if (this.busy()) return;
    this.busy.set(true);
    try {
      await firstValueFrom(
        this.api.updateStandupIssue(entryId, issueId, toIssueUpdateBody(issue, solutionText, status)),
      );
      this.editingSolution.update(s => remove(s, issueId));
      this.toast.show('Solution saved');
      await this.reload();
    } catch (err) {
      if (isConflict(err)) {
        this.toast.show('This issue was changed by someone else. Reloading the latest version.');
        await this.reload();
      } else {
        this.toast.show(apiError(err, 'Could not save the issue.'));
      }
    } finally {
      this.busy.set(false);
    }
  }

  async deleteIssue(entryId: number, issueId: number | undefined): Promise<void> {
    if (!issueId) return;

    await this.run('Could not delete the issue.', async () => {
      await firstValueFrom(this.api.deleteStandupIssue(entryId, issueId));
      await this.reload();
    });
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // ARCHIVE WEEK — [ADMIN]. Offered only to an admin; see `boot`.
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  /**
   * 🔴 Returns a SERVER-SIDE PATH, not a download — the browser cannot open it. It is there to be SHOWN.
   * "No standup data this week" is a 400, which `report` surfaces verbatim.
   */
  async archiveWeek(): Promise<void> {
    await this.run('Could not archive the week.', async () => {
      const file = await firstValueFrom(this.api.archiveStandupWeek(this.date()));
      this.toast.show(file.path ? `Archived to ${file.path}` : 'Archived.');
    });
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // template helpers
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  entriesOf(section: StandupSection): readonly SettingsStandupEntryView[] {
    const day = this.myDay();
    if (!day) return [];
    return (section === 'yesterday' ? day.yesterday : day.today) ?? [];
  }

  /** The board's yesterday/today for one member. The mock's `TeamMember` had no `yesterday` at all. */
  bandOf(member: SettingsUserStandup, section: StandupSection): readonly SettingsStandupEntryView[] {
    return (section === 'yesterday' ? member.yesterday : member.today) ?? [];
  }

  issuesOf(view: SettingsStandupEntryView): readonly StandupIssueDto[] {
    return view.issues ?? [];
  }

  trackEntry = (_: number, view: SettingsStandupEntryView): number => view.entry?.id ?? 0;
  trackIssue = (_: number, issue: StandupIssueDto): number => issue.id ?? 0;
  trackMember = (_: number, member: SettingsUserStandup): number => member.userId ?? 0;

  initial(name: string | null | undefined): string {
    return name && name.length > 0 ? name[0] : '?';
  }

  avatar(name: string | null | undefined): string {
    return this.api.avatarColor(name ?? null);
  }

  asStatus(raw: string): StandupStatus {
    // The <select>'s options ARE `statuses`, so this narrowing is total in practice; the fallback exists only
    // to keep the signature honest without an `any`.
    return STANDUP_STATUSES.includes(raw as StandupStatus) ? (raw as StandupStatus) : STANDUP_STATUSES[0];
  }

  asSection(raw: string): StandupSection {
    return SECTIONS.includes(raw as StandupSection) ? (raw as StandupSection) : 'today';
  }

  // ═════════════════════════════════════════════════════════════════════════════════════════════════════
  // the mutation wrapper
  // ═════════════════════════════════════════════════════════════════════════════════════════════════════

  /**
   * 🔴 EVERY mutation goes through here, and it does two things that are not optional.
   *
   *   1. RE-ENTRANCY GUARD. A second click while a write is in flight is DROPPED. Without it, a double-click
   *      on "+ Add entry" creates the row twice — and `StandupEntry` has no version, so nothing downstream
   *      would catch it.
   *   2. IT CATCHES AND NEVER RE-THROWS. These are `async` methods bound to template outputs: anything that
   *      escapes becomes an UNHANDLED PROMISE REJECTION, which lands in the console and NOWHERE THE USER CAN
   *      SEE IT. The write fails, the screen keeps showing stale rows, and nobody is told.
   */
  private async run(fallback: string, action: () => Promise<void>): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      await action();
    } catch (err) {
      this.report(err, fallback);
    } finally {
      this.busy.set(false);
    }
  }

  /** Surface what the SERVER said — its 400s carry the real reason ("the day is locked ..."). */
  private report(err: unknown, fallback: string): void {
    this.toast.show(apiError(err, fallback));
  }
}

function toggle(set: ReadonlySet<number>, id: number): ReadonlySet<number> {
  const next = new Set(set);
  if (!next.delete(id)) next.add(id);
  return next;
}

function remove(set: ReadonlySet<number>, id: number): ReadonlySet<number> {
  const next = new Set(set);
  next.delete(id);
  return next;
}
