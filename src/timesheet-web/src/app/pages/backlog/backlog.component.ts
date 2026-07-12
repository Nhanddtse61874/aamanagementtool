import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { Backlog } from '../../models/worklog.models';

@Component({
  selector: 'app-backlog',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './backlog.component.html',
  styleUrl: './backlog.component.scss',
})
export class BacklogComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);

  readonly all = signal<Backlog[]>([]);
  readonly search = signal('');
  readonly fProject = signal('All');
  readonly fType = signal('All');
  readonly fAssignee = signal('All');
  readonly fMonth = signal('All');

  readonly projectOpts = ['All', 'ARCS', 'ARMS', 'Other', 'PlusArcs', 'DEFAULT'];
  readonly typeOpts = ['All', 'Investigate', 'Implement', 'Continue', 'IT', 'Estimate'];
  readonly assigneeOpts = ['All', 'Nhan', 'An', 'An Nguyen', 'Binh', 'Binh Tran', 'Chi', 'Chi Le'];
  readonly monthOpts = ['All', '2026-06', '2026-07', '2026-08'];

  constructor() { this.api.getBacklogs().subscribe(b => this.all.set(b)); }

  readonly filtered = computed(() => {
    const q = this.search().toLowerCase();
    return this.all().filter(b =>
      (b.code.toLowerCase().includes(q) || b.project.toLowerCase().includes(q)) &&
      (this.fProject() === 'All' || b.project === this.fProject()) &&
      (this.fType() === 'All' || b.type === this.fType()) &&
      (this.fAssignee() === 'All' || b.assignee === this.fAssignee()) &&
      (this.fMonth() === 'All' || b.month === this.fMonth()));
  });

  typeColor(t: string | null) { return this.api.typeColor(t); }
  avatar(name: string | null) { return this.api.avatarColor(name); }

  clearFilters(): void {
    this.search.set(''); this.fProject.set('All'); this.fType.set('All');
    this.fAssignee.set('All'); this.fMonth.set('All');
  }
  notify(msg: string): void { this.toast.show(msg); }
}
