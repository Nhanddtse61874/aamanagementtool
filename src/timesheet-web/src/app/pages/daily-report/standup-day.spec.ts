import { ISSUE_STATUSES, SECTIONS, STANDUP_STATUSES, addDays, canEditDay, formatDay, toIsoDate } from './standup-day';

describe('standup-day', () => {
  describe('the literal wire values', () => {
    /**
     * 🔴 These are not cosmetic. `StandupService.ValidateDraft` does an exact
     * `StandupStatus.All.Contains(d.Status)` and throws -> 400 on a miss, so a renamed or re-cased status is a
     * screen on which nothing can be saved. The hyphen in `In-process` is the one people "correct".
     */
    it('pins the four entry statuses exactly as Core spells them', () => {
      expect(STANDUP_STATUSES).toEqual(['Todo', 'In-process', 'Done', 'Pending']);
    });

    it('pins the three issue statuses — lower-case, unlike the entry statuses', () => {
      expect(ISSUE_STATUSES).toEqual(['open', 'pending', 'resolved']);
    });

    it('pins the two sections', () => {
      expect(SECTIONS).toEqual(['yesterday', 'today']);
    });
  });

  describe('toIsoDate', () => {
    it('formats a date as yyyy-MM-dd', () => {
      expect(toIsoDate(new Date(2026, 6, 12))).toBe('2026-07-12');
    });

    it('zero-pads month and day', () => {
      expect(toIsoDate(new Date(2026, 0, 5))).toBe('2026-01-05');
    });

    /**
     * 🔴 THE UTC TRAP. `toISOString().slice(0,10)` converts to UTC first. At 23:00 local in a UTC+7 zone
     * (this project's) that yields TOMORROW — the screen would open on the wrong day, the client-side
     * edit-lock would disagree with the server's `IClock.Today`, and every write would 400 as "locked" on a
     * day the user is plainly looking at.
     *
     * This asserts the LOCAL calendar day is preserved at an hour where the two answers differ.
     */
    it('uses the LOCAL calendar day, not the UTC one', () => {
      const lateEvening = new Date(2026, 6, 12, 23, 30);
      expect(toIsoDate(lateEvening)).toBe('2026-07-12');

      const earlyMorning = new Date(2026, 6, 12, 0, 30);
      expect(toIsoDate(earlyMorning)).toBe('2026-07-12');
    });
  });

  describe('addDays', () => {
    it('moves forward and back', () => {
      expect(addDays('2026-07-12', 1)).toBe('2026-07-13');
      expect(addDays('2026-07-12', -1)).toBe('2026-07-11');
    });

    it('rolls over a month boundary', () => {
      expect(addDays('2026-07-31', 1)).toBe('2026-08-01');
      expect(addDays('2026-08-01', -1)).toBe('2026-07-31');
    });

    it('rolls over a year boundary', () => {
      expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
      expect(addDays('2027-01-01', -1)).toBe('2026-12-31');
    });

    it('handles a leap day', () => {
      expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
      expect(addDays('2028-03-01', -1)).toBe('2028-02-29');
    });
  });

  /**
   * 🔴 THE EDIT-LOCK MIRROR. `StandupService.CanEditDay`:
   *
   *     workDate == today || workDate == today.AddDays(-1)
   *
   * Two days back is LOCKED. Tomorrow is LOCKED. Anything else here is a screen offering buttons the server
   * will 400 — or, worse, hiding buttons that would have worked.
   */
  describe('canEditDay', () => {
    const TODAY = '2026-07-12';

    it('allows today', () => {
      expect(canEditDay('2026-07-12', TODAY)).toBeTrue();
    });

    it('allows yesterday', () => {
      expect(canEditDay('2026-07-11', TODAY)).toBeTrue();
    });

    it('LOCKS two days ago', () => {
      expect(canEditDay('2026-07-10', TODAY)).toBeFalse();
    });

    it('LOCKS tomorrow — the lock is not "the past", it is exactly two days', () => {
      expect(canEditDay('2026-07-13', TODAY)).toBeFalse();
    });

    it('allows yesterday across a month boundary', () => {
      expect(canEditDay('2026-07-31', '2026-08-01')).toBeTrue();
      expect(canEditDay('2026-07-30', '2026-08-01')).toBeFalse();
    });
  });

  describe('formatDay', () => {
    it('renders a locale-independent label', () => {
      expect(formatDay('2026-07-12')).toBe('Sun 12 Jul 2026');
    });
  });
});
