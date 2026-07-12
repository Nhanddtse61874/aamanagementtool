import { Injectable } from '@angular/core';
import { Observable, of } from 'rxjs';
import {
  Backlog, DailyEntry, LogGroup, Metric, MonthlyRow, Tag, TaskCard,
  TaskTemplate, TeamMember, TreeNode, User, WeeklyRow, DayColumn,
} from '../models/worklog.models';

/**
 * Central data access for the Worklog app.
 *
 * NOTE: This service intentionally ships NO mock data — every method returns
 * an empty stream. Replace each `of(...)` with a real HttpClient call, e.g.
 *
 *   getBacklogs() { return this.http.get<Backlog[]>('/api/backlogs'); }
 *
 * Constants that are pure presentation (type/avatar colors) live here so the
 * whole app derives colors from one place.
 */
@Injectable({ providedIn: 'root' })
export class WorklogService {
  // ---- presentation constants (safe to keep) ----
  readonly TYPE_COLORS: Record<string, { bg: string; c: string }> = {
    Investigate: { bg: '#E8EEFB', c: '#2A5BD7' },
    Implement:   { bg: '#E7F2EC', c: '#1B7A4B' },
    Continue:    { bg: '#E3F0F0', c: '#0E7C66' },
    IT:          { bg: '#ECEAFB', c: '#5B48D6' },
    Estimate:    { bg: '#FBF0DE', c: '#B5791F' },
  };

  readonly AVATAR_COLORS: Record<string, string> = {
    An: '#0E7C66', 'An Nguyen': '#2A6FDB', Binh: '#8B5CF6', 'Binh Tran': '#0EA5A0',
    Chi: '#0891B2', 'Chi Le': '#2563EB', 'Dung Pham': '#DB2777', 'Em Vo': '#9333EA',
    'Giang Do': '#0D9488', 'Huy Bui': '#CA8A04', Nhan: '#0E7C66', 'Phuc Hoang': '#7C3AED',
  };

  readonly WEEK_DAYS: DayColumn[] = [
    { dow: 'MON', date: '06/07' }, { dow: 'TUE', date: '07/07' },
    { dow: 'WED', date: '08/07' }, { dow: 'THU', date: '09/07' },
    { dow: 'FRI', date: '10/07' },
  ];

  avatarColor(name: string | null): string {
    return name ? (this.AVATAR_COLORS[name] ?? '#0E7C66') : '';
  }
  typeColor(type: string | null): { bg: string; c: string } | null {
    return type ? (this.TYPE_COLORS[type] ?? { bg: '#EEF1F0', c: '#5C6560' }) : null;
  }

  // ---- data (connect to API) ----
  getUsers(): Observable<User[]> { return of([]); }                 // TODO: GET /api/users
  getBacklogs(): Observable<Backlog[]> { return of([]); }           // TODO: GET /api/backlogs
  getLogGroups(): Observable<LogGroup[]> { return of([]); }         // TODO: GET /api/logwork?week=
  getTaskCards(): Observable<TaskCard[]> { return of([]); }         // TODO: GET /api/tasklist
  getDailyEntries(date: string): Observable<DailyEntry[]> { return of([]); }   // TODO
  getTeamBoard(date: string): Observable<TeamMember[]> { return of([]); }      // TODO
  getMetrics(): Observable<Metric[]> { return of([]); }             // TODO: GET /api/reports/metrics
  getMissing(): Observable<string[]> { return of([]); }             // TODO
  getWeekly(): Observable<WeeklyRow[]> { return of([]); }           // TODO
  getMonthly(): Observable<MonthlyRow[]> { return of([]); }         // TODO
  getDrilldown(): Observable<TreeNode | null> { return of(null); }  // TODO
  getTags(): Observable<Tag[]> { return of([]); }                   // TODO
  getTemplates(): Observable<TaskTemplate[]> { return of([]); }     // TODO
  getContacts(): Observable<string[]> { return of([]); }            // TODO
  getTeams(): Observable<string[]> { return of([]); }               // TODO
  getHolidays(): Observable<string[]> { return of([]); }            // TODO: ISO date strings

  // ---- mutations (connect to API) ----
  saveHours(key: string, value: string): Observable<void> { return of(void 0); }        // TODO
  saveProgress(key: string, pct: number): Observable<void> { return of(void 0); }        // TODO
  toggleUser(name: string): Observable<void> { return of(void 0); }                      // TODO
  toggleHoliday(iso: string): Observable<void> { return of(void 0); }                    // TODO
}
