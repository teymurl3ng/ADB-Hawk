import { test } from '@playwright/test';

const fixtures = [
  'connected-usb',
  'expanded',
  'multi-device',
  'empty',
  'pairing-offer',
  'pairing-code',
  'pairing-success',
  'pairing-failure',
  'discovery-health',
];

for (const fixture of fixtures) {
  test(`visual capture: ${fixture}`, async ({ page }, testInfo) => {
    const theme = testInfo.project.use.colorScheme || 'dark';
    const isMultiDevice = fixture === 'multi-device';

    await page.goto(`/?fixture=${fixture}`);
    await page.waitForLoadState('networkidle');
    await page.evaluate(() => document.fonts.ready);

    // Disable CSS animations and set explicit background for deterministic screenshots
    await page.evaluate(
      ({ theme }) => {
        const style = document.createElement('style');
        style.textContent = `* { animation: none !important; transition: none !important; }`;
        document.head.appendChild(style);

        // In browser context there is no native Acrylic backdrop.
        // Set an explicit background so dark-mode screenshots show the correct base color.
        if (theme === 'dark') {
          document.documentElement.style.backgroundColor = '#202329';
        } else {
          document.documentElement.style.backgroundColor = '#ffffff';
        }
      },
      { theme },
    );

    // Path from ui/ → up one level to repo root → artifacts/visual/phase-2
    const path = `../artifacts/visual/phase-2/browser-${theme}-${fixture}.png`;

    if (isMultiDevice) {
      await page.screenshot({ path, fullPage: true });
    } else {
      await page.screenshot({ path, clip: { x: 0, y: 0, width: 376, height: 600 } });
    }
  });
}
