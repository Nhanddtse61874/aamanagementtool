import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { TagDto } from '../../api/models';
import { TagPickerComponent } from './tag-picker.component';

/**
 * The tag picker (M9/P6c). Presentational: it SELECTS, and it never writes.
 *
 * The write belongs to the parent because `BacklogTags` has no `row_version` of its own -- the version RIDES
 * THE PARENT record, so a tag write is a checked write against the BACKLOG's version and can 409. A picker
 * that owned that write would be perfectly placed to swallow the 409. These tests pin the consequence of
 * that decision: what it EMITS is a complete replacement set, and it never touches its own input.
 */

const TAGS: TagDto[] = [
  { id: 1, text: 'Urgent', icon: '🔥', color: '#DC2626', rowVersion: 1 },
  { id: 2, text: 'Backend', icon: '⚙️', color: '#2563EB', rowVersion: 1 },
  { id: 3, text: 'Blocked', icon: '⛔', color: '#B45309', rowVersion: 1 },
];

describe('TagPickerComponent', () => {
  let fixture: ComponentFixture<TagPickerComponent>;
  let component: TagPickerComponent;
  let emitted: number[][];

  function setUp(selected: number[] = [], tags: TagDto[] = TAGS, disabled = false): void {
    TestBed.configureTestingModule({ imports: [TagPickerComponent] });

    fixture = TestBed.createComponent(TagPickerComponent);
    component = fixture.componentInstance;

    fixture.componentRef.setInput('tags', tags);
    fixture.componentRef.setInput('selected', selected);
    fixture.componentRef.setInput('disabled', disabled);

    emitted = [];
    component.selectionChange.subscribe(ids => emitted.push(ids));

    fixture.detectChanges();
  }

  function chipTexts(): string[] {
    return fixture.debugElement
      .queryAll(By.css('.tp__chip .tp__text'))
      .map(d => (d.nativeElement as HTMLElement).textContent?.trim() ?? '');
  }

  function checkboxes(): HTMLInputElement[] {
    return fixture.debugElement
      .queryAll(By.css('.tp__row input[type=checkbox]'))
      .map(d => d.nativeElement as HTMLInputElement);
  }

  function lastEmit(): number[] | undefined {
    return emitted[emitted.length - 1];
  }

  function typeFilter(text: string): void {
    const box = fixture.debugElement.query(By.css('.tp__filter')).nativeElement as HTMLInputElement;
    box.value = text;
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  // ---- rendering ---------------------------------------------------------------------------------------

  it('renders one checkable chip per tag', () => {
    setUp();

    expect(chipTexts()).toEqual(['Urgent', 'Backend', 'Blocked']);
    expect(checkboxes().length).toBe(3);
  });

  /** Icon + colour + text -- the three things a chip IS. `icon` is an EMOJI GLYPH, not an icon-set name:
   *  WPF renders it as `<TextBlock Text="{Binding Tag.Icon}" FontFamily="Segoe UI Emoji"/>`. */
  it('renders the icon glyph and the tag\'s OWN colour on the chip', () => {
    setUp();

    const chips = fixture.debugElement.queryAll(By.css('.tp__chip'));
    const first = chips[0].nativeElement as HTMLElement;

    expect(first.textContent).toContain('🔥');
    expect(first.style.background).toBe('rgb(220, 38, 38)');   // #DC2626
  });

  it('reflects the checked ids it was given', () => {
    setUp([2]);

    expect(checkboxes().map(b => b.checked)).toEqual([false, true, false]);
    expect(component.header()).toBe('Tags (1)');
  });

  // ---- 🔴 replace-all: it emits the WHOLE set, never a delta -------------------------------------------

  /**
   * 🔴 `setBacklogTags` / `setTaskTags` REPLACE the tag set -- an id you omit is REMOVED. So the emission must
   * be the complete new set. Emit a delta (just the toggled id) and every save would wipe every other tag.
   */
  it('🔴 emits the COMPLETE new set when a tag is checked -- the write is replace-all', () => {
    setUp([2]);

    component.toggle(1);

    expect(lastEmit()).toEqual([1, 2]);      // NOT [1]
  });

  it('🔴 emits the COMPLETE remaining set when a tag is unchecked', () => {
    setUp([1, 2, 3]);

    component.toggle(2);

    expect(lastEmit()).toEqual([1, 3]);      // NOT [2]
  });

  /** Unchecking the last tag is a legitimate "clear every tag" -- and unlike `teamIds: []` on a QUERY string,
   *  an empty `tagIds` in a JSON BODY says exactly what it looks like. The endpoint documents it as such. */
  it('emits [] when the last tag is unchecked -- clearing every tag is legal here', () => {
    setUp([1]);

    component.toggle(1);

    expect(lastEmit()).toEqual([]);
  });

  it('a real checkbox CLICK toggles it -- the (change) binding is really attached', () => {
    setUp([]);

    checkboxes()[2].click();     // Blocked
    fixture.detectChanges();

    expect(lastEmit()).toEqual([3]);
  });

  /**
   * 🔴 It must NOT write to its own input. `selected` is the PARENT's state; the picker asks for a new value
   * and the parent grants it. A component that mutated the array in place would leave the parent's
   * `expectedVersion` and its tag set out of step, and the replace-all write would go out against a set the
   * parent never agreed to.
   */
  it('🔴 does NOT mutate the `selected` input array', () => {
    const selected = [1];
    setUp(selected);

    component.toggle(2);

    expect(selected).toEqual([1]);           // untouched
    expect(lastEmit()).toEqual([1, 2]);      // ...and the new set was OFFERED, not applied
  });

  // ---- type-to-filter ----------------------------------------------------------------------------------

  it('narrows the list by the typed text, case-insensitively', () => {
    setUp();

    typeFilter('back');

    expect(chipTexts()).toEqual(['Backend']);
  });

  it('says so when the filter matches nothing', () => {
    setUp();

    typeFilter('nothing matches this');

    expect(chipTexts()).toEqual([]);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No tags match that');
  });

  it('restores the full list when the filter is cleared', () => {
    setUp();

    typeFilter('urg');
    expect(chipTexts()).toEqual(['Urgent']);

    typeFilter('');
    expect(chipTexts()).toEqual(['Urgent', 'Backend', 'Blocked']);
  });

  /** 🔴 A filter is a VIEW, not a selection. Hiding a checked tag must not uncheck it -- otherwise typing in
   *  the filter box would silently delete tags on the next replace-all save. */
  it('🔴 a filter that HIDES a checked tag does not deselect it', () => {
    setUp([1, 2]);

    typeFilter('blocked');            // hides both checked tags
    expect(chipTexts()).toEqual(['Blocked']);

    component.toggle(3);

    // Urgent and Backend are still checked, even though neither is on screen.
    expect(lastEmit()).toEqual([1, 2, 3]);
  });

  it('counts every checked tag in the header, not just the visible ones', () => {
    setUp([1, 2]);

    typeFilter('blocked');

    expect(component.header()).toBe('Tags (2)');
  });

  // ---- disabled ----------------------------------------------------------------------------------------

  /** Set while the parent's replace-all save is in flight. A second toggle would queue a second write against
   *  a version the first has already bumped -- a self-inflicted 409. */
  it('refuses to toggle while disabled', () => {
    setUp([1], TAGS, /* disabled */ true);

    component.toggle(2);

    expect(emitted).toEqual([]);
    expect(checkboxes().every(b => b.disabled)).toBeTrue();
  });

  // ---- empty catalogue ---------------------------------------------------------------------------------

  it('says the catalogue is empty rather than rendering nothing at all', () => {
    setUp([], []);

    expect(chipTexts()).toEqual([]);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No tags have been created yet');
  });

  /** `id` is `number | undefined` on every generated model. A tag with no id cannot be checked, stored or
   *  written -- rendering it as a chip that silently does nothing would be worse than dropping it. */
  it('drops a tag that arrived with no id, rather than rendering a chip that cannot be checked', () => {
    setUp([], [{ text: 'Ghost', icon: '👻', color: '#000' }, ...TAGS]);

    expect(chipTexts()).toEqual(['Urgent', 'Backend', 'Blocked']);
  });
});
