import { BacklogDto, BacklogUpdateRequest } from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';

/**
 * The month a "Move to next month" lands in.
 *
 * WPF uses `backlog.PeriodMonth ?? SelectedMonth`, where SelectedMonth is its month FILTER. The web Log
 * Work grid is a WEEK view and has no month filter, so the fallback is the month of the Monday currently
 * on screen. (User's explicit choice.)
 */
export function nextMonthFrom(periodMonth: string | null, displayedMonday: string): string {
  const base = periodMonth ?? displayedMonday.slice(0, 7);
  const [y, m] = base.split('-').map(Number);
  const next = m === 12 ? { y: y + 1, m: 1 } : { y, m: m + 1 };
  return `${next.y}-${String(next.m).padStart(2, '0')}`;
}

/**
 * The hidden DEFAULT backlog holds the recurring default tasks and must appear in EVERY month -- so it
 * must never belong to one. WPF guards this twice: the UI hides the button, AND MoveMonthAsync refuses.
 * Keep both. Defence in depth here is deliberate.
 */
export function canMoveMonth(backlogCode: string): boolean {
  return backlogCode !== 'DEFAULT';
}

/**
 * PUT /api/backlogs/{id} replaces the WHOLE record -- an omitted field is written as null, not left
 * alone. So a Move must carry every other field across untouched. Only `periodMonth` is ours to set.
 *
 * No auditNote: WPF sends none, and the field's established meaning in this codebase is "the reason a
 * HUMAN typed into a popup" (TaskListViewModel.CommitDeadlineAsync), not a system label. The audit row
 * still names the editor, via changedByName, which the server supplies.
 */
export function toUpdateRequest(b: BacklogDto, periodMonth: string): BacklogUpdateRequest {
  return {
    backlogCode: b.backlogCode!,
    project: b.project!,
    startDate: b.startDate ?? null,
    endDate: b.endDate ?? null,
    periodMonth,                                        // <-- the ONLY field this action changes
    type: b.type ?? null,
    assigneeUserId: b.assigneeUserId ?? null,
    deadlineInternal: b.deadlineInternal ?? null,
    deadlineExternal: b.deadlineExternal ?? null,
    roughEstimateHours: b.roughEstimateHours ?? null,
    officialEstimateHours: b.officialEstimateHours ?? null,
    progressPercent: b.progressPercent ?? null,
    note: b.note ?? null,
    pcaContactId: b.pcaContactId ?? null,
    expectedVersion: requireRowVersion(b.rowVersion),   // NEVER `!`. See grid-state.ts:54-58.
    auditNote: null,
  };
}
