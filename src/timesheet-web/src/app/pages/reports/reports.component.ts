import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { Metric, MonthlyRow, TreeNode, WeeklyRow } from '../../models/worklog.models';

interface FlatNode {
  id: string; label: string; hours: string; depth: number;
  hasChildren: boolean; expanded: boolean; isLeaf: boolean;
}

@Component({
  selector: 'app-reports',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './reports.component.html',
  styleUrl: './reports.component.scss',
})
export class ReportsComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);

  readonly metrics = signal<Metric[]>([]);
  readonly missing = signal<string[]>([]);
  readonly weekly = signal<WeeklyRow[]>([]);
  readonly monthly = signal<MonthlyRow[]>([]);
  readonly tree = signal<TreeNode | null>(null);
  readonly expanded = signal<Record<string, boolean>>({});

  readonly reportFilters = [
    { label: 'Report for', options: ['Whole team (all)', 'Nhan', 'An'] },
    { label: 'Project', options: ['All', 'ARCS', 'ARMS', 'Other'] },
    { label: 'Week (Mon)', options: ['7/6/2026', '6/29/2026'] },
    { label: 'Month', options: ['7/1/2026', '6/1/2026'] },
  ];

  constructor() {
    this.api.getMetrics().subscribe(m => this.metrics.set(m));
    this.api.getMissing().subscribe(m => this.missing.set(m));
    this.api.getWeekly().subscribe(w => this.weekly.set(w));
    this.api.getMonthly().subscribe(m => this.monthly.set(m));
    this.api.getDrilldown().subscribe(t => {
      this.tree.set(t);
      if (t) this.expanded.set({ [t.id]: true });
    });
  }

  metricColor(tone: string): string {
    return tone === 'accent' ? 'var(--accent-700)' : tone === 'warn' ? '#B5791F' : 'var(--ink)';
  }

  readonly flat = computed<FlatNode[]>(() => {
    const root = this.tree();
    if (!root) return [];
    const exp = this.expanded();
    const rows: FlatNode[] = [];
    const walk = (n: TreeNode, depth: number) => {
      const hasChildren = !!(n.children && n.children.length);
      rows.push({ id: n.id, label: n.label, hours: n.hours, depth, hasChildren, expanded: !!exp[n.id], isLeaf: !hasChildren });
      if (hasChildren && exp[n.id]) n.children!.forEach(c => walk(c, depth + 1));
    };
    walk(root, 0);
    return rows;
  });

  private allIds(): string[] {
    const ids: string[] = [];
    const walk = (n: TreeNode) => { if (n.children) { ids.push(n.id); n.children.forEach(walk); } };
    const r = this.tree(); if (r) walk(r);
    return ids;
  }
  anyExpanded(): boolean { return this.allIds().some(id => this.expanded()[id]); }
  toggleNode(id: string): void { this.expanded.update(e => ({ ...e, [id]: !e[id] })); }
  toggleAll(): void {
    if (this.anyExpanded()) this.expanded.set({});
    else { const e: Record<string, boolean> = {}; this.allIds().forEach(id => (e[id] = true)); this.expanded.set(e); }
  }

  labelColor(n: FlatNode): string {
    if (n.isLeaf) return 'var(--muted)';
    return ['var(--ink)', 'var(--accent-700)', '#5B8DEF', 'var(--muted)'][Math.min(n.depth, 3)];
  }
  notify(msg: string): void { this.toast.show(msg); }
}
