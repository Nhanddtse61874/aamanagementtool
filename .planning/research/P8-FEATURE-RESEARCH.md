# P8 Task List — FEATURE / DOMAIN Research

**Phase:** P8 (Task List — tracking, tags, holidays, Gantt) · Mode B · WPF .NET 8 internal tool (2–5 person team)
**Date:** 2026-06-27
**Author:** Feature Research agent
**Source of truth:** `.planning/REQUIREMENTS.md` (TL-01…TL-11, TAG-01/02, HOL-01/02) + locked design decisions D1–D9.
**Tag legend:** `[VERIFIED]` = confirmed against codebase/requirements · `[CITED]` = grounded in a referenced REQ/decision · `[ASSUMED]` = recommended behavior beyond literal text (spec author must confirm).

> This report answers *what the correct behavior should be*. It does NOT design schema/architecture (that is the architecture-lead's job). Every behavioral recommendation that is not literally in the requirements is `[ASSUMED]` and re-surfaced at the end as an OPEN QUESTION where a user decision is genuinely needed.

---

## 0. Codebase facts this report relies on

- `[VERIFIED]` `period_month` is stored as the string `"yyyy-MM"` (`Backlog.PeriodMonth`, `Entities.cs:9`, confirmed by `RepositoryCrudTests.cs:49,53`).
- `[VERIFIED]` "Move ▶ next month" already exists: it bumps `period_month` to the next month and writes a `BacklogAudit` row with `field = "period_month"`, `old_value`/`new_value` = the two `"yyyy-MM"` strings, plus `changed_by_*` and `changed_at` (`TimesheetViewModelTests.cs:54`, `BacklogRepository.UpdateAsync` audit block lines 114-118).
- `[VERIFIED]` Backlog **`Type`** (Continue/Implement/Investigate/IT/Estimate) is a *separate* concept from task **`Status`** (Todo/In-process/Done/Pending). A backlog has no stored status; "backlog status is derived from its tasks at runtime" (`Entities.cs:16,35`). So "Done backlog" must be a derived predicate, not a column.
- `[VERIFIED]` The current working-day helper (`SmartInputService.WorkingDays`, lines 45-52) excludes only Sat/Sun via `DayOfWeek` — it does **not** yet consult `Holidays`. HOL-02 mandates extending this.
- `[VERIFIED]` The standup archive pattern (`StandupArchiveService`) is the template TL-09 must mirror: `ExportWeekAsync` returns `null` when no data (no file), `BackfillMissingWeeks` writes any completed period with data but no file on startup, re-export overwrites idempotently, `|` is escaped via `Esc()`.
- `[VERIFIED]` Backlogs are NOT soft-deletable (decision 4); the DEFAULT backlog is excluded from Task List / tracking (TL-04, TL-03).

---

## 1. Working-day math (TL-07, TL-08, HOL-02)

### 1.1 The shared working-day predicate
`[CITED HOL-02]` A single helper `IsWorkingDay(date, holidays)` returns `false` for Saturday, Sunday, **and** any date present in the `Holidays` table. This same helper must back: smart-input distribution, the schedule-warning math, the "≤2 working days" window, and the Gantt day axis. (Extends the existing weekend-only check at `SmartInputService.cs:49`.)

### 1.2 `workingDaysElapsed(start, today)` — recommended definition
`[ASSUMED]` Count of working days in the **inclusive** range `[start, min(today, deadline)]`.
- Inclusive of `start` and of `today`: a one-day backlog (start == today) has elapsed = 1, not 0. This matches the standup/report convention where "the window includes today" (RPT-04, decision 6) — `[CITED]`.
- Clamp the upper bound to `today` so elapsed never exceeds total when today is before the deadline.
- **Edge — today before start:** elapsed = 0 (range is empty). The backlog has not started; treat as 0% time elapsed → never "behind" → no warning. `[ASSUMED]`
- **Edge — today after deadline:** TL-08 (late chip) takes over; elapsed is not used for the warning math. `[ASSUMED]`

### 1.3 `workingDaysTotal(start, deadline)` — recommended definition
`[ASSUMED]` Count of working days in the **inclusive** range `[start, deadline]`.
- **Edge — start == deadline:** total = 1 (if that day is a working day), avoiding division by zero.
- **Edge — deadline before start (data error):** total ≤ 0 → treat the whole schedule math as undefined → **hide** the warning chip (do not crash, do not show a false positive). `[ASSUMED]`
- **Edge — start or deadline lands on a non-working day:** the endpoint simply contributes 0 to the count; the count of *working* days between them is what matters, so this is self-correcting. `[ASSUMED]`

### 1.4 "Within ≤2 working days of deadline"
`[CITED TL-07]` "≤2 working days" = the number of **working** days strictly between `today` and `internalDeadline` is ≤ 2 (weekends + Holidays excluded). Recommended precise form: `workingDaysBetween(today_exclusive, deadline_inclusive) <= 2`, i.e. today, plus the deadline within the next two working days, is "near". `[ASSUMED]` exact endpoint inclusivity — see OPEN QUESTION Q1.
- If today is *after* the deadline this window is irrelevant (TL-08 late chip owns that case).

### 1.5 The "behind schedule" formula — CONFIRMED
`[CITED TL-07, confirmed by user 2026-06-27]`
```
behind  =  loggedHours / estimateHours  <  workingDaysElapsed(start, today) / workingDaysTotal(start, deadline_internal)
```
This is the standard "done% < elapsed%" formula and **supersedes** the original literal request (which compared to remaining-time). Use the *internal (PCT)* deadline as the schedule anchor.

### 1.6 Exactly when the WARNING chip (TL-07) renders vs hides
`[CITED TL-07]` Render the amber `warning` chip **only when ALL** of these hold:
1. `start_date` is present, AND
2. `deadline_internal` is present, AND
3. an estimate is present and > 0 (see §6 for which estimate), AND
4. `today <= deadline_internal` (not past — else TL-08 owns it), AND
5. the backlog is **not Done** (see §5.3), AND
6. `today` is within ≤2 working days of `deadline_internal` (§1.4), AND
7. `behind` is true (§1.5).
**Hide** in every other case. In particular: missing start/deadline/estimate, zero or null estimate, deadline in the past, or Done → no warning chip. `[CITED TL-07 acceptance]`

### 1.7 Exactly when the LATE chip (TL-08) renders vs hides
`[CITED TL-08]` Render the red `late deadline` chip **iff**: `today > deadline_internal` AND the backlog is **not Done**. It needs no estimate and no start_date. It **takes precedence** over the warning chip (never show both). A Done backlog never shows it. `[CITED TL-08]`

### 1.8 Estimate / deadline edge summary (render decision table)
`[ASSUMED]` (consolidates §1.6–1.7 for the spec author)

| Condition | Warning | Late | Reason |
|---|---|---|---|
| no `deadline_internal` | hide | hide | nothing to be late/behind against |
| `today > deadline_internal`, not Done | hide | **show** | TL-08 |
| `today > deadline_internal`, Done | hide | hide | Done suppresses (§5.3) |
| null/zero estimate, within window, behind | hide | — | no estimate → can't compute behind |
| no `start_date` (but estimate+deadline present) | hide | (late still applies) | elapsed undefined → warning needs start |
| within ≤2 wd, behind, all present, not Done | **show** | hide | TL-07 |
| within ≤2 wd, on/ahead of schedule | hide | hide | not behind |

---

## 2. Manual progress % (TL-06) vs auto schedule warning (TL-07)

`[CITED TL-06 ASSUMED note + TL-07]` They are **independent** and communicate different things:
- **`progress_percent`** = a *human judgment* of completeness (0–100, nullable). It is what the PCT *believes* is done. Null renders as "not set", **not** 0% (`[CITED TL-06]`).
- **`warning` chip** = an *objective, computed* signal that logged effort is lagging the elapsed schedule near the deadline. It uses `loggedHours/estimate`, **not** `progress_percent`.

`[ASSUMED]` Recommendation for presentation, to avoid confusion:
- Show the manual % as a progress bar / number in its own column.
- Show the warning/late chips in the **tag/chips** column (TAG-02), visually distinct from the progress bar.
- Do **not** drive the warning off `progress_percent`, and do **not** auto-fill `progress_percent` from logged hours. Keeping them visibly separate (a "what I think" bar vs a "what the clock says" chip) is the clearest UX. A small tooltip on the warning chip explaining the done%<elapsed% reason is helpful. `[ASSUMED]`

---

## 3. Month scoping & carry-over (TL-04 view, TL-09 export)

### 3.1 Base scoping
`[CITED TL-04]` Both the monthly Task List view and the monthly export are scoped by `period_month == selectedMonth` (`"yyyy-MM"`). DEFAULT backlog excluded.

### 3.2 What "moved out of a month" means
`[VERIFIED]` "Move ▶ next month" mutates `period_month` from e.g. `"2026-06"` → `"2026-07"` and records a `BacklogAudit` row `field="period_month", old_value="2026-06", new_value="2026-07"`. After the move the backlog **no longer matches** `period_month=="2026-06"`, so it disappears from June's *live* view. That is correct for the live view (`[ASSUMED]` — June's view shows only backlogs currently belonging to June).

### 3.3 "Include history moved to next month" in the export (TL-09)
`[CITED TL-09]` The June export must still be a faithful record of "what was June's workload", so it must list backlogs that *were* in June but got moved to July. Concretely, to build the export for month `M`:
1. **Current members:** all backlogs with `period_month == M` (excluding DEFAULT).
2. **Moved-out members:** all backlogs that have a `BacklogAudit` row with `field == "period_month"` AND `old_value == M` AND `new_value` = a *later* month (the next month per the existing action), regardless of where they sit now. Render these under a **"Moved to next month"** section. `[CITED TL-09]`
   - Derivation detail `[ASSUMED]`: query `BacklogAudit WHERE field='period_month' AND old_value=@M`. Since the action only ever advances by one month, `new_value` will be `M+1`; a simple `old_value == M` filter is sufficient and robust.
   - **Dedup rule `[ASSUMED]`:** if a backlog appears in both sets (e.g. moved to July then moved back to June), prefer its *current* membership — list it in the main section and skip the moved-out section. Use the latest `period_month` audit row to decide.
   - **`[ASSUMED]`** the moved-out section should still show the same columns (status, assignees, deadlines, estimate, logged hours, progress, tags) plus a "→ moved to {new month}" note, because the export's purpose is the month's accountability snapshot.

### 3.4 Auto-backfill timing
`[CITED TL-09 + D7, mirrors DR-09]` On startup, for every month strictly before the current month that has backlog data (current OR moved-out) but no `{yyyyMM}_tasklist.md` file → generate it. A month with no data → no file. Manual "Export this month" overwrites idempotently. Archive dir `…/Documents/TimesheetApp/TaskListArchives/` (`[ASSUMED]` mirrors standup `StandupArchives`).

`[ASSUMED]` Backfill caveat worth flagging: because the export reads *current* logged hours/progress/tags at generation time, a month archived early then edited later will not reflect later edits unless re-exported. Mirrors the standup archive's existing behavior (acceptable for an internal tool); manual re-export covers it.

---

## 4. Gantt semantics (TL-10)

`[CITED TL-10]` One horizontal bar per backlog, spanning **start_date → deadline** across working days (weekends + Holidays visually skipped/marked), colored by schedule state (normal / warning / late). Native WPF Canvas (D3).

`[ASSUMED]` Recommended precise semantics (spec author to confirm Q3):
- **Bar span:** `start_date` → **`deadline_internal`** (the PCT deadline) as the primary bar. Internal is the team's own commitment and is the one that drives warning/late, so it should define the bar. `[ASSUMED]`
- **Deadline markers:** draw a marker line/triangle at `deadline_internal` (bar end) and a **second distinct marker** at `deadline_external` (PCA) — typically external ≥ internal, so the external marker sits to the right of the bar end. If external < internal, still draw both honestly. `[ASSUMED]`
- **`end_date` vs deadline:** the backlog also has `start_date/end_date` (the planned working window) separate from the two deadlines. Recommendation: the bar = `start_date → deadline_internal`; if you prefer the bar to represent planned work, use `start_date → end_date` and keep both deadlines as markers. This is a genuine ambiguity → **OPEN QUESTION Q3**.
- **Missing `start_date`:** no bar can be positioned. Options `[ASSUMED]`: (a) render a zero-width marker at the deadline only, or (b) render a faint "no start date" placeholder row. Recommend (b) — keep the backlog visible with a hint, rather than silently dropping it from the chart. → confirm in Q3.
- **Missing `deadline_internal` but has `end_date`:** fall back to `start_date → end_date`, neutral color (no schedule state computable). `[ASSUMED]`
- **Color:** late = red, warning = amber, otherwise normal/neutral — same state machine as the chips (§1), so Grid and Gantt agree. `[CITED TL-10/TAG-02]`

---

## 5. Tag UX (TAG-01, TAG-02, TL-07/08)

### 5.1 System chip styling defaults
`[CITED TAG-02]` Fixed styling: `warning` = amber, `late deadline` = red. System chips are **not editable** (D4). Custom tags carry user-chosen icon (emoji/Segoe glyph, `[CITED TAG-01 ASSUMED]` — no image upload), hex color, and text.

### 5.2 Chip ordering
`[ASSUMED]` Recommend: **system chips first** (most urgent signal), with **late before warning** (only one of the two ever shows anyway), then custom tags in a stable order (by `Tags.id` or `text`). Rationale: schedule risk is the highest-salience info; putting it first matches the "scan the list for problems" use case of a small-team overview. Spec author may prefer custom-first — minor, flag as low-priority Q.

### 5.3 Does "Done" suppress warning/late?
`[CITED TL-07/TL-08]` **Yes — a Done backlog shows neither chip.** Both REQs explicitly say the chip is not shown when done. `[VERIFIED]` "Done" must be a **derived** predicate (no backlog status column): recommend **Done = the backlog has ≥1 active task AND every active task's `Status == "Done"`** (`[ASSUMED]` — consistent with "backlog status derived from its tasks", `Entities.cs:35`). Edge: a backlog with **no active tasks** → treat as **not Done** (nothing has been completed) → chips may apply. → confirm derivation in Q2.

---

## 6. Estimate fields — rough vs official (TL-05, TL-07)

`[CITED TL-05]` Display uses `official_estimate_hours`, **falling back to `rough_estimate_hours` when official is null** (TL-05 acceptance is explicit).

`[ASSUMED]` Recommended fallback rule for the **warning math (§1.5)** — apply the *same* precedence so the displayed estimate and the math agree:
```
estimateHours = official_estimate_hours ?? rough_estimate_hours
```
- Both present → use **official** (the committed number).
- Only rough present → use rough.
- Neither present (or ≤ 0) → estimate undefined → **no warning chip** (TL-07 condition 3). Late chip (TL-08) is unaffected — it needs no estimate.
- `[ASSUMED]` Estimates are a duration in **hours** (D8), so they are directly comparable to `SUM(TimeLogs.hours)` with no day-conversion. Both sides of the `loggedHours/estimateHours` ratio are in hours — dimensionally consistent.

---

## OPEN QUESTIONS (need a user/spec-author decision)

- **Q1 — "≤2 working days" endpoint inclusivity (§1.4).** Is "within 2 working days of the deadline" measured as *working days strictly between today and deadline ≤ 2* (so the deadline-day + the prior working day + today all count as "near"), or some other endpoint convention? Affects exactly which day the warning first appears. Recommend: count working days in `(today, deadline]` ≤ 2.
- **Q2 — Definition of a "Done backlog" (§5.3).** Confirm: Done = all *active* tasks are `Status=="Done"` (and at least one active task exists)? And a backlog with zero active tasks = not Done? This governs chip suppression (TL-07/08).
- **Q3 — Gantt bar span & deadline markers (§4).** Should the bar represent `start_date → deadline_internal` (recommended, drives color) or `start_date → end_date` (planned window)? Where do the two deadline markers (internal vs external) sit, and how to render a backlog with **no start_date** (placeholder row vs deadline-only marker vs hide)?
- **Q4 (low priority) — Chip ordering (§5.2).** System chips first (recommended) or custom tags first?
- **Q5 — Moved-out dedup & "moved back" (§3.3).** Confirm the dedup rule (current membership wins; use latest period_month audit) is acceptable, and that only single-step forward moves need handling.
