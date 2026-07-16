import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, forkJoin } from 'rxjs';

import {
  DefaultTaskDto, PcaContactDto, RetentionPreview, TagDto, TeamDto, UserDto,
} from '../../api/models';
import { ConfirmDialogComponent } from '../../core/confirm-dialog/confirm-dialog.component';
import { ThemeService } from '../../services/theme.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService, requireRowVersion } from '../../services/worklog.service';
import { CalCell, buildCalendar, isoDate, monthLabel, shiftMonth } from './holiday-calendar';
import {
  TemplateGroup, TemplateSaveError, groupTemplates, nextOrderIndex, saveTemplate,
} from './settings-templates';

type SettingsTab = 'general' | 'workflow' | 'teams' | 'ops';

/** The N-day "not logged" warning window (SET-02). The key the API reads; the default it falls back to. */
const WARNING_DAYS_KEY = 'chua_log_n_days';
const WARNING_DAYS_DEFAULT = 3;

/** A tag's colour when the admin does not pick one. */
const TAG_COLOR_DEFAULT = '#0F766E';

/** A question the user must answer before something irreversible happens. */
interface PendingConfirm {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
  readonly run: () => void;
}

interface TagDraft { id: number | null; text: string; icon: string; color: string; }
interface TemplateDraft { originalName: string | null; name: string; tasks: string; }

/**
 * The Settings screen. ADMIN-ONLY (`adminGuard` on `/settings`), which is what makes it safe to call the
 * ~20 [ADMIN] methods below. Never call any of them from a screen an ordinary user can reach: a 403 inside
 * the `forkJoin` here would take the whole screen down, not just the panel that asked.
 *
 * ══ FIVE SECTIONS OF THE OLD MOCKUP ARE DELIBERATELY GONE ═══════════════════════════════════════════════
 *
 * DB path · daily-report archive path · both export roots · backup folder + auto-backup + keep-count ·
 * retention enable + retention months. All of them were `<input>`s bound to a `storageFields` array of empty
 * strings, wired to nothing.
 *
 * They are not "not wired yet" — THEY MUST NOT EXIST HERE. Every one of them would have to write through
 * `IAppConfig.Set*`, and the API's own header says why that is forbidden:
 *
 *     "Never call any IAppConfig.Set* from an endpoint. It is a process-wide singleton with ten setters;
 *      on a server every one of them is CROSS-USER STATE — one user toggling dark mode flips it for
 *      everyone, and SetDbPath repoints the whole server's database."
 *
 * These are HOST configuration. They live in `appsettings.json`, where exactly one person can change them and
 * a restart makes it so. Shipping them as web inputs would let any admin repoint the production database
 * from a browser tab.
 *
 * 🔴 DARK MODE IS NOT ONE OF THEM, and the distinction is the whole point: it is a PER-USER preference that
 * `ThemeService` persists to `localStorage`. It never touches the server, so it cannot leak across users.
 * It stays, accent picker and all.
 *
 * What DOES stay from Ops is the four `/api/ops/*` ACTIONS — backup / export / retention preview / retention
 * run. Those are operations, not configuration: they act, they do not persist a cross-user setting.
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, ConfirmDialogComponent],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsComponent {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);
  readonly theme = inject(ThemeService);

  readonly tab = signal<SettingsTab>('general');
  readonly tabs: { id: SettingsTab; label: string }[] = [
    { id: 'general', label: 'General' },
    { id: 'workflow', label: 'Workflow' },
    { id: 'teams', label: 'Teams' },
    { id: 'ops', label: 'Operations' },
  ];

  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  /** 🔴 The re-entrancy guard. Every mutation checks it; every button is disabled while it is set. */
  readonly busy = signal(false);

  /** Messages that must not fade — a half-written template, above all. See `settings-templates.ts`. */
  readonly notice = signal<string | null>(null);

  readonly confirm = signal<PendingConfirm | null>(null);

  // ---- data ----
  readonly warningDays = signal(WARNING_DAYS_DEFAULT);
  readonly tags = signal<TagDto[]>([]);
  readonly templates = signal<TemplateGroup[]>([]);
  readonly contacts = signal<PcaContactDto[]>([]);
  readonly teams = signal<TeamDto[]>([]);
  readonly defaultTasks = signal<DefaultTaskDto[]>([]);
  readonly activeUsers = signal<UserDto[]>([]);
  readonly holidays = signal<ReadonlySet<string>>(new Set());

  // ---- the holiday calendar ----
  readonly weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  private readonly today = new Date();
  readonly calYear = signal(this.today.getFullYear());
  readonly calMonth = signal(this.today.getMonth() + 1);
  readonly calendar = computed(() => buildCalendar(this.calYear(), this.calMonth()));
  readonly calLabel = computed(() => monthLabel(this.calYear(), this.calMonth()));

  // ---- editors ----
  readonly tagDraft = signal<TagDraft | null>(null);
  readonly templateDraft = signal<TemplateDraft | null>(null);
  readonly newContact = signal('');
  readonly newTeam = signal('');
  readonly newDefaultTask = signal('');
  readonly renamingTeamId = signal<number | null>(null);
  readonly renamingContactId = signal<number | null>(null);
  readonly renameText = signal('');

  // ---- team membership overlay ----
  readonly membersTeamId = signal<number | null>(null);
  readonly memberIds = signal<ReadonlySet<number>>(new Set());

  // ---- ops ----
  readonly retentionPreview = signal<RetentionPreview | null>(null);
  readonly opsResult = signal<string | null>(null);
  readonly archiveDate = signal(isoDate(new Date()));

  constructor() {
    this.load();
  }

  // 🔴 These exist because an Angular TEMPLATE EXPRESSION IS NOT JAVASCRIPT — its parser is a restricted
  // subset with NO SPREAD OPERATOR, so `tagDraft.set({ ...d, text: $event })` does not compile (it fails at
  // JIT with "Unexpected token ."). The spread has to live in the class. One setter per field, which also
  // keeps the template free of any knowledge of the draft's shape.
  setTagIcon(v: string): void { this.tagDraft.update(d => (d === null ? d : { ...d, icon: v })); }
  setTagText(v: string): void { this.tagDraft.update(d => (d === null ? d : { ...d, text: v })); }
  setTagColor(v: string): void { this.tagDraft.update(d => (d === null ? d : { ...d, color: v })); }

  setTemplateName(v: string): void {
    this.templateDraft.update(d => (d === null ? d : { ...d, name: v }));
  }
  setTemplateTasks(v: string): void {
    this.templateDraft.update(d => (d === null ? d : { ...d, tasks: v }));
  }

  // =======================================================================================================
  // READ — one forkJoin. Every call here is reachable only by an admin, which is why a 403 cannot arise.
  // =======================================================================================================

  private load(): void {
    this.loading.set(true);

    forkJoin({
      setting: this.api.getSetting(WARNING_DAYS_KEY),
      tags: this.api.getTagList(),
      templates: this.api.getTemplateList(),
      contacts: this.api.getPcaContactsAll(),
      teams: this.api.getTeamsAll(),
      defaults: this.api.getDefaultTasksAll(),
      users: this.api.getUsersActive(),
      holidays: this.api.getHolidayList(),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: r => {
          // 🔴 An UNSET key is a 200 with a NULL value, not a 404 — every key is unset on a fresh database.
          // The correct response to a null is the documented default, NOT an error.
          const parsed = Number(r.setting.value);
          this.warningDays.set(
            r.setting.value !== null && r.setting.value !== undefined && Number.isFinite(parsed) && parsed > 0
              ? parsed
              : WARNING_DAYS_DEFAULT);

          this.tags.set(r.tags);
          this.templates.set(groupTemplates(r.templates));
          this.contacts.set(r.contacts);
          this.teams.set(r.teams);
          this.defaultTasks.set(r.defaults);
          this.activeUsers.set(r.users);
          this.holidays.set(new Set(r.holidays.map(h => (h.date ?? '').slice(0, 10)).filter(d => d !== '')));

          this.loadError.set(null);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loadError.set(describeError(err));
          this.loading.set(false);
        },
      });
  }

  reload(): void { this.load(); }
  dismissNotice(): void { this.notice.set(null); }

  // =======================================================================================================
  // GENERAL — the warning window (SET-02) and the holiday calendar
  // =======================================================================================================

  /**
   * SET-02. Until M9 this was `<input value="3">` — a literal, bound to nothing, unreachable from the web.
   * The API reads this key SERVER-side for `/api/reports/missing-logs` (which takes no client parameters at
   * all, precisely so nobody can ask for an arbitrarily large scan window).
   */
  saveWarningDays(): void {
    const n = this.warningDays();
    if (this.busy() || !Number.isFinite(n) || n < 1) return;   // ← re-entrancy guard
    this.run(this.api.setSetting(WARNING_DAYS_KEY, String(Math.trunc(n))), 'Saved');
  }

  prevMonth(): void { this.stepMonth(-1); }
  nextMonth(): void { this.stepMonth(1); }

  private stepMonth(delta: number): void {
    const next = shiftMonth(this.calYear(), this.calMonth(), delta);
    this.calYear.set(next.year);
    this.calMonth.set(next.month);
  }

  isHoliday(iso: string): boolean { return this.holidays().has(iso); }

  /**
   * 🔴 TWO ROUTES, NOT ONE TOGGLE — `POST /api/holidays` upserts and `DELETE /api/holidays/{date}` removes.
   *
   * And weekends ARE clickable: WPF allows it, and the API has no weekday guard (the mockup's
   * `if (c.weekend) return;` was a client-only invention). A holiday that lands on a Saturday is still a
   * holiday.
   */
  toggleHoliday(cell: CalCell): void {
    if (this.busy()) return;   // ← re-entrancy guard

    const on = this.isHoliday(cell.iso);
    const call = on ? this.api.deleteHoliday(cell.iso) : this.api.upsertHoliday(cell.iso);

    this.busy.set(true);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        this.holidays.update(set => {
          const next = new Set(set);
          if (on) next.delete(cell.iso); else next.add(cell.iso);
          return next;
        });
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  // =======================================================================================================
  // WORKFLOW — tags, templates, default tasks
  // =======================================================================================================

  // ---- tags: create / update (CHECKED) / delete (HARD, cascading) ----

  newTag(): void { this.tagDraft.set({ id: null, text: '', icon: '', color: TAG_COLOR_DEFAULT }); }

  editTag(t: TagDto): void {
    this.tagDraft.set({
      id: t.id ?? null,
      text: t.text ?? '',
      icon: t.icon ?? '',
      color: t.color ?? TAG_COLOR_DEFAULT,
    });
  }

  cancelTag(): void { this.tagDraft.set(null); }

  saveTag(): void {
    const draft = this.tagDraft();
    if (this.busy() || draft === null || draft.text.trim() === '') return;   // ← re-entrancy guard

    const body = { text: draft.text.trim(), icon: draft.icon.trim(), color: draft.color };

    if (draft.id === null) {
      this.run(this.api.createTag(body), 'Tag created', () => this.tagDraft.set(null));
      return;
    }

    // A CHECKED write: the version comes from the row we loaded. Never `!`, never 0.
    const original = this.tags().find(t => t.id === draft.id);
    if (original === undefined) return;

    this.run(
      this.api.updateTag(draft.id, { ...body, expectedVersion: requireRowVersion(original.rowVersion) }),
      'Tag saved',
      () => this.tagDraft.set(null),
    );
  }

  /**
   * 🔴 A HARD DELETE, and it CASCADES. `TagRepository.DeleteAsync` opens a transaction and runs
   * `DELETE FROM BacklogTags`, `DELETE FROM TaskTags`, `DELETE FROM Tags` — the tag is removed from every
   * backlog and every task that carries it, permanently. Nothing is soft here and nothing can be restored.
   *
   * Contrast `deactivateContact` below, which is SOFT for exactly the opposite reason.
   */
  askDeleteTag(t: TagDto): void {
    const id = t.id;
    if (typeof id !== 'number') return;

    this.confirm.set({
      title: `Delete “${t.text ?? 'this tag'}”?`,
      message:
        'This permanently deletes the tag AND removes it from every backlog and task it is on. It cannot be ' +
        'undone.',
      confirmLabel: 'Delete tag',
      run: () => this.run(this.api.deleteTag(id), 'Tag deleted'),
    });
  }

  // ---- templates: DELETE-by-name then N × POST. Not transactional. ----

  newTemplate(): void { this.templateDraft.set({ originalName: null, name: '', tasks: '' }); }

  editTemplate(g: TemplateGroup): void {
    this.templateDraft.set({ originalName: g.name, name: g.name, tasks: g.taskNames.join('\n') });
  }

  cancelTemplate(): void { this.templateDraft.set(null); }

  /**
   * 🔴 NOT TRANSACTIONAL, and the failure is surfaced honestly — see `settings-templates.ts`.
   *
   * A rename is delete-the-old-name + write-the-new-name, which is two saves, so it is done as two explicit
   * steps: the old name is deleted only once the new one is safely written. Doing it the other way round
   * (delete old, then write new) would risk destroying the template and failing to recreate it.
   */
  saveTemplateDraft(): void {
    const draft = this.templateDraft();
    if (this.busy() || draft === null || draft.name.trim() === '') return;   // ← re-entrancy guard

    const name = draft.name.trim();
    const tasks = draft.tasks.split('\n').map(t => t.trim()).filter(t => t !== '');
    if (tasks.length === 0) return;

    this.busy.set(true);
    saveTemplate(this.api, name, tasks)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.templateDraft.set(null);

          // A rename leaves the OLD name behind — it was never touched. Remove it now that the new one exists.
          const old = draft.originalName;
          if (old !== null && old !== name) {
            this.run(this.api.deleteTemplateByName(old), 'Template saved');
            return;
          }

          this.toast.show('Template saved');
          this.load();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.templateDraft.set(null);
          // 🔴 PERSISTENT. "Deleted the old rows and wrote back 2 of 5" is not a two-second message.
          this.notice.set(describeError(err));
          this.load();
        },
      });
  }

  askDeleteTemplate(g: TemplateGroup): void {
    this.confirm.set({
      title: `Delete “${g.name}”?`,
      message: `This deletes all ${g.taskNames.length} task(s) in this template. Backlogs already created ` +
        'from it are not affected.',
      confirmLabel: 'Delete template',
      run: () => this.run(this.api.deleteTemplateByName(g.name), 'Template deleted'),
    });
  }

  // ---- default tasks: create / deactivate (ONE-WAY) / sync ----

  addDefaultTask(): void {
    const name = this.newDefaultTask().trim();
    if (this.busy() || name === '') return;   // ← re-entrancy guard

    // 🔴 The highest existing index + 1, NOT the count — soft deletes leave gaps. See `nextOrderIndex`.
    this.run(
      this.api.createDefaultTask(name, nextOrderIndex(this.defaultTasks())),
      'Default task added',
      () => this.newDefaultTask.set(''),
    );
  }

  /**
   * 🔴 REVERSIBLE now — the one-way door is gone. `getDefaultTasksAll()` (admin) lists inactive rows, so a
   * deactivated default task is visible and can be brought back. `setDefaultTaskActive` flips is_active in
   * either direction; the flag is passed through, never hard-coded. No confirm — this is a soft toggle,
   * exactly like the Team / PCA-contact toggles above.
   */
  toggleDefaultTaskActive(d: DefaultTaskDto): void {
    const id = d.id;
    if (this.busy() || typeof id !== 'number') return;   // ← re-entrancy guard
    const next = !(d.isActive === true);
    this.run(
      this.api.setDefaultTaskActive(id, next),
      next ? 'Default task activated' : 'Default task deactivated');
  }

  /** Pushes the default-task set into EXISTING backlogs. Bulk and cross-backlog — hence admin-gated. */
  syncDefaultTasks(): void {
    if (this.busy()) return;   // ← re-entrancy guard
    this.run(this.api.syncDefaultTasks(), 'Default tasks synced into existing backlogs');
  }

  // =======================================================================================================
  // TEAMS — teams, membership, PCA contacts
  // =======================================================================================================

  addTeam(): void {
    const name = this.newTeam().trim();
    if (this.busy() || name === '') return;   // ← re-entrancy guard

    // 🔴 `POST /api/teams` used to SKIP the TM-04 bootstrap. It no longer does: the endpoint now calls
    // `EnsureDefaultBacklogIdAsync(id)` + `SyncAsync()`, so a new team gets its own DEFAULT backlog with the
    // global default tasks under it. Without those a team was born broken — its members had nothing to log
    // Annual Leave / Meeting / Other against. Nobody had noticed only because no client could reach the route.
    this.run(this.api.createTeam(name), 'Team created — with its DEFAULT backlog', () => this.newTeam.set(''));
  }

  startRenameTeam(t: TeamDto): void {
    this.renamingTeamId.set(t.id ?? null);
    this.renameText.set(t.name ?? '');
  }

  saveRenameTeam(t: TeamDto): void {
    const id = t.id;
    const name = this.renameText().trim();
    if (this.busy() || typeof id !== 'number' || name === '') return;   // ← re-entrancy guard

    this.run(
      this.api.renameTeam(id, name, requireRowVersion(t.rowVersion)),   // CHECKED
      'Team renamed',
      () => this.renamingTeamId.set(null),
    );
  }

  /** SOFT — restorable through the same route, flag passed through verbatim. */
  toggleTeamActive(t: TeamDto): void {
    const id = t.id;
    if (this.busy() || typeof id !== 'number') return;   // ← re-entrancy guard
    const next = !(t.isActive === true);
    this.run(this.api.setTeamActive(id, next), next ? 'Team activated' : 'Team deactivated');
  }

  /** The membership READ is itself [ADMIN] — unlike every other read in the service. */
  openMembers(t: TeamDto): void {
    const id = t.id;
    if (typeof id !== 'number') return;

    this.membersTeamId.set(id);
    this.api.getTeamMembers(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ids => this.memberIds.set(new Set(ids)),
        error: (err: unknown) => { this.membersTeamId.set(null); this.fail(err); },
      });
  }

  closeMembers(): void { this.membersTeamId.set(null); }

  isMember(userId: number | undefined): boolean {
    return typeof userId === 'number' && this.memberIds().has(userId);
  }

  toggleMember(userId: number | undefined): void {
    if (typeof userId !== 'number') return;
    this.memberIds.update(set => {
      const next = new Set(set);
      if (next.has(userId)) next.delete(userId); else next.add(userId);
      return next;
    });
  }

  /**
   * 🔴 REPLACE-ALL. An id omitted from `userIds` is REMOVED from the team — so this must send the FULL
   * membership, not the one checkbox that was just ticked. That is why the overlay holds the whole set.
   */
  saveMembers(t: TeamDto): void {
    const id = t.id;
    if (this.busy() || typeof id !== 'number') return;   // ← re-entrancy guard

    this.run(
      this.api.setTeamMembers(id, [...this.memberIds()], requireRowVersion(t.rowVersion)),   // CHECKED
      'Members saved',
      () => this.membersTeamId.set(null),
    );
  }

  addContact(): void {
    const name = this.newContact().trim();
    if (this.busy() || name === '') return;   // ← re-entrancy guard
    this.run(this.api.createPcaContact(name), 'Contact added', () => this.newContact.set(''));
  }

  startRenameContact(c: PcaContactDto): void {
    this.renamingContactId.set(c.id ?? null);
    this.renameText.set(c.name ?? '');
  }

  saveRenameContact(c: PcaContactDto): void {
    const id = c.id;
    const name = this.renameText().trim();
    if (this.busy() || typeof id !== 'number' || name === '') return;   // ← re-entrancy guard

    this.run(
      this.api.renamePcaContact(id, name, requireRowVersion(c.rowVersion)),   // CHECKED
      'Contact renamed',
      () => this.renamingContactId.set(null),
    );
  }

  /**
   * 🔴 SOFT — and this is the one that is most tempting to get wrong, because the Tags list right next to it
   * hard-deletes. `setPcaContactActive(id, false)` flips a flag; the row survives, so HISTORICAL BACKLOGS
   * KEEP THE REFERENCE and still render the contact's name. A hard delete here would blank the PCA contact
   * off every backlog that ever used them.
   */
  toggleContactActive(c: PcaContactDto): void {
    const id = c.id;
    if (this.busy() || typeof id !== 'number') return;   // ← re-entrancy guard
    const next = !(c.isActive === true);
    this.run(this.api.setPcaContactActive(id, next), next ? 'Contact activated' : 'Contact deactivated');
  }

  // =======================================================================================================
  // OPS — the four /api/ops/* actions, plus the standup week archive (DR-09)
  // =======================================================================================================

  runBackup(): void {
    if (this.busy()) return;   // ← re-entrancy guard
    this.busy.set(true);
    this.api.runBackup().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => {
        this.busy.set(false);
        this.opsResult.set(`Backup written to ${r.value ?? 'the configured folder'}`);
        this.toast.show('Backup complete');
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  runExport(): void {
    if (this.busy()) return;   // ← re-entrancy guard
    this.busy.set(true);
    this.api.runExport().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => {
        this.busy.set(false);
        this.opsResult.set(`Export written to ${r.value ?? 'the configured folder'}`);
        this.toast.show('Export complete');
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  /** A POST that changes NOTHING. Showing this before `runRetention` is the entire point of it existing. */
  previewRetention(): void {
    if (this.busy()) return;   // ← re-entrancy guard
    this.busy.set(true);
    this.api.previewRetention().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: p => {
        this.busy.set(false);
        this.retentionPreview.set(p);
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  /**
   * 🔴 DESTRUCTIVE, and ASYNCHRONOUS.
   *
   * Gated behind BOTH a preview (the button does not exist until one has been taken — you cannot authorise
   * a deletion whose contents you have not seen) and a confirm.
   *
   * The route answers **202 Accepted**, not 204: the work is handed to a background job and the response
   * means "STARTED", not "finished". There is no completion signal, so we do not synthesise one and we do not
   * re-read — a re-read would show the OLD data and read as "nothing happened".
   */
  askRunRetention(): void {
    const preview = this.retentionPreview();
    if (preview === null) return;

    const total = (preview.months ?? []).reduce(
      (sum, m) => sum + (m.timeLogs ?? 0) + (m.backlogs ?? 0) + (m.tasks ?? 0)
        + (m.standupEntries ?? 0) + (m.standupIssues ?? 0),
      0);

    this.confirm.set({
      title: 'Run retention now?',
      message:
        `This PERMANENTLY DELETES roughly ${total} record(s) older than ${preview.cutoff ?? 'the cutoff'}, ` +
        'across time logs, backlogs, tasks and standup. It is archived first, then removed from the live ' +
        'database. This cannot be undone from the app.',
      confirmLabel: 'Delete permanently',
      run: () => {
        this.busy.set(true);
        this.api.runRetention().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
          next: () => {
            this.busy.set(false);
            // "Started", not "done". The route is 202 and there is no completion signal on it.
            this.opsResult.set(
              'Retention has STARTED. It runs in the background on the server, so the data will not ' +
              'disappear from this screen immediately — there is no completion signal to wait for.');
            this.retentionPreview.set(null);   // the preview is stale the moment the run begins
          },
          error: (err: unknown) => this.fail(err),
        });
      },
    });
  }

  /**
   * DR-09. Archives the standup week containing `date` to a file ON THE SERVER.
   *
   * 🔴 Returns a SERVER-SIDE PATH, not a download. The browser cannot open it. It is there to be SHOWN.
   */
  archiveStandupWeek(): void {
    const date = this.archiveDate();
    if (this.busy() || date === '') return;   // ← re-entrancy guard

    this.busy.set(true);
    this.api.archiveStandupWeek(date).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: file => {
        this.busy.set(false);
        this.opsResult.set(`Standup week archived on the server: ${file.path ?? '(no path returned)'}`);
        this.toast.show('Week archived');
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  // =======================================================================================================
  // The confirm dialog, and the one write path everything funnels through.
  // =======================================================================================================

  confirmYes(): void {
    const pending = this.confirm();
    if (pending === null || this.busy()) return;
    this.confirm.set(null);
    pending.run();
  }

  confirmNo(): void { this.confirm.set(null); }

  /** Fire a write, reload on success, and never let an error escape a template-bound handler. */
  private run(call: Observable<unknown>, done: string, after?: () => void): void {
    this.busy.set(true);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        after?.();
        this.toast.show(done);
        this.load();
      },
      error: (err: unknown) => this.fail(err),
    });
  }

  /**
   * Every async path ends here and NOTHING re-throws — an unhandled rejection out of a template-bound
   * handler leaves `busy` stuck true and the whole screen dead behind disabled buttons.
   *
   * A 409 means the row moved under an open editor, so the version we hold is stale: re-read.
   */
  private fail(err: unknown): void {
    this.busy.set(false);
    this.toast.show(describeError(err));
    if (err instanceof HttpErrorResponse && err.status === 409) this.load();
  }
}

/** Turn whatever came back into a sentence the admin can act on. */
export function describeError(err: unknown): string {
  // The only error that knows a multi-call write half-succeeded. It must win.
  if (err instanceof TemplateSaveError) return err.message;

  if (err instanceof HttpErrorResponse) {
    if (err.status === 409) return 'Someone else changed this while you had it open. Reloading — try again.';
    if (err.status === 403) return 'You are not an administrator.';
    if (err.status === 400) return readMessage(err.error) ?? 'The server rejected that.';
    if (err.status === 0) return 'Cannot reach the server.';
    return `The server returned ${err.status}.`;
  }

  if (err instanceof Error) return err.message;
  return 'Something went wrong.';
}

/** `ValidationBody` is `{ message }`. Read it defensively — this is the network boundary. */
function readMessage(body: unknown): string | null {
  if (typeof body === 'object' && body !== null && 'message' in body) {
    const message = (body as { message: unknown }).message;
    if (typeof message === 'string' && message !== '') return message;
  }
  return null;
}
