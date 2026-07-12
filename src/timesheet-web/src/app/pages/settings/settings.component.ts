import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ThemeService } from '../../services/theme.service';
import { ToastService } from '../../services/toast.service';
import { Tag, TaskTemplate } from '../../models/worklog.models';

type SettingsTab = 'general' | 'storage' | 'data' | 'workflow' | 'teams';

interface CalCell { day: number; iso: string; weekend: boolean; inMonth: boolean; }

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);
  readonly theme = inject(ThemeService);

  readonly tab = signal<SettingsTab>('general');
  readonly tabs: { id: SettingsTab; label: string }[] = [
    { id: 'general', label: 'General' }, { id: 'storage', label: 'Storage' },
    { id: 'data', label: 'Data' }, { id: 'workflow', label: 'Workflow' }, { id: 'teams', label: 'Teams' },
  ];

  readonly autoBackup = signal(false);
  readonly retention = signal(false);

  readonly holidays = signal<Record<string, boolean>>({});
  readonly weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  readonly calendar = this.buildCalendar();

  readonly tags = signal<Tag[]>([]);
  readonly templates = signal<TaskTemplate[]>([]);
  readonly contacts = signal<string[]>([]);
  readonly teams = signal<string[]>([]);

  readonly storageFields = [
    { title: 'Database file', desc: 'Path to the SQLite database file (.db) used to store data.', value: '' },
    { title: 'Daily report archive', desc: 'Folder where weekly daily-report markdown files are exported.', value: '' },
    { title: 'Export logs — Shared / SharePoint', desc: 'Structured per-team export mirror to a shared folder.', value: '' },
    { title: 'Export logs — Local folder', desc: 'A local mirror of the structured export.', value: '' },
  ];

  constructor() {
    this.api.getTags().subscribe(t => this.tags.set(t));
    this.api.getTemplates().subscribe(t => this.templates.set(t));
    this.api.getContacts().subscribe(c => this.contacts.set(c));
    this.api.getTeams().subscribe(t => this.teams.set(t));
    this.api.getHolidays().subscribe(list => {
      const map: Record<string, boolean> = {};
      list.forEach(iso => (map[iso] = true));
      this.holidays.set(map);
    });
  }

  private buildCalendar(): CalCell[] {
    const cells: CalCell[] = [];
    const start = new Date(2026, 5, 29); // Mon Jun 29
    for (let i = 0; i < 42; i++) {
      const d = new Date(start); d.setDate(start.getDate() + i);
      const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
      const dow = d.getDay();
      cells.push({ day: d.getDate(), iso, weekend: dow === 0 || dow === 6, inMonth: d.getMonth() === 6 });
    }
    return cells;
  }

  isHoliday(iso: string): boolean { return !!this.holidays()[iso]; }
  toggleHoliday(c: CalCell): void {
    if (c.weekend) return;
    this.holidays.update(h => { const n = { ...h }; if (n[c.iso]) delete n[c.iso]; else n[c.iso] = true; return n; });
    this.api.toggleHoliday(c.iso).subscribe();
  }

  notify(msg: string): void { this.toast.show(msg); }
}
