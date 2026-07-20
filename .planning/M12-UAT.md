# M12 — Tag icon presets · UAT

**Date:** 2026-07-20 · **REQ:** `TAG-03` · **Mode A**
**Branch:** `feature/tag-icon-presets-2026-07-20` (off `d69cb9a`) — **not merged.**
**Gate:** Angular **806 → 814**, build clean, 0 `ERROR` / `FAILED` in raw output.
**Spec:** `docs/superpowers/specs/2026-07-20-tag-icon-presets-design.md` · **Plan:** `docs/superpowers/plans/2026-07-20-M12-tag-icon-presets.md`

---

## How to run it

The app must be rebuilt — this is a UI change and the running process serves a built `wwwroot`.

```
cd src/timesheet-web
npm run build
cd ../..
rmdir /s /q src\TimesheetApp.Api\wwwroot
xcopy /e /i /y src\timesheet-web\dist\worklog\browser src\TimesheetApp.Api\wwwroot
```

Restart `TimesheetApp.Api.exe` with its **working directory set to `src/TimesheetApp.Api`** — that is what makes the content root resolve `data/`.

Then `http://localhost:5080` → **Settings → Workflow** → the **Tags** card. Settings is admin-only, so sign in as `admin`.

🔴 The real company database at `C:\Users\Admin\Documents\TimesheetApp\timesheet.db` must not be touched. The running app uses the disposable `src/TimesheetApp.Api/data/timesheet.db`.

---

## The checks

### G-1 — The glyphs render as glyphs 🔴 the one thing no test can prove

Click **`+ New tag`**. A row captioned **Quick icons** appears under the three fields.

**Expected:** ten coloured emoji.

```
🔥  ⏳  ⬇️  👀  💣  🚫  ❓  ⛰️  📈  📉
```

**FAIL if any renders as `▯` (tofu), or as a flat monochrome outline instead of colour.**

Why this is the highest-value check: **no `font-family` pins an emoji font anywhere in this app** (`styles.scss` declares only body and mono). Rendering relies entirely on system fallback. Two of the ten — **⬇️ and ⛰️** — are ordinary text symbols promoted to emoji by an invisible variation selector, so they are the likeliest to render wrong. Look at those two specifically.

### G-2 — A preset writes the icon and nothing else

With the editor open: type `Blocker` into **Text**, pick any colour, then click **💣**.

**Expected:** 💣 appears in the **Icon** box. **`Blocker` is still in Text. The colour is unchanged.**

### G-3 — Typing still works

Clear the Icon box and type your own glyph — anything not in the ten. Save the tag.

**Expected:** it saves and the chip in the list below shows your glyph. Presets are an addition, not a replacement.

### G-4 — It survives a reload

Reload the page. The tag you created still shows its icon.

### G-5 — 💣 does not look like a system warning

Go to **Task List**. The app draws its own `⚠ Late` and `⚠ At risk` chips there, computed from deadlines.

**Expected:** a tag you made is visibly a tag, not mistakable for one of those automatic warnings. This is why ⚠️ was deliberately excluded from the ten.

### G-6 — Keyboard reach

Tab to the preset row. Each button should be focusable and activate on Enter or Space.

*(Note: the focus ring is the browser default, not the app's themed ring. Known, recorded, not fixed — see §6 of the spec.)*

---

## 🔴 A question the spec never considered — please answer during UAT

**Try this:** edit an existing tag that already has an icon you care about, then click a preset.

**What happens:** the old glyph is gone. There is no undo — clearing the Icon box by hand leaves it *empty*, not restored, and **Cancel** discards the whole edit including your other changes.

**Why it was never weighed.** Spec §5.5 declined a click-again-to-deselect toggle, reasoning that *"the text input sits directly above and clears by hand."* That is true when the box started empty — i.e. when creating a tag. **The spec only ever discussed creating.** On the edit path the reasoning does not hold, and nobody noticed until the final review.

**The decision is yours:**

| Option | |
|---|---|
| **Accept it** | Clicking a preset overwrites, as any picker would. Cheapest, and arguably what a user expects. |
| **Add deselect** | Clicking the active preset again restores nothing — it clears. Does not actually solve this. |
| **Remember the original** | Editing seeds an "original glyph" the user can restore. Real work, new state, and the spec's one-write-path property gets more complicated. |

Recommendation: **accept it for now** and revisit only if it bites in practice. Recorded here so the choice is visible rather than defaulted into.

---

## Result — ✅ PASS (user, 2026-07-20)

**The user ran this and reported UAT OK.** Recorded as a single overall pass, which is what was reported — no per-check breakdown was given back, so none is invented here.

| Check | Verdict |
|---|---|
| G-1 glyphs render | ✅ covered by the user's pass |
| G-2 icon only | ✅ |
| G-3 free text still works | ✅ |
| G-4 survives reload | ✅ |
| G-5 not confusable with system chips | ✅ |
| G-6 keyboard | ✅ |
| Edit-path overwrite | ⏸ **still undecided** — see below |

🔴 **G-1 is the one that mattered**, and it is now the only evidence that exists for it. No test renders a font, so nothing in the 814-test suite touches this. The two at risk were **⬇️ (U+2B07 + FE0F)** and **⛰️ (U+26F0 + FE0F)** — text-presentation characters promoted to emoji by a variation selector, on an app that pins no emoji `font-family`. The user's pass is the record that they render.

⚠️ **Scope of the evidence, stated plainly:** this was confirmed on the user's Windows machine. Emoji fonts differ by platform — an Android or iOS browser reaching this over the LAN resolves different glyphs entirely. If other people will use this from phones, that is a separate observation nobody has made yet.

**Note on ordering:** the merge preceded this pass, on explicit user instruction and consistent with the 2026-07-19 batching decision. The UAT was run afterwards against the merged code and passed, so the gap closed — but it closed after the fact, not before.

## ⏸ Still open: the edit-path decision

The question in the section above — *editing a tag that carries a custom glyph and clicking a preset destroys it with no undo* — was **not answered**. It is a product call, not a defect, and M12 is shippable either way. Left recorded rather than defaulted.
