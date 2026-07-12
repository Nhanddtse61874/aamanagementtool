import { fromIsoDate, mondayOf, shiftWeeks, toIsoDate, weekDays } from './week';

describe('toIsoDate', () => {
  // 🔴 THE bug this function exists to avoid. `toISOString()` converts to UTC first, so in ANY timezone east
  // of UTC (this project runs at UTC+07) local midnight falls back into the previous day:
  //     new Date(2026, 6, 13).toISOString()  ===  '2026-07-12T17:00:00.000Z'  -> '2026-07-12'  (SUNDAY)
  // The grid would key, display and SAVE every cell one day early -- and the API's weekend guard would then
  // reject "Monday" as a Sunday. Formatting from LOCAL components cannot drift.
  it('formats from LOCAL components, so a date never slips to the previous day', () => {
    expect(toIsoDate(new Date(2026, 6, 13))).toBe('2026-07-13');   // Mon 13 Jul 2026, local midnight
  });

  it('does NOT agree with toISOString() when the runtime is east of UTC (proof the trap is real)', () => {
    const localMidnight = new Date(2026, 6, 13, 0, 0, 0);

    // Only assert the inequality where it actually holds -- east of UTC. West of UTC (or at UTC) the two
    // agree and there is nothing to prove; the point is that toIsoDate is correct in BOTH cases.
    if (localMidnight.getTimezoneOffset() < 0) {
      expect(toIsoDate(localMidnight)).not.toBe(localMidnight.toISOString().slice(0, 10));
    }
    expect(toIsoDate(localMidnight)).toBe('2026-07-13');
  });

  it('pads single-digit months and days', () => {
    expect(toIsoDate(new Date(2026, 0, 5))).toBe('2026-01-05');
  });
});

describe('fromIsoDate', () => {
  it('parses to LOCAL midnight, not UTC midnight', () => {
    const d = fromIsoDate('2026-07-13');

    expect(d.getFullYear()).toBe(2026);
    expect(d.getMonth()).toBe(6);      // July
    expect(d.getDate()).toBe(13);
    expect(d.getHours()).toBe(0);
  });

  it('round-trips with toIsoDate', () => {
    expect(toIsoDate(fromIsoDate('2026-07-13'))).toBe('2026-07-13');
  });
});

describe('mondayOf', () => {
  it('returns the Monday itself unchanged', () => {
    expect(mondayOf(new Date(2026, 6, 13))).toBe('2026-07-13');   // a Monday
  });

  it('walks back from a midweek day', () => {
    expect(mondayOf(new Date(2026, 6, 15))).toBe('2026-07-13');   // Wednesday -> that Monday
    expect(mondayOf(new Date(2026, 6, 17))).toBe('2026-07-13');   // Friday    -> that Monday
  });

  it('treats Saturday as belonging to the week that is ending', () => {
    expect(mondayOf(new Date(2026, 6, 18))).toBe('2026-07-13');
  });

  // JS makes Sunday day 0. A naive `date - getDay()` leaves Sunday where it is, and `date - (getDay() - 1)`
  // pushes it FORWARD to the coming Monday -- showing the user next week whenever they open the app on a
  // Sunday. Sunday must walk BACK six days.
  it('treats Sunday as the END of the week, not the start of the next one', () => {
    expect(mondayOf(new Date(2026, 6, 19))).toBe('2026-07-13');   // Sunday -> back to Mon 13th, not Mon 20th
  });

  it('crosses a month boundary', () => {
    expect(mondayOf(new Date(2026, 7, 1))).toBe('2026-07-27');    // Sat 1 Aug -> Mon 27 Jul
  });
});

describe('weekDays', () => {
  it('derives five columns MON..FRI from the selected Monday -- not a hard-coded literal', () => {
    const days = weekDays('2026-07-13');

    expect(days.map(d => d.dow)).toEqual(['MON', 'TUE', 'WED', 'THU', 'FRI']);
    expect(days.map(d => d.iso)).toEqual([
      '2026-07-13', '2026-07-14', '2026-07-15', '2026-07-16', '2026-07-17',
    ]);
  });

  it('labels each column dd/MM for display', () => {
    expect(weekDays('2026-07-13').map(d => d.label))
      .toEqual(['13/07', '14/07', '15/07', '16/07', '17/07']);
  });

  it('has no weekend columns -- the API WeekRow has exactly five slots', () => {
    expect(weekDays('2026-07-13').length).toBe(5);
  });

  it('crosses a month boundary without breaking the axis', () => {
    const days = weekDays('2026-07-27');
    expect(days.map(d => d.iso)).toEqual([
      '2026-07-27', '2026-07-28', '2026-07-29', '2026-07-30', '2026-07-31',
    ]);
  });

  it('crosses a year boundary', () => {
    const days = weekDays('2026-12-28');
    expect(days.map(d => d.iso)).toEqual([
      '2026-12-28', '2026-12-29', '2026-12-30', '2026-12-31', '2027-01-01',
    ]);
  });
});

describe('shiftWeeks', () => {
  // Before W4, prevWeek()/nextWeek() were `toast.show('Loaded previous week')` -- they moved nothing and
  // re-fetched nothing. This is the arithmetic that makes them real.
  it('steps back a week', () => {
    expect(shiftWeeks('2026-07-13', -1)).toBe('2026-07-06');
  });

  it('steps forward a week', () => {
    expect(shiftWeeks('2026-07-13', 1)).toBe('2026-07-20');
  });

  it('steps across a month boundary', () => {
    expect(shiftWeeks('2026-07-27', 1)).toBe('2026-08-03');
  });

  it('steps across a year boundary', () => {
    expect(shiftWeeks('2026-12-28', 1)).toBe('2027-01-04');
  });

  // Sanity: whatever a shift lands on must still BE a Monday, or the whole axis silently skews.
  it('always lands on a Monday', () => {
    let monday = '2026-07-13';
    for (let i = 0; i < 60; i++) {
      monday = shiftWeeks(monday, 1);
      expect(fromIsoDate(monday).getDay()).toBe(1);
    }
  });
});
