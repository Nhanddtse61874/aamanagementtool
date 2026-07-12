import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SidebarComponent } from '../sidebar/sidebar.component';

/**
 * The authenticated chrome: sidebar + page outlet. Split out of `AppComponent` in M8.4/W3 so `/login` can
 * render WITHOUT the sidebar -- `app.routes.ts` puts this component on a layout route wrapping the 7
 * existing pages (guarded by `authGuard`), with `/login` as a sibling route outside it.
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShellComponent {}
