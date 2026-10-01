import { test, expect } from '@playwright/test';

type GapMeasure = {
  shellWidth: number;
  scrollClientWidth: number;
  scrollOffsetWidth: number;
  scrollWidth: number;
  scrollbarGutter: string;
  paddingInline: string;
  leftGap: number;
  rightGap: number;
  difference: number;
  hasOverflow: boolean;
};

async function measureGaps(page: import('@playwright/test').Page): Promise<GapMeasure> {
  return page.evaluate(() => {
    const shell = document.querySelector('[data-testid="flyout-shell"]') as HTMLElement;
    const scroll = document.querySelector('[data-testid="flyout-scroll"]') as HTMLElement;
    const card =
      (document.querySelector('[data-testid="device-card"]') as HTMLElement | null) ??
      (document.querySelector('[data-testid="pairing-card"]') as HTMLElement | null) ??
      (document.querySelector('[data-testid="empty-state"]') as HTMLElement | null) ??
      (scroll.firstElementChild as HTMLElement | null);

    if (!shell || !scroll || !card) {
      throw new Error('missing shell/scroll/card');
    }

    const shellRect = shell.getBoundingClientRect();
    const cardRect = card.getBoundingClientRect();
    const cs = getComputedStyle(scroll);
    const leftGap = cardRect.left - shellRect.left;
    const rightGap = shellRect.right - cardRect.right;
    return {
      shellWidth: shellRect.width,
      scrollClientWidth: scroll.clientWidth,
      scrollOffsetWidth: scroll.offsetWidth,
      scrollWidth: scroll.scrollWidth,
      scrollbarGutter: cs.scrollbarGutter,
      paddingInline: cs.paddingInline || `${cs.paddingLeft} ${cs.paddingRight}`,
      leftGap,
      rightGap,
      difference: Math.abs(leftGap - rightGap),
      hasOverflow: scroll.scrollHeight > scroll.clientHeight + 1,
    };
  });
}

const fixtures: { name: string; expand?: string }[] = [
  { name: 'empty' },
  { name: 'connected-usb' },
  { name: 'expanded', expand: 'd1' },
  { name: 'multi-device' },
  { name: 'pairing-offer' },
  { name: 'pairing-code' },
];

for (const fixture of fixtures) {
  test(`centering gaps ≤1px: ${fixture.name}`, async ({ page }) => {
    await page.goto(`/?fixture=${fixture.name}`);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);

    if (fixture.expand) {
      await page.evaluate((id) => {
        const w = window as unknown as { __adbToggleExpand?: (deviceId: string) => void };
        w.__adbToggleExpand?.(id);
      }, fixture.expand);
      await page.waitForTimeout(50);
    }

    const m = await measureGaps(page);
    expect(m.scrollbarGutter, JSON.stringify(m)).toMatch(/stable/);
    expect(m.scrollbarGutter, JSON.stringify(m)).toMatch(/both-edges/);
    expect(m.difference, JSON.stringify(m)).toBeLessThanOrEqual(1);
  });
}

test('centering: forced scroll overflow stays symmetric', async ({ page }) => {
  await page.goto('/?fixture=multi-device');
  await page.waitForLoadState('networkidle');
  await page.setViewportSize({ width: 376, height: 280 });
  await page.evaluate(() => document.fonts.ready);

  const m = await measureGaps(page);
  expect(m.difference, JSON.stringify(m)).toBeLessThanOrEqual(1);
});

for (const scale of [1, 1.25, 1.5, 2] as const) {
  test(`centering at deviceScaleFactor ${scale}`, async ({ browser }) => {
    const context = await browser.newContext({
      viewport: { width: 376, height: 600 },
      deviceScaleFactor: scale,
      colorScheme: 'dark',
    });
    const page = await context.newPage();
    await page.goto('http://localhost:5173/?fixture=connected-usb');
    await page.waitForLoadState('networkidle');
    const m = await measureGaps(page);
    expect(m.difference, JSON.stringify(m)).toBeLessThanOrEqual(1);
    await context.close();
  });
}
