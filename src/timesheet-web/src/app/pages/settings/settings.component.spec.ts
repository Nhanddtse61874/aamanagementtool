import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { of } from 'rxjs';

import {
  DefaultTaskDto, HolidayDto, PcaContactDto, RetentionPreview, SettingDto, TagDto, TaskTemplateDto, TeamDto,
  UserDto,
} from '../../api/models';
import { ConfirmDialogComponent } from '../../core/confirm-dialog/confirm-dialog.component';
import { ThemeService } from '../../services/theme.service';
import { ToastService } from '../../services/toast.service';
import { WorklogService } from '../../services/worklog.service';
import { SettingsComponent } from './settings.component';

/**
 * The Settings screen. Every panel here was a mockup: `storageFields` was an array of empty strings, the
 * warning-days box was `<input value="3">` bound to nothing, the calendar was HARD-CODED to July 2026 with no
 * prev/next handler, and every button called `notify('Saved')` — a toast, and no request.
 *
 * The first block below is the one that would be easiest to skip and is the most important: it asserts that
 * five whole sections are GONE. They cannot be "wired up later" — every one of them would write through
 * `IAppConfig.Set*`, which is process-wide CROSS-USER state on a server, and one of them (`SetDbPath`)
 * repoints the entire server's database.
 */

const TAGS: TagDto[] = [{ id: 1, text: 'Urgent', icon: '🔥', color: '#B91C1C', rowVersion: 3 }];
const TEMPLATES: TaskTemplateDto[] = [
  { id: 1, templateName: 'Sprint', taskName: 'Standup', orderIndex: 0 },
  { id: 2, templateName: 'Sprint', taskName: 'Retro', orderIndex: 1 },
];
const CONTACTS: PcaContactDto[] = [{ id: 5, name: 'Ana', isActive: true, rowVersion: 2 }];
const TEAMS: TeamDto[] = [{ id: 7, name: 'Core', isActive: true, rowVersion: 4 }];
const DEFAULTS: DefaultTaskDto[] = [{ id: 9, taskName: 'Annual Leave', isActive: true, orderIndex: 2 }];
const USERS: UserDto[] = [
  { id: 1, name: 'Nhan', isActive: true }, { id: 2, name: 'Dana', isActive: true },
];
const HOLIDAYS: HolidayDto[] = [{ date: '2026-07-06', description: 'Test holiday' }];

const PREVIEW: RetentionPreview = {
  cutoff: '2026-04-01',
  months: [{ month: '2026-01', timeLogs: 12, backlogs: 2, tasks: 5, standupEntries: 3, standupIssues: 1 }],
};

describe('SettingsComponent', () => {
  let api: jasmine.SpyObj<WorklogService>;
  let fixture: ComponentFixture<SettingsComponent>;

  function text(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  function buttons(label: string): HTMLButtonElement[] {
    return fixture.debugElement.queryAll(By.css('button'))
      .map(d => d.nativeElement as HTMLButtonElement)
      .filter(b => (b.textContent ?? '').trim().startsWith(label));
  }

  function tab(label: string): void {
    buttons(label)[0].click();
    fixture.detectChanges();
  }

  /** The screen renders a `<app-confirm-dialog>` and nothing has happened yet. Say yes. */
  function confirmYes(): void {
    const dialog = fixture.debugElement.query(By.directive(ConfirmDialogComponent));
    expect(dialog).withContext('a confirm dialog must be showing').not.toBeNull();
    dialog.componentInstance.confirm.emit();
    fixture.detectChanges();
  }

  beforeEach(() => {
    api = jasmine.createSpyObj<WorklogService>('WorklogService', [
      'getSetting', 'setSetting', 'getTagList', 'createTag', 'updateTag', 'deleteTag',
      'getTemplateList', 'createTemplate', 'deleteTemplateByName',
      'getPcaContactsAll', 'createPcaContact', 'renamePcaContact', 'setPcaContactActive',
      'getTeamsAll', 'createTeam', 'renameTeam', 'setTeamActive', 'getTeamMembers', 'setTeamMembers',
      'getDefaultTasks', 'createDefaultTask', 'setDefaultTaskActive', 'syncDefaultTasks',
      'getHolidayList', 'upsertHoliday', 'deleteHoliday', 'getUsersActive',
      'runBackup', 'runExport', 'previewRetention', 'runRetention', 'archiveStandupWeek',
    ]);

    api.getSetting.and.returnValue(of({ key: 'chua_log_n_days', value: '5' } satisfies SettingDto));
    api.getTagList.and.returnValue(of(TAGS));
    api.getTemplateList.and.returnValue(of(TEMPLATES));
    api.getPcaContactsAll.and.returnValue(of(CONTACTS));
    api.getTeamsAll.and.returnValue(of(TEAMS));
    api.getDefaultTasks.and.returnValue(of(DEFAULTS));
    api.getUsersActive.and.returnValue(of(USERS));
    api.getHolidayList.and.returnValue(of(HOLIDAYS));
    api.getTeamMembers.and.returnValue(of([1]));

    api.setSetting.and.returnValue(of(void 0));
    api.deleteTag.and.returnValue(of(void 0));
    api.setPcaContactActive.and.returnValue(of(void 0));
    api.setTeamMembers.and.returnValue(of({ rowVersion: 5 }));
    api.setDefaultTaskActive.and.returnValue(of(void 0));
    api.createDefaultTask.and.returnValue(of(DEFAULTS[0]));
    api.syncDefaultTasks.and.returnValue(of(void 0));
    api.upsertHoliday.and.returnValue(of(void 0));
    api.deleteHoliday.and.returnValue(of(void 0));
    api.previewRetention.and.returnValue(of(PREVIEW));
    api.runRetention.and.returnValue(of(void 0));
    api.runBackup.and.returnValue(of({ value: '/srv/backups/db.bak' }));

    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [{ provide: WorklogService, useValue: api }, ToastService],
    });

    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
  });

  // ══ the five sections that must NOT exist ═════════════════════════════════════════════════════════

  /**
   * 🔴 DB path · archive path · both export roots · backup folder/auto/keep-count · retention enable+months.
   *
   * Each would have to write through `IAppConfig.Set*` — "a process-wide singleton with ten setters; on a
   * server every one of them is CROSS-USER state, and SetDbPath repoints the whole server's database."
   * They belong in `appsettings.json` on the host. Shipping them as web inputs would let any admin repoint
   * the production database from a browser tab.
   *
   * MUTATION-CHECK: put the `storageFields` array and its `@for` block back and this goes red.
   */
  it('has NO host-configuration inputs anywhere — no DB path, no export roots, no backup folder', () => {
    // Visit every tab; none of them may offer a path input.
    for (const label of ['General', 'Workflow', 'Teams', 'Operations']) {
      tab(label);

      expect(text()).not.toContain('Database file');
      expect(text()).not.toContain('SharePoint');
      expect(text()).not.toContain('Browse');
      expect(fixture.debugElement.queryAll(By.css('input[placeholder*="older" i]')).length).toBe(0);
      expect(fixture.debugElement.queryAll(By.css('.input--mono')).length).toBe(0);
    }
  });

  it('has no "Storage" or "Data" tab left to put them back on', () => {
    const tabs = fixture.debugElement.queryAll(By.css('.stabs button'))
      .map(d => ((d.nativeElement as HTMLElement).textContent ?? '').trim());

    expect(tabs).toEqual(['General', 'Workflow', 'Teams', 'Operations']);
  });

  it('does NOT offer to enable retention or set its month window — that is server config', () => {
    tab('Operations');

    expect(text()).not.toContain('Enable retention');
    expect(text()).not.toContain('Keep last');
  });

  /**
   * 🔴 DARK MODE IS NOT ONE OF THEM, and that is the whole distinction. It is a PER-USER preference persisted
   * to localStorage by ThemeService — it never touches the server, so it cannot leak across users.
   */
  it('KEEPS dark mode and the accent picker — they are per-user, not cross-user', () => {
    expect(text()).toContain('Dark mode');
    expect(fixture.debugElement.queryAll(By.css('.swatch')).length).toBeGreaterThan(0);
  });

  // ══ the dark toggle misfire (UAT round-1) ═════════════════════════════════════════════════════════
  //
  // 🔴 UAT: "clicking around in Settings auto-toggles dark mode without touching the dark switch." The fix
  // makes the dark toggle an ISOLATED control (a real <button role="switch">), so ONLY the switch flips it —
  // clicking the caption text or the row can never toggle the theme.

  /** The regression: clicking the "Dark mode" CAPTION (not the switch) must never flip the theme. */
  it('clicking the "Dark mode" caption does NOT toggle the theme — only the switch control does', () => {
    const theme = TestBed.inject(ThemeService);
    const toggle = spyOn(theme, 'toggleDark').and.callThrough();

    const caption = fixture.debugElement.queryAll(By.css('.toggle-row .s-strong'))
      .find(d => ((d.nativeElement as HTMLElement).textContent ?? '').trim() === 'Dark mode');
    expect(caption).withContext('the Dark mode caption should render').toBeTruthy();

    (caption!.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(toggle).not.toHaveBeenCalled();
  });

  /** The switch itself is the one control that DOES flip it — the fix must not break that. */
  it('clicking the dark switch itself toggles the theme exactly once', () => {
    const theme = TestBed.inject(ThemeService);
    const toggle = spyOn(theme, 'toggleDark');   // no callThrough: do not mutate real theme state

    const sw = fixture.debugElement.query(By.css('.toggle-row .switch'));
    expect(sw).withContext('the switch should render').toBeTruthy();

    (sw.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(toggle).toHaveBeenCalledTimes(1);
  });

  /** The structural guarantee that makes the misfire impossible: an isolated button, not a <label>-span. */
  it('renders the dark toggle as an isolated <button role="switch">, never a <label>-wrapped span', () => {
    const sw = fixture.debugElement.query(By.css('.toggle-row .switch')).nativeElement as HTMLElement;
    expect(sw.tagName).toBe('BUTTON');
    expect(sw.getAttribute('role')).toBe('switch');

    // The row is no longer a <label> — a <label> is the element that forwards a stray click onto a control.
    const row = fixture.debugElement.query(By.css('.toggle-row')).nativeElement as HTMLElement;
    expect(row.tagName).not.toBe('LABEL');
  });

  // ══ the warning window (SET-02) ═══════════════════════════════════════════════════════════════════

  /**
   * `fakeAsync` + `tick()`: NgModel writes its value into the DOM on a MICROTASK (`resolvedPromise.then()`
   * inside `_updateValue`), so `detectChanges()` alone leaves `input.value` empty.
   *
   * 🔴 And the fixture is built INSIDE the fake zone. The one from `beforeEach` was created outside it, and
   * `tick()` cannot flush a microtask that was scheduled before the zone existed — the box would stay empty
   * no matter how long we ticked.
   */
  it('loads the warning window from the real setting, and writes it back under the key the API reads',
    fakeAsync(() => {
      const f = TestBed.createComponent(SettingsComponent);
      f.detectChanges();
      tick();
      f.detectChanges();

      expect(api.getSetting).toHaveBeenCalledWith('chua_log_n_days');

      const input = f.debugElement.query(By.css('input[type="number"]')).nativeElement as HTMLInputElement;
      expect(input.value).toBe('5');                     // ← from the API, not the hard-coded 3

      input.value = '7';
      input.dispatchEvent(new Event('input'));
      tick();
      f.detectChanges();

      f.debugElement.queryAll(By.css('button'))
        .map(d => d.nativeElement as HTMLButtonElement)
        .filter(b => (b.textContent ?? '').trim().startsWith('Save'))[0]
        .click();

      expect(api.setSetting).toHaveBeenCalledOnceWith('chua_log_n_days', '7');

      tick(2000);   // drain ToastService's 2s timer, or fakeAsync fails on "timer(s) still in the queue"
    }));

  it('falls back to 3 when the key is UNSET — a null value is a 200, not an error', fakeAsync(() => {
    // Every key is unset on a fresh database. The route answers 200 with value: null. Treating that as an
    // error would leave the screen dead on any new install.
    api.getSetting.and.returnValue(of({ key: 'chua_log_n_days', value: null }));

    const f = TestBed.createComponent(SettingsComponent);
    f.detectChanges();
    tick();
    f.detectChanges();

    const input = f.debugElement.query(By.css('input[type="number"]')).nativeElement as HTMLInputElement;
    expect(input.value).toBe('3');
  }));

  // ══ the holiday calendar ══════════════════════════════════════════════════════════════════════════

  it('toggling a holiday ON upserts, and toggling one OFF deletes — TWO routes, not one toggle', () => {
    const days = fixture.debugElement.queryAll(By.css('.day'));

    // 2026-07-06 is already a holiday in the fixture, so clicking it must DELETE.
    const holiday = days.find(d => (d.nativeElement as HTMLElement).classList.contains('holiday'));
    expect(holiday).withContext('the seeded holiday should be rendered').toBeDefined();
    (holiday?.nativeElement as HTMLElement).click();
    fixture.detectChanges();

    expect(api.deleteHoliday).toHaveBeenCalledOnceWith('2026-07-06');
    expect(api.upsertHoliday).not.toHaveBeenCalled();
  });

  /**
   * 🔴 The mockup did `if (c.weekend) return;`. WPF does not block this, and `POST /api/holidays` has NO
   * weekday guard — it upserts whatever date it is given. A public holiday that falls on a Saturday is still
   * a public holiday.
   */
  it('lets an admin mark a WEEKEND as a holiday — the mockup wrongly refused', () => {
    const weekend = fixture.debugElement.queryAll(By.css('.day.weekend'))[0];
    const button = weekend.nativeElement as HTMLButtonElement;

    expect(button.disabled).toBeFalse();
    button.click();
    fixture.detectChanges();

    expect(api.upsertHoliday).toHaveBeenCalledTimes(1);
  });

  it('the calendar has working prev/next — it is no longer nailed to July 2026', () => {
    const label = (): string =>
      ((fixture.debugElement.query(By.css('.cal__head span')).nativeElement as HTMLElement).textContent ?? '')
        .trim();

    const start = label();
    buttons('▶')[0].click();
    fixture.detectChanges();

    expect(label()).not.toBe(start);

    buttons('◀')[0].click();
    fixture.detectChanges();
    expect(label()).toBe(start);      // and back again
  });

  // ══ hard delete vs soft delete — the pair that is easiest to confuse ══════════════════════════════

  /** 🔴 HARD, and it cascades BacklogTags + TaskTags in one transaction. It must be confirmed. */
  it('deleting a tag asks first, then HARD-deletes', () => {
    tab('Workflow');
    buttons('Delete')[1].click();     // the tag's Delete (the template's is [0])
    fixture.detectChanges();

    expect(api.deleteTag).not.toHaveBeenCalled();      // nothing happens on the click alone

    confirmYes();
    expect(api.deleteTag).toHaveBeenCalledOnceWith(1);
  });

  /**
   * 🔴 SOFT — the opposite of the Tags list right next to it. The row survives, so historical backlogs keep
   * the reference. Calling `deleteTag`-style semantics here would blank the contact off every backlog.
   */
  it('deactivating a PCA contact is SOFT — setPcaContactActive(id, false), and nothing is destroyed', () => {
    tab('Teams');
    buttons('Deactivate')[1].click();   // teams' Deactivate is [0]; the contact's is [1]
    fixture.detectChanges();

    expect(api.setPcaContactActive).toHaveBeenCalledOnceWith(5, false);
  });

  // ══ default tasks: the one-way door ══════════════════════════════════════════════════════════════

  /**
   * 🔴 `GET /api/default-tasks` is ACTIVE-ONLY and there is no `GetAllAsync`. So a deactivated default task
   * can never be listed again and therefore never re-activated. A TOGGLE would be a lie — it implies a
   * round-trip that does not exist. It is rendered as a permanent Remove, and it is confirmed.
   */
  it('a default task offers a permanent REMOVE, never a toggle it cannot round-trip', () => {
    tab('Workflow');

    // The affordance is a REMOVE, and the screen says out loud that it is one-way.
    expect(buttons('Remove').length).toBe(1);
    expect(text()).toContain('there is no way to list it again or bring it back');

    buttons('Remove')[0].click();
    fixture.detectChanges();
    expect(api.setDefaultTaskActive).not.toHaveBeenCalled();   // it asks first

    // And the dialog states the consequence that makes this different from every other soft delete here.
    const dialog = fixture.debugElement.query(By.directive(ConfirmDialogComponent));
    expect(dialog.componentInstance.message()).toContain('there is no way to see it again');

    confirmYes();
    expect(api.setDefaultTaskActive).toHaveBeenCalledOnceWith(9, false);
  });

  it('adding a default task appends past the highest INDEX, not the count — soft deletes leave gaps', () => {
    tab('Workflow');

    const input = fixture.debugElement.query(By.css('input[placeholder="Annual Leave"]'))
      .nativeElement as HTMLInputElement;
    input.value = 'Meeting';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    buttons('Add')[0].click();

    // The one existing default task sits at orderIndex 2 (there is a gap where a removed one used to be).
    // A count-based append would send 1 and TIE with nothing — but on a real list it would tie with a live row.
    expect(api.createDefaultTask).toHaveBeenCalledOnceWith('Meeting', 3);
  });

  // ══ teams ════════════════════════════════════════════════════════════════════════════════════════

  /** 🔴 REPLACE-ALL: an unticked user is REMOVED, so the FULL set must ship, not just the box just clicked. */
  it('saving members sends the WHOLE membership, not the one checkbox that changed', () => {
    tab('Teams');
    buttons('Members')[0].click();
    fixture.detectChanges();

    // Nhan (1) is already a member. Tick Dana (2) as well.
    const boxes = fixture.debugElement.queryAll(By.css('.memgrid input[type="checkbox"]'));
    (boxes[1].nativeElement as HTMLInputElement).click();
    fixture.detectChanges();

    buttons('Save members')[0].click();

    // Both ids — and the team's own rowVersion, because this is a CHECKED write.
    expect(api.setTeamMembers).toHaveBeenCalledOnceWith(7, [1, 2], 4);
  });

  // ══ ops ══════════════════════════════════════════════════════════════════════════════════════════

  /**
   * 🔴 DESTRUCTIVE. The run button does not even EXIST until a preview has been taken — you cannot authorise
   * a deletion whose contents you have not seen — and then it still asks.
   */
  it('retention cannot be run until it has been previewed, and then it still confirms', () => {
    tab('Operations');

    expect(buttons('Run retention now').length).withContext('no run button before a preview').toBe(0);

    buttons('Preview')[0].click();
    fixture.detectChanges();

    expect(api.previewRetention).toHaveBeenCalled();
    expect(text()).toContain('2026-04-01');     // the cutoff is shown
    expect(text()).toContain('12');             // and the counts

    buttons('Run retention now')[0].click();
    fixture.detectChanges();
    expect(api.runRetention).not.toHaveBeenCalled();   // still not yet

    confirmYes();
    expect(api.runRetention).toHaveBeenCalledTimes(1);
  });

  /**
   * The route answers 202 Accepted — "started", not "finished". A screen that said "Retention complete" would
   * be claiming something the API never told it.
   */
  it('says retention has STARTED, not that it finished — the route is 202 with no completion signal', () => {
    tab('Operations');
    buttons('Preview')[0].click();
    fixture.detectChanges();
    buttons('Run retention now')[0].click();
    fixture.detectChanges();
    confirmYes();

    expect(text()).toContain('STARTED');
    expect(text()).not.toContain('Retention complete');
  });

  it('a backup shows where the server wrote it', () => {
    tab('Operations');
    buttons('Back up now')[0].click();
    fixture.detectChanges();

    expect(api.runBackup).toHaveBeenCalled();
    expect(text()).toContain('/srv/backups/db.bak');
  });
});
