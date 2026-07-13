import { TaskItemDto } from '../../api/models';
import { EditRow } from './backlog-form';
import { planTaskWrites } from './task-edit';

/** The three tasks as the server last handed them over: contiguous 0,1,2, each with its own status. */
const LOADED: TaskItemDto[] = [
  { id: 10, backlogId: 7, taskName: 'A', orderIndex: 0, status: 'Todo', rowVersion: 1, isActive: true },
  { id: 20, backlogId: 7, taskName: 'B', orderIndex: 1, status: 'In-process', rowVersion: 2, isActive: true },
  { id: 30, backlogId: 7, taskName: 'C', orderIndex: 2, status: 'Done', rowVersion: 3, isActive: true },
];

const UNTOUCHED: EditRow[] = [
  { existingTaskId: 10, name: 'A', orderIndex: 0, removed: false },
  { existingTaskId: 20, name: 'B', orderIndex: 1, removed: false },
  { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },
];

describe('planTaskWrites', () => {
  /**
   * 🔴 THE TEST THIS MODULE EXISTS FOR.
   *
   * PUT /api/tasks/{id} writes name, order AND status in ONE checked call:
   *   record TaskUpdateRequest(string TaskName, int OrderIndex, string Status, long ExpectedVersion)
   *   UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex, status = @Status, ...
   * and the handler (BacklogEndpoints.cs:328-333) binds `Status = req.Status` with NO null-guard -- only
   * TaskName is guarded. The backlog editor NEVER SHOWS a task's status, so the form does not have the
   * field at all: build the body from the form and `status` binds to null, and the save writes status =
   * NULL over Todo / In-process / Done / Pending. The entire Task List screen is built on that column.
   *
   * Forgetting a field the form does not have is the DEFAULT outcome, not an unlikely slip -- and the
   * generated TaskUpdateRequest has `status?: string | null`, so dropping it compiles clean.
   */
  it('round-trips status -- the form does not have it, and the UPDATE writes it unconditionally', () => {
    const loaded: TaskItemDto[] = [
      { id: 4, backlogId: 7, taskName: 'Old', orderIndex: 0, status: 'Done', rowVersion: 9, isActive: true },
    ];

    const plan = planTaskWrites(loaded, [{ existingTaskId: 4, name: 'New', orderIndex: 0, removed: false }], 7);

    expect(plan.updates[0].body.status).toBe('Done');          // 🔴 NOT undefined. NOT null.
    expect(plan.updates[0].body.expectedVersion).toBe(9);
  });

  it('round-trips EACH row\'s own status, not one status for all of them', () => {
    // Rename all three; every one keeps the status it was loaded with.
    const renamed: EditRow[] = UNTOUCHED.map(r => ({ ...r, name: `${r.name}!` }));

    expect(planTaskWrites(LOADED, renamed, 7).updates.map(u => u.body.status))
      .toEqual(['Todo', 'In-process', 'Done']);
  });

  it("falls back to 'Todo' only when the loaded task genuinely carries no status", () => {
    const loaded: TaskItemDto[] = [
      { id: 4, backlogId: 7, taskName: 'Old', orderIndex: 0, rowVersion: 9, isActive: true },   // no status
    ];

    const plan = planTaskWrites(loaded, [{ existingTaskId: 4, name: 'New', orderIndex: 0, removed: false }], 7);

    expect(plan.updates[0].body.status).toBe('Todo');
  });

  /**
   * 🔴 THE GAP. SetActiveAsync soft-deletes by setting is_active = 0 and LEAVES order_index untouched
   * (TaskRepository.cs:123-128), while the read is `WHERE is_active = 1 ORDER BY order_index`. So after one
   * delete the survivors sit at 1,2,3 while RENDERING at positions 0,1,2. Write only the rows that moved,
   * at their absolute position, and you create a TIE -- and `ORDER BY order_index` with a tie is arbitrary,
   * so the order silently scrambles. Reindexing EVERY survivor 0..n is self-healing and cannot tie. It is
   * also exactly what WPF does: BacklogEditorViewModel.ActiveTasks, "reindexed 0..n in display order".
   */
  it('REINDEXES every survivor 0..n, so the gap a soft delete leaves behind cannot tie', () => {
    // A was deleted in an earlier save. B, C, D survive at order_index 1, 2, 3 -- and render at 0, 1, 2.
    // 🔴 The orderIndex values ARE the gap (1,2,3, no 0). The array POSITIONS are 0,1,2. That divergence is
    // the entire hazard, and the fixture states it outright rather than only describing it.
    const afterDelete: TaskItemDto[] = [
      { id: 20, backlogId: 7, taskName: 'B', orderIndex: 1, status: 'Todo', rowVersion: 2, isActive: true },
      { id: 30, backlogId: 7, taskName: 'C', orderIndex: 2, status: 'Done', rowVersion: 3, isActive: true },
      { id: 40, backlogId: 7, taskName: 'D', orderIndex: 3, status: 'Pending', rowVersion: 4, isActive: true },
    ];
    const rows: EditRow[] = [
      { existingTaskId: 20, name: 'B', orderIndex: 1, removed: false },
      { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },
      { existingTaskId: 40, name: 'D', orderIndex: 3, removed: false },
    ];

    const plan = planTaskWrites(afterDelete, rows, 7);
    const indices = plan.updates.map(u => u.body.orderIndex!);

    expect(indices).toEqual([0, 1, 2]);                        // renormalised off the gap
    expect(new Set(indices).size).toBe(indices.length);        // NO TIES. This is the whole point.
    expect(plan.updates.map(u => u.body.status)).toEqual(['Todo', 'Done', 'Pending']);  // still round-tripped
  });

  it('emits ZERO writes for an untouched, already-contiguous list', () => {
    expect(planTaskWrites(LOADED, UNTOUCHED, 7)).toEqual({ deletes: [], inserts: [], updates: [] });
  });

  it('soft-deletes a removed row and RENUMBERS the survivors around the hole', () => {
    const rows: EditRow[] = [
      { existingTaskId: 10, name: 'A', orderIndex: 0, removed: true },     // delete A, which held index 0
      { existingTaskId: 20, name: 'B', orderIndex: 1, removed: false },
      { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },
    ];

    const plan = planTaskWrites(LOADED, rows, 7);

    expect(plan.deletes).toEqual([10]);
    // B and C shift 1->0 and 2->1 IN THE SAME SAVE. Leave them at 1,2 and the next inserted row takes 0
    // -- fine -- but the one after that takes 1 and TIES with B.
    expect(plan.updates).toEqual([
      { id: 20, body: { taskName: 'B', orderIndex: 0, status: 'In-process', expectedVersion: 2 } },
      { id: 30, body: { taskName: 'C', orderIndex: 1, status: 'Done', expectedVersion: 3 } },
    ]);
  });

  // Rule #9: PUT /api/tasks/{id}/active is BUMP-ONLY by design and TaskActiveRequest carries no version at
  // all. Sending one would make every delete fail. The plan therefore carries bare ids.
  it('plans deletes as bare ids -- a delete carries NO expectedVersion', () => {
    const rows: EditRow[] = [
      { existingTaskId: 10, name: 'A', orderIndex: 0, removed: true },
      { existingTaskId: 20, name: 'B', orderIndex: 1, removed: true },
      { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },
    ];

    expect(planTaskWrites(LOADED, rows, 7).deletes).toEqual([10, 20]);
  });

  it('INSERTS a new row at its reindexed position, with NO status -- the server defaults it to Todo', () => {
    const rows: EditRow[] = [
      ...UNTOUCHED,
      // orderIndex 99 is deliberate nonsense: the reindex must take the row's POSITION, not this field.
      { existingTaskId: 0, name: '  D  ', orderIndex: 99, removed: false },
    ];

    const plan = planTaskWrites(LOADED, rows, 7);

    expect(plan.inserts).toEqual([{ backlogId: 7, taskName: 'D', orderIndex: 3 }]);   // trimmed, position 3
    expect('status' in plan.inserts[0]).toBe(false);   // TaskCreateRequest has no status field to send
    expect(plan.updates).toEqual([]);                  // the three existing rows did not move
  });

  it('DROPS a removed brand-new row rather than trying to soft-delete id 0', () => {
    const rows: EditRow[] = [
      ...UNTOUCHED,
      { existingTaskId: 0, name: 'Added then removed before saving', orderIndex: 3, removed: true },
    ];

    const plan = planTaskWrites(LOADED, rows, 7);

    expect(plan.deletes).toEqual([]);
    expect(plan.inserts).toEqual([]);
    expect(plan.updates).toEqual([]);
  });

  /**
   * Rename and reorder ride in ONE checked write, which is why PUT /api/tasks/{id}/order is not used by
   * this screen at all -- so the "rename-before-reorder" ordering hazard cannot exist here.
   *
   * This also pins the reindex source: C still carries orderIndex 2, but it sits at ARRAY POSITION 0 and
   * must be written as 0. Position is the truth; the field is the server's last-known value, used only to
   * decide whether a write is needed at all. (WPF: `active[i].OrderIndex = i`.)
   */
  it('carries a rename and a reorder in ONE update, reindexed by POSITION not by the stale field', () => {
    const rows: EditRow[] = [
      { existingTaskId: 30, name: 'C renamed', orderIndex: 2, removed: false },   // dragged to the top
      { existingTaskId: 10, name: 'A', orderIndex: 0, removed: false },
      { existingTaskId: 20, name: 'B', orderIndex: 1, removed: false },
    ];

    expect(planTaskWrites(LOADED, rows, 7).updates).toEqual([
      { id: 30, body: { taskName: 'C renamed', orderIndex: 0, status: 'Done', expectedVersion: 3 } },
      { id: 10, body: { taskName: 'A', orderIndex: 1, status: 'Todo', expectedVersion: 1 } },
      { id: 20, body: { taskName: 'B', orderIndex: 2, status: 'In-process', expectedVersion: 2 } },
    ]);
  });

  it('updates ONLY the rows whose name or order actually changed', () => {
    const rows: EditRow[] = [
      { existingTaskId: 10, name: 'A', orderIndex: 0, removed: false },       // untouched
      { existingTaskId: 20, name: 'B renamed', orderIndex: 1, removed: false },
      { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },       // untouched
    ];

    const plan = planTaskWrites(LOADED, rows, 7);

    expect(plan.updates).toEqual([
      { id: 20, body: { taskName: 'B renamed', orderIndex: 1, status: 'In-process', expectedVersion: 2 } },
    ]);
  });

  it('trims a renamed task, and does not mistake trailing space for a change', () => {
    const rows: EditRow[] = [
      { existingTaskId: 10, name: '  A  ', orderIndex: 0, removed: false },   // trims back to 'A' -> no write
      { existingTaskId: 20, name: 'B', orderIndex: 1, removed: false },
      { existingTaskId: 30, name: 'C', orderIndex: 2, removed: false },
    ];

    expect(planTaskWrites(LOADED, rows, 7).updates).toEqual([]);
  });

  it('THROWS rather than defaulting expectedVersion when a loaded task carries no rowVersion', () => {
    const loaded: TaskItemDto[] = [
      { id: 4, backlogId: 7, taskName: 'Old', orderIndex: 0, status: 'Done', isActive: true },   // no version
    ];

    expect(() => planTaskWrites(loaded, [{ existingTaskId: 4, name: 'New', orderIndex: 0, removed: false }], 7))
      .toThrowError(/rowVersion/);
  });

  // A silently SKIPPED update is precisely the class of bug this module exists to prevent, so a row whose
  // loaded twin is missing must be loud rather than quietly dropped.
  it('THROWS on an edited row that has no counterpart in the loaded set', () => {
    expect(() => planTaskWrites(LOADED, [{ existingTaskId: 999, name: 'Ghost', orderIndex: 0, removed: false }], 7))
      .toThrowError(/999/);
  });
});
