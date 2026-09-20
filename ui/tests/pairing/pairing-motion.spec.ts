import { test, expect } from '@playwright/test';

/**
 * Pairing structural motion — fixture-driven phase morphs.
 * Asserts stable shell, digit geometry, dwell hook.
 */

test.describe('pairing structural motion', () => {
  test('pairing fixtures expose stable shell regions', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 600 });

    for (const fixture of [
      'pairing-offer',
      'pairing-code',
      'pairing-progress',
      'pairing-success',
      'pairing-failure',
    ]) {
      await page.goto(`/?fixture=${fixture}`);
      await page.waitForSelector('[data-testid="pairing-card"]');
      const meta = await page.evaluate(() => {
        const el = document.querySelector('[data-testid="pairing-card"]');
        return {
          shell: el?.getAttribute('data-pairing-shell'),
          hasRegions:
            !!el?.querySelector('[data-pairing-region="header"]') &&
            !!el?.querySelector('[data-pairing-region="content"]') &&
            !!el?.querySelector('[data-pairing-region="actions"]'),
        };
      });
      expect(meta.shell, fixture).toBe('stable');
      expect(meta.hasRegions, fixture).toBe(true);
    }
  });

  test('digit entry does not change PairingCard outer height (20 cycles)', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 600 });
    await page.goto('/?fixture=pairing-code');
    await page.waitForSelector('[data-testid="pairing-code-input"]');
    const h0 = await page
      .locator('[data-testid="pairing-card"]')
      .evaluate((el) => el.getBoundingClientRect().height);
    const input = page.getByTestId('pairing-code-input');
    for (let i = 0; i < 20; i++) {
      await input.fill(String((i % 9) + 1).repeat(6));
      await input.fill('');
      await input.fill('12345');
      await input.fill('123456');
    }
    const h1 = await page
      .locator('[data-testid="pairing-card"]')
      .evaluate((el) => el.getBoundingClientRect().height);
    expect(h1).toBe(h0);
  });

  test('success dwell hook exposed in test mode', async ({ page }) => {
    await page.goto('/?fixture=pairing-success');
    await page.waitForSelector('[data-testid="pairing-card"]');
    const dwell = await page.evaluate(
      () => (window as unknown as { __adbPairingDwellMs?: number }).__adbPairingDwellMs,
    );
    expect(dwell).toBe(750);
  });
});
