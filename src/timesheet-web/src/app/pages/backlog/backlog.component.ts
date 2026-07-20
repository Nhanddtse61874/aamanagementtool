import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { EMPTY, Subject, catchError, forkJoin, startWith, switchMap } from 'rxjs';

import { TeamFilterComponent } from '../../components/team-filter/team-filter.component';
import { RealtimeService } from '../../core/realtime.service';
import { WorklogService } from '../../services/worklog.service';
import { BacklogEditorComponent } from './backlog-editor.component';
import {
  ALL, Filters, Options, Row, buildRows, coerceFilters, filterByTeams, filterRows, rebuildOptions, teamNameMap,
} from './backlog-list';

/**
 * What the editor is open ON.
 *
 * 🔴 `null` = CLOSED · `{ id: null }` = CREATE · `{ id: 7 }` = EDIT backlog 7.
 *
 * A bare `number | null` signal CANNOT express this, and the wrapper is not ceremony: `null` would have to
 * mean BOTH "the editor is closed" AND "the create dialog is open", which are the two states this whole
 * screen turns on. (`BacklogEditorComponent.backlogId` is itself `number | null`, where null means create —
 * so the id passed down is `Editing['id']` verbatim, and the wrapper is the only thing distinguishing it
 * from "no dialog at all".)
 */
interface Editing {
  readonly id: number | null;
}

/** Nothing filtered: the four dropdowns on ALL and an empty search box. */
function noFilters(): Filters {
  return { term: '', project: ALL, type: ALL, assignee: ALL, month: ALL };
}

/**
 * The Backlog grid — the list, against the real API.
 *
 * Before M8.6/T6 this screen was a mockup wired to nothing: `getBacklogs()` returned `of([])`, the four
 * dropdowns were hard-coded literals (`['All','2026-06','2026-07','2026-08']` — three months, chosen by hand,
 * wrong the moment the year turns), and the two buttons raised a TOAST and did nothing else. "+ New backlog"
 * said `New backlog created` and created no backlog. Both toasts are gone.
 *
 * Three things here are load-bearing and none of them are visible on screen:
 *
 * 1. **`getUserNames()`, never `getUsersAll()`.** `GET /api/users/all` is `RequireAuthorization(AdminPolicy)`
 *    and is deliberately absent from the generated client. An ordinary user reading it gets a 403 — which
 *    would take the `forkJoin` below down with it and BLANK THE ENTIRE LIST. `/api/users/names` is the
 *    non-admin route, and it returns DEACTIVATED users too, which is what lets a departed assignee's name
 *    still render on her row (see `buildRows`).
 *
 * 2. **`refresh` is a Subject, not a signal.** See its own comment.
 *
 * 3. **`DEFAULT` is excluded on the CLIENT, by `buildRows`.** The API must keep returning it, because Log
 *    Work needs it (ReadModels.cs:68 — "EVERY backlog item (incl. DEFAULT...)"). The exclusion belongs to the
 *    screen that does not want it. This component's whole job on that front is to call `buildRows` and not
 *    undo it.
 */
@Component({
  selector: 'app-backlog',
  standalone: true,
  imports: [FormsModule, BacklogEditorComponent, TeamFilterComponent],
  templateUrl: './backlog.component.html',
  styleUrl: './backlog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BacklogComponent {
  private readonly api = inject(WorklogService);
  private readonly realtime = inject(RealtimeService);

  /** Every backlog the user may see, DEFAULT already excluded. The grid's source of truth. */
  readonly rows = signal<Row[]>([]);

  /** The four dropdowns' contents, derived FROM THE LOADED ROWS — never hard-coded. See `applyRows`. */
  readonly options = signal<Options>(rebuildOptions([]));

  readonly filters = signal<Filters>(noFilters());

  /**
   * P6 — the shared multi-team checkbox filter (`<app-team-filter>`, TL-12's pattern reused verbatim).
   *
   * 🔴 Unlike the Task List, this NEVER triggers a re-fetch: `GET /api/backlogs` takes no team parameter by
   * design (see `WorklogService.getBacklogList`'s doc) and already returns everything the caller's teams own
   * — team filtering here is client-side, one more AND alongside the other four dropdowns. See `filterByTeams`
   * for the three-valued contract (`undefined`/`[]`/real ids) this signal carries.
   */
  readonly teamIds = signal<number[] | undefined>(undefined);

  /** True only once the user has explicitly unchecked every team — never on an unloaded/failed filter. */
  readonly noTeams = computed(() => this.teamIds()?.length === 0);

  /** WPF's `ShowTeamColumn`: the TEAM column stays hidden — no added noise — until >1 team is checked. */
  readonly showTeam = computed(() => (this.teamIds()?.length ?? 0) > 1);

  readonly loading = signal(false);
  readonly loadError = signal<string | null>(null);

  readonly editing = signal<Editing | null>(null);

  /**
   * 🔴 `computed`, not a template call. `filterRows`/`filterByTeams` are O(n), and `@for (r of ...)` calling
   * them inline would re-run on EVERY change-detection cycle — every keystroke, every hover, for every row.
   * As a computed it runs only when `rows`, `teamIds` or `filters` actually change. (`rebuildOptions` is O(n)
   * too; it is called once per load, in `applyRows`, for the same reason.)
   */
  readonly visible = computed(() => filterRows(filterByTeams(this.rows(), this.teamIds()), this.filters()));

  /**
   * "Re-read the list" (SignalR, a save, a retry after a failed load).
   *
   * 🔴 A Subject, NOT a signal — and that distinction is load-bearing, not stylistic. A refresh is an EVENT;
   * the rows on screen are STATE. `toObservable()` REPLAYS a signal's current value on subscribe, so a
   * `refresh` signal would fetch the list TWICE on every single load; and it propagates through an effect, so
   * a bump would only reach the pipeline on the next change-detection pass — a failed re-fetch would silently
   * not happen until something else moved. `LogWorkComponent` paid for both of these and documents them.
   */
  private readonly refresh = new Subject<void>();

  constructor() {
    this.refresh.pipe(
      // The initial load. `startWith` emits ONCE, at subscribe time — the one honest way to say "and also
      // fetch it now" without a signal's replay semantics (see `refresh`).
      startWith(undefined),
      switchMap(() => {
        this.loading.set(true);

        // getTeamsActive() — P6's team-NAME source, joined alongside the other two reads and refreshed on the
        // same cadence as getUserNames(): treated as equally essential, per this file's existing pattern,
        // rather than degrading independently the way `TaskListComponent.loadLookups` does. A team-list
        // failure now also blocks the grid load — see the PORT report for the trade-off.
        return forkJoin([this.api.getBacklogList(), this.api.getUserNames(), this.api.getTeamsActive()]).pipe(
          // 🔴 catchError INSIDE the switchMap. Outside, the FIRST failed read would complete the outer
          // stream and the screen would never fetch again — no SignalR refresh, no Retry button, nothing.
          // The pipeline has to survive its own errors, because the Retry button runs through it.
          catchError(() => {
            this.loading.set(false);
            // All three calls are plain GETs and declare only a 200 body; a ValidationBody branch here would
            // be dead code, so there is not one. One honest sentence, and a button to try again.
            this.loadError.set('Could not load the backlogs. Please try again.');
            return EMPTY;
          }),
        );
      }),
      takeUntilDestroyed(),
    ).subscribe(([items, names, teams]) => this.applyRows(buildRows(items, names, teamNameMap(teams))));

    // Someone ELSE changed this team's data. The server already excluded us from its own broadcast (that is
    // what X-Connection-Id buys), so there is no echo of our own writes to filter out here.
    this.realtime.dataChanged.pipe(takeUntilDestroyed()).subscribe(() => this.refresh.next());
    this.realtime.start();
  }

  private applyRows(rows: Row[]): void {
    this.loading.set(false);
    this.loadError.set(null);
    this.rows.set(rows);

    // 🔴 BOTH, and in this order — `rebuildOptions` alone is HALF the job.
    //
    // It rebuilds the four dropdowns from the data that actually arrived, but it CANNOT reset a filter that
    // is now pointing at a value the data no longer has: it never sees the filters. Delete the second line
    // and a user filtered to a backlog someone else just deleted sits in front of an empty grid, with a
    // dropdown displaying a value that is not in its own option list, and no way to understand why.
    const options = rebuildOptions(rows);
    this.options.set(options);
    this.filters.set(coerceFilters(this.filters(), options));
  }

  // ---- the filters ---------------------------------------------------------------------------------

  patch<K extends keyof Filters>(key: K, value: Filters[K]): void {
    this.filters.update(f => ({ ...f, [key]: value }));
  }

  clearFilters(): void {
    this.filters.set(noFilters());
  }

  /** `<app-team-filter>`'s `(selectionChange)`. No re-fetch — see `teamIds`'s own doc for why. */
  onTeams(ids: number[]): void {
    this.teamIds.set(ids);
  }

  // ---- the editor ----------------------------------------------------------------------------------

  openCreate(): void {
    this.editing.set({ id: null });
  }

  openEdit(row: Row): void {
    this.editing.set({ id: row.id });
  }

  closeEditor(): void {
    this.editing.set(null);
  }

  /**
   * The editor wrote a record. Close it, and RE-READ.
   *
   * The re-read is the point: a create adds a row, an edit can change the code, the project, the assignee or
   * the task count — all four of them columns on this grid — and it can move a row INTO or OUT OF the current
   * filter. Only the server knows what the list is now.
   *
   * No toast here. The editor already showed the one that names what actually happened ("Backlog created." /
   * "Backlog saved."), and it is the only party that knows which. A second one from up here would either
   * duplicate it or lie.
   */
  onSaved(): void {
    this.editing.set(null);
    this.refresh.next();
  }

  // ---- presentation --------------------------------------------------------------------------------

  reload(): void {
    this.refresh.next();
  }

  typeColor(type: string | null): { bg: string; c: string } | null {
    return this.api.typeColor(type);
  }

  avatar(name: string | null): string {
    return this.api.avatarColor(name);
  }
}
