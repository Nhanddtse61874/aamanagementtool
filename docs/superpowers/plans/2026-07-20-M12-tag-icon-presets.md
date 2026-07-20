# Tag Icon Presets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the tag editor ten preset glyph buttons so the icon field stops being the only unassisted field of the three.

**Architecture:** A `readonly` const of ten `{ glyph, label }` pairs in `settings.component.ts`, exposed to the template as a class property, rendered as a `@for` row of `type="button"` buttons under the existing `.fields` block. Each button calls the **existing** `setTagIcon()` — no new state, no second write path, so a preset and a typed glyph cannot diverge.

**Tech Stack:** Angular (standalone component, signals, `@for`), Jasmine + Karma (ChromeHeadless), SCSS.

**Spec:** `docs/superpowers/specs/2026-07-20-tag-icon-presets-design.md` · **REQ-ID:** `TAG-03`

---

## must_haves

```yaml
truths:
  - Clicking a preset button puts that glyph in the tag draft's icon field and changes nothing else.
  - The free-text icon input still works and still accepts a glyph outside the ten.
  - Every preset survives the existing maxlength="4" without truncation.
  - Presets write the icon only; the tag's text is never set, suggested, or cleared by a preset.
artifacts:
  - PRESET_ICONS exported from settings.component.ts with ten unique entries.
  - A preset button row rendered inside the tag editor.
  - Three list-integrity tests plus one interaction test.
key_links:
  - Preset button (click) -> setTagIcon() -> tagDraft signal -> the icon <input> [ngModel]
  - PRESET_ICONS.glyph.length <= 4 -> the maxlength="4" on settings.component.html:164
```

---

## Context an implementer will not guess

**The tag editor only renders when a draft exists.** `settings.component.html:159` is `@if (tagDraft(); as d)`. In a test you must click `+ New tag` (or `Edit`) first, or the row you are asserting on does not exist.

**The Tags card lives in the `Workflow` tab.** The spec file's `tab('Workflow')` helper switches to it. A test that forgets this queries an unrendered card.

**Why the setters look strange.** `settings.component.ts:164-167` explains it: an Angular template expression is a restricted subset of JavaScript with **no spread operator**, so `tagDraft.set({ ...d, icon: v })` cannot live in the template. That is why `setTagIcon` exists — reuse it, do not add a variant.

**Do not put the buttons inside `<label class="field">`.** A `<label>` delegates clicks to its control; nesting buttons inside one makes a preset click also focus the text input. The row goes **after** the `.fields` div closes, as a sibling.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/timesheet-web/src/app/pages/settings/settings.component.ts` | `PresetIcon` type, `PRESET_ICONS` const, `presetIcons` class property | Modify — add ~30 lines near `TAG_COLOR_DEFAULT:28` and one property on the class |
| `src/timesheet-web/src/app/pages/settings/settings.component.html` | The button row | Modify — insert after the `.fields` div that closes at `:177` |
| `src/timesheet-web/src/app/pages/settings/settings.component.scss` | Row layout | Modify — append near `.tagchip:82` |
| `src/timesheet-web/src/app/pages/settings/settings.component.spec.ts` | Four tests | Modify — append a `describe` block |

**No C#. No schema. No API contract. No `npm run gen:api`.**

---

## Task 1: The preset list

**Model:** `sonnet` → dispatch **`claude-sonnet-5`** (CLAUDE.md §Model Selection Rule — never an older Sonnet).

**Files:**
- Modify: `src/timesheet-web/src/app/pages/settings/settings.component.ts:28`
- Test: `src/timesheet-web/src/app/pages/settings/settings.component.spec.ts`

- [ ] **Step 1: Write the failing tests**

Append to `settings.component.spec.ts`. First widen the existing import at line 14:

```typescript
// was: import { SettingsComponent } from './settings.component';
import { PRESET_ICONS, SettingsComponent } from './settings.component';
```

```typescript
// ══ TAG-03 preset icons ════════════════════════════════════════════════════════════════════════════
//
// These three assert the LIST, not the UI, so they need no TestBed. The length one is the point of the
// block: the icon input carries maxlength="4", so a ZWJ sequence (👨‍💻 = 5 units), a skin-tone modifier
// or a regional-indicator flag would be SILENTLY TRUNCATED into a different glyph. Nothing else in the
// app guards that — there is no server-side cap (SettingsEndpoints.cs:176-177 validates Text only).
describe('PRESET_ICONS', () => {
  it('offers exactly ten glyphs', () => {
    expect(PRESET_ICONS.length).toBe(10);
  });

  it('has no duplicate glyph', () => {
    const glyphs = PRESET_ICONS.map(p => p.glyph);
    expect(new Set(glyphs).size).withContext('two buttons with the same glyph').toBe(glyphs.length);
  });

  // 🔴 The guard. If this fails, someone added an emoji the icon field will cut in half.
  it('every glyph fits the icon input maxlength of 4', () => {
    for (const p of PRESET_ICONS) {
      expect(p.glyph.length).withContext(`${p.label} (${p.glyph}) is ${p.glyph.length} UTF-16 units`)
        .toBeLessThanOrEqual(4);
    }
  });

  it('gives every glyph a non-empty accessible label', () => {
    for (const p of PRESET_ICONS) {
      expect(p.label.trim()).withContext(`glyph ${p.glyph} has no label`).not.toBe('');
    }
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd src/timesheet-web && npm test
```

Expected: FAIL at compile — `"PRESET_ICONS" has no exported member` (TypeScript error TS2305). A compile failure is the correct first red here; the symbol does not exist yet.

- [ ] **Step 3: Add the type and the const**

In `settings.component.ts`, directly below `TAG_COLOR_DEFAULT` at line 28:

```typescript
/** One quick-pick glyph in the tag editor. `label` names the BUTTON for assistive tech — it is never
 *  written into the tag's text. The user still types their own label. */
export interface PresetIcon { readonly glyph: string; readonly label: string; }

/**
 * The ten quick-pick glyphs (TAG-03). Before this the icon field was the only one of the three with no
 * assistance at all — colour has the OS picker (`settings.component.html:174`), text needs none.
 *
 * 🔴 EVERY GLYPH MUST STAY <= 4 UTF-16 UNITS. The input carries maxlength="4" and there is no server-side
 * cap, so a ZWJ sequence (👨‍💻), a skin-tone modifier or a flag would be silently truncated into some other
 * character. A test pins this — do not add an emoji without running it.
 *
 * ⚠️ IS DELIBERATELY ABSENT. Task List builds its own computed "⚠ Late" / "⚠ At risk" chips in
 * `task-list.model.ts:64,66`; a user-authored tag carrying ⚠ would be indistinguishable from an
 * automatic assessment. 💣 carries "risk" without borrowing the system's voice.
 *
 * 📈/📉 DESCRIBE THE ESTIMATE, not effort spent — so "Over" pairs with up and "Under" with down, and the
 * word agrees with the picture. The opposite reading (arrow = hours spent) is defensible and was rejected:
 * it puts an up arrow on UnderEstimate, which reads backwards to anyone skimming a report.
 */
export const PRESET_ICONS: readonly PresetIcon[] = [
  { glyph: '🔥', label: 'Urgent' },
  { glyph: '⏳', label: 'Pending' },
  { glyph: '⬇️', label: 'Low Priority' },
  { glyph: '👀', label: 'Review' },
  { glyph: '💣', label: 'Risk' },
  { glyph: '🚫', label: 'Dropped' },
  { glyph: '❓', label: 'Unclear' },
  { glyph: '⛰️', label: 'Difficult' },
  { glyph: '📈', label: 'OverEstimate' },
  { glyph: '📉', label: 'UnderEstimate' },
];
```

- [ ] **Step 4: Expose it to the template**

A template can only read class members. Add this property to `SettingsComponent` **above the `// 🔴 These exist because an Angular TEMPLATE EXPRESSION IS NOT JAVASCRIPT` comment block** — that block explains the three `setTag*` setters directly beneath it, and inserting between the comment and its setters would make it read as describing `presetIcons` instead.

⚠️ **Do not trust a line number here.** Step 3 inserted ~32 lines above this point, so every line reference in the original file has shifted. Search for the comment text, not a number.

```typescript
  /** TAG-03 — the quick-pick row. The template cannot see a module-level const. */
  readonly presetIcons = PRESET_ICONS;
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd src/timesheet-web && npm test
```

Expected: PASS. The four new specs are green and the previously-passing count is unchanged apart from `+4`.

- [ ] **Step 6: Commit**

```bash
git add src/timesheet-web/src/app/pages/settings/settings.component.ts src/timesheet-web/src/app/pages/settings/settings.component.spec.ts
git commit -m "feat(tags): the ten preset glyphs, with a test that pins them under maxlength"
```

---

## Task 2: The button row

**Model:** `sonnet` → dispatch **`claude-sonnet-5`**.

**Files:**
- Modify: `src/timesheet-web/src/app/pages/settings/settings.component.html:177`
- Modify: `src/timesheet-web/src/app/pages/settings/settings.component.scss:82`
- Test: `src/timesheet-web/src/app/pages/settings/settings.component.spec.ts`

- [ ] **Step 1: Write the failing test**

Append inside the existing top-level `describe('SettingsComponent', ...)` block, so the `tab`, `buttons` and `fixture` helpers are in scope:

```typescript
  // ══ TAG-03 — the preset row writes the icon and NOTHING else ══════════════════════════════════════
  //
  // The editor only exists while a draft does (`@if (tagDraft(); as d)`), so "+ New tag" comes first.
  //
  // 🔴 `fakeAsync` + `tick()` IS NOT OPTIONAL HERE. NgModel writes model→view on a MICROTASK
  // (`resolvedPromise.then()` inside `_updateValue`), so `detectChanges()` alone leaves `input.value`
  // EMPTY — this file already documents the trap twice, at the warning-window test near the top and
  // again above the backup-settings test. Without `tick()` the icon assertion fails outright AND the
  // "text untouched" assertion passes vacuously, which is worse: it would green-light a preset that
  // overwrote the tag's text. No fresh fixture is needed — every click here happens inside the fake
  // zone, so `tick()` can flush what they schedule.
  it('clicking a preset glyph sets the icon and leaves the text alone', fakeAsync(() => {
    tab('Workflow');
    buttons('+ New tag')[0].click();
    fixture.detectChanges();

    const riskBtn = fixture.debugElement.queryAll(By.css('.tagpresets__btn'))[4]
      .nativeElement as HTMLButtonElement;
    expect(riskBtn.textContent?.trim()).withContext('the 5th preset is Risk').toBe('💣');

    riskBtn.click();
    tick();
    fixture.detectChanges();

    const inputs = fixture.debugElement.queryAll(By.css('.editor .input'))
      .map(d => d.nativeElement as HTMLInputElement);
    expect(inputs[0].value).withContext('the icon input').toBe('💣');
    expect(inputs[1].value).withContext('the text input must be untouched').toBe('');
  }));

  // TAG-03 keeps free-text entry. Asserted through the OUTGOING REQUEST rather than the input's own
  // value, which would only prove the test typed into a box.
  it('the free-text icon input still accepts a glyph outside the ten', fakeAsync(() => {
    tab('Workflow');
    buttons('+ New tag')[0].click();
    fixture.detectChanges();

    const inputs = (): HTMLInputElement[] => fixture.debugElement.queryAll(By.css('.editor .input'))
      .map(d => d.nativeElement as HTMLInputElement);

    inputs()[0].value = '🦄';                      // deliberately NOT one of the ten
    inputs()[0].dispatchEvent(new Event('input'));
    inputs()[1].value = 'Mythical';
    inputs()[1].dispatchEvent(new Event('input'));
    tick();
    fixture.detectChanges();

    buttons('Save tag')[0].click();
    expect(api.createTag).toHaveBeenCalledWith(
      jasmine.objectContaining({ icon: '🦄', text: 'Mythical' }));
  }));

  // Every button must be type="button". A <button> with no type defaults to SUBMIT — if this editor is
  // ever wrapped in a <form>, picking an icon would submit it. Cheap to assert, invisible when it breaks.
  it('every preset button is type=button', () => {
    tab('Workflow');
    buttons('+ New tag')[0].click();
    fixture.detectChanges();

    const btns = fixture.debugElement.queryAll(By.css('.tagpresets__btn'))
      .map(d => d.nativeElement as HTMLButtonElement);
    expect(btns.length).toBe(10);
    for (const b of btns) expect(b.type).toBe('button');
  });
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd src/timesheet-web && npm test
```

Expected: FAIL — `Cannot read properties of undefined (reading 'nativeElement')`, because `.tagpresets__btn` matches nothing yet.

- [ ] **Step 3: Add the row to the template**

In `settings.component.html`, insert between the closing `</div>` of `.fields` (line 177) and `<div class="acts">` (line 178):

```html
              <!-- TAG-03 — quick-pick glyphs. Deliberately a SIBLING of .fields, not inside the icon
                   <label>: a label delegates clicks to its control, so a button nested in one would
                   also focus the text box. `type="button"` is required, not stylistic — an untyped
                   <button> defaults to submit. -->
              <div class="tagpresets">
                <span class="tagpresets__cap">Quick icons</span>
                <div class="tagpresets__row">
                  @for (p of presetIcons; track p.glyph) {
                    <button type="button" class="tagpresets__btn" [disabled]="busy()"
                            [title]="p.label" [attr.aria-label]="p.label"
                            (click)="setTagIcon(p.glyph)">{{ p.glyph }}</button>
                  }
                </div>
              </div>
```

`[disabled]="busy()"` matches the three sibling buttons in this card (`Cancel`, `Save tag`, `+ New tag`), which are all disabled mid-request.

- [ ] **Step 4: Add the styles**

Append to `settings.component.scss` after `.tagchip` (line 82). These variables are the ones this file already uses — checked, not assumed:

```scss
/* TAG-03 preset glyph row */
.tagpresets { margin-top: 10px; }
.tagpresets__cap { display: block; font-size: 12px; color: var(--muted); margin-bottom: 6px; }
.tagpresets__row { display: flex; flex-wrap: wrap; gap: 6px; }
.tagpresets__btn {
  font-size: 16px; line-height: 1; padding: 6px 9px; cursor: pointer;
  border: 1px solid var(--line); border-radius: 9px; background: var(--card); color: var(--ink);
}
.tagpresets__btn:hover:not(:disabled) { border-color: var(--accent); background: var(--surf3); }
.tagpresets__btn:disabled { opacity: .5; cursor: default; }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd src/timesheet-web && npm test
```

Expected: PASS, all suites green, 0 warnings.

- [ ] **Step 6: Verify the build is clean**

```bash
cd src/timesheet-web && npm run build
```

Expected: build succeeds with no new warnings. This catches an SCSS typo or a template expression the JIT accepts but AOT rejects — the failure mode `settings.component.ts:164-167` was written about.

- [ ] **Step 7: Commit**

```bash
git add src/timesheet-web/src/app/pages/settings/settings.component.html src/timesheet-web/src/app/pages/settings/settings.component.scss src/timesheet-web/src/app/pages/settings/settings.component.spec.ts
git commit -m "feat(tags): a quick-pick row so the icon field is no longer the unassisted one"
```

---

## Waves

**One wave, strictly sequential.** Task 2's template reads `presetIcons`, which Task 1 creates — Task 1 alone compiles, Task 2 alone does not. `parallelization: true` yields nothing here: two tasks, one file pair, one domain. Dispatching them concurrently would only produce two agents contending over the same component.

## Out of scope — do not touch

Recorded in the spec §6 and repeated here because a helpful implementer will be tempted:

- **Do not add a server-side icon length cap.** Real (`SettingsEndpoints.cs:176-177` validates `Text` only), out of scope.
- **Do not pin an emoji `font-family`.** Real (only body/mono exist, `styles.scss:53,61,114`), out of scope.
- **Do not add the missing `@if` guard at `settings.component.html:187`.** Real, out of scope.
- **Do not build a live preview chip or colour swatches.** Explicit user decision.
- **Do not change `maxlength="4"`.** All ten glyphs fit; the test proves it.
- **Do not add click-again-to-deselect, and do not highlight the active preset.** Both were considered and rejected in spec §5.5 — the icon input above already shows the current glyph and clears by hand. Adding either is speculative work the requirement did not ask for.

`.planning/REQUIREMENTS.md` already carries `TAG-03` (committed `2ed6440`), so no task here edits it.

If any of these looks necessary to finish the task, stop and report instead — it means an assumption in the spec was wrong.

## Verification

- `npm test` green; the Angular gate rises by **7** (4 list + 3 DOM). A rising gate is expected here, not a regression.
- `npm run build` clean.
- 👤 **UAT:** Settings → Workflow → `+ New tag`. Ten glyphs render as glyphs, not `▯`. Click 💣 → it lands in the icon box, the text box stays empty. Type a label, Save. The chip in the list shows 💣 with that text. Reload — it survives.
- Existing tags are untouched: no migration, no rewrite. A tag carrying a glyph outside the ten keeps it and stays editable by hand.
