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
  // 🔴 DEFAULT LIGHT — do NOT follow the OS. It used to fall back to
  // `prefers-color-scheme: dark`, which produced a real UAT bug: ThemeService is
  // injected ONLY by the Settings screen, so it is constructed the FIRST TIME you
  // open Settings — and at that moment it read the OS preference and flipped the
  // whole app to dark. The report was "just clicking into Settings switches it".
  // main.ts (the pre-paint applier) only ever reads localStorage, never the OS, so
  // the two disagreed and Settings was where the disagreement surfaced. Dark is now
  // an EXPLICIT opt-in that persists; nothing switches the theme but the toggle.
  private readDark(): boolean {
    const v = this.read().dark;
    return typeof v === 'boolean' ? v : false;
  }
  private readAccent(): string { return this.read().accent || '#0E7C66'; }
}
