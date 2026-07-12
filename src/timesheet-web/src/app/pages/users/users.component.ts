import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorklogService } from '../../services/worklog.service';
import { ToastService } from '../../services/toast.service';
import { User } from '../../models/worklog.models';

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './users.component.html',
  styleUrl: './users.component.scss',
})
export class UsersComponent {
  private api = inject(WorklogService);
  private toast = inject(ToastService);

  readonly users = signal<User[]>([]);
  readonly search = signal('');

  constructor() { this.api.getUsers().subscribe(u => this.users.set(u)); }

  avatar(name: string) { return this.api.avatarColor(name); }

  readonly filtered = computed(() =>
    this.users().filter(u => u.name.toLowerCase().includes(this.search().toLowerCase())));

  toggle(name: string): void {
    this.users.update(list => list.map(u => u.name === name ? { ...u, active: !u.active } : u));
    const now = this.users().find(u => u.name === name);
    this.api.toggleUser(name).subscribe();
    this.toast.show((now?.active ? 'Activated ' : 'Deactivated ') + name);
  }
  addUser(): void { this.toast.show('User added'); }
}
