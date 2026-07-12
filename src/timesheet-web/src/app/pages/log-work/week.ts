/**
 * The day axis of the Log Work grid.
 *
 * Before M8.4/W4 the axis was a HARD-CODED FIVE-DAY LITERAL in `WorklogService.WEEK_DAYS`
 * (`MON 06/07` ... `FRI 10/07`) and `prevWeek()` / `nextWeek()` were `toast.show('Loaded previous week')` --
 * they changed nothing. The grid always showed the same five labels no matter which week was loaded, and
 * "navigation" was theatre. The axis is now DERIVED from the selected Monday, and it is the single source of
 * the ISO dates that key every cell and go out on the wire.
 *
 * `GET /api/timesheet/week` returns `WeekRow { mon, tue, wed, thu, fri }` -- five positional slots and NO
 * dates -- so the client is the only thing that knows what date each slot is. Get this wrong and every cell
 * is keyed, displayed, and WRITTEN against the wrong day.
 */

/** One column of the grid: the label the user reads, and the ISO date the API speaks. */
export interface WeekDay {
  /** `MON` .. `FRI` -- the column header. */
  readonly dow: string;
  /** `dd/MM` -- the small date under the header. Display only; never sent anywhere. */
  readonly label: string;
  /** `yyyy-MM-dd` -- THE key. Half of `cellKey`, and the `date` on every write. */
  readonly iso: string;
}

const DOW = ['MON', 'TUE', 'WED', 'THU', 'FRI'];

/**
 * Format a Date as `yyyy-MM-dd` FROM ITS LOCAL COMPONENTS.
 *
 * 🔴 NOT `toISOString().slice(0, 10)`. `toISOString()` converts to UTC first, and for any timezone EAST of
 * UTC that shifts local midnight back into the PREVIOUS DAY: in UTC+07 (this project's timezone),
 * `new Date(2026, 6, 13)` is `2026-07-12T17:00:00Z`, so `toISOString()` yields `"2026-07-12"` -- Sunday.
 * The grid would then key, display and SAVE every cell one day early, silently, and the weekend guard would
 * start rejecting Monday as a Sunday. Local components cannot drift, because they never leave local time.
 */
export function toIsoDate(date: Date): string {
  const y = date.getFullYear();
  const m = `${date.getMonth() + 1}`.padStart(2, '0');
  const d = `${date.getDate()}`.padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** Parse a `yyyy-MM-dd` into a LOCAL midnight Date. `new Date('2026-07-13')` would parse as UTC -- same
 *  off-by-one-day trap as above, in the other direction -- so the parts are passed explicitly. */
export function fromIsoDate(iso: string): Date {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
}

/** The Monday of the week containing `date`. Sunday belongs to the week that is ENDING, not the one that is
 *  starting: JS makes Sunday day 0, so a naive `date - getDay()` would jump a Sunday FORWARD to the coming
 *  Monday and show the user next week. */
export function mondayOf(date: Date): string {
  const day = date.getDay();                       // 0 = Sun, 1 = Mon, ... 6 = Sat
  const backToMonday = day === 0 ? 6 : day - 1;    // Sunday -> back 6 days, not forward 1
  const monday = new Date(date.getFullYear(), date.getMonth(), date.getDate() - backToMonday);
  return toIsoDate(monday);
}

/** The five columns MON..FRI of the week starting at `monday` (an ISO date). The grid has no weekend
 *  columns -- the API's `WeekRow` has exactly five slots -- so this is always five days. */
export function weekDays(monday: string): WeekDay[] {
  const start = fromIsoDate(monday);
  return DOW.map((dow, i) => {
    const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
    return {
      dow,
      label: `${`${d.getDate()}`.padStart(2, '0')}/${`${d.getMonth() + 1}`.padStart(2, '0')}`,
      iso: toIsoDate(d),
    };
  });
}

/** Shift a Monday by whole weeks. `-1` = previous week, `+1` = next. */
export function shiftWeeks(monday: string, weeks: number): string {
  const d = fromIsoDate(monday);
  return toIsoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() + weeks * 7));
}
