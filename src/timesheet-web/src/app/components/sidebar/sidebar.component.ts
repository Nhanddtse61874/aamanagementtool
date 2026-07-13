import { Component, DestroyRef, computed, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../core/auth.service';

interface NavItem { link: string; label: string; icon: string; }

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
  // M8.4/W3: the footer user block used to be static markup ("Nhan" / "Active"). It is now wired to
  // whoever the auth cookie actually belongs to, and doubles as the app's only sign-out control.
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly currentUser = this.auth.currentUser;
  readonly initials = computed(() => {
    const name = this.currentUser()?.name;
    return name && name.length > 0 ? name.charAt(0).toUpperCase() : '?';
  });

  /**
   * M9/P6a. Gates the ADMIN section below -- until now the sidebar offered `/users` and `/settings` to
   * EVERYONE, and `AuthService.currentUser().isAdmin` was read by nothing in the entire app.
   *
   * This hides the links; `adminGuard` (`app.routes.ts`) is what actually makes the routes unreachable. A
   * hidden link is not a guard -- the URL is still typeable -- which is exactly why both exist.
   *
   * 🔴 `=== true`: `isAdmin` is `boolean | undefined` (every generated model field is optional). Undefined
   * FAILS CLOSED, and the section stays hidden. Same rule as `adminGuard.decide`.
   */
  readonly isAdmin = computed(() => this.currentUser()?.isAdmin === true);

  readonly workspace: NavItem[] = [
    { link: '/log', label: 'Log Work', icon: 'log' },
    { link: '/backlog', label: 'Backlog', icon: 'backlog' },
    { link: '/tasklist', label: 'Task List', icon: 'tasklist' },
    { link: '/daily', label: 'Daily Report', icon: 'daily' },
    { link: '/reports', label: 'Reports', icon: 'reports' },
  ];
  readonly admin: NavItem[] = [
    { link: '/users', label: 'Users', icon: 'users' },
    { link: '/settings', label: 'Settings', icon: 'settings' },
  ];

  logout(): void {
    this.auth.logout().pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.router.navigateByUrl('/login');
    });
  }
}
