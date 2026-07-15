import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.removeItem('worklog.theme');
    document.documentElement.classList.remove('dark');
  });

  afterEach(() => {
    localStorage.removeItem('worklog.theme');
    document.documentElement.classList.remove('dark');
  });

  it('🔴 defaults to LIGHT even when the OS prefers dark — the UAT bug (opening Settings flipped the app to dark because ThemeService is lazy-injected there and used to follow prefers-color-scheme)', () => {
    // OS says dark, and the user has no stored preference:
    spyOn(window, 'matchMedia').and.returnValue({ matches: true } as MediaQueryList);
    localStorage.removeItem('worklog.theme');

    const theme = TestBed.inject(ThemeService);

    // Nothing switches the theme but the toggle. Light, NOT following the OS.
    expect(theme.dark()).toBe(false);
  });

  it('honours an EXPLICIT stored dark choice', () => {
    localStorage.setItem('worklog.theme', JSON.stringify({ dark: true }));
    const theme = TestBed.inject(ThemeService);
    expect(theme.dark()).toBe(true);
  });

  it('honours an explicit stored LIGHT choice even when the OS prefers dark', () => {
    spyOn(window, 'matchMedia').and.returnValue({ matches: true } as MediaQueryList);
    localStorage.setItem('worklog.theme', JSON.stringify({ dark: false }));
    const theme = TestBed.inject(ThemeService);
    expect(theme.dark()).toBe(false);
  });

  it('toggleDark flips the signal', () => {
    const theme = TestBed.inject(ThemeService);
    expect(theme.dark()).toBe(false);
    theme.toggleDark();
    expect(theme.dark()).toBe(true);
  });
});
