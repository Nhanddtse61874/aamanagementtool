import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CellConflict, ConflictDialogComponent } from './conflict-dialog.component';

const CONFLICT: CellConflict = {
  // The real shape, verified on the wire. `id` is 0 for a timesheet cell (its key is the natural triple
  // user/task/date, so there IS no id), which is exactly why `detail` has to carry the identity.
  detail: 'user 1, task 7, 2026-07-13',
  message: 'TimeLogs (user 1, task 7, 2026-07-13) was changed by someone else (you last saw row_version 11).',
  taskName: 'Design schema',
  dayLabel: '13/07',
  yours: 6,
  theirs: 3,
};

describe('ConflictDialogComponent', () => {
  let fixture: ComponentFixture<ConflictDialogComponent>;
  let text: string;

  function setUp(conflict: CellConflict = CONFLICT): void {
    TestBed.configureTestingModule({ imports: [ConflictDialogComponent] });
    fixture = TestBed.createComponent(ConflictDialogComponent);
    fixture.componentRef.setInput('conflict', conflict);
    fixture.detectChanges();
    text = (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  it('shows DETAIL -- the only field that says which cell the conflict is about', () => {
    setUp();
    expect(text).toContain('user 1, task 7, 2026-07-13');
  });

  it('shows the server\'s own message', () => {
    setUp();
    expect(text).toContain('was changed by someone else');
  });

  it('shows BOTH values, so the user can actually decide', () => {
    setUp();

    expect(text).toContain('6h');    // what they typed
    expect(text).toContain('3h');    // what is on the server now
  });

  it('names the cell in human terms, not just the server\'s detail string', () => {
    setUp();

    expect(text).toContain('Design schema');
    expect(text).toContain('13/07');
  });

  it('renders an absent value as "(empty)" -- never as "0", which the API rejects outright', () => {
    setUp({ ...CONFLICT, yours: null, theirs: null });

    expect(text).toContain('(empty)');
    expect(text).not.toContain('0h');
  });

  it('emits keepTheirs -- the safe choice, which discards nothing of anyone else\'s', () => {
    setUp();
    const spy = jasmine.createSpy('keepTheirs');
    fixture.componentInstance.keepTheirs.subscribe(spy);

    buttonWith('Keep their value').click();

    expect(spy).toHaveBeenCalled();
  });

  it('emits overwrite only when the destructive button is chosen deliberately', () => {
    setUp();
    const spy = jasmine.createSpy('overwrite');
    fixture.componentInstance.overwrite.subscribe(spy);

    buttonWith('Overwrite with mine').click();

    expect(spy).toHaveBeenCalled();
  });

  it('is a modal alertdialog, so a screen reader announces it instead of losing it', () => {
    setUp();
    const dialog = (fixture.nativeElement as HTMLElement).querySelector('[role="alertdialog"]');

    expect(dialog).toBeTruthy();
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
  });

  function buttonWith(label: string): HTMLButtonElement {
    const buttons = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button'));
    const found = buttons.find(b => (b.textContent ?? '').trim().includes(label));
    if (!found) throw new Error(`No button labelled "${label}"`);
    return found as HTMLButtonElement;
  }
});
