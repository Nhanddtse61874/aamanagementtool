import { BacklogDto } from '../../api/models';
import {
  EditForm, EditRow, parseEstimate, parseProgress, periodMonth,
  toCreateRequest, toUpdateRequest, validate,
} from './backlog-form';

/**
 * 🔴 THE SIX CREATE-ONLY FIELDS BELOW DELIBERATELY DISAGREE WITH EVERY DTO FIXTURE IN THIS FILE.
 *
 * That divergence is load-bearing. `EditForm` CARRIES startDate/endDate/deadlines/progress/pcaContactId
 * because CREATE needs them -- which means `form.startDate` COMPILES on the edit path too. If this fixture
 * agreed with the DTO, the hidden-field test below would pass whether toUpdateRequest read from `dto` or
 * from `form`, and would therefore prove exactly nothing.
 */
const FORM: EditForm = {
  code: '  ARCS-1  ',           // padded: the trim is part of the contract
  project: 'ARCS',
  year: 2026,
  month: 7,
  type: 'Implement',
  assigneeUserId: 5,
  roughEstimateText: '8',
  officialEstimateText: '12.5',
  note: null,

  // --- create-only. Hidden on the EDIT form; these values must NEVER reach an update request. ---
  startDate: '1999-01-01',
  endDate: '1999-12-31',
  deadlineInternal: '1999-06-01',
  deadlineExternal: '1999-06-15',
  progressText: '99',
  pcaContactId: 999,
};

/** The loaded record. Its six hidden fields are the ONLY honest source for an update. */
const DTO: BacklogDto = {
  id: 7, backlogCode: 'ARCS-1', project: 'ARCS', rowVersion: 11,
  startDate: '2026-07-01', endDate: '2026-07-31',
  deadlineInternal: '2026-07-20', deadlineExternal: '2026-07-25',
  progressPercent: 60, pcaContactId: 3,
};

const ROWS: EditRow[] = [{ existingTaskId: 0, name: 'T', orderIndex: 0, removed: false }];

describe('toUpdateRequest', () => {
  /**
   * 🔴 THE TEST THIS MODULE EXISTS FOR.
   *
   * PUT /api/backlogs/{id} REPLACES the whole record: BacklogEndpoints.cs:142-158 does `existing with { ...
   * StartDate = req.StartDate, ... PcaContactId = req.PcaContactId }` unconditionally, and
   * BacklogRepository.cs:148-157 then writes all 15 data columns. Six of those columns are HIDDEN on the
   * edit form (they are edited inline on the Task List screen). Build the request from the form and every
   * one of them is written as NULL -- and nothing in TypeScript objects, because the generated DTOs are
   * all-optional (Swashbuckle emits no `required` for C# records).
   */
  it('carries every HIDDEN field across from the DTO -- build it from the form and they are all NULLED', () => {
    const req = toUpdateRequest(DTO, { ...FORM, note: 'edited' });

    expect(req.startDate).toBe('2026-07-01');
    expect(req.endDate).toBe('2026-07-31');
    expect(req.deadlineInternal).toBe('2026-07-20');
    expect(req.deadlineExternal).toBe('2026-07-25');
    expect(req.progressPercent).toBe(60);
    expect(req.pcaContactId).toBe(3);
  });

  it('takes the EDIT-owned fields from the form', () => {
    const req = toUpdateRequest(DTO, { ...FORM, note: 'edited' });

    expect(req.backlogCode).toBe('ARCS-1');            // trimmed
    expect(req.project).toBe('ARCS');
    expect(req.periodMonth).toBe('2026-07');
    expect(req.type).toBe('Implement');
    expect(req.assigneeUserId).toBe(5);
    expect(req.roughEstimateHours).toBe(8);
    expect(req.officialEstimateHours).toBe(12.5);
    expect(req.note).toBe('edited');
    expect(req.auditNote).toBeNull();
  });

  // grid-state.ts:54-65 -- `expectedVersion` is a CLAIM, not a placeholder. Zero is a DIFFERENT assertion,
  // not a safe default, and `dto.rowVersion!` would send `undefined`. requireRowVersion throws instead.
  it('sends the loaded version as expectedVersion -- never 0, never `!`', () => {
    expect(toUpdateRequest(DTO, FORM).expectedVersion).toBe(11);
  });

  it('THROWS rather than defaulting expectedVersion when the DTO carries no rowVersion', () => {
    const noVersion: BacklogDto = { id: 7, backlogCode: 'X', project: 'P' };
    expect(() => toUpdateRequest(noVersion, FORM)).toThrowError(/rowVersion/);
  });

  it('normalises an ABSENT hidden field to null rather than undefined', () => {
    const bare: BacklogDto = { id: 7, backlogCode: 'X', project: 'P', rowVersion: 1 };
    const req = toUpdateRequest(bare, FORM);

    expect(req.startDate).toBeNull();
    expect(req.endDate).toBeNull();
    expect(req.deadlineInternal).toBeNull();
    expect(req.deadlineExternal).toBeNull();
    expect(req.progressPercent).toBeNull();
    expect(req.pcaContactId).toBeNull();
  });

  // BacklogUpdateRequest has no TeamId property AT ALL (BacklogEndpoints.cs:140-141), and the handler
  // re-reads the entity and `with{}`-patches only the DTO's own fields. That is what makes team_id
  // unnullable from the wire. A team_id of NULL makes the backlog invisible to everyone, permanently.
  it('sends NO teamId -- the field does not exist on the wire, by design', () => {
    expect('teamId' in toUpdateRequest(DTO, FORM)).toBe(false);
  });
});

describe('toCreateRequest', () => {
  it('carries all 14 domain fields, INCLUDING the create-only six -- the form owns them on THIS path', () => {
    const req = toCreateRequest(FORM);

    expect(req.backlogCode).toBe('ARCS-1');            // trimmed
    expect(req.project).toBe('ARCS');
    expect(req.periodMonth).toBe('2026-07');
    expect(req.type).toBe('Implement');
    expect(req.assigneeUserId).toBe(5);
    expect(req.roughEstimateHours).toBe(8);
    expect(req.officialEstimateHours).toBe(12.5);
    expect(req.note).toBeNull();

    // The six the EDIT path must read from the DTO -- but CREATE has no DTO, so here they come from the form.
    expect(req.startDate).toBe('1999-01-01');
    expect(req.endDate).toBe('1999-12-31');
    expect(req.deadlineInternal).toBe('1999-06-01');
    expect(req.deadlineExternal).toBe('1999-06-15');
    expect(req.progressPercent).toBe(99);
    expect(req.pcaContactId).toBe(999);

    expect(Object.keys(req).length).toBe(14);
  });

  // The server sets team_id from the SESSION (BacklogEndpoints.cs:87) and 400s a user in no team rather
  // than writing the orphan. Nothing about the team may come off the wire.
  it('sends NO teamId -- the server sets it from the session', () => {
    expect('teamId' in toCreateRequest(FORM)).toBe(false);
  });
});

describe('parseEstimate', () => {
  it('treats empty / whitespace as a CLEAR, not an error', () => {
    expect(parseEstimate('')).toEqual({ value: null, error: null });
    expect(parseEstimate('   ')).toEqual({ value: null, error: null });
  });

  it('parses a number', () => {
    expect(parseEstimate('8')).toEqual({ value: 8, error: null });
    expect(parseEstimate(' 12.5 ')).toEqual({ value: 12.5, error: null });
    expect(parseEstimate('0')).toEqual({ value: 0, error: null });
  });

  it('ERRORS on unparseable input instead of silently nulling it', () => {
    const r = parseEstimate('abc');
    expect(r.value).toBeNull();
    expect(r.error).toBeTruthy();
  });

  it('ERRORS on a negative estimate', () => {
    const r = parseEstimate('-1');
    expect(r.value).toBeNull();
    expect(r.error).toBeTruthy();
  });
});

describe('parseProgress', () => {
  /**
   * 🔴 THE WPF BUG THIS FIXES -- and WPF has a TEST PINNING THE BUG.
   *
   * RequestEditorViewModel.ParseEstimate/ParseProgress set an ErrorMessage and then `return null`. The
   * ErrorMessage does not block anything: SaveNewAsync/SaveEditAsync write `Editor.ProgressPercent` -- the
   * null -- regardless. RequestsViewModelTests.SaveNew_does_not_persist_out_of_range_progress types "150",
   * saves, and asserts `Assert.Null(saved.ProgressPercent)`: the typed value is DISCARDED and the row saves
   * anyway. Here a parse error is an ERROR, and `validate` below refuses the save.
   */
  it('ERRORS on 150 -- it must not silently null it, which is what WPF does', () => {
    const r = parseProgress('150');
    expect(r.value).toBeNull();
    expect(r.error).toBeTruthy();
  });

  it('ERRORS below 0', () => {
    expect(parseProgress('-1').error).toBeTruthy();
  });

  it('accepts the 0..100 bounds inclusively', () => {
    expect(parseProgress('0')).toEqual({ value: 0, error: null });
    expect(parseProgress('100')).toEqual({ value: 100, error: null });
    expect(parseProgress('60')).toEqual({ value: 60, error: null });
  });

  it('treats empty / whitespace as a CLEAR, not an error', () => {
    expect(parseProgress('  ')).toEqual({ value: null, error: null });
  });

  it('ERRORS on unparseable input', () => {
    expect(parseProgress('abc').error).toBeTruthy();
  });
});

describe('validate', () => {
  it('passes a well-formed form', () => {
    expect(validate(FORM, ROWS)).toBeNull();
  });

  it('requires a code', () => {
    expect(validate({ ...FORM, code: '   ' }, ROWS)).toBeTruthy();
  });

  it('requires a project', () => {
    expect(validate({ ...FORM, project: '' }, ROWS)).toBeTruthy();
  });

  // WPF enforces this on CREATE only; we enforce it on both. A backlog with no tasks is unloggable.
  it('requires at least one NON-REMOVED task', () => {
    expect(validate(FORM, [])).toBeTruthy();
    expect(validate(FORM, [{ existingTaskId: 3, name: 'T', orderIndex: 0, removed: true }])).toBeTruthy();
  });

  // 🔴 A parse error must BLOCK THE SAVE. In WPF it does not -- it writes null and carries on.
  it('BLOCKS the save on a parse error instead of writing null', () => {
    expect(validate({ ...FORM, progressText: '150' }, ROWS)).toBeTruthy();
    expect(validate({ ...FORM, roughEstimateText: 'abc' }, ROWS)).toBeTruthy();
    expect(validate({ ...FORM, officialEstimateText: '-3' }, ROWS)).toBeTruthy();
  });
});

describe('periodMonth', () => {
  it('zero-pads the month', () => {
    expect(periodMonth(2026, 7)).toBe('2026-07');
    expect(periodMonth(2026, 12)).toBe('2026-12');
    expect(periodMonth(2026, 1)).toBe('2026-01');
  });
});
