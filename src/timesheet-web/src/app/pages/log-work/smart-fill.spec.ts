import { buildSmartFillRequest, distributeHours } from './smart-fill';

describe('distributeHours', () => {
  it('splits evenly when it divides cleanly', () => {
    expect(distributeHours(8, 4)).toEqual([2, 2, 2, 2]);
    expect(distributeHours(10, 5)).toEqual([2, 2, 2, 2, 2]);
  });

  // The API rejects more than ONE decimal place outright ("Hours may have at most 1 decimal place"), so a
  // raw 8/3 = 2.666… would be refused and NOTHING would be written.
  it('never produces more than one decimal place', () => {
    for (const [total, days] of [[8, 3], [7, 3], [5, 3], [8, 6], [3.5, 4]] as const) {
      for (const h of distributeHours(total, days)) {
        expect(Math.round(h * 10) / 10).withContext(`${total}h over ${days} days`).toBe(h);
      }
    }
  });

  // Rounding each day independently does NOT preserve the total: 8/3 rounds to 2.7, and 2.7 x 3 = 8.1 --
  // an hour the user never asked for, which can breach the 8h/day cap on a day that was already full.
  it('still sums EXACTLY to the requested total after rounding', () => {
    for (const [total, days] of [[8, 3], [7, 3], [5, 3], [8, 6], [1, 5], [3.5, 4]] as const) {
      const sum = distributeHours(total, days).reduce((a, b) => a + b, 0);
      expect(Math.round(sum * 10) / 10).withContext(`${total}h over ${days} days`).toBe(total);
    }
  });

  /**
   * 🔴 THIS TEST USED TO ASSERT [2.7, 2.7, 2.6] AND WAS WRONG, ALONG WITH THE CODE IT PINNED.
   *
   * The old client rounded each day UP and then took the drift OFF the last day. Core floors and ADDS the
   * remainder to the last day (SmartInputService.cs:37-39), which is what SI-01 actually specifies:
   * "remainder lands on the last working day". Same total, opposite direction, different per-day hours --
   * so the same request produced different timesheets depending on which app the user was in.
   *
   * Nothing could have caught it: both versions summed correctly and both respected the 1-decimal API
   * limit, so neither the suite nor the server's validation had anything to object to. Only comparing the
   * two implementations reveals it, and smart-fill.ts claimed they were "the same rule".
   */
  it('puts the remainder ON the last day, matching Core exactly', () => {
    expect(distributeHours(8, 3)).toEqual([2.6, 2.6, 2.8]);    // was [2.7, 2.7, 2.6] -- Core disagreed
    expect(distributeHours(10, 3)).toEqual([3.3, 3.3, 3.4]);   // SI-01's own worked example, verbatim
    expect(distributeHours(7, 3)).toEqual([2.3, 2.3, 2.4]);
    expect(distributeHours(5, 3)).toEqual([1.6, 1.6, 1.8]);
  });

  it('is empty for a nonsense request rather than sending zeros', () => {
    expect(distributeHours(0, 5)).toEqual([]);
    expect(distributeHours(8, 0)).toEqual([]);
    expect(distributeHours(-4, 5)).toEqual([]);
  });
});

describe('buildSmartFillRequest', () => {
  it('builds one task with a cell per chosen day', () => {
    const req = buildSmartFillRequest(7, ['2026-07-13', '2026-07-14'], 8);

    expect(req).toEqual([{
      taskId: 7,
      cells: [
        { date: '2026-07-13', hours: 4 },
        { date: '2026-07-14', hours: 4 },
      ],
    }]);
  });

  it('drops zero-hour cells rather than sending them (the API rejects hours <= 0)', () => {
    // 0.1h over 5 days rounds to 0 on every day -- there is nothing to write.
    const req = buildSmartFillRequest(7, ['a', 'b', 'c', 'd', 'e'], 0.1);
    const cells = req[0]?.cells ?? [];

    for (const c of cells) expect(c.hours).toBeGreaterThan(0);
  });

  it('is empty when no days are chosen -- so the caller can refuse before hitting the wire', () => {
    expect(buildSmartFillRequest(7, [], 8)).toEqual([]);
  });
});
