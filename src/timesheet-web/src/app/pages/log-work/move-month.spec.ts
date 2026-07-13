import { BacklogDto } from '../../api/models';
import { canMoveMonth, nextMonthFrom, toUpdateRequest } from './move-month';

describe('nextMonthFrom', () => {
  it('bumps the backlog own period when it has one -- the displayed week is irrelevant', () => {
    expect(nextMonthFrom('2026-07', '2026-12-28')).toBe('2026-08');
  });

  it('falls back to the month of the displayed Monday when the backlog has no period', () => {
    expect(nextMonthFrom(null, '2026-07-06')).toBe('2026-08');
  });

  it('rolls December into the FOLLOWING January', () => {
    expect(nextMonthFrom('2026-12', '2026-01-05')).toBe('2027-01');
    expect(nextMonthFrom(null, '2026-12-28')).toBe('2027-01');
  });

  it('pads single-digit months to two digits', () => {
    expect(nextMonthFrom('2026-08', '2026-01-05')).toBe('2026-09');
  });
});

describe('canMoveMonth', () => {
  // The hidden DEFAULT backlog holds the recurring default tasks and must appear in EVERY month, so it
  // must never belong to one. WPF guards this twice -- the UI hides the button AND the command refuses.
  it('refuses the hidden DEFAULT backlog', () => expect(canMoveMonth('DEFAULT')).toBe(false));
  it('allows a normal ticket', () => expect(canMoveMonth('PCT-123')).toBe(true));
});

describe('toUpdateRequest', () => {
  // PUT /api/backlogs/{id} replaces the WHOLE record -- an omitted field is written as NULL, not left
  // alone. M8.3 already paid for this: "a DTO that merely omits teamId sets team_id = NULL and the
  // backlog drops out of every team, invisible to everyone, permanently, while every test still passes."
  it('carries every field across unchanged, and changes ONLY periodMonth', () => {
    const dto = { backlogCode: 'PCT-1', project: 'Alpha', note: 'keep me',
                  progressPercent: 40, rowVersion: 7, periodMonth: '2026-07' } as BacklogDto;
    const req = toUpdateRequest(dto, '2026-08');
    expect(req.periodMonth).toBe('2026-08');
    expect(req.note).toBe('keep me');
    expect(req.progressPercent).toBe(40);
    expect(req.expectedVersion).toBe(7);
  });

  // grid-state.ts:54-58 says it in capitals: NEVER `rowVersion!`. worklog.service.ts ships
  // requireRowVersion() for exactly this, noting a malformed 200 "is not an impossible scenario".
  // An undefined version serialises as absent -> C# binds ExpectedVersion = 0 -> a checked write
  // asserting "I last saw version 0". Fail loud instead.
  it('THROWS rather than sending a version it does not have', () => {
    const dto = { backlogCode: 'PCT-1', project: 'Alpha' } as BacklogDto;   // no rowVersion
    expect(() => toUpdateRequest(dto, '2026-08')).toThrow();
  });

  it('does NOT send an auditNote -- WPF sends none, and the field means "the reason a HUMAN typed"', () => {
    const dto = { backlogCode: 'PCT-1', project: 'Alpha', rowVersion: 1 } as BacklogDto;
    expect(toUpdateRequest(dto, '2026-08').auditNote).toBeNull();
  });
});
