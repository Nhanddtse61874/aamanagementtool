import { HttpErrorResponse } from '@angular/common/http';

import {
  BacklogListItemDto, SettingsStandupEntryCreateRequest, SettingsStandupIssueUpdateRequest, StandupIssueDto,
} from '../../api/models';
import { SECTIONS, STANDUP_STATUSES, StandupSection, StandupStatus } from './standup-day';

/**
 * The Add-entry form's state. Mirrors WPF's `StandupDraftVm`.
 *
 * 🔴 `backlogId` IS NULL WHEN THE CODE WAS TYPED AD-HOC, and that is the whole of DR-03. An ad-hoc line is a
 * standup row for work that has no backlog record — `backlog_id` stays null and only `backlog_code` (free
 * text) is stored. WPF expresses this as `BacklogId => SelectedBacklog?.Id`: pick a backlog and you get its
 * id, type a code by hand and you get null. `StandupService.ResolveBacklogIdAsync` then keeps a supplied id
 * ONLY IF THE BACKLOG STILL EXISTS, so a stale id degrades to ad-hoc rather than to a foreign-key error.
 */
export interface EntryDraft {
  readonly section: StandupSection;
  readonly backlogId: number | null;
  readonly backlogCode: string;
  readonly taskText: string;
  readonly description: string;
  /** OPTIONAL. `yyyy-MM-dd` or null — `StandupEntry.Deadline` is `DateOnly?` and blank is a legitimate value. */
  readonly deadline: string | null;
  readonly status: StandupStatus;
}

export function emptyDraft(section: StandupSection): EntryDraft {
  return {
    section,
    backlogId: null,
    backlogCode: '',
    taskText: '',
    description: '',
    deadline: null,
    // WPF: `_status = StandupStatus.All[0]`.
    status: STANDUP_STATUSES[0],
  };
}

/**
 * The client-side mirror of `StandupService.ValidateDraft`. Returns an error message, or null when valid.
 *
 * The server throws `ArgumentException` -> 400 for each of these four, so this is a courtesy that saves a
 * round-trip — NOT a gate. The caller still surfaces whatever the server says; see `apiError`.
 *
 * 🔴 Description and deadline are NOT validated, deliberately: both are optional server-side.
 */
export function validateDraft(draft: EntryDraft): string | null {
  if (!SECTIONS.includes(draft.section)) return `Invalid section '${draft.section}'.`;
  if (!STANDUP_STATUSES.includes(draft.status)) return `Invalid status '${draft.status}'.`;
  if (!draft.backlogCode.trim()) return 'Backlog code is required.';
  if (!draft.taskText.trim()) return 'Task text is required.';
  return null;
}

/** The draft, as `POST /api/standup/entries` wants it. `workDate` is the day the screen is showing. */
export function toCreateRequest(draft: EntryDraft, workDate: string): SettingsStandupEntryCreateRequest {
  return {
    workDate,
    section: draft.section,
    // null for an ad-hoc code — see EntryDraft's doc.
    backlogId: draft.backlogId,
    backlogCode: draft.backlogCode.trim(),
    taskText: draft.taskText.trim(),
    description: draft.description.trim(),
    // '' from an empty <input type="date"> must go on the wire as null, not as an empty string: the server
    // binds this to `DateOnly?` and an empty string is a 400, where null is a legitimate "no deadline".
    deadline: draft.deadline ? draft.deadline : null,
    status: draft.status,
  };
}

/**
 * 🔴 THE ISSUE UPDATE IS A WHOLE-RECORD OVERWRITE, AND THAT IS A TRAP TYPESCRIPT CANNOT SEE.
 *
 * `PUT /api/standup/entries/{entryId}/issues/{issueId}` does this, unconditionally, with no null-guard:
 *
 *     var updated = existingIssue with {
 *         IssueText = req.IssueText, SolutionText = req.SolutionText, Status = req.Status,
 *     };
 *     await standup.UpdateIssueCheckedAsync(updated, req.ExpectedVersion);
 *
 * and `UpdateIssueCheckedAsync` opens with:
 *
 *     if (string.IsNullOrWhiteSpace(issue.IssueText)) throw new ArgumentException("issue text required");
 *     if (!StandupIssueStatus.All.Contains(issue.Status)) throw new ArgumentException("invalid issue status");
 *
 * Every field on `SettingsStandupIssueUpdateRequest` is OPTIONAL (`issueText?: string | null`). So a body
 * built from the solution box alone — the only thing the UI actually lets you change — omits `issueText` and
 * `status`, COMPILES PERFECTLY CLEAN, and 400s at runtime on the happy path, every save, forever.
 *
 * This is the same shape as the `updateTask`/`status` hazard documented in WorklogService, and it is why
 * BOTH are round-tripped from the loaded issue here rather than assembled from the form. Only `solutionText`
 * and `status` are the user's to change (DR-04: text is read-only after creation, solution + status are
 * collaborative), and `status` is passed in explicitly because the status dropdown is bound to it.
 *
 * `expectedVersion` is the issue's `rowVersion`. StandupIssues is the ONE standup table that carries one —
 * it is deliberately collaborative, so it is the one that can be raced. A stale version is a 409.
 */
export function toIssueUpdateBody(
  issue: StandupIssueDto,
  solutionText: string | null,
  status: string,
): SettingsStandupIssueUpdateRequest {
  return {
    // 🔴 ROUND-TRIPPED, not omitted. See above.
    issueText: issue.issueText ?? '',
    status,
    // `NullIfBlank` server-side: a blank solution is null ("pending discussion"), not an empty string.
    solutionText: solutionText && solutionText.trim() ? solutionText.trim() : null,
    expectedVersion: issue.rowVersion ?? 0,
  };
}

/**
 * Mirrors `StandupIssue.HasSolution` — `!string.IsNullOrWhiteSpace(SolutionText)`. A solved issue renders
 * green ✓; an unsolved one renders amber ⚠ with a "No solution yet" placeholder.
 */
export function hasSolution(issue: StandupIssueDto): boolean {
  return !!issue.solutionText && issue.solutionText.trim().length > 0;
}

/**
 * 🔴 `DEFAULT` IS EXCLUDED CLIENT-SIDE, and it has to be.
 *
 * `GET /api/backlogs` returns EVERY backlog including the hidden `DEFAULT` one, because Log Work genuinely
 * needs it (it holds the recurring default tasks and must appear in every month). The standup picker must not
 * offer it. WPF does exactly this, in `DailyReportViewModel.FillPicker`:
 *
 *     foreach (var r in backlogs.Where(r => r.BacklogCode != "DEFAULT")) draft.Backlogs.Add(r);
 *
 * There is no server-side variant of this list to reach for instead — see the component's doc on why the
 * picker's team scope also cannot be narrowed from here.
 */
export function pickableBacklogs(backlogs: readonly BacklogListItemDto[]): BacklogListItemDto[] {
  return backlogs.filter(b => b.backlogCode !== 'DEFAULT');
}

/**
 * The message the server actually sent, or `fallback`.
 *
 * The API answers a rejected write with `ValidationBody` — `{ error: string }` — for a 400, and `ConflictBody`
 * — `{ message, detail, ... }` — for a 409. Both are worth showing verbatim: "Cannot add: the day is locked
 * (editable only today or yesterday)." tells the user something a generic "Save failed" cannot.
 */
export function apiError(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    const body: unknown = err.error;
    if (body !== null && typeof body === 'object') {
      const shape = body as { error?: unknown; message?: unknown };
      if (typeof shape.error === 'string' && shape.error.length > 0) return shape.error;
      if (typeof shape.message === 'string' && shape.message.length > 0) return shape.message;
    }
  }
  return fallback;
}

/**
 * A 409 from the checked issue write.
 *
 * 🔴 The caller's answer is a toast and a RE-READ — never the `ConflictDialogComponent`. That dialog exists to
 * merge a TIMESHEET CELL: two numbers, "yours" vs "theirs", pick one. An issue's solution is free text and a
 * status; there is nothing to merge and no arithmetic to show. Say it was changed, reload the truth, stop.
 */
export function isConflict(err: unknown): boolean {
  return err instanceof HttpErrorResponse && err.status === 409;
}
