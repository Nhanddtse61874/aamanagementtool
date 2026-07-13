import { Routes } from '@angular/router';
import { adminGuard } from './core/admin.guard';
import { authGuard } from './core/auth.guard';

// M8.4/W3: this used to be 7 flat sibling routes with no layout parent, and AppComponent hard-coded the
// sidebar next to <router-outlet> -- so there was no way for one route (/login) to render without it.
// Login is now a sibling OUTSIDE the layout route below; everything else is a child of it, behind
// authGuard, rendered inside ShellComponent (sidebar + inner outlet).
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./pages/login/login.component').then(m => m.LoginComponent),
    data: { label: 'Log in' },
  },
  {
    path: '',
    loadComponent: () => import('./components/shell/shell.component').then(m => m.ShellComponent),
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'log' },
      {
        path: 'log',
        loadComponent: () => import('./pages/log-work/log-work.component').then(m => m.LogWorkComponent),
        data: { label: 'Log Work' },
      },
      {
        path: 'backlog',
        loadComponent: () => import('./pages/backlog/backlog.component').then(m => m.BacklogComponent),
        data: { label: 'Backlog' },
      },
      {
        path: 'tasklist',
        loadComponent: () => import('./pages/task-list/task-list.component').then(m => m.TaskListComponent),
        data: { label: 'Task List' },
      },
      {
        path: 'daily',
        loadComponent: () => import('./pages/daily-report/daily-report.component').then(m => m.DailyReportComponent),
        data: { label: 'Daily Report' },
      },
      {
        path: 'reports',
        loadComponent: () => import('./pages/reports/reports.component').then(m => m.ReportsComponent),
        data: { label: 'Reports' },
      },
      // M9/P6a: the two ADMIN screens. `authGuard` on the parent proves you are SIGNED IN; `adminGuard` here
      // proves you are an ADMIN. Both are needed: every read these two screens make is admin-gated
      // server-side (`/api/users/all`, `/api/pca-contacts/all`, `/api/teams/all`, the membership read...), so
      // a non-admin who reaches them 403s on every call at once and sees a broken screen, not a hidden one.
      {
        path: 'users',
        loadComponent: () => import('./pages/users/users.component').then(m => m.UsersComponent),
        canActivate: [adminGuard],
        data: { label: 'Users' },
      },
      {
        path: 'settings',
        loadComponent: () => import('./pages/settings/settings.component').then(m => m.SettingsComponent),
        canActivate: [adminGuard],
        data: { label: 'Settings' },
      },
      { path: '**', redirectTo: 'log' },
    ],
  },
];
