import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive } from '@angular/router';

interface NavItem { link: string; label: string; icon: string; }

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive],
  templateUrl: './sidebar.component.html',
  styleUrl: './sidebar.component.scss',
})
export class SidebarComponent {
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
}
