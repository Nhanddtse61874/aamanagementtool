/**
 * Date arithmetic, the edit-lock, and the LITERAL WIRE VALUES for the Daily Report (standup) screen.
 *
 * Pure — no Angular, no HTTP. Everything here is a mirror of a server-side rule, and each mirror says which
 * rule it mirrors, because a mirror that drifts from its original is worse than no mirror at all.
 */

/**
 * 🔴 THE FOUR ENTRY STATUSES, AS THEY GO ON THE WIRE. Mirrors `StandupStatus.All` (Core/Models/StandupModels.cs):
 *
 *     new[] { "Todo", "In-process", "Done", "Pending" }
 *
 * NOTE THE HYPHEN AND THE CAPITALISATION of `In-process`. `StandupService.ValidateDraft` does an exact
 * `StandupStatus.All.Contains(d.Status)` and throws `ArgumentException` -> 400 on anything else, so
 * "In Progress" / "in-process" / "InProcess" are all a rejected write, not a near miss.
 */
export const STANDUP_STATUSES = ['Todo', 'In-process', 'Done', 'Pending'] as const;
export type StandupStatus = (typeof STANDUP_STATUSES)[number];

/** The three ISSUE statuses. Mirrors `StandupIssueStatus.All` — lower-case, unlike the entry statuses. */
export const ISSUE_STATUSES = ['open', 'pending', 'resolved'] as const;
export type IssueStatus = (typeof ISSUE_STATUSES)[number];

/** The two day-sections. Mirrors `StandupSection` — lower-case string constants, not an enum. */
export const SECTIONS = ['yesterday', 'today'] as const;
export type StandupSection = (typeof SECTIONS)[number];

/**
 * `yyyy-MM-dd` in LOCAL time.
 *
 * 🔴 NOT `toISOString().slice(0, 10)`. That converts to UTC first, so for anyone east of Greenwich late in the
 * evening (and west of it early in the morning) it yields the WRONG DAY — the standup screen would open on
 * tomorrow, the edit-lock would disagree with the server's `IClock.Today`, and every write would 400 with
 * "the day is locked" on a day the user is plainly looking at.
 */
export function toIsoDate(d: Date): string {
  const year = d.getFullYear();
  const month = `${d.getMonth() + 1}`.padStart(2, '0');
  const day = `${d.getDate()}`.padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/** Today, local, as `yyyy-MM-dd`. */
export function todayIso(): string {
  return toIsoDate(new Date());
}

/**
 * Shift an ISO day by `delta` days. `Date` normalises month/year rollover for us, so month ends and leap days
 * need no special case here.
 */
export function addDays(iso: string, delta: number): string {
  const [year, month, day] = iso.split('-').map(Number);
  return toIsoDate(new Date(year, month - 1, day + delta));
}

/**
 * 🔴 THE EDIT-LOCK, CLIENT-SIDE. Mirrors `StandupService.CanEditDay` exactly:
 *
 *     workDate == today || workDate == today.AddDays(-1)
 *
 * AND IT IS NOT THE AUTHORITY. The server gates all five entry writes on its own `CanEditDay` and the API
 * re-checks it to turn a silent no-op into an honest 400 — a web client cannot bypass it, and must not try to
 * second-guess it. This exists for ONE reason: `GET /api/standup/entries` reports the lock only as a PER-ENTRY
 * `editable` bool, so a day with ZERO entries carries no lock signal at all and the "+ Add entry" button would
 * have nothing to gate on. So we compute it here to decide what to OFFER, and we let the server's 400 decide
 * what actually happens. Never let this function PERMIT something the server would refuse: surface the error.
 */
export function canEditDay(iso: string, today: string): boolean {
  return iso === today || iso === addDays(today, -1);
}

const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;
const MONTHS = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
] as const;

/**
 * `2026-07-12` -> `Sun 12 Jul 2026`. Deterministic and locale-independent on purpose: `toLocaleDateString`
 * would render differently per machine and make the header untestable.
 */
export function formatDay(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number);
  const date = new Date(year, month - 1, day);
  return `${WEEKDAYS[date.getDay()]} ${day} ${MONTHS[month - 1]} ${year}`;
}
