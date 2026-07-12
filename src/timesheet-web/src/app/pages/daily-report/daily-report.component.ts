import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { DailyEntry, TeamMember } from '../../models/worklog.models';

@Component({
  selector: 'app-daily-report',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './daily-report.component.html',
  styleUrl: './daily-report.component.scss',
})
export class DailyReportComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);

  readonly date = '7/12/2026';
  readonly tab = signal<'input' | 'team'>('input');
  readonly entries = signal<DailyEntry[]>([]);
  readonly team = signal<TeamMember[]>([]);

  constructor() {
    this.api.getDailyEntries(this.date).subscribe(e => this.entries.set(e));
    this.api.getTeamBoard(this.date).subscribe(t => this.team.set(t));
  }

  initial(name: string): string { return name[0]; }
  avatar(name: string) { return this.api.avatarColor(name); }
  notify(msg: string): void { this.toast.show(msg); }
}
