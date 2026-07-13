import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

import { TagDto } from '../../api/models';

/**
 * The checkable tag chips. Two consumers: the Backlog editor and the Task List.
 *
 * Ports WPF's `Views/Controls/TagPicker.xaml`, which is itself "mirrors TeamFilter" -- a `Tags (N)`
 * disclosure over a type-to-filter box and a list of checkable chips (emoji icon + the tag's own colour +
 * text). Same shape as `TeamFilterComponent`, deliberately.
 *
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 * 🔴 THIS COMPONENT DOES NOT WRITE, AND THAT IS THE WHOLE DESIGN.
 *
 * `BacklogTags` HAS NO `row_version` OF ITS OWN -- THE VERSION RIDES THE PARENT RECORD:
 *
 *     // BacklogEndpoints.cs, on PUT /api/backlogs/{id}/tags
 *     // "BacklogTags carries no row_version of its own, so SetTagsCheckedAsync checks and bumps the
 *     //  PARENT backlog's version and throws ConcurrencyConflictException when it has moved on."
 *
 * So a tag write is a CHECKED write against the BACKLOG's `rowVersion`, and it can 409 on a backlog that
 * someone else renamed while the picker was open. A picker that owned the write would have to own that 409 --
 * and would be perfectly placed to SWALLOW it. It does not own the write, so it structurally cannot.
 *
 * The PARENT holds the record, holds its `expectedVersion`, and already has conflict machinery. It calls:
 *
 *     setBacklogTags(backlogId, ids, expectedVersion)   ->  Observable<SavedBody>
 *     setTaskTags(taskId, ids, expectedVersion)         ->  Observable<SavedBody>
 *
 * 🔴 AND IT MUST STORE THE `rowVersion` THAT COMES BACK. The tag write BUMPED THE PARENT'S VERSION. A screen
 * that saves tags and then saves the backlog itself, still holding the version it loaded, will 409 AGAINST
 * ITS OWN TAG WRITE -- on the happy path, every single time. This is the same rule `saveHours` follows: store
 * the version the WRITE returned, never re-read it.
 *
 * 🔴 AND THE WRITE IS REPLACE-ALL. `ids` is the COMPLETE new set; an id you omit is REMOVED. That is why
 * `selectionChange` emits the whole set and never a delta. (An empty array here is FINE and means "clear
 * every tag" -- it is a JSON body, not a query array. It has NOTHING to do with the `teamIds: []` trap in
 * `TeamFilterComponent`, which is a query-string problem.)
 * ═══════════════════════════════════════════════════════════════════════════════════════════════════════
 *
 * 🔴 THE PICKER SELECTS; IT DOES NOT MANAGE. `tagCreate` / `tagUpdate` / `tagDelete` are ADMIN-ONLY (403 for
 * everyone else), and this control renders on two screens any ordinary user can reach. Tag CRUD lives on the
 * Settings screen. Do not add a "+ New tag" button here.
 */
@Component({
  selector: 'app-tag-picker',
  standalone: true,
  templateUrl: './tag-picker.component.html',
  styleUrl: './tag-picker.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TagPickerComponent {
  /**
   * The tag CATALOGUE -- `getTagList()` (`GET /api/tags`, OPEN to any authenticated caller).
   *
   * An INPUT, not a read this component makes for itself: the Task List renders one picker PER ROW, and a
   * self-loading picker would fire one `/api/tags` per row. The parent reads it once and passes it down.
   */
  readonly tags = input.required<readonly TagDto[]>();

  /** The currently-checked tag ids -- `getBacklogTags(id)` / `getTaskTags(id)`, which return `number[]`, not
   *  `TagDto[]`. Owned by the PARENT: this component never mutates it, it only asks for a new one. */
  readonly selected = input.required<readonly number[]>();

  /** Set while the parent's save is in flight, so a double-click cannot queue a second replace-all. */
  readonly disabled = input(false);

  /** 🔴 The COMPLETE new tag set. Not a delta -- the write REPLACES. See the class doc. */
  readonly selectionChange = output<number[]>();

  /** The type-to-filter box. Filters on the tag's TEXT, exactly as WPF's `FilterBox_TextChanged` does. */
  protected readonly filterText = signal('');

  private readonly selectedSet = computed(() => new Set(this.selected()));

  /** `id` is `number | undefined` on every generated model -- a tag without one cannot be checked, stored or
   *  written, so it is dropped here rather than rendered as a chip that silently does nothing. */
  private readonly usableTags = computed(() => this.tags().filter(t => t.id !== undefined));

  readonly header = computed(() => `Tags (${this.selectedSet().size})`);

  readonly visibleTags = computed(() => {
    const query = this.filterText().trim().toLowerCase();
    if (query === '') return this.usableTags();
    return this.usableTags().filter(t => (t.text ?? '').toLowerCase().includes(query));
  });

  /** True when a filter is hiding every tag -- distinct from "there are no tags at all". */
  readonly noMatches = computed(() => this.usableTags().length > 0 && this.visibleTags().length === 0);

  isChecked(tagId: number): boolean {
    return this.selectedSet().has(tagId);
  }

  /**
   * Emits the FULL new set. It does NOT mutate `selected()` -- that is the parent's, and a component that
   * wrote to its own input would break the data flow the replace-all write depends on.
   */
  toggle(tagId: number): void {
    if (this.disabled()) return;

    const next = new Set(this.selectedSet());
    if (!next.delete(tagId)) next.add(tagId);

    this.selectionChange.emit([...next].sort((a, b) => a - b));
  }

  onFilter(event: Event): void {
    this.filterText.set((event.target as HTMLInputElement).value);
  }
}
