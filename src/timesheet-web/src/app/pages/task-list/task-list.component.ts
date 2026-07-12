import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ProgressMap, TaskCard, Tag } from '../../models/worklog.models';

@Component({
  selector: 'app-task-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './task-list.component.html',
  styleUrl: './task-list.component.scss',
})
export class TaskListComponent {
  private api = inject(WorklogService);

  readonly cards = signal<TaskCard[]>([]);
  readonly progress = signal<ProgressMap>({});
  readonly tags = signal<Tag[]>([]);

  constructor() {
    this.api.getTaskCards().subscribe(c => this.cards.set(c));
    this.api.getTags().subscribe(t => this.tags.set(t));
  }

  typeColor(t: string) { return this.api.typeColor(t); }
  avatar(name: string) { return this.api.avatarColor(name); }
  tagColor(label: string): string {
    return this.tags().find(t => t.label === label)?.color ?? '#5C6560';
  }

  key(ci: number, ti: number): string { return `${ci}-${ti}`; }
  pct(ci: number, ti: number): number { return this.progress()[this.key(ci, ti)] ?? 0; }

  setPct(ci: number, ti: number, value: string): void {
    let v = Math.round(parseFloat(value));
    if (isNaN(v)) v = 0;
    v = Math.max(0, Math.min(100, v));
    const k = this.key(ci, ti);
    this.progress.update(p => ({ ...p, [k]: v }));
    this.api.saveProgress(k, v).subscribe();
  }

  overall(ci: number): number {
    const c = this.cards()[ci];
    if (!c.tasks.length) return 0;
    const sum = c.tasks.reduce((s, _, ti) => s + this.pct(ci, ti), 0);
    return Math.round(sum / c.tasks.length);
  }
  doneCount(ci: number): number {
    return this.cards()[ci].tasks.reduce((s, _, ti) => s + (this.pct(ci, ti) >= 100 ? 1 : 0), 0);
  }
}
