import { reorderPlan } from './reorder';

// `orderIndex` is the row's order_index ON THE SERVER. Untouched, it matches the screen position -- it is
// only a DELETE that pulls the two apart (see the gap fixture below). reorderPlan reads neither: it plans
// from the array's ORDER. The field is here because TaskRow carries it, and a fixture that lies about the
// shape it is standing in for is a fixture that stops catching things.
const rows = [
  { taskId: 10, taskName: 'A', orderIndex: 0 },
  { taskId: 20, taskName: 'B', orderIndex: 1 },
  { taskId: 30, taskName: 'C', orderIndex: 2 },
  { taskId: 40, taskName: 'D', orderIndex: 3 },
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
    // 🔴 The orderIndex values ARE the gap -- 1,2,3 with no 0. The array POSITIONS are 0,1,2. That divergence
    // is the entire hazard, and the fixture now states it outright instead of only describing it in a comment.
    const afterDelete = [
      { taskId: 20, taskName: 'B', orderIndex: 1 },
      { taskId: 30, taskName: 'C', orderIndex: 2 },
      { taskId: 40, taskName: 'D', orderIndex: 3 },
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
