import { TaskRow } from './grid-state';

/** One row's new position on the server. */
export interface OrderWrite {
  readonly taskId: number;
  readonly orderIndex: number;
}

/**
 * A drag, expressed as the new `orderIndex` of EVERY row in the group.
 *
 * 🔴 Every row, not just the ones that moved -- and that is not belt-and-braces, it is the fix for a real
 * corruption. `SetActiveAsync` soft-deletes by setting `is_active = 0` and LEAVES `order_index` alone,
 * while the read is `WHERE is_active = 1 ORDER BY order_index`. So one delete leaves the survivors at
 * 1,2,3 -- a GAP. A reorder that writes only the displaced rows, at absolute index lo+i, then produces a
 * TIE, and `ORDER BY order_index` with a tie is arbitrary: the order silently scrambles on the next
 * reload. Rewriting every row renormalises the gap on every drag. This is exactly what WPF does
 * (`TimesheetViewModel.cs:421`).
 *
 * The writes are BUMP-ONLY (`PUT /api/tasks/{id}/order`, no expectedVersion). That is a recorded M8.2
 * decision, not an oversight: SetOrderAsync runs once per row, so a checked variant would 409-storm on
 * an ordinary drag. Do NOT add a version to these.
 */
export function reorderPlan(rows: readonly TaskRow[], from: number, to: number): OrderWrite[] {
  if (from === to) return [];

  const next = [...rows];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);

  return next.map((row, i) => ({ taskId: row.taskId, orderIndex: i }));
}
