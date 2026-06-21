# STATE — TimesheetApp

**Last updated:** 2026-06-21

## Current Position
- **Phase:** Step 6 — Plan (all 6 phase plans written + orchestrator-reviewed)
- **Status:** waiting_for_user (approval of full plan set + 1 cross-plan reconciliation before any implementer dispatch)
- **Approved Mode:** Mode B (user override from suggested Mode A, 2026-06-21)

## Next Action
User to approve the 6 plans in `docs/superpowers/plans/` and the template-repository reconciliation (see Open Items). On approval: choose execution mode (Inline executing-plans vs Subagent-Driven), then build phase-by-phase P1→P6 (P1 is prerequisite for all; dependencies: P1→P2→{P3,P4,P5}→P6).

## Open Items (must resolve before execution)
- **CONFLICT — TaskTemplate repository home:** P4 plan added `ITaskTemplateRepository.GetAllAsync()` (read for request creation); P6 plan routed template CRUD through `ITaskRepository` template methods. These contradict. **Recommended fix:** single `ITaskTemplateRepository` with full CRUD (GetAll/Insert/Delete/GetTasksForTemplate) used by BOTH P4 and P6; remove template methods from ITaskRepository. Requires patching architecture spec + P4 + P6 plans before execution. (Awaiting user OK.)
- **Minor — `IAppConfig.DbPath` member name:** reconcile at pre-task confirmation (one-line grep).

## Plan Set (Step 6)
| Phase | Plan file | Tasks / Waves | REQ coverage |
|---|---|---|---|
| P1 Data+Schema | `2026-06-21-P1-data-schema.md` | 6 / 4 | DATA-01..07, XC-01, XC-08, XC-09 ✓ |
| P2 Services | `2026-06-21-P2-services.md` | 7 / 3 | XC-02..07, XC-10, SI-01..04 ✓ |
| P3 Timesheet UI | `2026-06-21-P3-timesheet-ui.md` | 5 / 3 | TS-01..07, SI-05, SI-06 ✓ |
| P4 Requests+Users UI | `2026-06-21-P4-requests-users-ui.md` | 5 / 4 | REQ-01..04, USR-01..03 ✓ |
| P5 Reports | `2026-06-21-P5-reports.md` | 4 / 4 | RPT-01..04 ✓ |
| P6 Settings+Export | `2026-06-21-P6-settings-export.md` | 3 / 2 | SET-01..04, EXP-01..04 ✓ |

All plans: goal-backward `must_haves`, checkbox TDD tasks with full code, per-task `<model>` (base; quality profile shift applied at dispatch), zero intra-wave file overlap. XAML tasks are manual-verify (no headless WPF tests); all testable logic lives in unit-tested ViewModels/services.

## Key Decisions Made
- Project = WPF Desktop Timesheet Tool (.NET 8 / WPF MVVM / SQLite + Dapper / ClosedXML). Folder `AgentArchitectureManagement` is container; app = `TimesheetApp`.
- User identity via Windows username (`Users.windows_username`), fallback `SelectUserDialog` once.
- `TimeLogs` = single FK `task_id`; DefaultTasks unified into `Tasks` under hidden `DEFAULT` request.
- Concurrency: single-writer / last-write-wins on shared SQLite (OneDrive); conflict at file level = accepted risk.
- Mode B chosen by user (override of suggested Mode A).
- Research resolved: journal_mode=DELETE (not WAL), CommunityToolkit.Mvvm, MS.Data.Sqlite 8.0.x, MS.DI. 8 policy/scope decisions logged in spec appendix.
- Spec (STEP 5): 45 REQs (43 + XC-09, XC-10; advisory edit-lock deferred). 0 traceability gaps.
- Plan (STEP 6): 6 phase plans (30 tasks total) written + each orchestrator-reviewed to APPROVE. Execution mode TBD by user.

## Artifacts
- Design spec: `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
- Architecture spec: `docs/superpowers/specs/2026-06-21-timesheet-tool-architecture.md`
- REQUIREMENTS: `.planning/REQUIREMENTS.md` (45 REQ, P1–P6)
- Plans: `docs/superpowers/plans/` (P1–P6)
- Config: `.planning/config.json`
- Research: `.planning/research/` (STACK, FEATURE, ARCHITECTURE, PITFALL, RESEARCH-SYNTHESIS)
