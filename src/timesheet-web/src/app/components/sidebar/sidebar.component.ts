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
