import {
  ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, output, signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, filter, forkJoin, of, switchMap } from 'rxjs';

import { TeamDto } from '../../api/models';
import { DataKind, RealtimeService } from '../../core/realtime.service';
import { WorklogService } from '../../services/worklog.service';

/**
 * The shared multi-team checkbox filter. Four screens need it: Backlog, Task List, Reports, Daily Board.
 * Ported from WPF's `TeamFilterViewModel` (TM-07), whose contract it keeps exactly.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 * 🔴 THE CONTRACT PHASE 2 MUST HONOUR, IN ONE SENTENCE:
 *
 *     WHEN `empty()` IS TRUE THE SCREEN MUST RENDER ITS EMPTY STATE LOCALLY AND MAKE NO API CALL AT ALL --
 *     BECAUSE `teamIds: []` ON THE WIRE MEANS "ALL MY TEAMS", THE EXACT INVERSE OF WHAT IT LOOKS LIKE.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 *
 * <b>🔴 WHY AN EMPTY SELECTION CANNOT BE SENT.</b> WPF's contract is `CheckedTeamIds = []` -> NO teams, never
 * "all". That sentence CANNOT BE PUT ON THIS WIRE:
 *
 *   - the generated `RequestBuilder` serialises a query array with `explode: true` -- one `?teamIds=` entry
 *     PER ELEMENT (`QueryParameter.append`). An empty array iterates ZERO times and appends NOTHING, so
 *     `teamIds: []` goes out BYTE-IDENTICAL to `teamIds: undefined`: the key is ABSENT from the URL.
 *   - and the server reads an ABSENT key as "every team the caller belongs to":
 *         if (!http.Request.Query.TryGetValue("teamIds", out var raw)) return ctx.MemberTeamIds;
 *
 * So a screen that "filters to nothing" by passing `[]` renders EVERYTHING -- every backlog of every team the
 * user is in. The worst possible direction for this bug to fail in, and it fails SILENTLY.
 *
 * The server CAN tell `?teamIds=` (present, empty) from an absent key -- it hand-reads the raw query string
 * precisely so it can. THE GENERATED CLIENT SIMPLY CANNOT EMIT THAT FORM. There is no sentinel that helps
 * either: a fake id (`-1`, `0`) is INTERSECTED away server-side into the same empty set that already means
 * "all". 🔴 DO NOT INVENT ONE. Read `empty()` and do not call.
 *
 * <b>🔴 WHY `availableTeams` IS NOT `me.memberTeamIds`.</b> `MeResponse` hands the client the WIDER set:
 *
 *     ctx.MemberTeamIds = GetTeamIdsForUserAsync = every UserTeams row, with NO is_active filter.
 *     AvailableTeams    = GetActiveAsync() ∩ memberships  -- strictly NARROWER.   (SettingsEndpoints.cs)
 *
 * Bind the filter to `memberTeamIds` and it offers a DEACTIVATED team that WPF hides -- and that
 * `PUT /api/me/active-team` would reject with a 400. The intersection below IS `AvailableTeams`, and it is
 * computed HERE, once, so that four screens cannot each get it subtly wrong.
 */
@Component({
  selector: 'app-team-filter',
  standalone: true,
  templateUrl: './team-filter.component.html',
  styleUrl: './team-filter.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TeamFilterComponent implements OnInit {
  private readonly api = inject(WorklogService);
  private readonly realtime = inject(RealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * The checked team ids, whenever they change -- INCLUDING when they become empty.
   *
   * 🔴 An empty emission is a real, meaningful value: it means "the user has unchecked everything". It does
   * NOT mean "no filter". Consumers must branch on `empty()` (or on `ids.length === 0`) and skip the call.
   */
  readonly selectionChange = output<number[]>();

  private readonly _teams = signal<readonly TeamDto[]>([]);
  private readonly _checked = signal<ReadonlySet<number>>(new Set());
  private readonly _loaded = signal(false);

  /** `AvailableTeams`: the ACTIVE teams the user is a member of. Never `memberTeamIds` -- see the class doc. */
  readonly availableTeams = this._teams.asReadonly();

  /**
   * True once the two reads have SUCCEEDED. Stays false forever if they failed -- which is deliberate, and is
   * what keeps `empty()` from reporting a network error as "the user deselected everything". See `load()`.
   */
  readonly loaded = this._loaded.asReadonly();

  /** Hidden entirely for a single-team user: there is nothing to filter. (WPF: `ShowFilter`.) */
  readonly visible = computed(() => this._loaded() && this._teams().length > 1);

  /** WPF: `HeaderText` -- "Teams (1)" / "Teams (2)". */
  readonly header = computed(() => `Teams (${this._checked().size})`);

  /** WPF: `ShowTeamColumn`. Owners show a per-team chip/column only when more than one team is in scope. */
  readonly showTeamColumn = computed(() => this._checked().size > 1);

  /**
   * 🔴 THE ONE THE FOUR SCREENS MUST READ. True when the user has unchecked every team.
   *
   * `selectedIds()` is then `[]`, and `[]` CANNOT BE SENT (see the class doc). Render the empty state
   * locally; do not call the API.
   */
  readonly empty = computed(() => this._loaded() && this._checked().size === 0);

  /** The checked ids, ascending. Safe to pass to the API **only when `empty()` is false**. */
  readonly selectedIds = computed(() => [...this._checked()].sort((a, b) => a - b));

  /**
   * 🔴 THE SEED RUNS HERE, NOT IN THE CONSTRUCTOR, AND THAT IS LOAD-BEARING.
   *
   * `load()` emits `selectionChange` with the seeded default. Angular wires a parent's `(selectionChange)`
   * subscription AFTER the directive is constructed but BEFORE `ngOnInit` -- so an emission from the
   * constructor is sent to NOBODY. In production the two reads are real HTTP and land asynchronously, which
   * hides the bug; the moment a parent (or a test) has them resolve synchronously, the screen never receives
   * its initial team ids and silently queries with `undefined` -- i.e. ALL teams.
   *
   * Found by the test `emits the seeded default, so a screen never has to guess it`, which is exactly what it
   * is there for.
   */
  ngOnInit(): void {
    this.load();
  }

  constructor() {
    /**
     * WPF's `CurrentTeamService` re-resolves `AvailableTeams` on a `DataKind.Teams` broadcast, and the filter
     * resets with it. The web can only do this now that the feed says WHAT changed (M9/P6d) -- and this is
     * that payload's first consumer.
     *
     * `Teams` is the kind for team CRUD and membership changes. A team the user was just removed from, or one
     * an admin just deactivated, must not linger in the list as a checkable option that returns nothing.
     *
     * NOTE it does NOT fire when the user switches their OWN active team: `PUT /api/me/active-team` is the one
     * mutating route in the API that notifies nobody, deliberately (it would broadcast to the team group MINUS
     * the caller -- i.e. to everyone EXCEPT the one person whose scope actually changed). That path calls
     * `reload()` directly; see its doc.
     */
    this.realtime.dataChanged
      .pipe(filter(e => e.kind === DataKind.Teams), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.reload());
  }

  /**
   * Re-seed from the server and RESET the selection to `{new active team}`.
   *
   * 🔴 THIS IS THE "on active-team change -> reset" HALF OF THE CONTRACT (WPF `OnActiveTeamChanged` ->
   * `Reload()` + `SelectionChanged`, resolved decision F-Q3). It is a PUBLIC METHOD rather than a subscription
   * because the web app has no team switcher yet: `WorklogService.setActiveTeam()` exists and has no caller,
   * and the sidebar's team `<select>` is still hard-coded mockup markup. Whoever wires that switcher calls
   * this immediately after `setActiveTeam()` completes -- the server will not tell us, by design.
   */
  reload(): void {
    this.load();
  }

  toggle(teamId: number): void {
    const next = new Set(this._checked());
    if (!next.delete(teamId)) next.add(teamId);
    this._checked.set(next);
    this.emit();
  }

  isChecked(teamId: number): boolean {
    return this._checked().has(teamId);
  }

  /**
   * The two reads, joined, then intersected.
   *
   * 🔴 A FAILED READ MUST NOT LOOK LIKE AN EMPTY SELECTION, and getting this wrong is a data leak in the
   * making. `empty()` is derived from `_loaded && _checked.size === 0` -- so setting `_loaded` on the failure
   * path would leave zero teams checked and make `empty()` TRUE, telling the screen "the user unchecked
   * everything: render an empty view and call nothing". A network error is not a user's choice.
   *
   * So the failure path sets NOTHING. The filter stays UNLOADED, which means: hidden (`visible()` false),
   * NOT empty (`empty()` false), and no `selectionChange` is emitted -- so the screen keeps its own
   * `teamIds: undefined`, which the server reads as "all the teams you belong to". That is the correct
   * degradation: a broken team list narrows nobody's view and takes no screen down with it.
   */
  private load(): void {
    of(null)
      .pipe(
        switchMap(() => forkJoin({ me: this.api.me(), teams: this.api.getTeamsActive() })),
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(result => {
        if (result === null) return;

        const { me, teams } = result;

        // 🔴 THE INTERSECTION. `memberTeamIds` is the WIDER set (no `is_active` filter); the active teams are
        // the narrower one. `AvailableTeams` is what falls out of both. Using either alone is a bug.
        const memberIds = new Set(me.memberTeamIds ?? []);
        const available = teams.filter(t => t.id !== undefined && memberIds.has(t.id));

        // Default = the ACTIVE TEAM ONLY. Not "all", and not "none".
        const active = me.activeTeamId ?? null;
        const checked = new Set<number>(
          active !== null && available.some(t => t.id === active) ? [active] : [],
        );

        this._teams.set(available);
        this._checked.set(checked);
        this._loaded.set(true);

        this.emit();
      });
  }

  private emit(): void {
    this.selectionChange.emit(this.selectedIds());
  }
}
