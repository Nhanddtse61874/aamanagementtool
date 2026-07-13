import {
  MissingLogWarning, TeamNode, TimesheetWeeklyReportResponse,
} from '../../api/models';

/**
 * The Reports screen's pure arithmetic and shaping. No Angular, no HTTP — so every rule below is unit
 * testable without a TestBed, which is the point: three of these functions encode a bug the WPF app already
 * paid for once.
 */

// =======================================================================================================
// THE PROJECT LIST
//
// 🔴 `PlusArcs` IS REAL AND THE OLD MOCKUP DROPPED IT. The source of truth is `BacklogProjects.All`
// (TimesheetApp.Core/Models/Entities.cs) = ARCS · PlusArcs · ARMS · Other, and `ReportsViewModel` prepends
// "All". A project missing from this list is a project nobody can filter by — silently.
// =======================================================================================================

/** WPF: `new[] { "All" }.Concat(BacklogProjects.All)`. */
export const PROJECTS: readonly string[] = ['All', 'ARCS', 'PlusArcs', 'ARMS', 'Other'] as const;

/** The "no project filter" sentinel. Never goes on the wire — see `ReportsComponent.filter()`. */
export const ALL_PROJECTS = 'All';

/** The "whole team" sentinel of the Report-for dropdown. 🔴 A UI VALUE ONLY — see `ReportsComponent.filter()`. */
export const WHOLE_TEAM_USER_ID = 0;

/** WPF: `ReportTarget(int UserId, string Display)`. `userId === 0` is the whole team. */
export interface ReportTarget {
  readonly userId: number;
  readonly display: string;
}

export const WHOLE_TEAM_TARGET: ReportTarget = {
  userId: WHOLE_TEAM_USER_ID,
  display: 'Whole team (all)',
};

// =======================================================================================================
// FORMATTING
// =======================================================================================================

/**
 * .NET's `N1` — one decimal, with the thousands separator. `toFixed(1)` alone would render a team's monthly
 * total as `1234.5` where every other surface in the product says `1,234.5`.
 */
export function n1(value: number): string {
  return value.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

/** `N1` + the unit, as every WPF stat card and tree node renders it: `"12.5h"`. */
export function hoursText(value: number | null | undefined): string {
  return `${n1(value ?? 0)}h`;
}

const DAY_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

/**
 * Parse a wire date (`DateOnly` -> `"2026-07-06"`) into a LOCAL `Date`.
 *
 * 🔴 NEVER `new Date("2026-07-06")`. A bare date string is parsed by the spec as UTC MIDNIGHT, so west of
 * Greenwich `getDate()` returns the PREVIOUS DAY — every date in the weekly grid and every leaf of the tree
 * would render one day early, for half the planet, while every test written in UTC stayed green. Splitting
 * the string and using the multi-argument constructor builds a LOCAL midnight, which cannot drift.
 */
export function parseIsoDate(iso: string | null | undefined): Date | null {
  if (!iso) return null;
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  if (!match) return null;
  const [, y, m, d] = match;
  const date = new Date(Number(y), Number(m) - 1, Number(d));
  return Number.isNaN(date.getTime()) ? null : date;
}

function pad2(value: number): string {
  return String(value).padStart(2, '0');
}

/** `yyyy-MM-dd`, the wire format. The inverse of `parseIsoDate`. */
export function toIsoDate(date: Date): string {
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`;
}

/** WPF `StringFormat=ddd dd/MM` — the weekly grid's Date column. `"Mon 06/07"`. */
export function formatDayShort(iso: string | null | undefined): string {
  const date = parseIsoDate(iso);
  if (!date) return '';
  return `${DAY_NAMES[date.getDay()]} ${pad2(date.getDate())}/${pad2(date.getMonth() + 1)}`;
}

/** WPF `StringFormat='ddd, yyyy-MM-dd'` — the drill-down tree's Date LEAF. `"Mon, 2026-07-06"`. */
export function formatLeafDate(iso: string | null | undefined): string {
  const date = parseIsoDate(iso);
  if (!date) return '';
  return `${DAY_NAMES[date.getDay()]}, ${toIsoDate(date)}`;
}

/**
 * Snap any date to the Monday of its week (WPF `DateHelpers.MondayOf`).
 *
 * The field is labelled "Week (Mon)" and the API takes a `monday`: it reads rows over `monday .. monday+4`
 * while `DaysLogged` independently normalises to the real Monday for its working-day denominator. Send a
 * Wednesday and those two disagree — you get a Wed–Sun row set measured against a Mon–Fri denominator, and
 * DAYS LOGGED becomes quietly meaningless. Normalising on input is what keeps them the same week.
 */
export function mondayOf(iso: string): string {
  const date = parseIsoDate(iso);
  if (!date) return iso;
  const shift = (date.getDay() + 6) % 7;      // Sun(0) -> 6, Mon(1) -> 0, ... Sat(6) -> 5
  date.setDate(date.getDate() - shift);
  return toIsoDate(date);
}

/** `"2026-07"` -> `[2026, 7]`. The `<input type="month">` value; `month` is 1-based, as the API wants. */
export function parseMonth(value: string): readonly [year: number, month: number] | null {
  const match = /^(\d{4})-(\d{2})$/.exec(value);
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  if (month < 1 || month > 12) return null;
  return [year, month];
}

/** WPF `SuggestedExportFileName()`: `Worklog-2026-07-Nhan.xlsx`, whole team -> `Worklog-2026-07-team.xlsx`. */
export function exportFileName(month: string, target: ReportTarget): string {
  const who = target.userId > 0 ? target.display : 'team';
  // WPF strips Path.GetInvalidFileNameChars(); the browser equivalent is the same idea, narrower charset.
  const safe = who.replace(/[\\/:*?"<>|]/g, '').trim() || 'team';
  return `Worklog-${month}-${safe}.xlsx`;
}

// =======================================================================================================
// THE FOUR STAT CARDS — CLIENT-SIDE ARITHMETIC
//
// 🔴 THERE IS NO `GET /api/reports/metrics`, AND THERE NEVER WAS. `/api/reports/*` is weekly, monthly and
// missing-logs — that is all three of them. Every number below is derived from the WEEKLY response (which
// carries `dayTotals` AND `daysLogged` in ONE round-trip, deliberately, so the cards cannot show three
// different snapshots of the week) plus the length of the missing-logs list.
// =======================================================================================================

export type StatTone = 'ink' | 'accent' | 'warn';

export interface StatCard {
  readonly label: string;
  readonly value: string;
  readonly sub: string;
  readonly tone: StatTone;
  readonly icon: string;
}

export function statCards(
  weekly: TimesheetWeeklyReportResponse | null,
  missing: readonly MissingLogWarning[],
  weekMonday: string,
): StatCard[] {
  const dayTotals = weekly?.dayTotals ?? [];
  const total = dayTotals.reduce((sum, d) => sum + (d.totalHours ?? 0), 0);

  // 🔴 THE SERVER OWNS `daysLogged`. DO NOT RE-DERIVE IT FROM `dayTotals`.
  //
  // The denominator used to be `rows.Count` — and `rows` only ever holds days that HAVE logs, so it moved
  // in lockstep with the numerator and the stat could only ever read N/N. It is real business arithmetic
  // (Mon–Fri MINUS public holidays, HOL-02), it needs the holiday table, and `ReportAggregator.DaysLogged`
  // is where it lives. `dayTotals.length` is the same wrong number wearing a different name.
  const logged = weekly?.daysLogged?.logged ?? 0;
  const workingDays = weekly?.daysLogged?.workingDays ?? 0;

  // 🔴 GUARD THE ZERO. A week with nothing logged divides by 0 -> NaN (or Infinity), and `NaN.toFixed(1)`
  // renders the literal string "NaN" on the card. WPF: `stat.Logged == 0 ? 0m : total / stat.Logged`.
  const avg = logged === 0 ? 0 : total / logged;

  const monday = parseIsoDate(weekMonday);
  const friday = monday ? new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 4) : null;
  const range = monday && friday
    ? `${pad2(monday.getDate())}/${pad2(monday.getMonth() + 1)} – ${pad2(friday.getDate())}/${pad2(friday.getMonth() + 1)}`
    : '';

  return [
    { icon: '⏱', label: 'WEEK TOTAL', value: hoursText(total), sub: range, tone: 'accent' },
    { icon: '📊', label: 'AVG / DAY', value: hoursText(avg), sub: 'across days logged', tone: 'ink' },
    { icon: '📅', label: 'DAYS LOGGED', value: `${logged} / ${workingDays}`, sub: 'working days', tone: 'ink' },
    // 🔴 This card, and the banner it counts, IGNORE THE TEAM FILTER — `GET /api/reports/missing-logs` is
    // scoped to the caller's ACTIVE team server-side and accepts no team parameter at all. WPF behaves
    // identically. The sub-label says so out loud rather than leaving the user to wonder.
    { icon: '⚠', label: 'NOT LOGGED', value: String(missing.length), sub: 'in your active team', tone: 'warn' },
  ];
}

// =======================================================================================================
// THE DRILL-DOWN TREE
//
// 🔴 AN ARRAY OF ROOTS, FIVE HETEROGENEOUS LEVELS, AND NO USABLE IDS.
//
//   TeamNode(teamName, totalHours, projects[])
//     ProjectNode(project, totalHours, backlogs[])
//       BacklogNode(backlogCode, project, totalHours, tasks[])
//         TaskNode(taskId, taskName, totalHours, dates[])
//           DateEntry(date, totalHours)                      <- leaf
//
// Five record types, five different name fields, five different child-collection names. Only `TaskNode`
// carries an id at all, and one id at one level cannot key an expand/collapse map that spans five. So the
// ids are SYNTHESISED here, as the PATH to the node.
//
// 🔴 THE IDS MUST BE STABLE ACROSS RELOADS, which is why they are built from NAMES and not from array
// indices. Every filter change re-fetches the tree; index-based ids ("root 0") would silently re-point the
// user's open branches at whatever team happens to sort first in the NEW result.
// =======================================================================================================

/** Unit Separator. Cannot occur in a team name, project, backlog code or task name — so a path built with
 *  it cannot be forged into a collision by data. */
const SEP = '';

export interface ReportTreeNode {
  readonly id: string;
  readonly label: string;
  readonly hours: string;
  readonly children: readonly ReportTreeNode[];
}

/** The row the template renders. Depth 0..4. */
export interface FlatNode {
  readonly id: string;
  readonly label: string;
  readonly hours: string;
  readonly depth: number;
  readonly hasChildren: boolean;
  readonly expanded: boolean;
  readonly isLeaf: boolean;
}

/** `TeamNode[]` (the wire) -> `ReportTreeNode[]` (uniform, with synthesised path ids). */
export function buildTree(roots: readonly TeamNode[] | null | undefined): ReportTreeNode[] {
  return (roots ?? []).map(team => {
    const teamId = `${SEP}${team.teamName ?? ''}`;
    return {
      id: teamId,
      label: team.teamName ?? '(no team)',
      hours: hoursText(team.totalHours),
      children: (team.projects ?? []).map(project => {
        const projectId = `${teamId}${SEP}${project.project ?? ''}`;
        return {
          id: projectId,
          label: project.project ?? '(no project)',
          hours: hoursText(project.totalHours),
          children: (project.backlogs ?? []).map(backlog => {
            const backlogId = `${projectId}${SEP}${backlog.backlogCode ?? ''}`;
            return {
              id: backlogId,
              label: backlog.backlogCode ?? '(no backlog)',
              hours: hoursText(backlog.totalHours),
              children: (backlog.tasks ?? []).map(task => {
                // TaskNode is the ONE level with a real id — use it, and fall back to the name so a task
                // the server did not id cannot collapse two rows into one.
                const taskId = `${backlogId}${SEP}${task.taskId ?? task.taskName ?? ''}`;
                return {
                  id: taskId,
                  label: task.taskName ?? '(no task)',
                  hours: hoursText(task.totalHours),
                  children: (task.dates ?? []).map(entry => ({
                    id: `${taskId}${SEP}${entry.date ?? ''}`,
                    label: formatLeafDate(entry.date),
                    hours: hoursText(entry.totalHours),
                    children: [] as readonly ReportTreeNode[],
                  })),
                };
              }),
            };
          }),
        };
      }),
    };
  });
}

/** Depth-first flatten of the visible rows — a node's children are emitted only when it is expanded. */
export function flattenTree(
  roots: readonly ReportTreeNode[],
  expanded: Readonly<Record<string, boolean>>,
): FlatNode[] {
  const rows: FlatNode[] = [];

  const walk = (node: ReportTreeNode, depth: number): void => {
    const hasChildren = node.children.length > 0;
    const isOpen = !!expanded[node.id];
    rows.push({
      id: node.id,
      label: node.label,
      hours: node.hours,
      depth,
      hasChildren,
      expanded: isOpen,
      isLeaf: !hasChildren,
    });
    if (hasChildren && isOpen) node.children.forEach(child => walk(child, depth + 1));
  };

  roots.forEach(root => walk(root, 0));
  return rows;
}

/** Every node that CAN be expanded (i.e. has children), at any depth. Drives Expand-all / Collapse-all. */
export function branchIds(roots: readonly ReportTreeNode[]): string[] {
  const ids: string[] = [];
  const walk = (node: ReportTreeNode): void => {
    if (node.children.length === 0) return;
    ids.push(node.id);
    node.children.forEach(walk);
  };
  roots.forEach(walk);
  return ids;
}
