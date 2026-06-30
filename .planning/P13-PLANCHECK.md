Confirmed the key issues. `--filter FullyQualifiedName~Backlog` matches by **fully-qualified name** (namespace.class.method). The namespace is `TimesheetApp.Tests.Data` and no class/method contains "Backlog" â€” so `~Backlog` matches **zero tests** (the word "Backlog" appears only in method bodies, not in FQNs). This makes W1-T3's `dotnet test ... ~Backlog` a vacuous green. Similarly W2-T1's `~Editor` correctly matches `RequestEditorViewModelTests` (FQN `...ViewModels.RequestEditorViewModelTests`). And `~Task` (W1-T2, W3-T1) over-matches but is non-empty, so it's fine.

I now have everything needed to render the verdict. Let me compile the final assessment across all 11 dimensions.

VERDICT: APPROVE-WITH-FIXES

The plan is well-grounded, source-verified (schema v8â†’v9, TaskItem 6-field, audit patterns all confirmed against the actual code), 100% REQ-covered, correct on dependency order, and the inferred Progress decision is properly flagged. Two real Critical issues (a vacuous test filter and a missing migration test) plus several Important/Suggestion fixes prevent a clean APPROVE.

---

**Dimension 1 â€” REQ coverage (A1-A4, B1-B3, C1, D1):** PASS.
1. (Suggestion) All 9 REQs map to >=1 task; matrix is 1:1. No issue. Note: the bare "(schema)" row is infra enabling B1/B2/B3, correctly not a REQ â€” fine.

**Dimension 2 â€” Schema migration additive + ordered + SchemaVersion bumped:** PASS with one convention flag.
2. (Important) **Table-creation convention inverted.** Every other table in this codebase is created idempotently in `CreateTables` (lines 40-191); migration steps only `ALTER`/data-migrate (the v5/v8 step comments explicitly say "tables are created idempotently in CreateTables"). W1-T1 instead puts `CREATE TABLE TaskTags/TaskAudit` *inside* the migration step. It is still functionally correct on a fresh DB (user_version=0 runs steps 0-8) and on upgrade, and all changes are additive/forward-only. Fix: for convention-consistency and to make a re-run/partial-failure safe, move the two `CREATE TABLE IF NOT EXISTS TaskTags/TaskAudit` into `CreateTables`, and keep ONLY the three `ALTER TABLE ... ADD COLUMN` (Tasks.type, Tasks.assignee_user_id, BacklogAudit.note) in migration index 8. SchemaVersion 8â†’9 bump and index placement are correct.

**Dimension 3 â€” No same-wave file overlap:** PASS.
3. (No issue) W1 four tasks disjoint; W2 two disjoint; W3 three disjoint by file ownership (VM / dialog / XAML); W4 two disjoint. The Theme.xaml cross-wave overlap is correctly resolved by pulling it into W1-T4. Note the plan body has a stale paragraph (lines 70-72) that first says "W4-T1 owns Theme.xaml" then corrects to W1-T4 â€” see issue 11.

**Dimension 4 â€” Each task has model/read_first/action/verify/done:** PASS.
4. (No issue) All 11 tasks have all five elements plus `<title>`. Models assigned per quality profile (haiku/sonnet/opus).

**Dimension 5 â€” Verify steps automated & <60s:** PASS with one precision flag (overlaps dim 11).
5. (Critical) **W1-T3 verify is vacuous â€” `--filter FullyQualifiedName~Backlog` matches ZERO tests.** `~` matches the *fully-qualified* name (namespace.class.method); no test class or method contains "Backlog" (namespace is `TimesheetApp.Tests.Data`; "Backlog" appears only in method bodies). So `dotnet test ... ~Backlog` returns "no tests matched" = false green; it never exercises the note-column / tag-audit regression it claims to. Fix: change the filter to actually-existing classes, e.g. `--filter "FullyQualifiedName~RepositoryCrud|FullyQualifiedName~TaskListRepository|FullyQualifiedName~RequestsViewModel"` (these contain the SetTagsAsync/GetAuditAsync/UpdateAsync coverage). (W1-T2 `~Task` and W3-T1 `~TaskList`, W2-T1 `~Editor`â†’`RequestEditorViewModelTests` all match real tests â€” fine.)
6. (Suggestion) All verifies are `dotnet build`/`dotnet test` â€” automated; each scoped, well under 60s. Good.

**Dimension 6 â€” Goal-backward must_haves present + traceable:** PASS.
7. (No issue) must_haves has Observable Truths (OT-1..8), Required Artifacts, Required Wiring, Key Links. OT-1â†”W1-T1; OT-2/3â†”W2; OT-4/5/6â†”W3; OT-7â†”W1-T4+W4-T1; OT-8â†”W4-T2. Each artifact maps to an owning task. Traceable.

**Dimension 7 â€” Dependency order (schema/repos before consumers):** PASS.
8. (No issue) W1 (schema+repos+models+read-model+theme keys) precedes W2/W3/W4 consumers. Intra-W3 sequence T1(VM)â†’T2(dialog)â†’T3(XAML) is correct since T3 binds T1's commands and `new DeadlineNoteDialog()` from T2. W2 T1â†’T2 correct. W1-T2/T3 explicitly noted as must-land-before-W3.

**Dimension 8 â€” Destructive/schema steps flagged for pause-gate:** PASS.
9. (No issue) Wave 1 carries a prominent ðŸš© PAUSE-GATE banner (lines 61, 76-78) citing CLAUDE.md STEP 7, recommends DB backup, and Risk #1 repeats it. Strong.

**Dimension 9 â€” Simplicity (no speculative scope):** PASS with two minor flags.
10. (Suggestion) **`AuditRaw.note` + GetAuditAsync SELECT extension is speculative this phase.** W1-T3 adds `note` to the read path but explicitly does NOT map it into `BacklogAuditEntry` (Risk #5 calls it "write-only, for a future history panel"). Per "Simplicity First / write the minimum for the REQ," the read-side `note` plumbing satisfies no P13 REQ (B2 only requires *storing* the reason). Fix: drop the `AuditRaw.note` field + GetAuditAsync SELECT change from W1-T3; add them in the future phase that surfaces history. Keep only the INSERT-side note (which B2 needs). Low-risk either way; flag at QA.
11. (Suggestion) `CompactComboBox` (W1-T4) is justified (B3 sub-row combos, confirmed absent from Theme). No other speculative abstractions found â€” commit commands, VMs, and dialog are all REQ-driven.

**Dimension 10 â€” INFERRED decision (Progress added to Task List inline) flagged for user confirm:** PASS.
12. (No issue) REQUIREMENTS line 47 marks it "*Inferred* â€¦ Flagged for user confirm," and the plan surfaces Progress as an inline-edit field in OT-4, W3-T1 (`EditProgressPercent`/`EditProgressText`), W3-T3 (PROGRESS column), and B1 coverage. Risk #6 ties it to the WPF render-crash pattern (ProgressBar stays OneWay). Adequately flagged. (Minor: the matrix/Risks could state "pending user confirm" more loudly, but REQUIREMENTS carries the gate.)

**Dimension 11 â€” Unit tests for migration + new repo methods:** FAIL â€” the weakest dimension.
13. (Critical) **No migration test for v9.** The codebase has a clear precedent â€” `SchemaV7UpgradeTests.cs` and `SchemaV8UpgradeTests.cs` assert each upgrade (column existence, user_version, no row mutation). W1-T1's `<verify>` is `dotnet build` ONLY; no `SchemaV9UpgradeTests` is created and no existing test is run. OT-1 ("PRAGMA user_version reads 9; Tasks has type/assignee_user_id; TaskTags/TaskAudit exist; BacklogAudit has note; no row mutated") is therefore unverified by automation. Fix: add a task step (or fold into W1-T1) to create `src/TimesheetApp.Tests/Data/SchemaV9UpgradeTests.cs` mirroring V8: seed a v8 DB â†’ initialize â†’ assert user_version==9, the two Tasks columns + BacklogAudit.note present, TaskTags/TaskAudit tables exist, and a pre-existing row is untouched. Then W1-T1 `<verify>` runs `--filter FullyQualifiedName~SchemaV9`.
14. (Important) **No unit tests for the new repo methods.** W1-T2 adds `UpdateExtendedAsync`/`UpdateStatusAsync`/`SetTaskTagsAsync`/`GetTagIdsAsync` (each writing TaskAudit) and W1-T3 adds deadline-note audit + tag-change audit, but neither task creates tests asserting the audit rows / tag links are written; they only rely on *existing* tests still passing (and W1-T3's filter matches nothing â€” issue 5). Fix: add focused tests (extend `TaskListRepositoryTests`/`RepositoryCrudTests`) â€” e.g. UpdateExtendedAsync writes one TaskAudit row per changed field and none when unchanged; SetTaskTagsAsync replace-all + single 'tags' audit; BacklogRepository.UpdateAsync with auditNote writes `note` only on deadline rows; SetTagsAsync writes a 'tags' BacklogAudit row on change. Point each task's `<verify>` filter at the real class names.
15. (Suggestion) `TaskItem` gaining two params should keep `EntitiesTests.cs` green; cheap to assert the new defaults (Type==null, AssigneeUserId==null) â€” optional but trivial.

---
**Summary of required fixes before execution:** (Critical) issue 5 â€” fix W1-T3's zero-match test filter; issue 13 â€” add SchemaV9 migration test. (Important) issue 2 â€” move CREATE TABLE into CreateTables; issue 14 â€” add repo-method audit tests with correct filters. The rest are Suggestions. Source files of record: `src/TimesheetApp/Data/DatabaseInitializer.cs` (SchemaVersion line 14, migrations 200-268, CreateTables 37-191), `src/TimesheetApp/Models/Entities.cs:51`, `src/TimesheetApp/Data/Repositories/BacklogRepository.cs` (LogAsync 143-152, SetTagsAsync 199-213, GetAuditAsync 225-238, AuditRaw 293-303), `src/TimesheetApp/Data/Repositories/TaskRepository.cs:100-112`, `src/TimesheetApp/Models/ReadModels.cs:90-95`, test precedents `src/TimesheetApp.Tests/Data/SchemaV8UpgradeTests.cs`.
