import { test, expect } from '@playwright/test';

/**
 * Rapid DeviceCard disclosure — no structural animation; latest click wins.
 * Fail on blank frames / disappearing cards / stale expansion.
 */

test.describe('rapid-toggle stability (v1 freeze)', () => {
  test('100 rapid expand/collapse cycles — no blank / disappear / stale', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 420 });
    await page.goto('/?fixture=connected-usb');
    await page.waitForSelector('[data-testid="device-card"]');

    const result = await page.evaluate(async () => {
      const w = window as unknown as { __adbToggleExpand?: (id: string) => void };
      const card = document.querySelector<HTMLElement>('[data-device-id]');
      const id = card?.dataset.deviceId;
      if (!id || !w.__adbToggleExpand) {
        return {
          ok: false,
          reason: 'missing hook or card',
          blank: 0,
          missing: 0,
          stale: 0,
          cycles: 0,
        };
      }

      const settle = () =>
        new Promise<void>((resolve) => {
          requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
        });

      let blank = 0;
      let missing = 0;
      let stale = 0;
      const cycles = 100;

      for (let i = 0; i < cycles; i++) {
        w.__adbToggleExpand(id);
        await settle();

        const root = document.querySelector('[data-testid="flyout-shell"]');
        const cards = document.querySelectorAll('[data-testid="device-card"]');
        const expanded = document.querySelectorAll('.device-card-details-grid.is-expanded');
        const expectExpanded = i % 2 === 0; // first toggle expands

        if (!root || (root as HTMLElement).childElementCount === 0) blank++;
        if (cards.length !== 1) missing++;
        if (expectExpanded && expanded.length !== 1) stale++;
        if (!expectExpanded && expanded.length !== 0) stale++;
      }

      return { ok: true, blank, missing, stale, cycles };
    });

    expect(result.ok, result.reason ?? 'probe failed').toBe(true);
    expect(result.blank, 'blank frames').toBe(0);
    expect(result.missing, 'disappearing/duplicate cards').toBe(0);
    expect(result.stale, 'stale expansion state').toBe(0);
    expect(result.cycles).toBe(100);
  });
});
