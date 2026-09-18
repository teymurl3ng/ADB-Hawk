const { chromium } = require('../ui/node_modules/playwright');

const command = process.argv[2];
const arg1 = process.argv[3];

(async () => {
  let browser;
  try {
    const cdpPort = process.env.CDP_PORT || '9333';
    browser = await chromium.connectOverCDP(`http://127.0.0.1:${cdpPort}`);
    const defaultContext = browser.contexts()[0];
    let page = defaultContext.pages().find((p) => p.url().includes('app.adbview.local'));
    if (!page) {
      page = defaultContext.pages()[0];
      try {
        await page.waitForURL((url) => url.href.includes('app.adbview.local'), { timeout: 10000 });
      } catch {}
    }

    if (!page) {
      console.error(JSON.stringify({ error: 'Page not found' }));
      process.exit(1);
    }

    if (command === 'wait-ready') {
      await page.waitForFunction(() => window.__adbSnapshot !== undefined, { timeout: 10000 });
      const snapshot = await page.evaluate(() => window.__adbSnapshot);
      console.log(JSON.stringify({ success: true, snapshot }));
    } else if (command === 'send-ready') {
      await page.evaluate(() => {
        window.chrome.webview.postMessage({ type: 'ready', protocol: 1 });
      });
      await page.waitForTimeout(200);
      const data = await page.evaluate(() => ({
        snapshot: window.__adbSnapshot,
        count: window.__adbSnapshotCount,
      }));
      console.log(JSON.stringify({ success: true, data }));
    } else if (command === 'send-invalid-ready') {
      const countBefore = await page.evaluate(() => window.__adbSnapshotCount || 0);
      await page.evaluate(() => {
        window.chrome.webview.postMessage({ type: 'ready', protocol: 999 });
      });
      await page.waitForTimeout(200);
      const countAfter = await page.evaluate(() => window.__adbSnapshotCount || 0);
      console.log(JSON.stringify({ success: true, rejected: countBefore === countAfter }));
    } else if (command === 'click-pin') {
      const revBefore = await page.evaluate(() => window.__adbSnapshot?.revision || 0);
      await page.click('[data-testid="pin-button"]');
      await page.waitForFunction(
        (prevRev) => (window.__adbSnapshot?.revision || 0) > prevRev,
        revBefore,
        { timeout: 5000 },
      );
      const data = await page.evaluate(() => ({
        snapshot: window.__adbSnapshot,
        actionCount: window.__adbActionCount,
        buttonTitle: document.querySelector('[data-testid="pin-button"]')?.getAttribute('title'),
      }));
      console.log(JSON.stringify({ success: true, data }));
    } else if (command === 'test-ordering') {
      const results = await page.evaluate(() => {
        const current = window.__adbSnapshot;
        if (!current) return { error: 'No current snapshot' };

        const rev0 = current.revision;
        const count0 = window.__adbSnapshotCount;

        const target = window.chrome?.webview || window;

        // 1. Stale revision (rev < latest)
        const staleEnvelope = {
          type: 'snapshot',
          protocol: 1,
          snapshot: { ...current, revision: Math.max(1, rev0 - 1), shell: { ...current.shell, pinned: !current.shell.pinned } },
        };
        target.dispatchEvent(new MessageEvent('message', { data: staleEnvelope }));
        const staleRejected = window.__adbSnapshot.revision === rev0 && window.__adbSnapshotCount === count0;

        // 2. Duplicate revision (rev == latest)
        const duplicateEnvelope = {
          type: 'snapshot',
          protocol: 1,
          snapshot: { ...current, revision: rev0, shell: { ...current.shell, pinned: !current.shell.pinned } },
        };
        target.dispatchEvent(new MessageEvent('message', { data: duplicateEnvelope }));
        const duplicateRejected = window.__adbSnapshot.revision === rev0 && window.__adbSnapshotCount === count0;

        // 3. Malformed payload
        target.dispatchEvent(new MessageEvent('message', { data: '{ malformed json' }));
        target.dispatchEvent(new MessageEvent('message', { data: { type: 'wrong-type' } }));
        const malformedRejected = window.__adbSnapshot.revision === rev0 && window.__adbSnapshotCount === count0;

        // 4. Newer revision (rev > latest)
        const newerRev = rev0 + 5;
        const newerEnvelope = {
          type: 'snapshot',
          protocol: 1,
          snapshot: { ...current, revision: newerRev },
        };
        target.dispatchEvent(new MessageEvent('message', { data: newerEnvelope }));
        const newerAccepted = window.__adbSnapshot.revision === newerRev && window.__adbSnapshotCount === count0 + 1;

        return {
          staleRejected,
          duplicateRejected,
          malformedRejected,
          newerAccepted,
          revAfter: window.__adbSnapshot.revision,
        };
      });

      // Reload page to re-synchronize frontend state with C# authoritative state
      await page.reload();
      await page.waitForFunction(() => window.__adbSnapshot !== undefined, { timeout: 10000 });

      console.log(JSON.stringify({ success: true, results }));
    } else if (command === 'press-escape') {
      await page.keyboard.press('Escape');
      await page.waitForTimeout(200);
      console.log(JSON.stringify({ success: true }));
    } else if (command === 'report-size') {
      const height = parseFloat(arg1);
      await page.evaluate((h) => {
        window.chrome.webview.postMessage({ type: 'reportContentSize', heightEffectivePx: h });
      }, height);
      await page.waitForTimeout(200);
      console.log(JSON.stringify({ success: true, height }));
    } else if (command === 'reload-page') {
      await page.reload();
      await page.waitForFunction(() => window.__adbSnapshot !== undefined, { timeout: 10000 });
      const data = await page.evaluate(() => ({
        snapshot: window.__adbSnapshot,
        buttonTitle: document.querySelector('[data-testid="pin-button"]')?.getAttribute('title'),
        actionCount: window.__adbActionCount,
      }));
      console.log(JSON.stringify({ success: true, data }));
    } else if (command === 'get-state') {
      const data = await page.evaluate(() => ({
        snapshot: window.__adbSnapshot,
        actionCount: window.__adbActionCount,
        buttonTitle: document.querySelector('[data-testid="pin-button"]')?.getAttribute('title'),
      }));
      console.log(JSON.stringify({ success: true, data }));
    } else if (command === 'test-instrumentation-gating') {
      const fixtureMode = await page.evaluate(() => ({
        hasActionCount: window.__adbActionCount !== undefined,
        hasSnapshot: window.__adbSnapshot !== undefined,
      }));

      await page.goto('https://app.adbview.local/index.html');
      await page.waitForTimeout(400);

      await page.evaluate(() => {
        const pinBtn = document.querySelector('[data-testid="pin-button"]');
        if (pinBtn) pinBtn.click();
      });
      await page.waitForTimeout(200);

      const normalMode = await page.evaluate(() => ({
        hasActionCount: window.__adbActionCount !== undefined,
        hasSnapshot: window.__adbSnapshot !== undefined,
        hasSnapshotCount: window.__adbSnapshotCount !== undefined,
      }));

      await page.goto('https://app.adbview.local/index.html?fixture=connected-usb');
      await page.waitForFunction(() => window.__adbSnapshot !== undefined, { timeout: 10000 });

      console.log(JSON.stringify({
        success: true,
        fixtureMode,
        normalMode,
      }));
    } else {
      console.error(JSON.stringify({ error: `Unknown command: ${command}` }));
      process.exit(1);
    }
  } catch (err) {
    console.error(JSON.stringify({ error: err.message, stack: err.stack }));
    process.exit(1);
  } finally {
    if (browser) {
      await browser.close();
    }
  }
})();
