import { Injectable, signal } from '@angular/core';

/** Tiny transient toast used across screens for action feedback. */
@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly message = signal<string | null>(null);
  private timer?: ReturnType<typeof setTimeout>;

  show(msg: string): void {
    this.message.set(msg);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.message.set(null), 2000);
  }
}
