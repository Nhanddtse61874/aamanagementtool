import { BacklogCreateRequest, BacklogDto, BacklogUpdateRequest } from '../../api/models';
import { requireRowVersion } from '../../services/worklog.service';

/**
 * The backlog editor's form state.
 *
 * Six of these fields -- startDate, endDate, deadlineInternal, deadlineExternal, progressText,
 * pcaContactId -- are CREATE-ONLY. They are hidden on the edit form, because they are edited inline on the
 * Task List screen. They live here anyway because CREATE genuinely owns them.
 *
 * 🔴 Which means `form.startDate` COMPILES on the edit path. See toUpdateRequest.
 */
export interface EditForm {
  code: string;
  project: string;
  year: number;
  month: number;                    // 1..12
  type: string | null;
  assigneeUserId: number | null;
  roughEstimateText: string;        // raw text; parsed, never a number
  officialEstimateText: string;     // raw text; parsed, never a number
  note: string | null;

  // --- CREATE-ONLY (hidden on the EDIT form, but PRESENT here because CREATE needs them). ---
  startDate: string | null;         // 'yyyy-MM-dd'
  endDate: string | null;
  deadlineInternal: string | null;
  deadlineExternal: string | null;
  progressText: string;             // raw text; parsed by parseProgress
  pcaContactId: number | null;
}

/** One row of the editor's task grid. */
export interface EditRow {
  existingTaskId: number;           // 0 = new
  name: string;
  orderIndex: number;               // the server's last-known order_index; REINDEXED on save
  removed: boolean;
}

/**
 * The outcome of parsing one numeric text box.
 *
 * 🔴 An unparseable value is an ERROR, never a silent null. WPF's ParseEstimate/ParseProgress set an
 * ErrorMessage and then `return null` -- and the ErrorMessage blocks nothing, so the save writes the null.
 * RequestsViewModelTests.SaveNew_does_not_persist_out_of_range_progress pins that behaviour: type "150",
 * save, and ProgressPercent is null. We are fixing that bug; `validate` refuses the save instead.
 */
export interface ParseResult {
  value: number | null;
  error: string | null;
}

/** Empty is a CLEAR (null, no error). Anything non-numeric, negative, or over `max` is an ERROR. */
function parseNumber(text: string, label: string, max: number | null): ParseResult {
  const trimmed = text.trim();
  if (trimmed === '') return { value: null, error: null };

  const n = Number(trimmed);
  // Number('') is 0 and Number('12abc') is NaN -- the empty case is already gone, and NaN/Infinity are the
  // only remaining ways this can be a non-number. (parseFloat would happily read '12abc' as 12.)
  if (!Number.isFinite(n)) return { value: null, error: `${label} must be a number.` };
  if (n < 0) return { value: null, error: `${label} cannot be negative.` };
  if (max !== null && n > max) return { value: null, error: `${label} must be between 0 and ${max}.` };

  return { value: n, error: null };
}

export function parseEstimate(text: string): ParseResult {
  return parseNumber(text, 'Estimate', null);
}

export function parseProgress(text: string): ParseResult {
  return parseNumber(text, 'Progress', 100);
}

export function periodMonth(year: number, month: number): string {
  return `${year}-${String(month).padStart(2, '0')}`;
}

/**
 * The save gate. Returns the first problem, or null when the form may be saved.
 *
 * A parse error BLOCKS the save -- that is the entire point (see ParseResult). The one-task rule is
 * enforced on BOTH paths; WPF enforces it on create only, but a backlog with no tasks is unloggable
 * either way.
 */
export function validate(form: EditForm, rows: EditRow[]): string | null {
  if (form.code.trim() === '') return 'Backlog code is required.';
  if (form.project.trim() === '') return 'Project is required.';

  const parseError =
    parseEstimate(form.roughEstimateText).error ??
    parseEstimate(form.officialEstimateText).error ??
    parseProgress(form.progressText).error;
  if (parseError !== null) return parseError;

  if (!rows.some(r => !r.removed)) return 'A backlog must have at least one task.';

  return null;
}

/**
 * CREATE carries all 14 domain fields -- including the six the edit form hides, which on this path the
 * form genuinely owns.
 *
 * No teamId: BacklogCreateRequest has no such field, and the server sets team_id from the SESSION
 * (BacklogEndpoints.cs:87). A user in no team is 400'd rather than allowed to write an orphan row that
 * would be invisible to everyone, forever.
 */
export function toCreateRequest(form: EditForm): BacklogCreateRequest {
  return {
    backlogCode: form.code.trim(),
    project: form.project,
    startDate: form.startDate,
    endDate: form.endDate,
    periodMonth: periodMonth(form.year, form.month),
    type: form.type,
    assigneeUserId: form.assigneeUserId,
    deadlineInternal: form.deadlineInternal,
    deadlineExternal: form.deadlineExternal,
    roughEstimateHours: parseEstimate(form.roughEstimateText).value,
    officialEstimateHours: parseEstimate(form.officialEstimateText).value,
    progressPercent: parseProgress(form.progressText).value,
    note: form.note,
    pcaContactId: form.pcaContactId,
  };
}

/**
 * 🔴 START FROM THE LOADED DTO, NEVER FROM THE FORM.
 *
 * PUT /api/backlogs/{id} REPLACES the whole record. BacklogEndpoints.cs:142-158 patches the entity with
 * `existing with { ... StartDate = req.StartDate, ... PcaContactId = req.PcaContactId }` unconditionally,
 * and BacklogRepository.cs:148-157 then writes all 15 data columns on every call. An omitted field is not
 * "left alone" -- it is written as NULL.
 *
 * Six of those columns are HIDDEN on the edit form (they are edited inline on the Task List screen).
 * `EditForm` still CARRIES them, because CREATE needs them -- so `form.startDate` compiles here, and the
 * generated DTOs are all-optional (Swashbuckle emits no `required` for C# records), so a dropped field
 * compiles too. Nothing in the type system will catch this. Only backlog-form.spec.ts will.
 *
 * teamId is deliberately absent from BacklogUpdateRequest altogether: there is nothing on the wire that
 * could null it, which is what stops an edit from making a backlog invisible to its whole team.
 */
export function toUpdateRequest(dto: BacklogDto, form: EditForm): BacklogUpdateRequest {
  return {
    // --- the EDIT form owns these ---
    backlogCode: form.code.trim(),
    project: form.project,
    periodMonth: periodMonth(form.year, form.month),
    type: form.type,
    assigneeUserId: form.assigneeUserId,
    roughEstimateHours: parseEstimate(form.roughEstimateText).value,
    officialEstimateHours: parseEstimate(form.officialEstimateText).value,
    note: form.note,

    // --- 🔴 HIDDEN ON EDIT. From the DTO. NOT from `form`, even though `form` has them. ---
    startDate: dto.startDate ?? null,
    endDate: dto.endDate ?? null,
    deadlineInternal: dto.deadlineInternal ?? null,
    deadlineExternal: dto.deadlineExternal ?? null,
    progressPercent: dto.progressPercent ?? null,
    pcaContactId: dto.pcaContactId ?? null,

    expectedVersion: requireRowVersion(dto.rowVersion),   // NEVER `!`, NEVER 0. See grid-state.ts:54-65.
    auditNote: null,
  };
}
