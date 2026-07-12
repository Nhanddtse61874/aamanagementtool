import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SidebarComponent } from './components/sidebar/sidebar.component';
import { ToastService } from './services/toast.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent],
  template: `
    <div class="shell">
      <app-sidebar />
      <main class="shell__main">
        <router-outlet />
      </main>
    </div>

    @if (toast.message(); as msg) {
      <div class="toast"><span class="tick">✓</span>{{ msg }}</div>
    }
  `,
  styles: [`
    .shell { display: flex; height: 100vh; width: 100%; overflow: hidden; }
    .shell__main { flex: 1; height: 100%; overflow-y: auto; position: relative; }
  `],
})
export class AppComponent {
  readonly toast = inject(ToastService);
}
