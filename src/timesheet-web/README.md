# Worklog — Angular 17 (standalone)

Angular port of the Worklog UI. Standalone components, one page per screen, lazy-loaded routes, SCSS with a CSS-variable theme (light + dark), and a service layer with **no mock data** — every data call is a stub you connect to your API.

## Run

```bash
cd angular
npm install
npm start          # http://localhost:4200
```

## Structure

```
src/
  styles.scss                     Theme tokens (:root + .dark) + reusable UI primitives
  main.ts                         Bootstrap; applies persisted theme before first paint
  app/
    app.config.ts                 Router + HttpClient providers
    app.routes.ts                 Lazy routes, one per screen
    app.component.ts              Shell: <app-sidebar> + <router-outlet> + toast host
    models/worklog.models.ts      All interfaces (User, Backlog, LogGroup, TaskCard, ...)
    services/
      worklog.service.ts          Data access — STUBS return of([]); wire to HttpClient
      theme.service.ts            Dark-mode + accent (signals), persisted to localStorage
      toast.service.ts            Transient action feedback
    components/sidebar/           Left nav (routerLinkActive drives the highlight)
    pages/
      log-work/                   Editable hour grid, live totals, collapse
      backlog/                    Search + 4 filters + empty state
      task-list/                  Cards with editable per-task % progress
      daily-report/               Input / Team board tabs
      reports/                    Metric cards, weekly/monthly tables, drill-down tree
      users/                      Search + activate/deactivate
      settings/                   Tabbed; dark toggle, accent swatches, holiday calendar
```

## Connect your API

Open `src/app/services/worklog.service.ts`. Each method is a stub:

```ts
getBacklogs() { return of([]); }   // TODO: return this.http.get<Backlog[]>('/api/backlogs');
```

`HttpClient` is already provided (`provideHttpClient(withFetch())`), so inject it:

```ts
private http = inject(HttpClient);
getBacklogs() { return this.http.get<Backlog[]>('/api/backlogs'); }
```

Every page subscribes into a signal, so the UI updates as soon as data arrives. Until then, each screen shows a labelled empty state pointing at the method to implement.

## Theming / dark mode

- Colors are CSS variables in `styles.scss`. `:root` is light; `.dark` on `<html>` overrides them.
- `ThemeService.toggleDark()` toggles the `.dark` class and persists the choice; `setAccent()` sets `--accent`.
- To theme any new element, use the variables (`var(--card)`, `var(--ink)`, `var(--line)`, `var(--accent)`, …) — never hardcode hex, so it follows both themes automatically.
- Dark mode + accent controls live on **Settings → General → Appearance**.

## Responsive

Layouts use intrinsic CSS (auto-fit grids, `flex-wrap`, `min-width`, horizontal scroll on wide tables, `clamp()` sidebar) — no media queries needed for desktop/laptop widths.
