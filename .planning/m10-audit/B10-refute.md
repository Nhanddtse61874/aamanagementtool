# B10 — Theme (light/dark) — Adversarial Refutation

Read-only audit. No build, no run, no DB touched.

## Verdict table

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| Live theme swap with no app restart | COVERED | **YES** | **PARTIAL** | Mechanism covered end-to-end, but theme *reach* is narrower. WPF swaps a 39-key palette incl. status/badge/amber/holiday (`Palette.Dark.xaml:30-58`: `DangerSoft #7F1D1D`, `BadgeGreenBg #14532D`, `AmberBg #422006`, `HolidayBg #334155`). Web `.dark` overrides only 13 of 22 tokens (`styles.scss:30-43`) and deliberately leaves status tints light-only (`styles.scss:24-27` — "status tints (shared)"), plus 23 hardcoded light hexes in 5 component stylesheets (`reports.component.scss:5-26`, `settings.component.scss:69,73`, `task-list.component.scss:89,130`, `log-work.component.scss:80-81`, `sidebar.component.scss:36,38`). |
| Theme persisted across sessions, applied before first paint | COVERED | no | **COVERED** | Traced. Web: `main.ts:6-10` reads `localStorage['worklog.theme']` and stamps `.dark` on `<html>` before `bootstrapApplication`; `theme.service.ts:17-27` writes it back via `effect()`. Correction to the auditor: the WPF *storage* is **Core**, not WPF — `TimesheetApp.Core/Config/IAppConfig.cs:52-54` + `JsonAppConfig.cs:66,78,134`. Only the startup application (`App.xaml.cs:46-48`) dies, and `main.ts:6-10` covers it. |
| Dark-mode toggle exposed in Settings UI | COVERED | no | **COVERED** | WPF `Views/Tabs/SettingsTab.xaml:9-11` (`<CheckBox Content="Dark mode" IsChecked="{Binding IsDarkMode}"/>`, ungated) → web `settings.component.html:41,52-53` (`<button role="switch" (click)="theme.toggleDark()">`, also ungated) wired via `settings.component.ts:81` `inject(ThemeService)`. Accent picker (`html:60-61`) is extra, not a gap. |

## What I actually traced (claim 1)

Toggle → signal → effect → DOM → CSS, end to end, no reload:

- `settings.component.html:53` `(click)="theme.toggleDark()"`
- `theme.service.ts:29` `toggleDark()` → `dark.update(v => !v)`
- `theme.service.ts:18-26` `effect()` → `root.classList.toggle('dark', this.dark())`
- `styles.scss:30-43` `.dark { --ink; --bg; --card; ... }` → `styles.scss:52-58` `body { color: var(--ink); background: var(--bg) }`

The no-restart claim itself is real. It is the *completeness* of the swap that fails.

## Why PARTIAL and not COVERED on claim 1

WPF's dark palette re-themes every semantic surface. The web's does not. Concretely, in dark mode the web still renders:

- Reports "missing" banner + warn metrics on `#FFF8EF` / `#F0DBBE` (`reports.component.scss:5,25`)
- Settings holiday calendar cells on `#FCE9E4` (`settings.component.scss:69,73`)
- Task-list warn chips on `#FBF0DE` (`task-list.component.scss:89`)
- Log-work dropzone on `#FFF8EF` (`log-work.component.scss:80`)
- `.badge-ok` / `.badge-warn` on `#E7F2EC` / `#FBF0DE` (`styles.scss:25-26,105-106`)
- `.toast` on `#151A18` against a `--bg` of `#141A18` — effectively the same colour (`styles.scss:40,139`)

Secondary risk: `Palette.Dark.xaml` is the **only** artifact recording the intended dark values for those tokens. Deleting `src/TimesheetApp/` destroys the design reference at the same moment it destroys the running app you would have compared against.

## Cost to close

Low. Add ~8 token overrides to the `.dark` block in `styles.scss` (ok/warn/danger tints), convert the 23 hardcoded component hexes to those tokens, and darken `.toast`. Port the values from `Palette.Dark.xaml:30-55` **before** it is deleted.

## Not refuted

- Persistence scope (`%APPDATA%` JSON vs `localStorage`) — both are per-user-per-machine. The auditor's "equivalent scope" call stands.
- Pre-paint anti-flash — `main.ts` runs as a deferred module before `bootstrapApplication`. A marginally weaker guarantee than WPF's pre-window `Apply()`, but the mechanism is genuinely present and `index.html` carries no competing inline styling.
- No server-side theme persistence exists on either side (grep over `src/TimesheetApp.Api` for theme/darkmode: zero hits), so nothing was dropped in the API layer.
