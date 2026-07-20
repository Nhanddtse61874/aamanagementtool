import { BacklogListItemDto, NamedRefDto, TeamDto } from '../../api/models';

/** One row of the backlog grid. */
export interface Row {
  id: number;
  code: string;
  project: string;
  taskCount: number;
  month: string | null;
  type: string | null;
  assigneeUserId: number | null;
  assigneeName: string | null;      // null = Unassigned
  teamId: number | null;
  teamName: string;                 // resolved client-side; '—' for no team, '#id' for an unresolved one
}

/** The sentinel every dropdown carries as its first entry: "no filter on this column". */
export const ALL = 'All';

export interface Filters {
  term: string;                     // free text; matches code OR project
  project: string;
  type: string;
  assignee: string;                 // an assignee NAME, or ALL
  month: string;
}

export interface Options {
  projects: string[];
  types: string[];
  assignees: string[];
  months: string[];
}

/** The hidden backlog that holds the recurring default tasks. It belongs to no month and no project. */
const DEFAULT_CODE = 'DEFAULT';

/**
 * The team NAME for one row -- resolved CLIENT-SIDE from `teamNames`, exactly as the Task List screen
 * resolves its own band keys (TL-12, `task-list.model.ts` `teamBandKey`). `teamId` is already on the wire
 * (`BacklogListItemDto.teamId`); the server sends only the id, never the name.
 *
 * No team (should not occur in practice -- every create stamps `TeamId = activeTeam`) renders '—'; an id
 * the caller's team-name map does not know about (a team the map failed to load, or one this user is not a
 * member of) renders '#id' rather than going blank.
 */
function teamNameOf(teamId: number | null, teamNames: ReadonlyMap<number, string>): string {
  if (teamId === null) return '—';
  return teamNames.get(teamId) ?? `#${teamId}`;
}

/** `GET /api/teams` (active teams), reduced to an id -> name lookup. Mirrors `TaskListComponent.loadLookups`. */
export function teamNameMap(teams: readonly TeamDto[]): ReadonlyMap<number, string> {
  return new Map(teams.filter(t => t.id !== undefined).map(t => [t.id!, t.name ?? '']));
}

/**
 * 🔴 EXCLUDE `DEFAULT` HERE, ON THE CLIENT -- never on the server.
 *
 * The API must keep returning DEFAULT, because Log Work needs it: ReadModels.cs:68 -- "EVERY backlog item
 * (incl. DEFAULT and empty ones) becomes one collapsible group". Filtering it out of the endpoint would
 * silently break the Timesheet grid. The exclusion belongs to the screen that does not want it, which is
 * exactly where WPF puts it (TaskListViewModel.cs:220).
 *
 * `names` comes from GET /api/users/names, which returns GetAllAsync() -- INCLUDING DEACTIVATED users.
 * That is the whole reason the route exists: a departed assignee's name must still render on the grid.
 * (/api/users omits her; /api/users/all could name her but is admin-only and would 403 an ordinary user,
 * taking the screen's forkJoin down with it.)
 *
 * `teamNames` is the id -> name map built from `GET /api/teams` (P6) -- see `teamNameOf`.
 */
export function buildRows(
  items: BacklogListItemDto[],
  names: NamedRefDto[],
  teamNames: ReadonlyMap<number, string>,
): Row[] {
  const byId = new Map(names.map(n => [n.id, n.name ?? null] as const));

  return items
    .filter(i => i.backlogCode !== DEFAULT_CODE)
    .map(i => ({
      id: i.id ?? 0,
      code: i.backlogCode ?? '',
      project: i.project ?? '',
      taskCount: i.taskCount ?? 0,
      month: i.periodMonth ?? null,
      type: i.type ?? null,
      assigneeUserId: i.assigneeUserId ?? null,
      assigneeName: i.assigneeUserId == null ? null : byId.get(i.assigneeUserId) ?? null,
      teamId: i.teamId ?? null,
      teamName: teamNameOf(i.teamId ?? null, teamNames),
    }));
}

/**
 * Narrow to the CHECKED teams (P6) -- the `<app-team-filter>` reused verbatim from the Task List (TL-12).
 *
 * `undefined` = the filter has not emitted yet, or its reads failed -- NO narrowing, matching
 * `TeamFilterComponent`'s own degrade-open contract ("a broken team list narrows nobody's view").
 * `[]` = the user unchecked every team -- narrows to NOTHING. `BacklogComponent` renders its own
 * "No teams selected" state in this case (mirroring the Task List's `noTeams()`) rather than falling
 * through to the generic "no rows match your filters" empty state, whose "Clear filters" button would not
 * touch the team selection at all.
 */
export function filterByTeams(rows: Row[], teamIds: number[] | undefined): Row[] {
  if (teamIds === undefined) return rows;
  return rows.filter(r => r.teamId !== null && teamIds.includes(r.teamId));
}

/** AND across all five. `term` is a case-insensitive CONTAINS against the code OR the project. */
export function filterRows(rows: Row[], f: Filters): Row[] {
  const term = f.term.trim().toLowerCase();

  return rows.filter(r =>
    (term === '' ||
      r.code.toLowerCase().includes(term) ||
      r.project.toLowerCase().includes(term)) &&
    (f.project === ALL || r.project === f.project) &&
    (f.type === ALL || r.type === f.type) &&
    (f.assignee === ALL || r.assigneeName === f.assignee) &&
    (f.month === ALL || r.month === f.month));
}

/** Distinct, sorted, nulls dropped, ALL first. */
function optionsFrom(values: (string | null)[]): string[] {
  const distinct = new Set(values.filter((v): v is string => v !== null && v !== ''));
  return [ALL, ...[...distinct].sort((a, b) => a.localeCompare(b))];
}

/**
 * The four dropdowns are built FROM THE LOADED DATA -- not hard-coded, which is what they are today
 * (`monthOpts = ['All','2026-06','2026-07','2026-08']`). A month nobody has a backlog in is a dead option;
 * a month somebody does is missing from that literal the moment the year turns.
 *
 * A row with no type / month / assignee contributes no option -- offering a filter that can only ever
 * return zero rows would be the same dead option in a different disguise. Such rows are still reachable
 * under ALL. (Pair with `coerceFilters` to drop a selection that has vanished from the data.)
 */
export function rebuildOptions(rows: Row[]): Options {
  return {
    projects: optionsFrom(rows.map(r => r.project)),
    types: optionsFrom(rows.map(r => r.type)),
    assignees: optionsFrom(rows.map(r => r.assigneeName)),
    months: optionsFrom(rows.map(r => r.month)),
  };
}

/**
 * Reset any selection that has VANISHED from the reloaded data back to ALL.
 *
 * Without this, a filter left pointing at a value the data no longer contains silently shows an empty grid
 * with a dropdown displaying a value that is not in its own option list. `term` is free text, not an option
 * list, so it is never coerced.
 */
export function coerceFilters(f: Filters, o: Options): Filters {
  const keep = (value: string, options: string[]) => (options.includes(value) ? value : ALL);

  return {
    term: f.term,
    project: keep(f.project, o.projects),
    type: keep(f.type, o.types),
    assignee: keep(f.assignee, o.assignees),
    month: keep(f.month, o.months),
  };
}
