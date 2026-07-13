import { TaskCreateRequest, TaskItemDto, TaskUpdateRequest } from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';
import { EditRow } from './backlog-form';

/**
 * The writes one editor save turns into, in the order T7 must execute them:
 *
 *   1. PUT /api/backlogs/{id}              -- checked, the BACKLOG's own version (not planned here)
 *   2. deletes: PUT /api/tasks/{id}/active { isActive: false }  -- BUMP-ONLY, no version
 *   3. inserts: POST /api/tasks            -- no status; the server defaults it to 'Todo'
 *   4. updates: PUT /api/tasks/{id}        -- checked
 *
 * Steps 2-4 touch DISJOINT rows, so no step can invalidate another's expectedVersion.
 *
 * `deletes` carries bare ids on purpose. PUT /api/tasks/{id}/active is bump-only BY DESIGN and
 * TaskActiveRequest has no version field at all -- sending one would make every delete fail.
 */
export interface TaskWritePlan {
  deletes: number[];
  inserts: TaskCreateRequest[];
  updates: { id: number; body: TaskUpdateRequest }[];
}

/**
 * 🔴 TWO TRAPS LIVE IN THIS FUNCTION, AND TYPESCRIPT CATCHES NEITHER.
 *
 * (1) STATUS. PUT /api/tasks/{id} writes name, order AND status in one call --
 *     `record TaskUpdateRequest(string TaskName, int OrderIndex, string Status, long ExpectedVersion)`,
 *     and BacklogEndpoints.cs:328-333 binds `Status = req.Status` with no null-guard. The backlog editor
 *     NEVER SHOWS a task's status, so the form does not carry the field: build the body from the form and
 *     `status` lands as null, silently wiping Todo / In-process / Done / Pending -- the column the whole
 *     Task List screen is built on. It must be ROUND-TRIPPED from the loaded task. The generated
 *     TaskUpdateRequest has `status?: string | null`, so omitting it compiles clean.
 *
 * (2) THE GAP. SetActiveAsync soft-deletes by setting is_active = 0 and LEAVES order_index untouched
 *     (TaskRepository.cs:123-128), while the read is `WHERE is_active = 1 ORDER BY order_index`. After one
 *     delete the survivors sit at 1,2,3 while RENDERING at 0,1,2. Writing only the rows that moved, at
 *     their absolute position, creates a TIE -- and ORDER BY with a tie is arbitrary, so the order
 *     silently scrambles. So EVERY survivor is reindexed 0..n: self-healing, and it cannot tie.
 *
 * The reindex takes each survivor's POSITION in `rows`, not its `orderIndex` field -- the field is the
 * server's last-known value, used only to decide whether a write is needed at all. This mirrors WPF
 * (BacklogEditorViewModel.ActiveTasks: "reindexed 0..n in display order", `active[i].OrderIndex = i`) and
 * reorder.ts, which likewise plans from the array's ORDER.
 *
 * Because rename and reorder ride in ONE write, PUT /api/tasks/{id}/order is not used by this screen at
 * all -- so the rename-before-reorder ordering hazard cannot exist here.
 */
export function planTaskWrites(loaded: TaskItemDto[], rows: EditRow[], backlogId: number): TaskWritePlan {
  const byId = new Map(loaded.map(t => [t.id, t] as const));

  // A removed row that was never saved (existingTaskId 0) is simply dropped -- there is nothing to delete.
  const deletes = rows.filter(r => r.removed && r.existingTaskId > 0).map(r => r.existingTaskId);

  const inserts: TaskCreateRequest[] = [];
  const updates: { id: number; body: TaskUpdateRequest }[] = [];

  // 🔴 Reindex EVERY survivor 0..n. `orderIndex` here is the POSITION, and it is the only order that ships.
  rows.filter(r => !r.removed).forEach((row, orderIndex) => {
    const taskName = row.name.trim();

    if (row.existingTaskId === 0) {
      inserts.push({ backlogId, taskName, orderIndex });   // no status -- the server defaults it to 'Todo'
      return;
    }

    const task = byId.get(row.existingTaskId);
    if (task === undefined) {
      // Impossible by construction (the rows are built FROM `loaded`), and loud on purpose anyway: a
      // silently skipped update is precisely the class of bug this module exists to prevent.
      throw new Error(
        `Task ${row.existingTaskId} is not in the loaded set. Refusing to plan a save that would silently ` +
        'drop this edit.',
      );
    }

    // An untouched, already-contiguous list must emit ZERO writes.
    if (task.taskName === taskName && task.orderIndex === orderIndex) return;

    updates.push({
      id: row.existingTaskId,
      body: {
        taskName,
        orderIndex,
        status: task.status ?? 'Todo',                       // 🔴 ROUND-TRIPPED from the loaded task.
        expectedVersion: requireRowVersion(task.rowVersion), // NEVER `!`, NEVER 0.
      },
    });
  });

  return { deletes, inserts, updates };
}
