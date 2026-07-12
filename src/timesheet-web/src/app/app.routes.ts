import { Routes } from '@angular/router';

export const routes: Routes = [
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
  {
    path: 'users',
    loadComponent: () => import('./pages/users/users.component').then(m => m.UsersComponent),
    data: { label: 'Users' },
  },
  {
    path: 'settings',
    loadComponent: () => import('./pages/settings/settings.component').then(m => m.SettingsComponent),
    data: { label: 'Settings' },
  },
  { path: '**', redirectTo: 'log' },
];
