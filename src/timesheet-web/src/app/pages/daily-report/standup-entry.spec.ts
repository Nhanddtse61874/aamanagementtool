import { HttpErrorResponse } from '@angular/common/http';

import { BacklogListItemDto, StandupIssueDto } from '../../api/models';
import { EntryDraft } from './standup-entry';
import {
  apiError, emptyDraft, hasSolution, isConflict, pickableBacklogs, toCreateRequest, toIssueUpdateBody,
  validateDraft,
} from './standup-entry';

function draft(over: Partial<EntryDraft> = {}): EntryDraft {
  return { ...emptyDraft('today'), backlogCode: 'ARCS-1001', taskText: 'Design schema', ...over };
}

describe('standup-entry', () => {
  describe('emptyDraft', () => {
    it('starts on Todo — StandupStatus.All[0], as WPF does', () => {
      expect(emptyDraft('today').status).toBe('Todo');
    });

    it('starts AD-HOC: no backlog id until one is picked', () => {
      expect(emptyDraft('today').backlogId).toBeNull();
    });
  });

  /** The client mirror of `StandupService.ValidateDraft` — its four throws, in its order. */
  describe('validateDraft', () => {
    it('accepts a complete draft', () => {
      expect(validateDraft(draft())).toBeNull();
    });

    it('rejects a blank backlog code', () => {
      expect(validateDraft(draft({ backlogCode: '   ' }))).toBe('Backlog code is required.');
    });

    it('rejects blank task text', () => {
      expect(validateDraft(draft({ taskText: '' }))).toBe('Task text is required.');
    });

    it('rejects an invalid status', () => {
      // 'In Progress' is the plausible-looking wrong one: the wire value is 'In-process'.
      const bad = { ...draft(), status: 'In Progress' } as unknown as EntryDraft;
      expect(validateDraft(bad)).toContain('Invalid status');
    });

    it('rejects an invalid section', () => {
      const bad = { ...draft(), section: 'tomorrow' } as unknown as EntryDraft;
      expect(validateDraft(bad)).toContain('Invalid section');
    });

    /** Both are nullable server-side. A screen that demanded them would be inventing a rule. */
    it('does NOT require a deadline or a description — both are optional', () => {
      expect(validateDraft(draft({ deadline: null, description: '' }))).toBeNull();
    });
  });

  describe('toCreateRequest', () => {
    it('sends the day on screen as workDate', () => {
      expect(toCreateRequest(draft(), '2026-07-12').workDate).toBe('2026-07-12');
    });

    /**
     * 🔴 DR-03. An ad-hoc code carries NO backlog id. Send one and you are asserting the backlog exists;
     * `ResolveBacklogIdAsync` would keep it, and the row would point at a backlog the user never chose.
     */
    it('keeps backlogId NULL for an ad-hoc code', () => {
      expect(toCreateRequest(draft({ backlogId: null }), '2026-07-12').backlogId).toBeNull();
    });

    it('sends the backlog id when one was picked', () => {
      expect(toCreateRequest(draft({ backlogId: 42 }), '2026-07-12').backlogId).toBe(42);
    });

    /**
     * 🔴 An empty `<input type="date">` yields `''`, and `''` binds to `DateOnly?` as a 400 — not as "no
     * deadline". The deadline is OPTIONAL, so the empty case must reach the server as a real null.
     */
    it('sends an empty deadline as NULL, never as an empty string', () => {
      const body = toCreateRequest(draft({ deadline: null }), '2026-07-12');
      expect(body.deadline).toBeNull();
    });

    it('sends a chosen deadline through', () => {
      expect(toCreateRequest(draft({ deadline: '2026-07-20' }), '2026-07-12').deadline).toBe('2026-07-20');
    });

    it('trims the code and the task, as the server does', () => {
      const body = toCreateRequest(draft({ backlogCode: '  ARCS-1  ', taskText: '  Do it  ' }), '2026-07-12');
      expect(body.backlogCode).toBe('ARCS-1');
      expect(body.taskText).toBe('Do it');
    });
  });

  /**
   * ═════════════════════════════════════════════════════════════════════════════════════════════════════
   * 🔴 THE LOAD-BEARING ONE. `PUT /api/standup/entries/{e}/issues/{i}` is a WHOLE-RECORD OVERWRITE:
   *
   *     var updated = existingIssue with {
   *         IssueText = req.IssueText, SolutionText = req.SolutionText, Status = req.Status,
   *     };
   *
   * and `UpdateIssueCheckedAsync` then throws `ArgumentException` -> 400 on a blank `IssueText` or a status
   * outside `StandupIssueStatus.All`. Every field on the request type is OPTIONAL, so a body assembled from
   * the solution box alone — the only thing the UI lets you change — omits both, COMPILES CLEAN, and 400s on
   * the happy path every single time.
   *
   * These two tests are the ones that go red if anyone "simplifies" the body down to what the form owns.
   * ═════════════════════════════════════════════════════════════════════════════════════════════════════
   */
  describe('toIssueUpdateBody', () => {
    const issue: StandupIssueDto = {
      id: 5, entryId: 9, issueText: 'DB deadlock on save', solutionText: null,
      status: 'open', orderIndex: 0, rowVersion: 7,
    };

    it('🔴 ROUND-TRIPS issueText from the loaded issue — omitting it is a guaranteed 400', () => {
      const body = toIssueUpdateBody(issue, 'Added an index', 'resolved');
      expect(body.issueText).toBe('DB deadlock on save');
    });

    it('🔴 ROUND-TRIPS a status the server will accept — a null status is a guaranteed 400', () => {
      const body = toIssueUpdateBody(issue, 'Added an index', 'resolved');
      expect(body.status).toBe('resolved');
    });

    it('carries the rowVersion as expectedVersion — this is the CHECKED write that can 409', () => {
      expect(toIssueUpdateBody(issue, 'x', 'open').expectedVersion).toBe(7);
    });

    it('sends a blank solution as NULL (the server NullIfBlank-es it: "pending discussion")', () => {
      expect(toIssueUpdateBody(issue, '   ', 'open').solutionText).toBeNull();
    });

    it('trims a real solution', () => {
      expect(toIssueUpdateBody(issue, '  Added an index  ', 'resolved').solutionText).toBe('Added an index');
    });
  });

  describe('hasSolution', () => {
    it('is false for null, empty and whitespace — mirrors StandupIssue.HasSolution', () => {
      expect(hasSolution({ solutionText: null })).toBeFalse();
      expect(hasSolution({ solutionText: '' })).toBeFalse();
      expect(hasSolution({ solutionText: '   ' })).toBeFalse();
    });

    it('is true once there is real text', () => {
      expect(hasSolution({ solutionText: 'Added an index' })).toBeTrue();
    });
  });

  /**
   * 🔴 `GET /api/backlogs` returns DEFAULT — Log Work needs it, so the route cannot drop it. The standup
   * picker must, client-side, exactly as `DailyReportViewModel.FillPicker` does.
   */
  describe('pickableBacklogs', () => {
    const list: BacklogListItemDto[] = [
      { id: 1, backlogCode: 'ARCS-1001', project: 'ARCS' },
      { id: 99, backlogCode: 'DEFAULT', project: 'Recurring' },
      { id: 2, backlogCode: 'ARCS-1002', project: 'ARCS' },
    ];

    it('excludes the hidden DEFAULT backlog', () => {
      expect(pickableBacklogs(list).map(b => b.backlogCode)).toEqual(['ARCS-1001', 'ARCS-1002']);
    });

    it('keeps everything else', () => {
      expect(pickableBacklogs(list).length).toBe(2);
    });
  });

  describe('apiError', () => {
    it('surfaces ValidationBody.error — the server says WHY, and "the day is locked" is worth reading', () => {
      const err = new HttpErrorResponse({
        status: 400,
        error: { error: 'Cannot add: the day is locked (editable only today or yesterday).' },
      });
      expect(apiError(err, 'fallback')).toBe('Cannot add: the day is locked (editable only today or yesterday).');
    });

    it('surfaces ConflictBody.message on a 409', () => {
      const err = new HttpErrorResponse({ status: 409, error: { message: 'Row changed' } });
      expect(apiError(err, 'fallback')).toBe('Row changed');
    });

    it('falls back when the body carries nothing useful', () => {
      expect(apiError(new HttpErrorResponse({ status: 500 }), 'Could not save.')).toBe('Could not save.');
    });

    it('falls back for a non-HTTP throw', () => {
      expect(apiError(new Error('boom'), 'Could not save.')).toBe('Could not save.');
    });
  });

  describe('isConflict', () => {
    it('is true only for a 409', () => {
      expect(isConflict(new HttpErrorResponse({ status: 409 }))).toBeTrue();
      expect(isConflict(new HttpErrorResponse({ status: 400 }))).toBeFalse();
      expect(isConflict(new Error('boom'))).toBeFalse();
    });
  });
});
