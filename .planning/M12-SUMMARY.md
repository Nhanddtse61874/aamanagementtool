# M12 — Tag icon presets · SUMMARY

**Shipped:** 2026-07-20 · **Merged:** `f3adf0f` (`--no-ff`), pushed to `origin/main`
**Mode:** A · **REQ:** `TAG-03` · **Branch:** `feature/tag-icon-presets-2026-07-20` (off `d69cb9a`)
**Gate:** Angular **806 → 814** (+8). .NET 475 / ApiTests 541 unchanged — **no C# was touched**, verified by diff rather than re-run.

---

## What shipped

Ten preset glyph buttons under the icon field in the Settings tag editor.

```
🔥 Urgent · ⏳ Pending · ⬇️ Low Priority · 👀 Review · 💣 Risk
🚫 Dropped · ❓ Unclear · ⛰️ Difficult · 📈 OverEstimate · 📉 UnderEstimate
```

The problem was an asymmetry, not a defect: creating a tag asks for icon, text and colour; colour had the OS picker, text needs no help, and **icon had a bare text box** — you had to know `Win + .` exists. Presets write the icon field **only**; the user still types their own label, and free-text icon entry is retained.

Angular only. No C#, no schema, no API contract, no client regen — `Tags.icon` has been `TEXT NOT NULL` since schema v7, and presets write the same field the text box already wrote, through the existing `setTagIcon()`.

## Step-by-step

| Step | Outcome |
|---|---|
| 1 Fast Lane | **NOT eligible** — hard exclusion `new_feature`. Full workflow. |
| 2 Brainstorm | Glyph set chosen against ten user-supplied meanings; two decisions escalated (below) |
| 3 Mode Gate | **0/5** Mode B signals, risk **low**, no hard exclusions → **Mode A** |
| 5 Spec | `docs/superpowers/specs/2026-07-20-tag-icon-presets-design.md` |
| 6 Plan + **Plan Checker** | 2 tasks, one wave. Checker verdict **APPROVE WITH CONDITIONS**, 8 defects, all fixed pre-dispatch |
| 7 Execute | Subagent-driven, both tasks `sonnet`. 2 implementer runs + 2 spec reviews + 2 quality reviews + 1 final review |
| 8 UAT | ⚠️ **Not completed** — see below |
| 10/11 Ship | Merged `--no-ff`, pushed |

## Key decisions

- **⚠️ deliberately excluded.** Task List builds its own computed `⚠ Late` / `⚠ At risk` chips (`task-list.model.ts:64,66`); a user tag carrying ⚠ would be indistinguishable from an automatic assessment. 💣 carries "risk" without borrowing the system's voice.
- **📈/📉 describe the ESTIMATE, not effort spent.** Surfaced as two readings rather than assumed — they produce opposite assignments, and the rejected one puts an up arrow on `UnderEstimate`. User chose estimate-direction so word and picture agree.
- **Picker sits beside free text, not instead of it.** User decision, and it is why no existing tag was orphaned.
- **TAG-03 as new scope rather than debt closure under TAG-01.** Closer than it looks — see Deviations.
- **The old desktop app dropped as a reference**, by user decision.

## Deviations

| Deviation | Why |
|---|---|
| **Preset row placed OUTSIDE the icon `<label>`, contradicting approved spec §5.2** | Caught by Plan Checker. A `<label>` delegates clicks to its control, so a nested button would also focus the text box. Has a **visible** consequence — the row spans all three fields — so it was recorded in CLAUDE.md's deviation table, not applied silently. Spec §5.2 amended. |
| **Task 1's quality-review condition DECLINED** | Reviewer suggested extracting `PRESET_ICONS` to its own file to match the `holiday-calendar.ts` precedent. Correct observation, but a refactor outside the task's file list; Surgical Changes says mention rather than fix. Worth revisiting if a second such constant appears. |
| **Merged without a completed UAT** | On explicit user instruction. Consistent with the standing 2026-07-19 batching decision (M9.1/M9.2/M10/M11 all merged un-UAT'd). |

## 🔴 The real lesson: four planning defects, none self-caught

Every one was caught by a gate, not by the plan's author.

| # | Defect | Caught by |
|---|---|---|
| 1 | The DOM test had **no `fakeAsync`/`tick()` at all** — `[ngModel]` writes model→view on a microtask, so it could not have passed | **Plan Checker** |
| 2 | With `tick()` added it was **in the wrong position**; correct order is `detectChanges() → tick() → detectChanges()` | Implementer |
| 3 | `createTag` was never stubbed → `run()` threw on `undefined.pipe()`, Angular swallowed it, and **4 errors printed while the suite reported 813 green** | Implementer |
| 4 | The `"text untouched"` assertion was **structurally vacuous** | **Final review** |

**Defect 4 is the one to carry forward.** On the create path the text field is `''` before the click and `''` after, so the assertion could catch a preset that *wrote* to text but never one that *cleared* it — and colour was unasserted entirely. Same family as M9.2's `grid-state.spec.ts:190`: an assertion that is *correct* and *proves nothing*.

It was closed with an edit-path test, and the vacuity was then **demonstrated rather than argued** — mutating `setTagIcon` to also clear the text fails the new test and leaves the old one **passing**. Same for colour.

**Plan Checker paid for itself.** M9.2 skipped it and got lucky; this time it caught defect 1 plus a styling bug that would have made all ten buttons invisible (`var(--surf2)` on a `var(--surf2)` panel — a true 1:1 contrast).

## ⚠️ What this milestone did NOT prove

- **`G-1` was never confirmed.** *"Do the ten glyphs render as colour emoji rather than tofu?"* is the one check 814 tests structurally cannot make, because no test renders a font. The app was built, deployed and verified to **serve** the feature chunk — but no human result was reported back. **⬇️ and ⛰️ are the two at risk**: they are text-presentation characters promoted to emoji by a variation selector, and nothing in this app pins an emoji `font-family`.
- **`G-2`…`G-6` unrun**, along with the still-owed `OT-13…OT-25` and M9.1's `G3`/`G6`/`G10`.

## 🟠 Open product question — recorded, not decided

On the **edit** path, clicking a preset **destroys an existing custom glyph with no undo** — clearing the box by hand yields empty, not the original, and Cancel discards the whole edit. Spec §5.5 declined a deselect toggle reasoning that *"the text input clears by hand"* — true only when creating, **and the spec only ever discussed creating**. Detail in `.planning/M12-UAT.md`.

## REQ-ID coverage

| REQ | Covered by | Verified |
|---|---|---|
| `TAG-03` | `PRESET_ICONS` + the button row | 8 tests: 4 list-integrity, 4 DOM/behavioural. Two `must_haves` are falsifiable **only** via the edit-path test — before it they passed vacuously. |

## Telemetry

| task | model | wall_clock | escalation | result |
|---|---|---|---|---|
| M12-T1 | sonnet | 2:07 | — | done |
| M12-T2 | sonnet | 8:48 | — | done_with_concerns |

No escalations. Both tasks ran at the tier the plan assigned.

## Artifacts

Spec: `docs/superpowers/specs/2026-07-20-tag-icon-presets-design.md` · Plan: `docs/superpowers/plans/2026-07-20-M12-tag-icon-presets.md` · Fast Lane: `.planning/fast-lane-tag-icon-presets.json` · UAT: `.planning/M12-UAT.md`
