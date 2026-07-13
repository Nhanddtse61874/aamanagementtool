import {
  ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Observable, Subject, catchError, finalize, of, switchMap } from 'rxjs';

import {
  MissingLogWarning, TimesheetMonthlyReportResponse, TimesheetWeeklyReportResponse,
} from '../../api/models';
import { TeamFilterComponent } from '../../components/team-filter/team-filter.component';
import { DataKind, RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../services/toast.service';
import { ReportFilter, WorklogService } from '../../services/worklog.service';
import {
  ALL_PROJECTS, FlatNode, PROJECTS, ReportTarget, StatCard, WHOLE_TEAM_TARGET, WHOLE_TEAM_USER_ID,
  branchIds, buildTree, exportFileName, flattenTree, formatDayShort, mondayOf, n1, parseMonth, statCards,
  toIsoDate,
} from './report-model';

/**
 * The Reports screen. A faithful port of `ReportsViewModel` + `ReportsTab.xaml`.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 * 🔴 THE THREE WAYS THIS SCREEN SILENTLY READS EMPTY (OR, WORSE, READS EVERYTHING)
 *
 * All three are inversions: the value that LOOKS like "no filter" is the one the server reads as a filter,
 * or vice versa. All three fail with a 200 and a plausible-looking screen, so none of them would be caught
 * by a smoke test. `filter()` below is the single place all three are resolved.
 *
 *   1. `userId: 0` — the whole-team sentinel of the DROPDOWN. It must NEVER reach the wire. The C# is
 *      `userId is int uid ? GetReportRowsAsync(uid, ...) : GetExportRowsAsync(...)`, and **`0` IS an int**:
 *      sending it queries the report rows OF USER #0, who does not exist, and every grid renders EMPTY —
 *      on the DEFAULT selection, for everyone. "Whole team" is `undefined`, i.e. the key ABSENT.
 *   2. `project: 'All'` — the same shape of bug. The C# filters `rows.Where(r => r.Project == project)`
 *      unless the string is empty, and no row's project is literally "All". "All" is `undefined`.
 *   3. `teamIds: []` — the INVERSE of the other two, and the dangerous direction. An empty array appends NO
 *      query key, and the server reads an ABSENT `teamIds` as "EVERY TEAM THE CALLER BELONGS TO". So a user
 *      who unchecks every team would be shown MORE data, not less. There is no sentinel that fixes this
 *      (a fake id is intersected away into the same empty set) — the ONLY correct response is to NOT CALL.
 *      That is what `teamEmpty()` gates, on all three reads AND on the export.
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 *
 * <b>Auto-load.</b> There are no Weekly/Monthly buttons (WPF removed them): every filter change reloads.
 * Week -> weekly only. Month -> monthly only. Target, project or team -> BOTH. The two reload streams are
 * `switchMap`ped, so a fast sequence of filter changes cannot land out of order: the stale request is
 * CANCELLED, not merely ignored, and can never overwrite the newer one.
 */
@Component({
  selector: 'app-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TeamFilterComponent],
  templateUrl: './reports.component.html',
  styleUrl: './reports.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReportsComponent implements OnInit {
  private readonly api = inject(WorklogService);
  private readonly toast = inject(ToastService);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  // ---- the filter bar ---------------------------------------------------------------------------------

  readonly projects = PROJECTS;

  /** WPF `Targets`: the whole-team option, then one per ACTIVE user. Rebuilt on a `Users` broadcast. */
  readonly targets = signal<readonly ReportTarget[]>([WHOLE_TEAM_TARGET]);

  /** 🔴 `0` = whole team. A UI SENTINEL ONLY — `filter()` maps it to `undefined`. See the class doc. */
  readonly selectedUserId = signal<number>(WHOLE_TEAM_USER_ID);

  /** 🔴 `'All'` = no project filter. A UI SENTINEL ONLY — `filter()` maps it to `undefined`. */
  readonly selectedProject = signal<string>(ALL_PROJECTS);

  /** Always a Monday: the input normalises on change (see `mondayOf`). */
  readonly weekMonday = signal<string>(mondayOf(toIsoDate(new Date())));

  /** `<input type="month">`, so `"2026-07"`. */
  readonly selectedMonth = signal<string>(toIsoDate(new Date()).slice(0, 7));

  /** `undefined` = "all my teams" (no filter). Never `[]` — see the class doc. */
  private readonly teamIds = signal<number[] | undefined>(undefined);

  /** 🔴 The user has unchecked EVERY team. Render locally; call nothing. */
  readonly teamEmpty = signal(false);

  // ---- the data ---------------------------------------------------------------------------------------

  readonly weekly = signal<TimesheetWeeklyReportResponse | null>(null);
  readonly monthly = signal<TimesheetMonthlyReportResponse | null>(null);
  readonly missing = signal<readonly MissingLogWarning[]>([]);
  readonly exporting = signal(false);

  readonly detailRows = computed(() => this.weekly()?.detailRows ?? []);
  readonly monthlyRows = computed(() => this.monthly()?.monthlyTotals ?? []);

  /** The four cards. Client-side arithmetic over the weekly response — there is no metrics route. */
  readonly cards = computed<StatCard[]>(
    () => statCards(this.weekly(), this.missing(), this.weekMonday()),
  );

  // ---- the drill-down tree ----------------------------------------------------------------------------

  private readonly tree = computed(() => buildTree(this.monthly()?.projectTree));
  readonly expanded = signal<Readonly<Record<string, boolean>>>({});
  readonly flat = computed<FlatNode[]>(() => flattenTree(this.tree(), this.expanded()));

  /**
   * Drives the Expand-all / Collapse-all button — and it is `every`, NOT `some`.
   *
   * The team roots are seeded OPEN (see `seedRootExpansion`), so `some` would report "expanded" on first
   * paint and the button's FIRST click would COLLAPSE the tree — the opposite of what a user who can see
   * unexplored branches is reaching for. With `every`, the button reads "Expand all" until the tree really
   * is fully open, which is both what it says and what the user wants.
   */
  readonly allExpanded = computed(() => {
    const ids = branchIds(this.tree());
    return ids.length > 0 && ids.every(id => this.expanded()[id]);
  });

  // ---- reload plumbing --------------------------------------------------------------------------------

  private readonly weeklyTrigger = new Subject<void>();
  private readonly monthlyTrigger = new Subject<void>();

  constructor() {
    this.weeklyTrigger
      .pipe(
        switchMap(() => this.guarded(() => this.api.getWeeklyReport(this.weekMonday(), this.filter()))),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(response => this.weekly.set(response));

    this.monthlyTrigger
      .pipe(
        switchMap(() => {
          const month = parseMonth(this.selectedMonth());
          if (!month) return of(null);
          return this.guarded(() => this.api.getMonthlyReport(month[0], month[1], this.filter()));
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(response => {
        this.monthly.set(response);
        this.seedRootExpansion();
      });

    /**
     * WPF: the banner refreshes on `Logs` OR `Users`; the target dropdown rebuilds on `Users`.
     *
     * 🔴 `kind` IS A NUMBER. `e.kind === 'Logs'` compiles clean under `strict` and matches nothing, forever —
     * SignalR serialises the bare C# enum as its integer ordinal. Compare against the `DataKind` constants.
     */
    this.realtime.dataChanged
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(event => {
        if (event.kind === DataKind.Logs || event.kind === DataKind.Users) this.loadBanner();
        if (event.kind === DataKind.Users) this.loadTargets();
      });
  }

  ngOnInit(): void {
    this.loadTargets();
    this.loadBanner();
    this.reloadAll();
  }

  // ---- the wire filter --------------------------------------------------------------------------------

  /**
   * 🔴 THE ONE PLACE ALL THREE SENTINELS ARE RESOLVED. Every read and the export go through here. See the
   * class doc for what each of them does if it leaks onto the wire.
   */
  private filter(): ReportFilter {
    const userId = this.selectedUserId();
    const project = this.selectedProject();
    return {
      userId: userId > WHOLE_TEAM_USER_ID ? userId : undefined,
      project: project === ALL_PROJECTS ? undefined : project,
      teamIds: this.teamIds(),
    };
  }

  /**
   * 🔴 THE EMPTY-TEAM GATE. `teamIds: []` cannot be expressed on the wire — the key simply vanishes, and the
   * server reads an absent key as ALL MY TEAMS. So when the user has unchecked everything we do not call at
   * all; we resolve to `null` and the template renders its empty state locally.
   *
   * A failed read is NOT an empty selection: it toasts and resolves to `null` so the outer stream survives
   * (an error would kill the trigger subscription and the screen would never reload again).
   */
  private guarded<T>(call: () => Observable<T>): Observable<T | null> {
    if (this.teamEmpty()) return of(null);
    return call().pipe(
      catchError(() => {
        this.toast.show('Could not load the report.');
        return of(null);
      }),
    );
  }

  // ---- loads ------------------------------------------------------------------------------------------

  /**
   * WPF `LoadUsersAsync`: whole-team first, then every ACTIVE user. The selection is PRESERVED across a
   * rebuild when that user still exists, and falls back to the whole team when they no longer do.
   */
  private loadTargets(): void {
    this.api.getUsersActive()
      .pipe(
        catchError(() => of([])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(users => {
        const targets: ReportTarget[] = [
          WHOLE_TEAM_TARGET,
          ...users
            .filter(u => u.id !== undefined)
            .map(u => ({ userId: u.id as number, display: u.name ?? `#${u.id}` })),
        ];
        this.targets.set(targets);

        const previous = this.selectedUserId();
        if (!targets.some(t => t.userId === previous)) this.selectedUserId.set(WHOLE_TEAM_USER_ID);
      });
  }

  /**
   * 🔴 THE BANNER AND THE "NOT LOGGED" CARD IGNORE THE TEAM FILTER, DELIBERATELY.
   *
   * `GET /api/reports/missing-logs` takes NO parameters at all: N is a server-side setting (a client-supplied
   * N is a DoS vector) and the team scope is the caller's ACTIVE team, applied inside the service. WPF is the
   * same. So this load is NOT part of `reloadAll()` and does not re-run on a team change — it re-runs only on
   * a `Logs`/`Users` broadcast, exactly as `ReportsViewModel` does.
   */
  private loadBanner(): void {
    this.api.getMissingLogs()
      .pipe(
        catchError(() => of([])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(missing => this.missing.set(missing));
  }

  private reloadAll(): void {
    this.weeklyTrigger.next();
    this.monthlyTrigger.next();
  }

  // ---- filter events (WPF: the four `OnSelected*Changed` partials) --------------------------------------

  /** Target changed -> BOTH grids (WPF `RefreshReportsAsync`). */
  onTargetChange(userId: string): void {
    this.selectedUserId.set(Number(userId));
    this.reloadAll();
  }

  /** Project changed -> BOTH grids. */
  onProjectChange(project: string): void {
    this.selectedProject.set(project);
    this.reloadAll();
  }

  /** Week changed -> the WEEKLY grid only. Normalised to that week's Monday first. */
  onWeekChange(iso: string): void {
    if (!iso) return;
    this.weekMonday.set(mondayOf(iso));
    this.weeklyTrigger.next();
  }

  /** Month changed -> the MONTHLY grid and the tree only. */
  onMonthChange(month: string): void {
    if (!parseMonth(month)) return;
    this.selectedMonth.set(month);
    this.monthlyTrigger.next();
  }

  /**
   * The shared `<app-team-filter>` emits its seeded default on init and on every toggle.
   *
   * 🔴 `[]` IS A REAL VALUE AND MUST NOT BE FORWARDED TO THE API. It means the user unchecked everything;
   * on the wire it would mean the exact opposite. We record it and stop calling.
   */
  onTeamSelectionChange(ids: number[]): void {
    const empty = ids.length === 0;
    this.teamEmpty.set(empty);
    this.teamIds.set(empty ? undefined : ids);
    this.reloadAll();
  }

  // ---- the tree -----------------------------------------------------------------------------------------

  /** Newly-seen team roots start OPEN, so the tree never renders as an unexplained wall of one-liners.
   *  A root the user has explicitly collapsed keeps its `false` and is not re-opened by a reload. */
  private seedRootExpansion(): void {
    const roots = this.tree();
    if (roots.length === 0) return;
    this.expanded.update(current => {
      const next = { ...current };
      for (const root of roots) if (!(root.id in next)) next[root.id] = true;
      return next;
    });
  }

  toggleNode(id: string): void {
    this.expanded.update(current => ({ ...current, [id]: !current[id] }));
  }

  /**
   * Expand-all / collapse-all (WPF `ToggleDrillExpand`).
   *
   * Collapse writes an explicit `false` for every branch rather than clearing the map — a cleared map would
   * make every root "never seen" again, and `seedRootExpansion` would helpfully re-open them all on the very
   * next filter change, undoing the user's collapse in front of them.
   */
  toggleAll(): void {
    const open = !this.allExpanded();
    const next: Record<string, boolean> = {};
    for (const id of branchIds(this.tree())) next[id] = open;
    this.expanded.set(next);
  }

  // ---- export -------------------------------------------------------------------------------------------

  /**
   * WPF `BuildExcelExportAsync` + `SuggestedExportFileName`.
   *
   * 🔴 THE EXPORT USES THE MONTH, NOT THE WEEK. `ExportFilter(userId, Year, Month, project, teamIds)` — the
   * selected week is not part of it and never was. A user who changes the week and hits Export gets the same
   * file; that is correct, and the button's tooltip says so.
   *
   * 🔴 AND IT IS GATED ON `teamEmpty()` TOO. The export shares `EffectiveTeamIds` with the two reads, so an
   * empty selection would export EVERY team the user belongs to — a wider file than the screen they are
   * looking at. The button is disabled; this is the belt to that braces.
   */
  exportExcel(): void {
    if (this.teamEmpty() || this.exporting()) return;

    const month = parseMonth(this.selectedMonth());
    if (!month) return;

    this.exporting.set(true);
    this.api.exportExcel(month[0], month[1], this.filter())
      .pipe(
        finalize(() => this.exporting.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: blob => {
          this.saveBlob(blob, exportFileName(this.selectedMonth(), this.currentTarget()));
          this.toast.show('Exported to Excel');
        },
        error: () => this.toast.show('Export failed.'),
      });
  }

  /** The object-URL / anchor dance. Its own method so a test can stub it — Karma runs a real Chrome, and a
   *  real `<a download>` click would try to write a real file. */
  saveBlob(blob: Blob, fileName: string): void {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);
  }

  private currentTarget(): ReportTarget {
    return this.targets().find(t => t.userId === this.selectedUserId()) ?? WHOLE_TEAM_TARGET;
  }

  // ---- presentation -------------------------------------------------------------------------------------

  readonly formatDay = formatDayShort;

  /** The two DataGrids render a BARE `N1` (WPF `StringFormat=N1`) — no unit. Only the stat cards and the
   *  tree carry the trailing "h" (`StringFormat=' — {0:N1}h'`). */
  hours(value: number | null | undefined): string {
    return n1(value ?? 0);
  }

  metricColor(tone: string): string {
    return tone === 'accent' ? 'var(--accent-700)' : tone === 'warn' ? '#B5791F' : 'var(--ink)';
  }

  /** Team · Project · Backlog · Task · Date — five levels, so five colours. */
  labelColor(node: FlatNode): string {
    if (node.isLeaf) return 'var(--muted)';
    return ['var(--ink)', 'var(--accent-700)', '#5B8DEF', 'var(--ink)', 'var(--muted)'][
      Math.min(node.depth, 4)
    ];
  }
}
