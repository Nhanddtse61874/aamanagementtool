// Domain models for the Worklog app.
// These describe the shapes the UI binds to. Wire them to your API in WorklogService.

export type ScreenId =
  | 'log' | 'backlog' | 'tasklist' | 'daily' | 'reports' | 'users' | 'settings';

export type BacklogType =
  | 'Investigate' | 'Implement' | 'Continue' | 'IT' | 'Estimate' | null;

export interface User {
  name: string;
  active: boolean;
}

export interface Backlog {
  code: string;
  project: string;
  month: string;          // e.g. '2026-07' or '—'
  type: BacklogType;
  assignee: string | null;
  tasks: number;          // task count
}

/** Log Work grid */
export interface LogGroup {
  code: string;
  project: string;
  type: BacklogType;
  assignee: string | null;
  tasks: string[];        // task names
  canMove: boolean;       // "Move to next month"
}

/** hours keyed by `${groupIndex}-${taskIndex}-${dayIndex}` */
export type HoursMap = Record<string, string>;

export interface DayColumn { dow: string; date: string; }

/** Task List cards */
export interface TaskCard {
  code: string;
  project: string;
  type: Exclude<BacklogType, null>;
  assignee: string;
  tags: string[];         // tag labels
  tasks: string[];        // task names
}

/** per-task progress keyed by `${cardIndex}-${taskIndex}` -> 0..100 */
export type ProgressMap = Record<string, number>;

/** Daily Report */
export interface DailyIssue {
  text: string;
  ok: boolean;
  solution?: string;
  hasSolution: boolean;
}
export interface DailyEntry {
  code: string;
  title: string;
  note: string;
  deadline: string;
  status: 'Done' | 'Todo';
  issues: DailyIssue[];
}
export interface TeamTodayItem {
  code: string; title: string; due: string; note: string;
  status: 'Done' | 'Todo';
  issues: { text: string; ok: boolean }[];
}
export interface TeamMember {
  name: string;
  today: TeamTodayItem[];
}

/** Reports */
export interface Metric { label: string; value: string; sub: string; tone: 'ink' | 'accent' | 'warn'; icon: string; }
export interface WeeklyRow { date: string; ticket: string; task: string; hours: string; }
export interface MonthlyRow { backlog: string; project: string; task: string; hours: string; }
export interface TreeNode {
  id: string; label: string; hours: string;
  children?: TreeNode[];
}

/** Settings */
export interface Tag { label: string; color: string; icon: string; }
export interface TaskTemplate { name: string; taskCount: number; }
