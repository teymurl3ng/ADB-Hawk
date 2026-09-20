import { test, expect, type Page, type Locator } from '@playwright/test';

/**
 * Pair button hit-test + hover/pressed feedback — ONE browser session, 50 cycles.
 * Covers PairingOffer Pair and AwaitingCode submit Pair.
 */

async function assertHitIsPair(page: Page, btn: Locator, label: string) {
  const box = await btn.boundingBox();
  expect(box, label).toBeTruthy();
  const cx = box!.x + box!.width / 2;
  const cy = box!.y + box!.height / 2;
  const hit = await page.evaluate(
    ({ x, y }) => {
      const el = document.elementFromPoint(x, y) as HTMLElement | null;
      if (!el) return { ok: false, tag: null, testId: null };
      const host = el.closest('[data-testid="pairing-pair-button"]') as HTMLElement | null;
      return {
        ok: host != null,
        tag: el.tagName,
        testId: host?.getAttribute('data-testid') ?? null,
      };
    },
    { x: cx, y: cy },
  );
  expect(hit.ok, `${label}: elementFromPoint must resolve to Pair button (got ${hit.tag})`).toBe(
    true,
  );
  return { cx, cy, box: box! };
}

async function runFiftyFeedbackCycles(page: Page, btn: Locator, suite: string) {
  // Establish a settled rest baseline away from the control (CSS transition = 83ms).
  await page.mouse.move(8, 8);
  await page.waitForTimeout(120);
  const restBg = await btn.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(await btn.getAttribute('data-pair-feedback')).toBe('rest');

  for (let i = 0; i < 50; i++) {
    await page.mouse.move(8, 8);
    await page.waitForTimeout(100);
    const atRest = await btn.evaluate((el) => ({
      bg: getComputedStyle(el).backgroundColor,
      feedback: el.getAttribute('data-pair-feedback'),
    }));
    expect(atRest.feedback, `${suite} cycle ${i}: rest feedback`).toBe('rest');
    expect(atRest.bg, `${suite} cycle ${i}: rest background`).toBe(restBg);

    const { cx, cy, box } = await assertHitIsPair(page, btn, `${suite} cycle ${i} hit`);

    await page.mouse.move(cx, cy);
    await page.waitForTimeout(40);
    const hovered = await btn.evaluate((el) => ({
      bg: getComputedStyle(el).backgroundColor,
      feedback: el.getAttribute('data-pair-feedback'),
      w: el.getBoundingClientRect().width,
      h: el.getBoundingClientRect().height,
    }));
    expect(hovered.feedback, `${suite} cycle ${i}: hover feedback MUST appear`).toBe('hover');
    expect(hovered.bg, `${suite} cycle ${i}: hover visual MUST change`).not.toBe(restBg);
    expect(hovered.w, `${suite} cycle ${i}: hover width stable`).toBe(box.width);
    expect(hovered.h, `${suite} cycle ${i}: hover height stable`).toBe(box.height);

    await page.mouse.down();
    await page.waitForTimeout(24);
    const pressed = await btn.evaluate((el) => ({
      bg: getComputedStyle(el).backgroundColor,
      feedback: el.getAttribute('data-pair-feedback'),
      w: el.getBoundingClientRect().width,
      h: el.getBoundingClientRect().height,
    }));
    expect(pressed.feedback, `${suite} cycle ${i}: pressed feedback MUST appear`).toBe('pressed');
    expect(pressed.bg, `${suite} cycle ${i}: pressed visual MUST appear`).not.toBe(restBg);
    expect(pressed.w).toBe(box.width);
    expect(pressed.h).toBe(box.height);

    await page.mouse.up();
    await page.waitForTimeout(24);
    const afterUp = await btn.evaluate((el) => el.getAttribute('data-pair-feedback'));
    expect(['hover', 'rest']).toContain(afterUp);
  }
}

test.describe('Pair button hover + hit-test', () => {
  test('50 cycles — PairingOffer Pair', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 600 });
    await page.goto('/?fixture=pairing-offer');
    const btn = page.getByTestId('pairing-pair-button');
    await expect(btn).toBeVisible();
    await expect(btn).toBeEnabled();

    await runFiftyFeedbackCycles(page, btn, 'offer');

    await btn.focus();
    await expect(btn).toBeFocused();

    await btn.click();
    const actions = await page.evaluate(() => {
      const w = window as unknown as { __adbActions?: { type: string; offerId?: string }[] };
      return w.__adbActions ?? [];
    });
    expect(actions.filter((a) => a.type === 'pairStart').length).toBeGreaterThanOrEqual(1);
  });

  test('50 cycles — AwaitingCode submit Pair', async ({ page }) => {
    await page.setViewportSize({ width: 376, height: 600 });
    await page.goto('/?fixture=pairing-code');
    const input = page.getByTestId('pairing-code-input');
    const btn = page.getByTestId('pairing-pair-button');
    await expect(btn).toBeVisible();
    await expect(btn).toBeDisabled();

    // Disabled: hover must NOT engage pair-feedback hover/pressed.
    await page.mouse.move(8, 8);
    await page.waitForTimeout(40);
    const box = await btn.boundingBox();
    expect(box).toBeTruthy();
    await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
    await page.waitForTimeout(30);
    expect(await btn.getAttribute('data-pair-feedback')).toBe('rest');
    await page.mouse.move(8, 8);

    await input.fill('248173');
    await expect(btn).toBeEnabled();
    await page.waitForTimeout(40);

    await runFiftyFeedbackCycles(page, btn, 'awaiting');
  });

  test('Pair remains hit-testable during synthetic DeviceCard FLIP overlap', async ({ page }) => {
    await page.goto('/?fixture=pairing-offer');
    const btn = page.getByTestId('pairing-pair-button');
    await expect(btn).toBeVisible();

    await page.evaluate(() => {
      const main = document.querySelector('main');
      const content = document.querySelector('[data-testid="pairing-card"]')?.parentElement;
      if (!main || !content) throw new Error('missing layout');
      main.dataset.flipActive = 'true';
      const card = document.createElement('div');
      card.dataset.deviceId = 'usb:FAKE';
      card.style.position = 'absolute';
      card.style.left = '0';
      card.style.right = '0';
      card.style.top = '40px';
      card.style.height = '120px';
      card.style.background = 'rgba(255,0,0,0.15)';
      card.style.transform = 'translateY(-48px)';
      card.style.zIndex = '1';
      content.appendChild(card);
    });

    await assertHitIsPair(page, btn, 'during FLIP overlap');
    const { cx, cy } = await assertHitIsPair(page, btn, 'during FLIP overlap 2');
    await page.mouse.move(cx, cy);
    await page.waitForTimeout(30);
    expect(await btn.getAttribute('data-pair-feedback')).toBe('hover');
  });

  test('MUTATION: transparent overlay intercepting Pair fails hit-test', async ({ page }) => {
    await page.goto('/?fixture=pairing-offer');
    const btn = page.getByTestId('pairing-pair-button');
    await expect(btn).toBeVisible();
    const box = await btn.boundingBox();
    expect(box).toBeTruthy();

    await page.evaluate(() => {
      const overlay = document.createElement('div');
      overlay.id = 'mutation-pair-intercept';
      overlay.style.position = 'fixed';
      overlay.style.inset = '0';
      overlay.style.zIndex = '9999';
      overlay.style.background = 'transparent';
      overlay.style.pointerEvents = 'auto';
      document.body.appendChild(overlay);
    });

    const cx = box!.x + box!.width / 2;
    const cy = box!.y + box!.height / 2;
    const hit = await page.evaluate(
      ({ x, y }) => {
        const el = document.elementFromPoint(x, y) as HTMLElement | null;
        return el?.closest('[data-testid="pairing-pair-button"]') != null;
      },
      { x: cx, y: cy },
    );
    expect(hit, 'mutation M-overlay: intercept MUST fail Pair hit-test').toBe(false);

    await page.mouse.move(cx, cy);
    await page.waitForTimeout(30);
    expect(await btn.getAttribute('data-pair-feedback')).toBe('rest');
  });
});
