import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { HoursMap, LogGroup } from '../../models/worklog.models';

@Component({
  selector: 'app-log-work',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './log-work.component.html',
  styleUrl: './log-work.component.scss',
})
export class LogWorkComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);

  readonly days = this.api.WEEK_DAYS;
  readonly groups = signal<LogGroup[]>([]);
  readonly hours = signal<HoursMap>({});
  readonly collapsed = signal<Record<number, boolean>>({});

  constructor() {
    this.api.getLogGroups().subscribe(g => this.groups.set(g));
  }

  // ---- helpers ----
  private num(v: string | undefined): number { const n = parseFloat(v ?? ''); return isNaN(n) ? 0 : n; }
  fmt(n: number): string { return n.toFixed(1); }
  key(gi: number, ti: number, di: number): string { return `${gi}-${ti}-${di}`; }
  hour(gi: number, ti: number, di: number): string { return this.hours()[this.key(gi, ti, di)] ?? ''; }

  typeColor(t: string | null) { return this.api.typeColor(t); }
  avatar(name: string | null) { return this.api.avatarColor(name); }
  isOpen(gi: number): boolean { return !this.collapsed()[gi]; }

  rowTotal(gi: number, ti: number): number {
    return this.days.reduce((s, _, di) => s + this.num(this.hour(gi, ti, di)), 0);
  }
  groupTotal(gi: number): number {
    const g = this.groups()[gi];
    return g.tasks.reduce((s, _, ti) => s + this.rowTotal(gi, ti), 0);
  }
  dayTotal(di: number): number {
    return this.groups().reduce((s, g, gi) =>
      s + g.tasks.reduce((t, _, ti) => t + this.num(this.hour(gi, ti, di)), 0), 0);
  }
  weekTotal = computed(() =>
    this.groups().reduce((s, _, gi) => s + this.groupTotal(gi), 0));

  // ---- actions ----
  editHour(gi: number, ti: number, di: number, value: string): void {
    this.hours.update(h => ({ ...h, [this.key(gi, ti, di)]: value }));
    this.api.saveHours(this.key(gi, ti, di), value).subscribe();
  }
  toggleGroup(gi: number): void {
    this.collapsed.update(c => ({ ...c, [gi]: !c[gi] }));
  }
  anyOpen(): boolean { return this.groups().some((_, gi) => this.isOpen(gi)); }
  collapseAll(): void {
    if (this.anyOpen()) {
      const c: Record<number, boolean> = {};
      this.groups().forEach((_, gi) => (c[gi] = true));
      this.collapsed.set(c);
    } else {
      this.collapsed.set({});
    }
  }
  smartFill(): void { this.toast.show('Smart fill applied'); }
  addTask(): void { this.toast.show('Task added'); }
  prevWeek(): void { this.toast.show('Loaded previous week'); }
  nextWeek(): void { this.toast.show('Loaded next week'); }
}
