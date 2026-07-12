import { cellKey } from './cell-key';

// The vendored design keyed hours by `${groupIndex}-${taskIndex}-${dayIndex}` -- ARRAY INDICES.
// One sort, filter or reorder and every key points at a different task: hours land on the wrong
// row, silently, with nothing to notice. That is the bug this key exists to make impossible.
describe('cellKey', () => {
  it('is derived from the task id and the date, never from a position', () => {
    expect(cellKey(42, '2026-07-13')).toBe('42-2026-07-13');
  });

  it('is stable when the same task moves to a different position in the list', () => {
    const before = cellKey(42, '2026-07-13');   // task 42 was 3rd on screen
    const after = cellKey(42, '2026-07-13');    // user filters; task 42 is now 1st
    expect(after).toBe(before);
  });

  it('distinguishes two tasks on the same day', () => {
    expect(cellKey(42, '2026-07-13')).not.toBe(cellKey(43, '2026-07-13'));
  });

  it('distinguishes the same task on two days', () => {
    expect(cellKey(42, '2026-07-13')).not.toBe(cellKey(42, '2026-07-14'));
  });
});
