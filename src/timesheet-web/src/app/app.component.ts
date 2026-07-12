import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ToastService } from './services/toast.service';

// M8.4/W3: the sidebar used to live here, directly next to <router-outlet>, which meant EVERY route
// (including a future /login) rendered it. The authenticated chrome (sidebar + outlet) now lives in
// ShellComponent, mounted only on the layout route in app.routes.ts; /login is a sibling route outside
// it and renders through this bare outlet instead. The toast stays here -- it is app-wide, not tied to
// the authenticated chrome, and `position: fixed` makes it render correctly regardless of what's beneath.
@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <router-outlet />

    @if (toast.message(); as msg) {
      <div class="toast"><span class="tick">✓</span>{{ msg }}</div>
    }
  `,
})
export class AppComponent {
  readonly toast = inject(ToastService);
}
