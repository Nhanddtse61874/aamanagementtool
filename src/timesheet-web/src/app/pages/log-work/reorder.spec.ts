import { reorderPlan } from './reorder';

const rows = [
  { taskId: 10, taskName: 'A' },
  { taskId: 20, taskName: 'B' },
  { taskId: 30, taskName: 'C' },
  { taskId: 40, taskName: 'D' },
];

describe('reorderPlan', () => {
  it('rewrites EVERY row, not just the ones that moved', () => {
    // Drag D (index 3) to the top.
    expect(reorderPlan(rows, 3, 0)).toEqual([
      { taskId: 40, orderIndex: 0 },
      { taskId: 10, orderIndex: 1 },
      { taskId: 20, orderIndex: 2 },
      { taskId: 30, orderIndex: 3 },
    ]);
  });

  // 🔴 THE BUG THIS FUNCTION EXISTS TO PREVENT.
  // SetActiveAsync soft-deletes by setting is_active = 0 and LEAVES order_index alone, and the read is
  // `WHERE is_active = 1 ORDER BY order_index`. So after one delete the surviving indices have a GAP
  // (1,2,3 — not 0,1,2). A reorder that writes only the displaced rows, at absolute index lo+i, then
  // creates a TIE — and `ORDER BY order_index` with a tie is arbitrary. The order silently scrambles.
  // Rewriting every row renormalises the gap on every drag. This is what WPF does.
  it('renormalises a gap left by a soft delete, so no two rows can tie', () => {
    // A was deleted; B, C, D survive at order_index 1, 2, 3 and are rendered at positions 0, 1, 2.
    const afterDelete = [
      { taskId: 20, taskName: 'B' },
      { taskId: 30, taskName: 'C' },
      { taskId: 40, taskName: 'D' },
    ];
    // Drag C (position 1) down to position 2.
    const writes = reorderPlan(afterDelete, 1, 2);
    expect(writes).toEqual([
      { taskId: 20, orderIndex: 0 },     // B renormalised 1 -> 0
      { taskId: 40, orderIndex: 1 },     // D
      { taskId: 30, orderIndex: 2 },     // C
    ]);
    const indices = writes.map(w => w.orderIndex);
    expect(new Set(indices).size).toBe(indices.length);   // NO TIES. This is the whole point.
  });

  it('writes nothing when the row is dropped where it started', () => {
    expect(reorderPlan(rows, 2, 2)).toEqual([]);
  });
});
