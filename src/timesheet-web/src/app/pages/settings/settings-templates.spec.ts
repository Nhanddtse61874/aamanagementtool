import { Observable, of, throwError } from 'rxjs';
import { TaskTemplateDto } from '../../api/models';
import {
  TemplateApi, TemplateSaveError, groupTemplates, nextOrderIndex, saveTemplate,
} from './settings-templates';

/** The flat rows the API returns — deliberately out of order, and with two templates interleaved. */
const ROWS: TaskTemplateDto[] = [
  { id: 3, templateName: 'Onboarding', taskName: 'Laptop', orderIndex: 1 },
  { id: 1, templateName: 'Sprint', taskName: 'Standup', orderIndex: 0 },
  { id: 2, templateName: 'Onboarding', taskName: 'Accounts', orderIndex: 0 },
  { id: 4, templateName: 'Sprint', taskName: 'Retro', orderIndex: 1 },
];

class FakeApi implements TemplateApi {
  readonly calls: string[] = [];
  failDelete = false;
  /** Fail the POST whose taskName matches. */
  failCreate: string | null = null;

  deleteTemplateByName(templateName: string): Observable<void> {
    this.calls.push(`delete(${templateName})`);
    return this.failDelete ? throwError(() => new Error('500')) : of(void 0);
  }

  createTemplate(body: { templateName: string; taskName: string; orderIndex: number }): Observable<unknown> {
    this.calls.push(`create(${body.templateName},${body.taskName},${body.orderIndex})`);
    return body.taskName === this.failCreate ? throwError(() => new Error('500')) : of({});
  }
}

describe('groupTemplates', () => {
  it('folds the flat rows into named groups, each ordered by orderIndex', () => {
    expect(groupTemplates(ROWS)).toEqual([
      { name: 'Onboarding', taskNames: ['Accounts', 'Laptop'] },
      { name: 'Sprint', taskNames: ['Standup', 'Retro'] },
    ]);
  });

  it('drops a row with no template name rather than collecting it under ""', () => {
    // There is no name to DELETE BY, so such a group could never be edited or removed. It is not renderable.
    const rows: TaskTemplateDto[] = [...ROWS, { id: 9, templateName: null, taskName: 'Orphan', orderIndex: 0 }];

    expect(groupTemplates(rows).map(g => g.name)).toEqual(['Onboarding', 'Sprint']);
  });

  it('returns nothing for no rows', () => {
    expect(groupTemplates([])).toEqual([]);
  });
});

describe('saveTemplate', () => {
  it('deletes by name FIRST, then posts each row in order — one call per task', done => {
    const api = new FakeApi();

    saveTemplate(api, 'Sprint', ['Standup', 'Retro', 'Demo']).subscribe(() => {
      expect(api.calls).toEqual([
        'delete(Sprint)',
        'create(Sprint,Standup,0)',
        'create(Sprint,Retro,1)',
        'create(Sprint,Demo,2)',
      ]);
      done();
    });
  });

  it('reindexes from POSITION, so the saved order is the order on screen', done => {
    const api = new FakeApi();

    saveTemplate(api, 'Sprint', ['Demo', 'Standup']).subscribe(() => {
      expect(api.calls).toEqual(['delete(Sprint)', 'create(Sprint,Demo,0)', 'create(Sprint,Standup,1)']);
      done();
    });
  });

  it('trims and drops blank task names — an empty row is not a task', done => {
    const api = new FakeApi();

    saveTemplate(api, 'Sprint', ['  Standup  ', '', '   ', 'Retro']).subscribe(() => {
      expect(api.calls).toEqual(['delete(Sprint)', 'create(Sprint,Standup,0)', 'create(Sprint,Retro,1)']);
      done();
    });
  });

  /**
   * 🔴 THE TEST THIS MODULE EXISTS FOR.
   *
   * There is no "update template" route: an edit is DELETE-by-name followed by N POSTs, and those N+1 calls
   * are NOT atomic. If a POST fails half-way, the old rows are ALREADY GONE and the template is live, real,
   * and missing tasks. "Save failed" would be a lie — the save half-SUCCEEDED, and the thing the admin is now
   * looking at is silently wrong. Anyone applying it to a backlog gets a truncated task list and no warning.
   *
   * MUTATION-CHECK: make `saveTemplate` swallow the error (or report a bare "failed" with no counters) and
   * this goes red on `created`, on `deleted`, and on the message.
   */
  it('a POST failing half-way reports that the template is INCOMPLETE and how far it got', done => {
    const api = new FakeApi();
    api.failCreate = 'Demo';   // the third of three

    saveTemplate(api, 'Sprint', ['Standup', 'Retro', 'Demo']).subscribe({
      error: (err: TemplateSaveError) => {
        expect(err).toBeInstanceOf(TemplateSaveError);
        expect(err.deleted).toBeTrue();     // ← the old rows are GONE and are not coming back
        expect(err.created).toBe(2);        // ← two of three landed
        expect(err.total).toBe(3);
        expect(err.message).toContain('INCOMPLETE');
        expect(err.message).toContain('2 of 3');

        // And it did NOT charge on to a fourth call after the failure.
        expect(api.calls).toEqual([
          'delete(Sprint)', 'create(Sprint,Standup,0)', 'create(Sprint,Retro,1)', 'create(Sprint,Demo,2)',
        ]);
        done();
      },
    });
  });

  /**
   * The opposite case, and it must NOT read the same. If the DELETE itself failed, nothing was destroyed —
   * the old template is intact. Telling the admin it is "incomplete" would send them to repair something
   * that is not broken.
   */
  it('a failed DELETE reports that NOTHING changed — the old tasks are intact', done => {
    const api = new FakeApi();
    api.failDelete = true;

    saveTemplate(api, 'Sprint', ['Standup']).subscribe({
      error: (err: TemplateSaveError) => {
        expect(err.deleted).toBeFalse();
        expect(err.created).toBe(0);
        expect(err.message).toContain('NOT changed');
        expect(err.message).toContain('still intact');
        expect(api.calls).toEqual(['delete(Sprint)']);   // no POST was attempted
        done();
      },
    });
  });

  it('creating a NEW template is the same path — the delete simply matches nothing', done => {
    const api = new FakeApi();

    saveTemplate(api, 'Brand New', ['First']).subscribe(() => {
      expect(api.calls).toEqual(['delete(Brand New)', 'create(Brand New,First,0)']);
      done();
    });
  });
});

describe('nextOrderIndex', () => {
  /**
   * 🔴 The highest existing index PLUS ONE — never the row COUNT.
   *
   * Default tasks are SOFT-deleted (`is_active = 0`) and the read filters on `is_active = 1`, while
   * `order_index` is left alone. So the live rows have GAPS: deactivate index 1 of [0,1,2] and the survivors
   * are [0, 2]. A count-based append returns 2 and TIES with the existing row — and `ORDER BY order_index`
   * resolves a tie arbitrarily, so the new task lands in a random place.
   *
   * MUTATION-CHECK: change the body to `items.length` and this goes red.
   */
  it('appends past the highest INDEX, not the count — soft deletes leave gaps', () => {
    expect(nextOrderIndex([{ orderIndex: 0 }, { orderIndex: 2 }])).toBe(3);   // NOT 2, which would tie
  });

  it('appends at 0 for an empty list', () => {
    expect(nextOrderIndex([])).toBe(0);
  });

  it('treats a missing orderIndex as absent rather than as 0', () => {
    expect(nextOrderIndex([{ orderIndex: 5 }, {}])).toBe(6);
  });
});
