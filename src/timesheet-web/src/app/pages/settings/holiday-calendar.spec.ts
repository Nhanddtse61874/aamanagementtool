import { buildCalendar, isoDate, monthLabel, shiftMonth } from './holiday-calendar';

describe('buildCalendar', () => {
  /**
   * 🔴 THE MUTATION-CHECK TARGET. The header row is Mon…Sun; `Date.getDay()` is SUNDAY-first.
   *
   * Use `getDay()` as the column offset directly and every month lands one column out — the user clicks the
   * cell under "Tue" and marks Monday a holiday. Change `(getDay() + 6) % 7` to `getDay()` and this goes red.
   *
   * 1 Jul 2026 is a WEDNESDAY (`getDay()` 3 → offset 2), so a Monday-first grid opens on Mon 29 Jun. That is
   * also exactly the start date the old mockup had hard-coded, which is what confirms the formula.
   */
  it('is MONDAY-first — July 2026 opens on Mon 29 Jun, not Sun 28', () => {
    const cells = buildCalendar(2026, 7);

    expect(cells[0].iso).toBe('2026-06-29');   // Monday
    expect(cells[0].day).toBe(29);
    expect(cells[0].inMonth).toBeFalse();

    expect(cells[2].iso).toBe('2026-07-01');   // Wednesday, the 1st, in the third column
    expect(cells[2].inMonth).toBeTrue();
  });

  it('always renders exactly 42 cells, so the grid never changes height', () => {
    expect(buildCalendar(2026, 7).length).toBe(42);
    expect(buildCalendar(2026, 2).length).toBe(42);    // a short month
    expect(buildCalendar(2027, 8).length).toBe(42);    // 31 days starting on a Sunday — the worst case
  });

  it('handles a month that STARTS on a Sunday — the widest possible offset', () => {
    // 1 Feb 2026 is a Sunday: getDay() 0, offset (0 + 6) % 7 = 6. A Sunday-first grid would give offset 0
    // and open the month on the 1st, in the Monday column.
    const cells = buildCalendar(2026, 2);

    expect(cells[6].iso).toBe('2026-02-01');
    expect(cells[6].weekend).toBeTrue();
    expect(cells[0].iso).toBe('2026-01-26');   // the Monday before
  });

  it('flags weekends for SHADING but leaves them usable — the API has no weekday guard', () => {
    // 🔴 The mockup refused to toggle a weekend. WPF does not, and POST /api/holidays upserts any date it is
    // given. A public holiday that falls on a Saturday is still a public holiday.
    const saturday = buildCalendar(2026, 7).find(c => c.iso === '2026-07-04');

    expect(saturday?.weekend).toBeTrue();
    // There is no `disabled`/`locked` field to check: the cell carries no such concept. Shading only.
    expect(Object.keys(saturday ?? {})).toEqual(['day', 'iso', 'weekend', 'inMonth']);
  });

  it('marks only the current month as inMonth, so the leading/trailing days can be dimmed', () => {
    const cells = buildCalendar(2026, 7);

    expect(cells.filter(c => c.inMonth).length).toBe(31);
    expect(cells.filter(c => c.inMonth).every(c => c.iso.startsWith('2026-07'))).toBeTrue();
  });

  it('does not confuse the same month number in a different year', () => {
    // `inMonth` compares the year too. Without that, a 42-cell grid spanning a year boundary would light up
    // December of the WRONG year.
    const jan = buildCalendar(2027, 1);
    expect(jan.filter(c => c.inMonth).every(c => c.iso.startsWith('2027-01'))).toBeTrue();
    expect(jan.filter(c => c.inMonth).length).toBe(31);
  });
});

describe('isoDate', () => {
  /**
   * 🔴 NOT `toISOString().slice(0, 10)`. That converts to UTC first — so east of Greenwich, local midnight is
   * the PREVIOUS day in UTC and every holiday would be written one day early, for some users only.
   */
  it('formats from LOCAL parts, never via UTC', () => {
    // Local midnight on 1 Jan. In any timezone ahead of UTC, toISOString() would render this as 2026-12-31.
    expect(isoDate(new Date(2026, 0, 1, 0, 0, 0))).toBe('2026-01-01');
  });

  it('zero-pads month and day', () => {
    expect(isoDate(new Date(2026, 6, 4))).toBe('2026-07-04');
  });
});

describe('shiftMonth', () => {
  it('steps forward and backward within a year', () => {
    expect(shiftMonth(2026, 7, 1)).toEqual({ year: 2026, month: 8 });
    expect(shiftMonth(2026, 7, -1)).toEqual({ year: 2026, month: 6 });
  });

  it('rolls over the year boundary in both directions', () => {
    expect(shiftMonth(2026, 12, 1)).toEqual({ year: 2027, month: 1 });
    expect(shiftMonth(2026, 1, -1)).toEqual({ year: 2025, month: 12 });
  });

  /**
   * The reason this is arithmetic and not `date.setMonth(m + 1)`: `setMonth` on the 31st rolls FORWARD
   * (31 Mar + 1 month → "31 Apr" → 1 May), so a naive implementation would skip a month at the end of every
   * long one. There is no day here to roll.
   */
  it('cannot skip a month — it carries no day at all', () => {
    // Walk a full year forward one step at a time and land exactly where we started.
    let cursor = { year: 2026, month: 1 };
    for (let i = 0; i < 12; i++) cursor = shiftMonth(cursor.year, cursor.month, 1);

    expect(cursor).toEqual({ year: 2027, month: 1 });
  });
});

describe('monthLabel', () => {
  it('names the month the grid is showing', () => {
    expect(monthLabel(2026, 7)).toBe('July 2026');
  });
});
