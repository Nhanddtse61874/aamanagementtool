import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { AppComponent } from './app/app.component';

// Apply persisted theme before first paint (avoids flash).
try {
  const t = JSON.parse(localStorage.getItem('worklog.theme') || '{}');
  if (t.dark) document.documentElement.classList.add('dark');
  if (t.accent) document.documentElement.style.setProperty('--accent', t.accent);
} catch {}

bootstrapApplication(AppComponent, appConfig).catch(err => console.error(err));
