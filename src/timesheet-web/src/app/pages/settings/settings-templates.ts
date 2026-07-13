import { Observable, catchError, concatMap, map, of, throwError } from 'rxjs';
import { TaskTemplateDto } from '../../api/models';

/**
 * Task templates: grouping them for display, and the honest truth about saving one.
 *
 * ── THERE IS NO TEMPLATE ENTITY ─────────────────────────────────────────────────────────────────────────
 * A "template" is a NAMED GROUP of rows sharing `templateName` — one row per task. `TaskTemplateDto` has no
 * `rowVersion` because there is nothing to version: the group is not a record. That is why there are two
 * deletes on the API (one row, one whole group), and it is why editing one works the way it does below.
 *
 * ── 🔴 EDITING IS DELETE-BY-NAME THEN N × POST, AND IT IS NOT TRANSACTIONAL ──────────────────────────────
 * There is no "update template" route. To change a template you delete every row carrying its name and post
 * the new rows back. Those are N+1 separate HTTP calls, and NOTHING makes them atomic:
 *
 *      DELETE ok → POST 1 ok → POST 2 ok → POST 3 **fails**
 *
 * ...leaves the template REAL, PRESENT, and MISSING TWO TASKS. The old rows are gone; they are not coming
 * back. A `catch` that shrugs and says "Save failed" here would be a lie: the save did not fail, it
 * half-succeeded, and the template the admin is looking at is now silently wrong. Anyone who then applies it
 * to a backlog gets a truncated task list and no indication anything is amiss.
 *
 * So {@link saveTemplate} reports exactly how far it got — see {@link TemplateSaveError}. This is the same
 * stance `user-create.ts` takes toward the three-step user create, for the same reason: a multi-call write
 * that cannot roll back MUST tell the truth about where it stopped.
 */

/** One template, as the screen shows it: a name and its task names in order. */
export interface TemplateGroup {
  readonly name: string;
  readonly taskNames: readonly string[];
}

export interface TemplateApi {
  deleteTemplateByName(templateName: string): Observable<void>;
  createTemplate(body: { templateName: string; taskName: string; orderIndex: number }): Observable<unknown>;
}

/**
 * A save that stopped part-way. `created` is how many rows made it back before the failure — so the message
 * can say "3 of 5", which is the only number that tells the admin what state the template is actually in.
 */
export class TemplateSaveError extends Error {
  constructor(
    readonly templateName: string,
    /** True once the DELETE has landed — i.e. the OLD rows are gone and cannot be recovered. */
    readonly deleted: boolean,
    readonly created: number,
    readonly total: number,
    override readonly cause: unknown,
  ) {
    super(
      deleted
        ? `“${templateName}” is now INCOMPLETE: its old tasks were deleted and only ${created} of ${total} ` +
          'new tasks were written back before the save failed. The template is live in this state. ' +
          'Re-save it to finish, or delete it.'
        : `“${templateName}” was NOT changed — the delete failed, so its old tasks are still intact.`,
    );
    this.name = 'TemplateSaveError';
  }
}

/**
 * Fold the flat rows the API returns into the groups the screen renders.
 *
 * Rows are ordered by `orderIndex` within each group, and a row with no `templateName` is dropped rather
 * than collected under `"undefined"` — the wire type allows null and a null-named template is not a thing
 * the screen can act on (there is no name to delete BY).
 */
export function groupTemplates(rows: readonly TaskTemplateDto[]): TemplateGroup[] {
  const byName = new Map<string, TaskTemplateDto[]>();

  for (const row of rows) {
    const name = row.templateName ?? '';
    if (name === '') continue;
    const bucket = byName.get(name);
    if (bucket === undefined) byName.set(name, [row]);
    else bucket.push(row);
  }

  return [...byName.entries()]
    .map(([name, group]) => ({
      name,
      taskNames: [...group]
        .sort((a, b) => (a.orderIndex ?? 0) - (b.orderIndex ?? 0))
        .map(r => r.taskName ?? '')
        .filter(t => t !== ''),
    }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * Save a template: delete every row under its name, then post the new rows back IN ORDER.
 *
 * `concatMap`, not `mergeMap`: the rows carry an `orderIndex` and the delete must land before the first
 * insert. Firing them concurrently would let an insert race the delete that is meant to precede it — and
 * the delete would then remove the row that had just been written.
 *
 * On failure, throws {@link TemplateSaveError} carrying how many rows landed. It does NOT try to roll back:
 * there is nothing to roll back TO (the old rows are already gone), and a "repair" pass that itself failed
 * would leave a state nobody has reasoned about at all.
 *
 * Used for CREATE as well as EDIT — a create is just a save whose delete matches nothing, and
 * `DELETE /api/templates?templateName=` on a name that does not exist is a no-op, not an error.
 */
export function saveTemplate(
  api: TemplateApi,
  templateName: string,
  taskNames: readonly string[],
): Observable<void> {
  const rows = taskNames.map(t => t.trim()).filter(t => t !== '');

  let deleted = false;
  let created = 0;

  return api.deleteTemplateByName(templateName).pipe(
    concatMap(() => {
      deleted = true;

      // Reindexed 0..n from POSITION, so the saved order is exactly the order on screen. The old rows are
      // gone, so there is no gap to step around here — unlike the soft-deleted default tasks.
      return rows.reduce<Observable<unknown>>(
        (chain$, taskName, orderIndex) => chain$.pipe(
          concatMap(() => api.createTemplate({ templateName, taskName, orderIndex })),
          map(result => { created++; return result; }),
        ),
        of(null),
      );
    }),
    map(() => void 0),
    // One catch for the whole sequence. `deleted` and `created` are read AT THROW TIME, and they are what
    // say where it stopped — which is the only thing the admin actually needs to know.
    catchError((err: unknown) =>
      throwError(() => new TemplateSaveError(templateName, deleted, created, rows.length, err))),
  );
}

/**
 * The next `order_index` to append at — the highest EXISTING index plus one, NOT the row count.
 *
 * 🔴 Used by the Default Tasks list, where deactivation is a SOFT delete that leaves `order_index` alone
 * while the read filters `WHERE is_active = 1`. So the surviving indices have GAPS in them: deactivate the
 * row at index 1 of [0,1,2] and the live rows are 0 and 2 — a count-based append would return 2 and TIE with
 * the existing row, which `ORDER BY order_index` then resolves arbitrarily.
 *
 * (`-1` as the seed makes an empty list append at 0.)
 */
export function nextOrderIndex(items: readonly { orderIndex?: number }[]): number {
  return items.reduce((max, item) => Math.max(max, item.orderIndex ?? -1), -1) + 1;
}
