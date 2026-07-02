# Dark Mode (P19)

**Date:** 2026-07-02 · **Status:** Approved-direction (design) — pending plan approval · **Mode:** A
**Topic:** A dark theme with an **instant, no-restart toggle** in Settings, done the idiomatic WPF way (palette split + `DynamicResource` + dictionary swap), persisted in config, applied on startup.

## Context / decision

The app has one light `Theme.xaml` (palette ~36 `SolidColorBrush`/`Color` keys at L13-35 + L509-525, then all the control styles). Views reference palette brushes via `{StaticResource}` (648 StaticResource refs, **0** DynamicResource — theming agent). StaticResource resolves once at load, so swapping the palette dictionary at runtime does **not** update StaticResource consumers.

The user chose the **high-impact, robust** approach over a low-risk brush-mutation trick (see memory `startup-phase-prefers-robust-over-minimal`): convert palette-brush references to `DynamicResource` + swap a palette dictionary. This is the correct WPF idiom, extensible to more themes, and gives a **live** switch. The user's condition: **verify thoroughly.**

## Locked decisions

| # | Decision | Value |
|---|----------|-------|
| D1 | Mechanism | Palette split into `Palette.Light.xaml` + `Palette.Dark.xaml` (same keys); `Theme.xaml` keeps styles and references palette keys via **`DynamicResource`**; a `ThemeService` swaps the palette dictionary at runtime → **live, no restart**. |
| D2 | View refs | Convert palette-brush `{StaticResource KEY}` → `{DynamicResource KEY}` across **all views + Theme.xaml styles**, for palette keys **only** (styles/templates/converters/type-keys stay `StaticResource`). |
| D3 | Hex literals | **Promote** hardcoded color literals in views/Theme.xaml that must switch (alt-row `#F8FAFC`, `#B91C1C`, `#E8ECF1`, TaskListTab card hover `#F8FAFC`, table-header literals) into new palette keys + `DynamicResource`. `DropShadowEffect Color="#0F172A"` shadows stay literal (work in both themes). |
| D4 | Persistence | `IAppConfig.IsDarkMode` (+ `SetIsDarkMode`), stored in appsettings.json like `RetentionEnabled`. Applied on startup. |
| D5 | Toggle | A "Dark mode" toggle in **Settings** → instantly applies (live) + persists. |
| D6 | Verification | STA render tests for **both** Light and Dark on the key views; a "no un-promoted color literal left in views" audit; contrast pass; full UAT sweep of every tab in both modes. |

## Approach

- **`Palette.Light.xaml` / `Palette.Dark.xaml`** — standalone `ResourceDictionary`s, each defining the SAME ~36 keys (Accent*, WindowBg, Surface, Border*, Text*, HeaderBg, Danger*, Disabled, HolidayBg, Sidebar*, Nav*, Table*, Group/StatCardBg, Badge*, Amber*, ResolvedBorder) + the newly-promoted keys (D3). Light = current values; Dark = a slate-based dark palette (tuned in the final wave).
- **`Theme.xaml`** — remove the palette definitions; its own style `Setter`/template refs to palette keys become `{DynamicResource}`; promote its internal literals (D3). All styles otherwise unchanged. (`FontBase`, doubles, geometry stay.)
- **`App.xaml` / `App.xaml.cs`** — merge `[Palette.Light.xaml, Theme.xaml]`; on startup `IThemeService.Apply(config.IsDarkMode)` swaps to Dark if configured (before the first window).
- **`IThemeService` / `ThemeService`** — `Apply(bool dark)` replaces the palette dictionary (index 0) in `Application.Current.Resources.MergedDictionaries` with the Light/Dark one; `IsDark` getter. DI singleton.
- **`SettingsViewModel` / `SettingsTab`** — `IsDarkMode` toggle → `_config.SetIsDarkMode(v)` + `_theme.Apply(v)` (instant).
- **Views** — the D2 conversion (scripted per palette key, verified by grep counts) + D3 promotions.

## Observable truths (must_haves)

1. A "Dark mode" toggle in Settings; toggling switches the whole app **instantly** (no restart), every tab/dialog.
2. The choice **persists** across restarts (appsettings.json) and is applied on next startup.
3. In dark mode, **all** surfaces (backgrounds, text, borders, badges, cards, sidebar, tables, dialogs) are dark-themed — no light patches left (D3 literals promoted).
4. Light mode is visually **unchanged** from today.
5. No render-crash in either theme (STA render guard, both palettes).
6. `dotnet build` clean (0 warnings), full suite green.

## Risks & testing (the "verify thoroughly" mandate)

- **Over-conversion:** only palette KEYS → DynamicResource; NEVER convert style/template/type refs (would hurt perf / break). Verified by a scripted, key-list-driven replacement + grep count reconciliation (palette StaticResource → 0; DynamicResource count == prior).
- **Missed literals:** post-conversion grep `#[0-9A-Fa-f]{3,6}` across `Views/**` (excluding Theme palette + shadow `#0F172A`) must be empty (or explicitly justified).
- **Render matrix:** extend STA render guards to instantiate each major tab/dialog under BOTH palettes and assert no throw.
- **Contrast:** eyeball dark palette for text/background contrast (WCAG-ish) during tuning.
- **UAT sweep:** toggle dark on every tab + dialog; confirm no light patch, readable, toggle instant + persists.

## Out of scope (YAGNI)
- More than 2 themes; per-user (vs per-machine) theme; auto/OS-follow theme; theming the Gantt canvas literals beyond palette brushes (code-behind Gantt already reads theme brushes via `TryFindResource` — will pick up the swap); animating the transition.
