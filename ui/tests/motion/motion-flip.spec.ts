import { test, expect } from '@playwright/test';

/**
 * Browser disclosure probe — v1 has no FLIP; assert instant layout + no blank.
 */

test.describe('disclosure stability (browser)', () => {
  test('expand under frozen viewport: card remains mounted', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 420 });
    await page.goto('/?fixture=multi-device');
    await page.waitForSelector('[data-testid="device-card"]');

    const cards = page.locator('[data-testid="device-card"]');
    const count = await cards.count();
    test.skip(count < 2, 'need >=2 device cards');

    await page.evaluate(() => {
      const w = window as unknown as { __adbToggleExpand?: (id: string) => void };
      const ids = [...document.querySelectorAll<HTMLElement>('[data-device-id]')].map(
        (n) => n.dataset.deviceId!,
      );
      const targetId = ids[ids.length - 1]!;
      w.__adbToggleExpand?.(targetId);
    });

    await expect(page.locator('[data-testid="device-card"]')).toHaveCount(count);
    await expect(page.locator('.device-card-details-grid.is-expanded')).toHaveCount(1);
    await expect(page.locator('[data-testid="flyout-shell"]')).not.toBeEmpty();
  });
});
