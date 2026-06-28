# UI Consistency Review — New Features (Task List, Local Backup, Multi-Team, Export Restructure, Retention)

**Date:** 2026-06-28
**Scope:** Reviewer checked the 5 new features' XAML/code-behind against the established Theme (`Views/Theme/Theme.xaml`) and the reference tabs (Timesheet, Reports, DailyInput/Board, Users, pre-existing Settings sections). READ-ONLY review.

**Overall verdict: MOSTLY — the new UI follows the existing conventions closely.** Section headers, cards, buttons, inputs, chips, and the Gantt canvas all use the shared Theme styles/brushes correctly. The single real outlier is the two startup banners in `MainWindow.xaml`, which hardcode amber/red hex instead of using the existing `AmberBg/AmberBorder/AmberFg` and `DangerSoft/Danger` brushes that every other banner in the app uses.

---

## Established conventions (baseline)

- **Brushes:** Accent (teal `#0F766E`), `AccentSoft #F0FDFA`, `Surface`, `WindowBg`, `Border/BorderStrong`, `HeaderBg`, `TextPrimary/TextSecondary`, `Danger #DC2626` / `DangerSoft #FEE2E2`, amber set `AmberBg #FFF7ED` / `AmberBorder #FDE9C8` / `AmberFg #B45309`, badge set `BadgeGreen* / BadgeGray*`, sidebar set `SidebarBg/SidebarBorder/NavText/NavHover/SectionLabel`.
- **Named styles:** `SectionTitle`, `FieldLabel`, `FieldHint`, `Card`, `StatCard`, `TableContainer`, `FeatureTitle`, `SidebarSection`, `NavItem`, buttons: default (primary teal) / `GhostButton` / `MiniGhostButton` / `DangerGhostButton` / `ToolbarButton` / `ToolbarGhostButton` / `ToolbarGhostToggle` / `TaskIconButton`.
- **Fonts:** Segoe UI, body 13px; SectionTitle 14px bold; FeatureTitle 17px bold; chips 11px SemiBold.
- **Chips/pills:** `Border CornerRadius=9/10, Padding=8,2`, `Background=AccentSoft`, `Foreground=Accent` for teal pills; custom tags use `HexToBrush` with white text; system chips use `AmberBg/AmberFg` and `Danger`.
- **Overlays:** `Background=#66000000` scrim + card `Surface, CornerRadius=12`, `DropShadowEffect Color=#0F172A`. (`#66000000` and `#0F172A` are the established, intentional literals — used in pre-existing overlays too, so NOT a drift.)
- **Existing banners** (Reports/Timesheet read-only & not-logged): `Background=AmberBg, BorderBrush=AmberBorder, Foreground=AmberFg`.

---

## Area-by-area findings

### 1. TaskListTab.xaml + .xaml.cs — CONSISTENT
- **Colors:** All `{StaticResource ...}` (HeaderBg, Border, AccentSoft, Accent, TextSecondary, AmberBg/AmberBorder/AmberFg, Danger). No hardcoded hex in XAML.
- **Chips:** `ChipTemplate` (L29-70) and the TEAM/TYPE pills (L162-184) match the reference pill exactly: `CornerRadius=9/10, Padding=8,2`, AccentSoft bg / Accent fg, 11px SemiBold. System chips correctly use AmberBg/AmberBorder/AmberFg and Danger. Row-detail status pill (L232-236) consistent.
- **Buttons:** `MiniGhostButton` for the expand toggle (L149) and `ToolbarButton` for Export (L106). Correct.
- **Inputs:** ComboBoxes/toggles rely on implicit styles. The Grid/Gantt `ToggleButton`s (L87-88) use implicit default ToggleButton style — consistent with how DailyInput/Timesheet use toggles.
- **Gantt canvas (code-behind):** `Res(key, fallback)` helper (L70-71) pulls `Accent / Danger / AmberFg / Border / HeaderBg / TextSecondary / TextPrimary` via `TryFindResource`, with literal fallbacks only for design-time. Bar colors late=Danger, warning=AmberFg, normal=Accent (L87-93, 164-169) — **exactly matches the chip color semantics.** This is the correct pattern.

### 2. TeamFilter.xaml (+.cs) — CONSISTENT
- Uses `ToolbarGhostToggle` style for the "Teams ▾" button (L10) — the purpose-built style for exactly this. Popup card uses Surface/Border/CornerRadius=8 + `DropShadowEffect Color=#0F172A` (L15-19), matching the ComboBox popup and other dropdowns. CheckBoxes implicit. No drift.

### 3. MainWindow.xaml — MINOR-DRIFT (one real issue)
- **Sidebar TEAM switcher (L59-69):** CONSISTENT — `SidebarSection` label + implicit ComboBox, mirrors the WORKSPACE/ADMIN nav structure. Good.
- **Nav restructure (L72-104):** CONSISTENT — all `NavItem` radio buttons, `SidebarSection` headers.
- **Per-feature headers (L162-263):** CONSISTENT — `FeatureTitle` + emoji + SectionLabel subtitle, identical pattern across all panels.
- **DRIFT — startup banners hardcode hex instead of Theme brushes:**
  - **XC-08 conflict banner (L119, L131):** `Background="#FEF3C7"`, `BorderBrush="#FDE68A"`, text `Foreground="#92400E"`. Should use `{StaticResource AmberBg}` / `{StaticResource AmberBorder}` / `{StaticResource AmberFg}` — the exact brushes the Reports/Timesheet amber banners use. The hardcoded values are even a *different shade* of amber than the theme's (`#FFF7ED`/`#FDE9C8`/`#B45309`), so these banners visibly differ from every other warning surface in the app.
  - **XC-09 journal banner (L135, L151):** `Background="#FEE2E2"`, `BorderBrush="#FECACA"`, text `Foreground="#991B1B"`. `#FEE2E2` is **identical** to `{StaticResource DangerSoft}` — should reference it. Border should use a danger-tint brush (closest theme token: `DangerSoft`/`Danger`; `#FECACA` has no exact token), text should use `{StaticResource Danger}` (`#DC2626`) or a dark-danger. As-is it is a one-off red not reused anywhere else.
  - The Dismiss button correctly uses `MiniGhostButton` (L149) — no issue there.
  - **Ellipse status dot (L53):** `Fill="#22C55E"` hardcoded green. No exact theme token exists (`BadgeGreenFg` is `#15803D`, a darker green). Low priority — it is a 7px decorative dot, but for strictness it could use `BadgeGreenFg`.

### 4. SettingsTab.xaml (new sections: Tags, PCA contacts, Holiday calendar, Teams, Backup & Restore, Export logs, Data retention) — CONSISTENT
- **Section headers:** Every new section uses `Style="{StaticResource SectionTitle}"` + `FieldHint` (Export logs L33-35, Backup L57-59, Retention L107-109, Tags L184-186, PCA L223-225, Teams L274-276, Holiday L331-333) — identical to the pre-existing DB-path / N-days sections. 
- **Cards/list rows:** All list rows use the established `Border BorderBrush=Border, BorderThickness=1, CornerRadius=6, Padding=10,6` pattern (L88, L153, L192, L235, L286) — matches the pre-existing templates/default-tasks list rows.
- **Buttons:** Correct vocabulary throughout — primary default for Apply/Backup now/Export now/Add (L17,29,51,65,78,119,228,279), `GhostButton` for Browse/Refresh/Preview (L16,28,40,64,80,123), `DangerGhostButton` for Restore/Delete/Deactivate/Run retention (L92,126,157,196,239,290), `MiniGhostButton` for Edit/Rename/Members (L162,201,245,296,302), `TaskIconButton` in the template overlay.
- **Inputs:** TextBoxes set `Width + Height=32` consistent with the pre-existing rows; CheckBoxes implicit. Retention preview uses `FontFamily=Consolas` (L131) — intentional monospace for the dry-run dump, acceptable (matches no other surface but is a code/log readout).
- **Chips:** Tag chip (L206-215) and tag-editor preview (L539-546) use `HexToBrush` + white text + `CornerRadius=10` — matches the Task List ChipTemplate.
- **Holiday calendar (L334-418):** Custom day-cell button template, but every color pulls from Theme (`Surface, Border, BadgeGrayBg, BadgeGrayFg, Accent, Disabled, TextPrimary, TextSecondary`). Holiday=Accent, weekend=BadgeGray, out-of-month=Disabled — consistent semantics. No hardcoded color. 
- **Overlays (template/tag/membership editors, L422-641):** Use the same `#66000000` scrim + `#0F172A` shadow + HeaderBg header/footer as the pre-existing Requests editor. Consistent (these literals are the established overlay convention).

### 5. RequestsTab.xaml — "Tracking" subsection + team chip — CONSISTENT
- **Team chip column (L72-83):** Identical to TaskListTab's TEAM pill — AccentSoft/Accent, CornerRadius=9, 11px SemiBold. Good.
- **Tracking subsection (L208-298):** Wrapped in `Border Background=HeaderBg, CornerRadius=8, Padding=12,10` (L210) — a grouped sub-panel, reasonable and uses theme brushes. "Tracking" title uses `FieldLabel` + `FontWeight=Bold` (L213-214) rather than `SectionTitle`; this is a *sub*-section inside a dialog, so using a bolded FieldLabel is acceptable and matches the dialog's other labels in scale. MINOR/nit only.
- **Tag multi-select chips (L283-292):** `HexToBrush` + white + CornerRadius=10 — matches the canonical chip. Good.
- **Fields:** All `FieldLabel` + implicit DatePicker/TextBox/ComboBox. Consistent with the rest of the editor.

---

## Prioritized fix list

### High (visually obvious outlier / off-brand hardcoded color)
1. **`MainWindow.xaml` L119,131 — XC-08 amber banner hardcodes `#FEF3C7 / #FDE68A / #92400E`.** Replace with `{StaticResource AmberBg}` / `{StaticResource AmberBorder}` / `{StaticResource AmberFg}`. These are a different amber shade than the app's standard warning banners, so it reads as a different component.
2. **`MainWindow.xaml` L135,151 — XC-09 red banner hardcodes `#FEE2E2 / #FECACA / #991B1B`.** `#FEE2E2` == `{StaticResource DangerSoft}` exactly; use it for Background. Use `{StaticResource Danger}` for the text foreground. (No exact token for the `#FECACA` border; `DangerSoft` or a new danger-border token is the closest — flag for a token addition if an exact match is wanted.)

### Medium
*(none — no medium-severity drift found.)*

### Low / nits
3. **`MainWindow.xaml` L53 — status Ellipse `Fill="#22C55E"`** (decorative online-dot green). No exact theme token; could use `{StaticResource BadgeGreenFg}` (`#15803D`) for strictness, or leave as a deliberate "online" accent.
4. **`RequestsTab.xaml` L213 — "Tracking" sub-heading** uses bold `FieldLabel` instead of `SectionTitle`. Intentional dialog-scale heading; leave as-is unless a dedicated sub-section title style is desired.
5. **`SettingsTab.xaml` L131 — retention preview `FontFamily=Consolas`** is the only monospace surface; acceptable for a dry-run log dump.

---

## Verdict

**Does the new UI follow the existing UI? MOSTLY (yes, with one fix).**
Four of the five new feature areas are fully consistent with the established Theme — they reuse the shared brushes, named button/label/card styles, chip pattern, and even the Gantt code-behind correctly resolves Theme brushes via `TryFindResource`. The only genuine inconsistency is the pair of startup banners in `MainWindow.xaml` that hardcode amber/red hex instead of the existing `AmberBg/AmberBorder/AmberFg` and `DangerSoft/Danger` brushes; fixing those two banners brings the new UI to full consistency.
