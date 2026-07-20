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
 * 🔴 THIS FILE ONCE CLAIMED TO BE "the same rule" AS CORE AND WAS NOT.
 *
 * The old body rounded each day to 1dp first and then subtracted the accumulated drift from the last day.
 * That agrees with Core whenever the division rounds DOWN, and disagrees whenever it rounds UP:
 *
 *     8h over 3 days    Core  [2.6, 2.6, 2.8]     old TypeScript  [2.7, 2.7, 2.6]
 *     10h over 3 days   Core  [3.3, 3.3, 3.4]     old TypeScript  [3.3, 3.3, 3.4]   (agreed, by luck)
 *
 * Both summed correctly, so no test and no API validation could catch it — but they are not the same
 * distribution, and the direction of the difference matters: SI-01 says the remainder LANDS ON the last
 * working day. Core floors and adds the remainder there, so the last day gets MORE. The old code rounded
 * up and then took hours OFF the last day, which is the opposite of the documented rule.
 *
 * This is now Core's algorithm exactly — integer tenths, floor, remainder onto the last day
 * (SmartInputService.cs:37-39). Integer tenths also avoid the binary-float drift the Core comment cites.
 */
export function distributeHours(total: number, dayCount: number): number[] {
  if (dayCount <= 0 || total <= 0) return [];

  const totalTenths = Math.round(total * 10);
  const baseTenths = Math.floor(totalTenths / dayCount);
  const remainder = totalTenths % dayCount;

  const cells = Array<number>(dayCount).fill(baseTenths / 10);
  cells[dayCount - 1] = (baseTenths + remainder) / 10;

  return cells;
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
