/**
 * The holiday calendar's 42-cell month grid.
 *
 * ── MONDAY-FIRST, AND `getDay()` IS NOT ─────────────────────────────────────────────────────────────────
 * The header row is Mon…Sun, but `Date.getDay()` is SUNDAY-first (Sun=0 … Sat=6). Using it as the column
 * offset directly puts every month one column out and silently mislabels every date in the grid — the user
 * clicks "Tuesday 7th" and marks Monday the 6th a holiday. So the offset is:
 *
 *      offset = (getDay() + 6) % 7        Mon→0, Tue→1, … Sun→6
 *
 * (Checked against the month the old mockup hard-coded: 1 Jul 2026 is a Wednesday, `getDay()` 3, offset 2 —
 * so the grid opens on Mon 29 Jun, which is exactly the start date that mockup had baked in.)
 *
 * ── 6 × 7 = 42, ALWAYS ──────────────────────────────────────────────────────────────────────────────────
 * A fixed 42 cells means the grid never changes height between months, and a 31-day month starting on a
 * Sunday (offset 6 + 31 = 37 cells) still fits. Cells outside the month are rendered dimmed, not blank —
 * they are real dates and remain clickable, which matches how the surrounding days behave.
 *
 * ── WEEKENDS ARE CLICKABLE ──────────────────────────────────────────────────────────────────────────────
 * 🔴 The mockup BLOCKED clicking a weekend (`if (c.weekend) return;`). WPF does not, and neither does the
 * API — `POST /api/holidays` has no weekday guard at all; it upserts whatever date it is given. Blocking it
 * here would be a client-only rule that WPF users do not have, and it is wrong on its own terms: a public
 * holiday that lands on a Saturday is still a public holiday, and an admin may well want it recorded. The
 * `weekend` flag survives for SHADING only.
 */
export interface CalCell {
  readonly day: number;
  /** `yyyy-MM-dd`, built from local date parts — never `toISOString()`, which would shift across UTC. */
  readonly iso: string;
  readonly weekend: boolean;
  readonly inMonth: boolean;
}

/** `month` is 1-12, as a human writes it — NOT the 0-11 a `Date` constructor takes. */
export function buildCalendar(year: number, month: number): CalCell[] {
  const first = new Date(year, month - 1, 1);

  // Monday-first. `getDay()` is Sunday-first; see the header comment.
  const offset = (first.getDay() + 6) % 7;

  const start = new Date(year, month - 1, 1 - offset);

  const cells: CalCell[] = [];
  for (let i = 0; i < 42; i++) {
    const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i);
    const dow = d.getDay();
    cells.push({
      day: d.getDate(),
      iso: isoDate(d),
      weekend: dow === 0 || dow === 6,
      inMonth: d.getMonth() === month - 1 && d.getFullYear() === year,
    });
  }
  return cells;
}

/**
 * Move the view one month. Returns `{ year, month }` with `month` still 1-12.
 *
 * Done in parts rather than by mutating a `Date`, because `setMonth` on the 31st of a month rolls FORWARD
 * into the next one (31 Mar → "31 Feb" → 3 Mar), which would make the calendar skip a month at the end of
 * every long one. There is no day here to roll.
 */
export function shiftMonth(year: number, month: number, delta: number): { year: number; month: number } {
  const zeroBased = month - 1 + delta;
  return {
    year: year + Math.floor(zeroBased / 12),
    month: ((zeroBased % 12) + 12) % 12 + 1,   // the double-mod keeps a negative delta positive
  };
}

/**
 * `yyyy-MM-dd` from LOCAL date parts.
 *
 * 🔴 NOT `toISOString().slice(0, 10)`. That converts to UTC first, so for anyone east of Greenwich midnight
 * local is the PREVIOUS DAY in UTC — every holiday would be written one day early, and only for some users.
 */
export function isoDate(d: Date): string {
  const month = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${d.getFullYear()}-${month}-${day}`;
}

/** The month's label, e.g. "July 2026". */
export function monthLabel(year: number, month: number): string {
  return new Date(year, month - 1, 1).toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
}
