import { HttpErrorResponse } from '@angular/common/http';

import {
  BacklogDto, BacklogUpdateRequest, NamedRefDto, TagDto, TaskItemDto, TaskListRowDto, ValidationBody,
} from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';

/**
 * The Task List screen's pure logic. Everything here is a FUNCTION OF ITS ARGUMENTS — no injection, no
 * signals, no DOM — so it can be tested without a TestBed, which is what makes the H1 guard below cheap
 * enough to actually mutation-check.
 */

// =====================================================================================================
// ScheduleState — 🔴 THE ORDINALS ARE A WIRE CONTRACT.
//
// `public enum ScheduleState { Normal, Warning, Late }` (Core/Models/ReadModels.cs:118) serialises as its
// ORDINAL: 0/1/2. The generated model is `export type ScheduleState = 0 | 1 | 2` and carries no names, so
// nothing in TypeScript will tell you if you get these backwards — you would simply paint every Late bar
// green. Named here once, and nowhere else.
// =====================================================================================================
export const SCHEDULE_NORMAL = 0;
export const SCHEDULE_WARNING = 1;
export const SCHEDULE_LATE = 2;

/** The fixed project order (C# `BacklogProjects.All`). Unknown / null projects sort LAST. */
export const PROJECTS: readonly string[] = ['ARCS', 'PlusArcs', 'ARMS', 'Other'];

/** C# `BacklogType.All` — the backlog/task type choices. */
export const TYPES: readonly string[] = ['Continue', 'Implement', 'Investigate', 'IT', 'Estimate'];

/** C# `TaskStatus.All` — the four task statuses. */
export const STATUSES: readonly string[] = ['Todo', 'In-process', 'Done', 'Pending'];

/** The month sentinel meaning "All months" (C# `TaskListService.AllMonths`). */
export const ALL_MONTHS = 0;

// =====================================================================================================
// CHIPS
// =====================================================================================================

export interface Chip {
  readonly kind: 'late' | 'warning' | 'custom';
  readonly text: string;
  readonly color: string | null;
  readonly icon: string | null;
}

/**
 * The chip row: AT MOST ONE system chip, then the custom tags.
 *
 * 🔴 `if / else if`, NEVER both — a backlog is Late OR At-risk, never both, because `ScheduleState` is one
 * value and `Late` already takes precedence over `Warning` server-side (ScheduleStateService step 3).
 * Rendering both would invent a state the server cannot produce.
 *
 * Custom tags are ordered by tag ID (Q4). The server already sorts them (`OrderBy(t => t.Id)`), so this
 * sort is defensive rather than load-bearing — but it is the screen's stated contract, and one sort of a
 * handful of tags costs nothing.
 */
export function buildChips(row: TaskListRowDto): Chip[] {
  const chips: Chip[] = [];

  if (row.scheduleState === SCHEDULE_LATE) {
    chips.push({ kind: 'late', text: '⚠ Late', color: null, icon: null });
  } else if (row.scheduleState === SCHEDULE_WARNING) {
    chips.push({ kind: 'warning', text: '⚠ At risk', color: null, icon: null });
  }

  for (const tag of [...(row.tags ?? [])].sort((a, b) => (a.id ?? 0) - (b.id ?? 0))) {
    chips.push({ kind: 'custom', text: tag.text ?? '', color: tag.color ?? null, icon: tag.icon ?? null });
  }

  return chips;
}

/**
 * 🔴 ZERO TASKS IS NOT DONE — and `[].every(...)` is `true`, so the guard is the whole function.
 *
 * The server applies this same rule (`tasks.Count > 0 && tasks.All(Status == "Done")`, TaskListService:104)
 * as an INPUT to ScheduleState, and then DISCARDS it: there is no `isDone` on `TaskListRowDto`. So this is
 * a re-derivation, and it exists only to tick the row's "done" affordance. It must not contradict the
 * server: invert it and every empty backlog reads Done, which is exactly backwards — a backlog with no plan
 * yet is the one most likely to be slipping.
 */
export function isDone(row: TaskListRowDto): boolean {
  const tasks = row.tasks ?? [];
  return tasks.length > 0 && tasks.every(t => t.status === 'Done');
}

// =====================================================================================================
// GROUPING — by PROJECT.
//
// 🔴 GROUPING BY TEAM IS NOT IMPLEMENTABLE AND IS DELIBERATELY ABSENT. The desktop groups by team when >1
// team is checked (TaskListViewModel:212, `GroupByTeam = showTeam`) — it reads `b.TeamId` straight off the
// Backlog ENTITY. The WIRE HAS NO SUCH FIELD: `TaskListRowDto` (Api/Contracts/Dtos.cs:141) carries no
// teamId and no team name, and neither does `BacklogListItemDto`. The only route that exposes a backlog's
// team is `GET /api/backlogs/{id}` — one call PER ROW.
//
// So team-banding needs `TeamId`/`TeamName` added to `TaskListRowDto` (a C# + `npm run gen:api` change).
// Until then this screen bands by project, always — which is precisely what the desktop does whenever a
// single team is in scope, so nothing here contradicts it.
// =====================================================================================================

export interface Band {
  readonly key: string;
  readonly rows: readonly TaskListRowDto[];
}

/** Index of a project in the fixed order; unknown / null sorts last. */
export function projectOrder(project: string | null | undefined): number {
  const i = PROJECTS.indexOf(project ?? '');
  return i === -1 ? Number.MAX_SAFE_INTEGER : i;
}

/**
 * Group the rows into project bands, ordered ARCS → PlusArcs → ARMS → Other, unknown last.
 *
 * Row order WITHIN a band is preserved — the server already sorts every row by `backlogCode`
 * (TaskListService:70), so re-sorting here would only risk disagreeing with it.
 */
export function groupRows(rows: readonly TaskListRowDto[]): Band[] {
  const bands = new Map<string, TaskListRowDto[]>();

  for (const row of rows) {
    const key = row.project ?? '—';
    const bucket = bands.get(key);
    if (bucket) bucket.push(row);
    else bands.set(key, [row]);
  }

  return [...bands.entries()]
    .map(([key, bandRows]) => ({ key, rows: bandRows }))
    .sort((a, b) => projectOrder(a.key) - projectOrder(b.key) || a.key.localeCompare(b.key));
}

// =====================================================================================================
// THE PROGRESS INPUT
// =====================================================================================================

export type ParsedProgress =
  | { readonly ok: true; readonly value: number | null }
  | { readonly ok: false };

/**
 * Parse the inline progress box.
 *
 *   ''      -> `{ ok: true, value: null }`   CLEARS the percent. The desktop does this too
 *                                            (TaskListRowVm:585 — empty ⇒ EditProgress = null ⇒ commit).
 *   '0'..'100' (a WHOLE number) -> that value.
 *   anything else -> `{ ok: false }`         🔴 NEVER COMMITS. Not "commit 0", not "commit null" — the
 *                                            caller must not write at all.
 *
 * The regex is deliberately strict: `^\d{1,3}$` rejects `5.5`, `-1`, `+5`, `1e2`, `0x10` and ` 5 ` before
 * the range check ever runs. `parseFloat`/`Number` would accept most of those and silently round.
 */
export function parseProgress(text: string): ParsedProgress {
  const trimmed = text.trim();
  if (trimmed === '') return { ok: true, value: null };
  if (!/^\d{1,3}$/.test(trimmed)) return { ok: false };

  const value = Number(trimmed);
  if (value < 0 || value > 100) return { ok: false };
  return { ok: true, value };
}

// =====================================================================================================
// "CONTINUE" — the target period
// =====================================================================================================

/**
 * The selected month + 1, rolling the year. `2026-12` -> `2027-01`.
 *
 * Never called in "All months": `continueBacklog` needs a concrete target and the button is hidden there.
 */
export function nextPeriod(year: number, month: number): string {
  const rollsOver = month === 12;
  const targetYear = rollsOver ? year + 1 : year;
  const targetMonth = rollsOver ? 1 : month + 1;
  return `${String(targetYear).padStart(4, '0')}-${String(targetMonth).padStart(2, '0')}`;
}

// =====================================================================================================
// 🔴 THE WHOLE-RECORD TRAP. THIS FUNCTION IS THE ONLY DEFENCE, AND IT IS LOAD-BEARING.
//
// `PUT /api/backlogs/{id}` REPLACES THE RECORD. BacklogEndpoints.cs:142-158 patches the entity with
// `existing with { BacklogCode = req.BacklogCode, ..., PcaContactId = req.PcaContactId }` — every field of
// `BacklogUpdateRequest`, UNCONDITIONALLY. A field you leave off the body is not "left alone": it is
// written NULL.
//
// 🔴 AND TYPESCRIPT WILL NOT CATCH IT. The generated DTOs are ALL-OPTIONAL (Swashbuckle emits no `required`
// for C# records), so a body missing `pcaContactId` COMPILES PERFECTLY CLEAN and silently wipes the column.
//
// So: START FROM THE LOADED DTO, override only what the user actually changed. `patch` is typed down to the
// eight fields this screen may touch (progress · the four dates · type · assignee · PCA), so nothing else can
// be patched by accident — and every OTHER field is copied across from `dto` below, by hand, one line each.
// If you add a column to the backlog, it must be added here too, or this screen will start nulling it.
//
// (`teamId` is the one field NOT copied — and must not be. `BacklogUpdateRequest` has no `teamId` property
// at all, deliberately: "there is nothing on the wire that could ever null it out" (BacklogEndpoints.cs:140).
// The server preserves it from the record it re-reads. Same for `id`, `createdAt` and `rowVersion`.)
// =====================================================================================================

/** The only fields the Task List may change on a backlog. Anything else is round-tripped, never patched. */
export type BacklogPatch = Partial<Pick<BacklogUpdateRequest,
  'progressPercent' | 'deadlineInternal' | 'deadlineExternal' | 'startDate' | 'endDate'
  | 'type' | 'assigneeUserId' | 'pcaContactId'>>;

export function toUpdateRequest(
  dto: BacklogDto,
  patch: BacklogPatch,
  auditNote: string | null = null,
): BacklogUpdateRequest {
  return {
    // ---- ROUND-TRIPPED. Not one of these is editable on this screen; every one is written by the PUT. ----
    backlogCode: dto.backlogCode,
    project: dto.project,
    periodMonth: dto.periodMonth,
    type: dto.type,
    assigneeUserId: dto.assigneeUserId,
    pcaContactId: dto.pcaContactId,
    note: dto.note,
    roughEstimateHours: dto.roughEstimateHours,
    officialEstimateHours: dto.officialEstimateHours,
    // These five are round-tripped too — `patch` overrides whichever ONE the user actually changed.
    startDate: dto.startDate,
    endDate: dto.endDate,
    deadlineInternal: dto.deadlineInternal,
    deadlineExternal: dto.deadlineExternal,
    progressPercent: dto.progressPercent,

    // ---- the user's actual edit ----
    ...patch,

    // ---- after the spread, so a patch can never clobber either ----
    // 🔴 A deadline change carries the reason note (H3). Other edits pass null: the audit row's Note column
    //    is for deadline reasons only.
    auditNote,
    // 🔴 The version we LOADED. `requireRowVersion` throws rather than send `undefined` — which would
    //    serialise as an absent key and assert "I believe this record is at version 0".
    expectedVersion: requireRowVersion(dto.rowVersion),
  };
}

/**
 * The body for a task's type + assignee.
 *
 * 🔴 BOTH FIELDS ARE WRITTEN VERBATIM, so both must be round-tripped from the loaded task even when the
 * user only changed one. `{ type: null }` CLEARS the type — it does not "leave it alone".
 */
export function toTaskExtended(
  task: TaskItemDto,
  patch: { type?: string | null; assigneeUserId?: number | null },
): { type: string | null; assigneeUserId: number | null; expectedVersion: number } {
  return {
    type: task.type ?? null,
    assigneeUserId: task.assigneeUserId ?? null,
    ...patch,
    expectedVersion: requireRowVersion(task.rowVersion),
  };
}

// =====================================================================================================
// THE INLINE PCT / PCA DROPDOWN OPTIONS
// =====================================================================================================

/** One option of an id-dropdown: a concrete id and the label to show. The "—" (null / unassigned) choice is
 *  rendered by the template, not here. */
export interface PickOption {
  readonly id: number;
  readonly label: string;
}

/** Map an ACTIVE `{ id, name }` list (users or PCA contacts) to pick options, dropping any row with no id. */
export function toPickOptions(rows: readonly { id?: number; name?: string | null }[]): PickOption[] {
  return rows
    .filter((r): r is { id: number; name?: string | null } => typeof r.id === 'number' && r.id > 0)
    .map(r => ({ id: r.id, label: r.name ?? '' }));
}

/**
 * The options for an inline PCT (assignee) / PCA dropdown seeded to `current`.
 *
 * 🔴 THE CHOICES ARE THE ACTIVE ROWS, BUT `current` MAY BE SOMEONE DEACTIVATED. `getUsersActive()` /
 * `getPcaContactsActive()` no longer list a departed person, and a `<select>` bound to an id that is not
 * among its options renders a BLANK box — the user cannot even see who is assigned. So when `current` is set
 * but absent from `active`, its name is resolved from `names` (id+name for EVERYONE, deactivated included)
 * and appended as "Name (inactive)". Same rule the backlog editor follows (WPF bug #6).
 *
 * The id still round-trips regardless — the write reads it from the freshly-GET'd DTO, not this dropdown — so
 * this is a RENDER fix, not a data-loss one. But a blank box the user cannot read is its own bug.
 */
export function pickOptions(
  active: readonly PickOption[],
  names: readonly NamedRefDto[],
  current: number | null | undefined,
): PickOption[] {
  if (current === null || current === undefined || active.some(o => o.id === current)) {
    return [...active];
  }
  const name = names.find(n => n.id === current)?.name;
  return [...active, { id: current, label: `${name ?? `#${current}`} (inactive)` }];
}

// =====================================================================================================
// ERRORS
// =====================================================================================================

/**
 * The server's own sentence, when it sent one.
 *
 * `ValidationBody` is `{ error?: string }` and the API uses it for every 400 that has something to say —
 * including the one this screen provokes most: continuing a backlog into a month that already has its code
 * ("'X' already exists in 2026-08"). Showing our own generic wording there would throw away the only
 * message that actually explains the refusal.
 */
export function messageOf(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as ValidationBody | null | undefined;
    if (typeof body?.error === 'string' && body.error !== '') return body.error;
    if (err.status === 409) {
      return 'Someone else changed this while you had it open. The screen has been refreshed — try again.';
    }
  }
  return fallback;
}

// =====================================================================================================
// SMALL VIEW HELPERS
// =====================================================================================================

/** The tag ids currently on a row — what seeds its `<app-tag-picker [selected]>`. */
export function tagIdsOf(tags: readonly TagDto[] | null | undefined): number[] {
  return (tags ?? []).map(t => t.id).filter((id): id is number => id !== undefined);
}

/** Whole hours, like the desktop (`$"{Row.LoggedHours:0}"`). */
export function hoursText(hours: number | null | undefined): string {
  return hours === null || hours === undefined ? '—' : String(Math.round(hours));
}

/** The desktop shows `0%` for an unset percent (P16), not an em dash. */
export function progressText(percent: number | null | undefined): string {
  return `${percent ?? 0}%`;
}
