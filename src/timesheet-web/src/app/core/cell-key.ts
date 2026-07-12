/**
 * The key for one timesheet cell.
 *
 * A cell is identified by (task, date) -- never by its position on screen. The vendored design
 * keyed hours by `${groupIndex}-${taskIndex}-${dayIndex}`, so a sort, a filter or a drag re-pointed
 * every key at a different task and hours landed on the wrong row with nothing to notice.
 *
 * `workDate` is an ISO date (`yyyy-MM-dd`) -- the same shape the API speaks.
 */
export function cellKey(taskId: number, workDate: string): string {
  return `${taskId}-${workDate}`;
}
