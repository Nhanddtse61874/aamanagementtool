# M12 — Tag icon presets

**Date:** 2026-07-20
**Status:** Approved (STEP 2 brainstorm, user-approved 2026-07-20)
**Origin:** User request during the STEP 8 UAT session — *"when creating tags, there should be 10 default icons for the user to pick."*
**Fast Lane assessment:** `.planning/fast-lane-tag-icon-presets.json` — **not eligible**, hard exclusion `new_feature`. Full workflow.

---

## 1. What this fixes

Creating a tag asks for three things: text, icon, colour. **Two of them are assisted and one is not.**

```html
<!-- settings.component.html:162-166 — the icon field today -->
<input class="input" placeholder="🔥" maxlength="4" [ngModel]="d.icon"
       (ngModelChange)="setTagIcon($event)" />

<!-- settings.component.html:174 — the colour field, right beside it -->
<input type="color" ... />
```

Colour gets the operating system's picker. Icon gets a text box and the hope that the user knows how to type an emoji. On Windows that means knowing `Win + .` exists.

This is not a defect — the field works, and a user who types a glyph gets exactly that glyph. It is an **asymmetry in assistance**, and closing it is the whole of this milestone.

## 2. Scope, stated narrowly

**A row of ten preset glyph buttons under the icon input. Clicking one writes that glyph into the icon field.** The free-text input stays exactly as it is.

Explicitly: presets write to the **icon field only**. They do not prefill, suggest, or touch the tag's text — the user types their own label. This was the user's stated emphasis and it is what keeps the feature small.

## 3. The ten glyphs

The user supplied the ten meanings; the glyphs were chosen against them and confirmed one at a time.

| # | Meaning | Glyph | Why this one |
|---|---|---|---|
| 1 | Urgent | 🔥 | Already the placeholder in the icon input today (`settings.component.html:165`) — continuity, not novelty |
| 2 | Pending | ⏳ | Sand still running: waiting, not stopped |
| 3 | Low Priority | ⬇️ | The common priority convention |
| 4 | Review | 👀 | Eyes are on it. **🔍 was rejected** — the app already has a backlog type `Investigate` (`worklog.service.ts:189-195`) and the two would read alike |
| 5 | Risk | 💣 | Danger accumulating. See §3.1 — this one has a history |
| 6 | Dropped / won't do | 🚫 | The "won't do" convention. **❌ rejected** (reads as *failed*), **🗑️ rejected** (reads as *deleted*) |
| 7 | Unclear | ❓ | Self-evident |
| 8 | Difficult | ⛰️ | A mountain to climb. **Human glyphs (🧗) rejected** — they carry skin-tone variants, which §4 shows would break |
| 9 | OverEstimate | 📈 | See §3.2 — direction is a decision, not an obvious fact |
| 10 | UnderEstimate | 📉 | See §3.2 |

### 3.1 Risk: ⚠️ is unavailable, and 🎲 was not good enough

The natural glyph for risk is ⚠️. **It is excluded because it collides with system output.** Task List draws its own `⚠ Late` and `⚠ At risk` chips (`task-list.component.html:216`), computed by the app from deadline and pace. A user-authored tag carrying ⚠ would be visually indistinguishable from an automatic assessment — a person reading the board could not tell which claims the system stands behind.

🎲 was proposed first and the user rejected it. Recorded because the reasoning matters: *dice = uncertain outcome* is a defensible metaphor but a second-order one, and this is the only slot in the ten where the obvious choice was unavailable. 💣 carries the meaning directly — something that will go off if left alone.

### 3.2 📈 / 📉 — the direction is load-bearing

The arrows are ambiguous, and the two readings produce **opposite** assignments:

| Reading | OverEstimate | UnderEstimate |
|---|---|---|
| **The arrow describes the ESTIMATE** ← chosen | 📈 estimated high | 📉 estimated low |
| The arrow describes ACTUAL EFFORT | 📉 spent less | 📈 spent more |

**Decision: the arrow describes the estimate.** *"Over"* pairs with the up arrow and *"Under"* with the down arrow, so the word and the picture say the same thing. Under the rejected reading, `UnderEstimate` would carry an up arrow — correct for anyone thinking in hours spent, and backwards for everyone skimming.

This was surfaced rather than assumed, per CLAUDE.md: *"If a REQ-ID has multiple interpretations, surface all of them — do not silently pick one."* Chosen wrong, every over/under tag in every report reads inverted.

## 4. Why no validation, schema, or contract work is needed

Every one of the ten is **at most 2 UTF-16 code units**, against an existing `maxlength="4"`:

| Glyph | Codepoints | `.length` |
|---|---|---|
| 🔥 👀 💣 🚫 📈 📉 | single astral (surrogate pair) | 2 |
| ⬇️ ⛰️ | BMP + `U+FE0F` variation selector | 2 |
| ⏳ ❓ | single BMP | 1 |

None uses a ZWJ sequence (👨‍💻), a skin-tone modifier, or a regional-indicator flag — the three families that exceed four units and would be **silently truncated** by the existing cap.

Consequently: **no change to `maxlength`, no server-side validation work, no schema change, no DTO change, no client regen.** `Tags.icon` has been `TEXT NOT NULL` with no length constraint since schema v7 (`DatabaseInitializer.cs:133-139`), and `TagDto` / `SettingsTagCreateRequest` / `SettingsTagUpdateRequest` already carry `Icon` as a string (`Dtos.cs:107-108`, `SettingsEndpoints.cs:1185`, `:1187`). A preset writes the same field the text box already writes.

This is the property that makes the milestone small, so §7 pins it with a test.

## 5. Design

### 5.1 One write path, not two

Preset buttons call the **existing** `setTagIcon()` (`settings.component.ts:168`). No new state, no second field, no parallel draft. Preset and typing converge on one setter, so the two cannot disagree — the failure mode where a picker and its text box drift apart is structurally absent rather than defended against.

### 5.2 Placement

A row of ten buttons directly beneath the icon input, inside the existing `<label class="field">` group (`settings.component.html:162-166`). The colour picker beside it is untouched.

### 5.3 `type="button"` is required, not stylistic

Every preset button must carry `type="button"`. A `<button>` with no `type` defaults to `submit`; if the tag editor sits within a `<form>`, picking an icon would submit it. This is correctness, not defensive coding.

### 5.4 Accessibility

Each button carries `title` and `aria-label` naming the intended meaning (`"Urgent"`, `"Pending"`, …). Without them the row is ten unlabelled buttons to a screen reader. The names are labels **for the button**, not text written into the tag.

### 5.5 Three things deliberately NOT built

Recorded as choices so their absence is not read as oversight. Per CLAUDE.md *Simplicity First*: the minimum that satisfies the requirement, nothing speculative.

| Not built | Why |
|---|---|
| Click-again-to-deselect | The text input sits directly above and clears by hand. A toggle adds a state machine to save one keystroke. |
| Active-state highlight on the chosen preset | The icon input already displays the current glyph. A second indicator of the same fact can only ever agree or be a bug. |
| Anything beyond the Settings tag card | See §6. |

## 6. Out of scope — three real findings, deliberately untouched

Found while mapping the feature. Each is real and verified; none is caused by this change; all are recorded rather than fixed, per *Surgical Changes*.

1. **No emoji font is pinned anywhere in CSS.** `tag-picker.component.html:37-38` states icons are emoji meant to match WPF's `Segoe UI Emoji`, and `tag-picker.component.spec.ts:75-76` asserts *"`icon` is an EMOJI GLYPH, not an icon-set name"* — yet the only `font-family` declarations in the app are body and mono (`styles.scss:53`, `:61`, `:114`). Rendering relies on system fallback. **This milestone does not make it worse** — it selects from the same glyph space the text box already accepts — but it does raise the stakes, since presets will be the path most users take.
2. **`maxlength="4"` is browser-only.** The API validates `Text` and never `Icon` (`SettingsEndpoints.cs:176-177`, `:193-194`), so an arbitrarily long icon is accepted server-side and an empty icon is valid end to end. Unreachable through this UI; reachable by any direct API call.
3. **`settings.component.html:187` renders `{{ t.icon }} {{ t.text }}` without a guard**, so a tag with an empty icon shows a leading space. The other two render sites (`tag-picker.component.html:40-42`, `task-list.component.html:216`) both guard with `@if`.

## 7. Testing

Angular only. No .NET test changes — no C# is touched.

| Test | Asserts | Why it earns its place |
|---|---|---|
| Clicking a preset sets the icon | The clicked glyph lands in the draft via `setTagIcon` | The feature's one behaviour |
| Every preset is ≤ 4 UTF-16 units | `PRESET_ICONS.every(p => p.glyph.length <= 4)` | **Pins §4.** Anyone later adding a flag or ZWJ emoji turns the gate red instead of shipping a glyph the cap silently truncates |
| The preset list has 10 entries with unique glyphs | Count and uniqueness | A duplicate glyph is two buttons that do the same thing — invisible in review, obvious to a user |

**Baseline note:** no existing test asserts anything about icon *content*, length, or validation. The two icon tests that exist only check a glyph reaches the DOM (`tag-picker.component.spec.ts:77`, `task-list.model.spec.ts:264-266`), and `maxlength="4"` is entirely untested today. This milestone is the first thing to guard the field.

**The Angular gate WILL move up.** Expected, and the size of the move is the number of tests added — not a regression.

## 8. Requirements

**New REQ-ID: `TAG-03`.** `REQUIREMENTS.md` **is** edited, joining `TAG-01` / `TAG-02` under the tag feature.

> **TAG-03 — Preset icons when creating a tag**
> Statement: The tag editor offers ten preset status glyphs; clicking one sets the tag's icon. Free-text icon entry remains available.
> Acceptance: Ten buttons render in the tag editor. Clicking one writes its glyph to the icon field and nothing else — the tag's text is unchanged. The text input still accepts an arbitrary glyph. Every preset is at most 4 UTF-16 code units.

This differs from M9.2, which deliberately added no REQ-IDs because both its defects violated requirements that already existed. This is genuinely new scope, so it carries an ID.

## 9. Files

| File | Change |
|---|---|
| `src/timesheet-web/src/app/pages/settings/settings.component.ts` | `PRESET_ICONS` const — ten `{ glyph, label }` entries, `label` feeding `title`/`aria-label` per §5.4 — plus a handler delegating to `setTagIcon` |
| `src/timesheet-web/src/app/pages/settings/settings.component.html` | The button row under the icon input |
| `src/timesheet-web/src/app/pages/settings/settings.component.scss` | Row layout |
| `src/timesheet-web/src/app/pages/settings/settings.component.spec.ts` | The three tests in §7 |
| `.planning/REQUIREMENTS.md` | `TAG-03` |

**No C#. No schema. No API contract. No client regen.**

## 10. Verification

- 👤 **UAT:** open Settings → Tags → `+ New tag`. The ten buttons render as glyphs, not as `▯` boxes. Click 💣 — it appears in the icon box. Type a label, save. The chip in the list below shows 💣 with the typed text. Reload; it survives.
- ✅ Angular suite green, gate moved up by the number of tests added, 0 warnings.
- ✅ Existing tags are untouched — no migration, no rewrite, no orphaning. A tag already carrying a glyph outside the ten keeps it and remains editable by hand.
