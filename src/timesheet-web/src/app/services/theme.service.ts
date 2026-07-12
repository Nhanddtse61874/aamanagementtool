import { Injectable, signal, effect } from '@angular/core';

const STORAGE_KEY = 'worklog.theme';

/**
 * Dark-mode + accent theming.
 * Toggles a `dark` class on <html> so global CSS variables switch,
 * and sets `--accent` from a small curated palette. Persists to localStorage.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly dark = signal<boolean>(this.readDark());
  readonly accent = signal<string>(this.readAccent());

  readonly accentOptions = ['#0E7C66', '#0B6B58', '#1F6FEB', '#7A5AF0'];

  constructor() {
    effect(() => {
      const root = document.documentElement;
      root.classList.toggle('dark', this.dark());
      root.style.setProperty('--accent', this.accent());
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ dark: this.dark(), accent: this.accent() }),
      );
    });
  }

  toggleDark(): void { this.dark.update(v => !v); }
  setAccent(hex: string): void { this.accent.set(hex); }

  private read(): { dark?: boolean; accent?: string } {
    try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}'); }
    catch { return {}; }
  }
  private readDark(): boolean {
    const v = this.read().dark;
    return typeof v === 'boolean'
      ? v
      : window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false;
  }
  private readAccent(): string { return this.read().accent || '#0E7C66'; }
}
