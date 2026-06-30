# P13 — Task List Operations & History — REQUIREMENTS (draft)

**Mode:** B (team spine). **Status:** STEP 2 (brainstorm/requirements) — awaiting decision confirmation before STEP 4 research.
**Schema:** current v8 → this milestone needs **v9** (additive).

## Grounded facts (from understanding agents)
- `Backlog` already has: Code, Project, Type, AssigneeUserId(PCT), DeadlineInternal/External, Rough/OfficialEstimateHours, ProgressPercent, Note, PcaContactId, PeriodMonth, Start/EndDate, TeamId.
- `TaskItem` has ONLY: Id, BacklogId, TaskName, OrderIndex, IsActive, Status. **No type/PCT/tags.**
- `BacklogAudit` = field-level (field/old/new/user/time); `UpdateAsync` auto-writes diff per changed field. **No note/reason column.** Tags NOT audited.
- No `TaskTags` table. Tags only on Backlogs (BacklogTags).
- `TimeLog` has **no note** column; Reports show no note anywhere.
- Backlog editor: create & edit currently identical (no field gating). Tags = checkbox list. Progress = TextBox Width=70.
- Holiday cells styled with `HeaderBg` (same as header) — needs distinct darker gray + "Holiday" placeholder.

---

## Requirements

### Group A — Backlog Create/Edit split (operational vs basic)
- **REQ-A1** — Backlog tab **Edit** restricted to *basic* fields only: Code, Project, Type, Assignee `[+ Month/Year + Tasks? → DECISION D4]`. Operational fields removed from Edit.
- **REQ-A2** — Backlog **Create**: operational fields shown but **disabled/grayed (not fillable)** `[scope → DECISION D3]`.
- **REQ-A3** — Fix Progress field layout (normalize size with sibling fields; user reports it renders larger).
- **REQ-A4** — Tags picker → **multi-select dropdown** (mirrors `TeamFilter` "Tags (N) ▾" popup + type-to-filter), in BOTH Create & Edit. *(default — not blocking)*

### Group B — Task List inline edit + history
- **REQ-B1** — Inline-edit **Type, PCT, PCA, Internal, External, Tags** on the grid via dropdowns; persist via `IBacklogRepository.UpdateAsync` (auto field-audit) + add tag-change auditing.
- **REQ-B2** — Editing **Internal/External** opens a **Note popup (reason)**; reason stored in audit → **BacklogAudit + `note` column (v9)**.
- **REQ-B3** — Task sub-rows (expand) gain **PCT, TAG, TYPE** + a **Status dropdown** → **Tasks + `type`,`assignee_user_id` (v9) + `TaskTags` table (v9)**. Task-level history `[→ DECISION D2]`.

### Group C — Log Work
- **REQ-C1** — Holiday entry cells: darker-gray background (distinct from header) + "Holiday" placeholder text. *(no schema)*

### Group D — Reports
- **REQ-D1** — "Note logged" per user: wrap to multiple lines, fixed max cell height, vertical scroll when overflow. Source of the note `[→ DECISION D1]` (TimeLog has no note today).

---

## Decisions — RESOLVED (2026-06-30)
- **D1 — Reports "note logged" = the NOT-LOGGED warning.** It is NOT a note field; it is the existing "⚠ NOT LOGGED" warning listing users who haven't logged work. REQ-D1 = make that warning **wrap per-user + fixed max height + vertical scroll**. **No schema change.**
- **D2 — Task-level history = YES** → new `TaskAudit` table (full per-task field history, mirrors BacklogAudit).
- **D3 — Create gating = ONLY Progress** is disabled/grayed at create. All other fields fillable at create.
- **D4 — Backlog Edit = non-operational fields.** Operational fields move to Task List.

## Resolved field mapping
- **Operational (Task List inline + history; REMOVED from Backlog Edit):** Progress, DeadlineInternal, DeadlineExternal, PcaContact.
  - Internal/External edits open a **Note (reason) popup** → stored in BacklogAudit.note.
  - *Inferred:* **Progress** added to Task List inline set (only place left to edit it after create-gray + edit-removal). Flagged for user confirm.
- **Dual (editable in BOTH Backlog Edit AND Task List inline):** Type, Assignee(PCT), Tags.
- **Basic / non-operational (Backlog Edit + Create):** Code, Project, Type, Assignee, Month/Year, Start/End, Rough/Official Estimate, Note, Tags.
- **Create form:** everything fillable EXCEPT Progress (grayed). Internal/External/PCA are *set-once at create*, then changed operationally in Task List.
- **Task sub-rows (expand):** show + edit PCT, TYPE, TAG, Status (dropdowns) → audited via TaskAudit.
- **Tags control everywhere:** multi-select dropdown (TeamFilter-style "Tags (N) ▾" + type-to-filter).

## v9 schema (final)
- Tasks: `+type TEXT NULL`, `+assignee_user_id INTEGER NULL`
- `TaskTags(task_id, tag_id)` join table (mirrors BacklogTags)
- `TaskAudit(id, task_id, field, old_value, new_value, changed_by_user_id, changed_by_name, changed_at)` (mirrors BacklogAudit)
- BacklogAudit: `+note TEXT NULL` (deadline-change reason)
- **No TimeLog change** (D1 is display-only).
