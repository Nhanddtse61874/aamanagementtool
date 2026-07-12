import { SmartFillTaskRequest } from '../../api/models';

/**
 * The Smart Fill distribution math — CLIENT-SIDE, because the API takes explicit per-cell hours.
 *
 * `POST /api/smartfill/apply` accepts `tasks: [{ taskId, cells: [{ date, hours }] }]` — there is no
 * "distribute evenly" mode on the wire, so whoever calls it must have already decided what each cell gets.
 * WPF's panel does this in `SmartInputService` and defaults to `DistributeEven`; this is the same rule.
 *
 * Two API constraints the split MUST respect, or the whole apply is rejected with a 400 and nothing is
 * written (VERIFIED on the wire):
 *   - **At most ONE decimal place.** `ValidateCellAsync` rejects `4.25` outright.
 *   - **The total must still land on the requested total.** Rounding each day independently does not: 8h
 *     across 3 days is 2.666…, which rounds to 2.7 × 3 = 8.1 — an hour the user never asked for, and enough
 *     to breach the 8h/day cap on a day that was already full.
 *
 * So: round every day to 1dp, then push the accumulated rounding drift onto the LAST day.
 */
export function distributeHours(total: number, dayCount: number): number[] {
  if (dayCount <= 0 || total <= 0) return [];

  const per = round1(total / dayCount);
  const cells = Array<number>(dayCount).fill(per);

  // Whatever the rounding gained or lost across the week, settle it on the final day so the sum is exact.
  const drift = round1(total - per * dayCount);
  cells[dayCount - 1] = round1(cells[dayCount - 1] + drift);

  return cells;
}

/** One decimal place — the most the API will accept. */
function round1(n: number): number {
  return Math.round(n * 10) / 10;
}

/**
 * Build the request body: one task, spread over the chosen days.
 *
 * Cells with 0h are dropped, not sent as zeros: the server filters `Hours > 0` when computing the affected
 * date range anyway, and a 0 would be rejected by the single-cell guard if it ever reached one.
 */
export function buildSmartFillRequest(
  taskId: number,
  isoDates: readonly string[],
  totalHours: number,
): SmartFillTaskRequest[] {
  const hours = distributeHours(totalHours, isoDates.length);

  const cells = isoDates
    .map((date, i) => ({ date, hours: hours[i] }))
    .filter(c => c.hours > 0);

  if (cells.length === 0) return [];
  return [{ taskId, cells }];
}
