const { chromium } = require('../ui/node_modules/playwright');
const fs = require('fs');
const path = require('path');

const action = process.argv[2];
const fixtureName = process.argv[3];
const extraArg = process.argv[4];

(async () => {
    let browser;
    try {
        const cdpPort = process.env.CDP_PORT || '9333';
        browser = await chromium.connectOverCDP(`http://127.0.0.1:${cdpPort}`);
        const defaultContext = browser.contexts()[0];
        let page = defaultContext.pages().find(p => p.url().includes('app.adbview.local'));
        if (!page) {
            page = defaultContext.pages()[0];
            try {
                await page.waitForURL(url => url.href.includes('app.adbview.local'), { timeout: 10000 });
            } catch {}
        }

        const baseDir = path.resolve(__dirname, '..', 'artifacts', 'phase2-audit');
        const baselineDir = path.join(baseDir, 'baseline');
        const finalDir = path.join(baseDir, 'final');
        const interactionsDir = path.join(baseDir, 'interactions');
        [baselineDir, finalDir, interactionsDir].forEach(d => {
            if (!fs.existsSync(d)) fs.mkdirSync(d, { recursive: true });
        });

        const result = { success: true, action, fixtureName, details: {} };

        if (action === 'measure-theme') {
            const mode = extraArg || 'dark';
            await page.emulateMedia({ colorScheme: mode });
            // Authoritative shell.theme is dark in fixtures; force data-theme for D.1 token audits.
            await page.evaluate((m) => {
                document.documentElement.dataset.theme = m;
            }, mode);
            await page.waitForTimeout(100);

            const metrics = await page.evaluate(() => {
                const htmlStyle = window.getComputedStyle(document.documentElement);
                const bodyStyle = window.getComputedStyle(document.body);
                const primaryEl = document.querySelector('header span.font-semibold, h3');
                const secondaryEl = document.querySelector('.text-win-text-secondary, [class*="text-win-text-secondary"]') || document.querySelector('p');
                return {
                    colorScheme: htmlStyle.colorScheme,
                    htmlBg: htmlStyle.backgroundColor,
                    bodyBg: bodyStyle.backgroundColor,
                    bodyColor: bodyStyle.color,
                    primaryTextColor: primaryEl ? window.getComputedStyle(primaryEl).color : null,
                    secondaryTextColor: secondaryEl ? window.getComputedStyle(secondaryEl).color : null
                };
            });

            result.details = metrics;
            const fileName = `browser-${mode}-${fixtureName}.png`;
            await page.screenshot({ path: path.join(baselineDir, fileName) });
            await page.screenshot({ path: path.join(finalDir, fileName) });
            result.details.screenshot = fileName;

        } else if (action === 'check-invariants') {
            result.details = await page.evaluate((fixture) => {
                const violations = [];
                const cards = Array.from(document.querySelectorAll('.flex-1.overflow-y-auto [data-testid="device-card"]'));

                cards.forEach((c, idx) => {
                    const hasExpandedChevron = !!c.querySelector('[data-testid="chevron-expanded"]') || !!c.querySelector('path[d*="18 15"]');
                    const hasCollapsedChevron = !!c.querySelector('[data-testid="chevron-collapsed"]') || !!c.querySelector('path[d*="6 9"]');
                    const grid = c.querySelector('[data-testid="device-card-details-grid"]');
                    const details = c.querySelector('[data-testid="device-card-details"]') || c.querySelector('div.space-y-2');
                    const gridExpanded = grid ? grid.classList.contains('is-expanded') : false;
                    const gridHeight = grid ? grid.getBoundingClientRect().height : 0;
                    const detailsHeight = details ? details.getBoundingClientRect().height : 0;

                    // Invariant: expanded chevron REQUIRES expanded grid with visible details
                    if (hasExpandedChevron && (!gridExpanded || detailsHeight <= 0)) {
                        violations.push({
                            rule: 'expanded-chevron-requires-visible-details',
                            cardIndex: idx,
                            detailsHeight,
                            gridHeight,
                            message: `Card ${idx} has expanded chevron but details not visibly expanded`
                        });
                    }

                    // Invariant: collapsed chevron REQUIRES collapsed grid (0fr — details may stay mounted)
                    if (hasCollapsedChevron && (gridExpanded || gridHeight > 1)) {
                        violations.push({
                            rule: 'collapsed-chevron-forbids-visible-details',
                            cardIndex: idx,
                            detailsHeight,
                            gridHeight,
                            message: `Card ${idx} has collapsed chevron but details grid still open (h=${gridHeight})`
                        });
                    }

                    // Invariant: USB devices must NOT show Address or IP in the *visible* expanded region
                    const isUsb = c.innerText.includes('USB') && !c.innerText.includes('Wi-Fi');
                    if (isUsb && gridExpanded) {
                        const detailText = details ? details.innerText : '';
                        if (detailText.includes('Address') || /\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}/.test(detailText)) {
                            violations.push({
                                rule: 'usb-device-must-not-show-address',
                                cardIndex: idx,
                                message: `Card ${idx} is USB but contains Address or IP pattern`
                            });
                        }
                    }
                });

                // Invariant: connected-usb must show USB, connected-wifi must show Wi-Fi
                if (fixture === 'connected-usb') {
                    const firstCard = cards[0];
                    if (firstCard) {
                        const text = firstCard.innerText;
                        if (!text.includes('USB') || text.includes('Wi-Fi')) {
                            violations.push({
                                rule: 'connected-usb-must-be-usb',
                                message: `connected-usb fixture reports Wi-Fi instead of USB`
                            });
                        }
                    }
                } else if (fixture === 'connected-wifi') {
                    const firstCard = cards[0];
                    if (firstCard) {
                        const text = firstCard.innerText;
                        if (!text.includes('Wi-Fi') || text.includes('USB')) {
                            violations.push({
                                rule: 'connected-wifi-must-be-wifi',
                                message: `connected-wifi fixture reports USB instead of Wi-Fi`
                            });
                        }
                    }
                }

                // Invariant: header top must be <= 16 (container padding)
                const header = document.querySelector('header');
                if (header) {
                    const rect = header.getBoundingClientRect();
                    if (rect.top > 16 || rect.top < 0) {
                        violations.push({
                            rule: 'header-must-be-at-top',
                            actualTop: rect.top,
                            message: `Header top is ${rect.top} instead of <= 16`
                        });
                    }
                }

                // Invariant: Fixture browser must not be open by default
                const modal = document.querySelector('.fixed.inset-0');
                if (modal) {
                    violations.push({
                        rule: 'fixture-browser-closed-by-default',
                        message: 'Fixture browser modal is visible by default'
                    });
                }

                // Invariant: html and body backgrounds transparent in Acrylic
                const htmlBg = window.getComputedStyle(document.documentElement).backgroundColor;
                const bodyBg = window.getComputedStyle(document.body).backgroundColor;
                if (htmlBg !== 'rgba(0, 0, 0, 0)' || bodyBg !== 'rgba(0, 0, 0, 0)') {
                    violations.push({
                        rule: 'acrylic-transparency',
                        htmlBg,
                        bodyBg,
                        message: `Backgrounds are not transparent in Acrylic mode (html: ${htmlBg}, body: ${bodyBg})`
                    });
                }

                return {
                    passed: violations.length === 0,
                    violations
                };
            }, fixtureName);

        } else if (action === 'card-expansion-detailed') {
            const cardIdx = parseInt(extraArg || '0', 10);
            const cards = await page.$$('.flex-1.overflow-y-auto [data-testid="device-card"]');
            if (cards.length <= cardIdx) {
                throw new Error(`Card index ${cardIdx} not found. Total cards: ${cards.length}`);
            }

            const targetCard = cards[cardIdx];

            // 1. Initial / Before Click
            const beforeMetrics = await targetCard.evaluate((c) => {
                const rect = c.getBoundingClientRect();
                const chevronDown = !!c.querySelector('[data-testid="chevron-collapsed"]') || !!c.querySelector('path[d*="6 9"]');
                const chevronUp = !!c.querySelector('[data-testid="chevron-expanded"]') || !!c.querySelector('path[d*="18 15"]');
                const grid = c.querySelector('[data-testid="device-card-details-grid"]');
                const details = c.querySelector('[data-testid="device-card-details"]') || c.querySelector('div.space-y-2');
                // Visible details height = grid box (0fr collapses to ~0; content may stay mounted).
                const detailsHeight = grid ? grid.getBoundingClientRect().height : 0;
                return {
                    width: rect.width,
                    height: rect.height,
                    chevronDown,
                    chevronUp,
                    detailsPresent: !!details,
                    detailsHeight
                };
            });

            const beforePngPath = path.join(interactionsDir, `${fixtureName}-card${cardIdx}-before.png`);
            const beforeBuf = await targetCard.screenshot({ path: beforePngPath });

            // 2. Click Chevron to Expand
            const chevron = await targetCard.$('.hit-area-40');
            if (chevron) await chevron.click();
            else await targetCard.click();
            await page.waitForTimeout(300);

            // 3. Expanded State
            const expandedMetrics = await targetCard.evaluate((c) => {
                const rect = c.getBoundingClientRect();
                const chevronDown = !!c.querySelector('[data-testid="chevron-collapsed"]') || !!c.querySelector('path[d*="6 9"]');
                const chevronUp = !!c.querySelector('[data-testid="chevron-expanded"]') || !!c.querySelector('path[d*="18 15"]');
                const grid = c.querySelector('[data-testid="device-card-details-grid"]');
                const details = c.querySelector('[data-testid="device-card-details"]') || c.querySelector('div.space-y-2');
                const detailsHeight = grid ? grid.getBoundingClientRect().height : 0;
                const text = details ? details.innerText : '';
                return {
                    width: rect.width,
                    height: rect.height,
                    chevronDown,
                    chevronUp,
                    detailsPresent: !!details,
                    detailsHeight,
                    hasModel: text.includes('Model'),
                    hasAndroid: text.includes('Android'),
                    hasSerial: text.includes('Serial'),
                    hasAddress: text.includes('Address'),
                    rawDetailsText: text
                };
            });

            const expandedPngPath = path.join(interactionsDir, `${fixtureName}-card${cardIdx}-expanded.png`);
            const expandedBuf = await targetCard.screenshot({ path: expandedPngPath });
            const pixelDiff = Buffer.compare(beforeBuf, expandedBuf) !== 0;

            // 4. Click Chevron again to Collapse
            const chevron2 = await targetCard.$('.hit-area-40');
            if (chevron2) await chevron2.click();
            else await targetCard.click();
            await page.waitForTimeout(300);

            // 5. Collapsed State
            const finalMetrics = await targetCard.evaluate((c) => {
                const rect = c.getBoundingClientRect();
                const chevronDown = !!c.querySelector('[data-testid="chevron-collapsed"]') || !!c.querySelector('path[d*="6 9"]');
                const chevronUp = !!c.querySelector('[data-testid="chevron-expanded"]') || !!c.querySelector('path[d*="18 15"]');
                const grid = c.querySelector('[data-testid="device-card-details-grid"]');
                const details = c.querySelector('[data-testid="device-card-details"]') || c.querySelector('div.space-y-2');
                const detailsHeight = grid ? grid.getBoundingClientRect().height : 0;
                return {
                    width: rect.width,
                    height: rect.height,
                    chevronDown,
                    chevronUp,
                    detailsPresent: !!details,
                    detailsHeight
                };
            });

            const finalPngPath = path.join(interactionsDir, `${fixtureName}-card${cardIdx}-collapsed.png`);
            await targetCard.screenshot({ path: finalPngPath });

            let passed = false;
            if (beforeMetrics.chevronDown) {
                // Started collapsed: test Expand -> Render Details -> Collapse -> Restore
                passed = 
                    beforeMetrics.chevronDown &&
                    !beforeMetrics.chevronUp &&
                    beforeMetrics.detailsHeight <= 1 &&
                    expandedMetrics.chevronUp &&
                    !expandedMetrics.chevronDown &&
                    expandedMetrics.detailsPresent &&
                    expandedMetrics.detailsHeight > 1 &&
                    expandedMetrics.height > beforeMetrics.height &&
                    pixelDiff &&
                    finalMetrics.chevronDown &&
                    !finalMetrics.chevronUp &&
                    finalMetrics.detailsHeight <= 1 &&
                    Math.abs(finalMetrics.height - beforeMetrics.height) <= 2;
            } else {
                // Started expanded (e.g. 'expanded' fixture): test Collapse -> Details Hidden -> Re-expand -> Restore
                passed = 
                    beforeMetrics.chevronUp &&
                    !beforeMetrics.chevronDown &&
                    beforeMetrics.detailsHeight > 1 &&
                    expandedMetrics.chevronDown &&
                    !expandedMetrics.chevronUp &&
                    expandedMetrics.detailsHeight <= 1 &&
                    expandedMetrics.height < beforeMetrics.height &&
                    pixelDiff &&
                    finalMetrics.chevronUp &&
                    !finalMetrics.chevronDown &&
                    finalMetrics.detailsPresent &&
                    finalMetrics.detailsHeight > 1 &&
                    Math.abs(finalMetrics.height - beforeMetrics.height) <= 2;
            }

            result.details = {
                cardIndex: cardIdx,
                before: beforeMetrics,
                expanded: expandedMetrics,
                final: finalMetrics,
                pixelDiff,
                passed
            };

        } else if (action === 'multi-device-full-audit') {
            await page.emulateMedia({ colorScheme: 'dark' });
            await page.waitForTimeout(100);
            await page.waitForSelector('main');

            const getCardState = async (idx) => {
                return await page.evaluate((i) => {
                    const cards = Array.from(document.querySelectorAll('.flex-1.overflow-y-auto [data-testid="device-card"]'));
                    const c = cards[i];
                    if (!c) return null;
                    const chevronUp = !!c.querySelector('[data-testid="chevron-expanded"]') || !!c.querySelector('path[d*="18 15"]');
                    const chevronDown = !!c.querySelector('[data-testid="chevron-collapsed"]') || !!c.querySelector('path[d*="6 9"]');
                    const grid = c.querySelector('[data-testid="device-card-details-grid"]');
                    const details = c.querySelector('[data-testid="device-card-details"]') || c.querySelector('div.space-y-2');
                    const gridHeight = grid ? grid.getBoundingClientRect().height : 0;
                    const gridExpanded = grid ? grid.classList.contains('is-expanded') : false;
                    const rect = c.getBoundingClientRect();
                    return {
                        index: i,
                        chevronUp,
                        chevronDown,
                        hasDetailsDOM: !!details,
                        detailsHeight: gridHeight,
                        isExpanded: chevronUp && gridExpanded && gridHeight > 1,
                        isCollapsed: chevronDown && !gridExpanded && gridHeight <= 1,
                        width: rect.width,
                        height: rect.height
                    };
                }, idx);
            };

            const toggleCard = async (idx) => {
                const cards = await page.$$('.flex-1.overflow-y-auto [data-testid="device-card"]');
                if (cards.length > idx) {
                    const chevron = await cards[idx].$('.hit-area-40');
                    if (chevron) await chevron.click();
                    else await cards[idx].click();
                    await page.waitForTimeout(300);
                }
            };

            const independentTests = [];
            for (let i = 0; i < 6; i++) {
                await toggleCard(i);
                const afterExpand = await getCardState(i);
                const allStates = await Promise.all([0,1,2,3,4,5].map(getCardState));
                // DESIGN: only one card expanded at a time.
                const othersCollapsed = allStates.every((st, idx) => idx === i ? st.isExpanded : st.isCollapsed);

                await toggleCard(i);
                const afterCollapse = await getCardState(i);

                independentTests.push({
                    cardIndex: i,
                    expandedCorrectly: afterExpand.isExpanded,
                    othersRemainedCollapsed: othersCollapsed,
                    collapsedCorrectly: afterCollapse.isCollapsed,
                    passed: afterExpand.isExpanded && othersCollapsed && afterCollapse.isCollapsed
                });
            }

            // Combination tests (single-expand):
            // 1. Expand card 0
            await toggleCard(0);
            const s1_0 = await getCardState(0);
            const s1_1 = await getCardState(1);

            // 2. Expand card 1 → card 0 collapses
            await toggleCard(1);
            const s2_0 = await getCardState(0);
            const s2_1 = await getCardState(1);

            // 3. Expand card 0 → card 1 collapses
            await toggleCard(0);
            const s3_0 = await getCardState(0);
            const s3_1 = await getCardState(1);

            // 4. Scroll down and expand card 5
            await page.evaluate(() => {
                const container = document.querySelector('.flex-1.overflow-y-auto');
                if (container) container.scrollTop = container.scrollHeight;
            });
            await page.waitForTimeout(100);
            await toggleCard(5);
            const s4_5 = await getCardState(5);
            const s4_0 = await getCardState(0);

            // Reset
            await toggleCard(5);
            await page.evaluate(() => {
                const container = document.querySelector('.flex-1.overflow-y-auto');
                if (container) container.scrollTop = 0;
            });

            // Layout & scroll checks
            const layoutCheck = await page.evaluate(() => {
                const container = document.querySelector('.flex-1.overflow-y-auto');
                const header = document.querySelector('header');
                const cards = Array.from(document.querySelectorAll('.flex-1.overflow-y-auto [data-testid="device-card"]'));
                const containerWidth = container.clientWidth;
                const allFullSize = cards.every(c => Math.abs(c.getBoundingClientRect().width - containerWidth) <= 4);

                const headerTopInitial = header.getBoundingClientRect().top;
                const canScroll = container.scrollHeight > container.clientHeight;
                container.scrollTop = 120;
                const headerTopAfter = header.getBoundingClientRect().top;

                return {
                    allFullSize,
                    canScroll,
                    headerFixed: headerTopInitial === headerTopAfter,
                    containerColorScheme: window.getComputedStyle(container).colorScheme
                };
            });

            // Scrollbar luminance test in dark mode
            const scrollbarCheck = await page.evaluate(async () => {
                const container = document.querySelector('.flex-1.overflow-y-auto');
                const cs = window.getComputedStyle(container).colorScheme;
                const rootCs = window.getComputedStyle(document.documentElement).colorScheme;
                return {
                    colorScheme: cs,
                    rootColorScheme: rootCs,
                    passed: cs === 'dark' || rootCs === 'dark'
                };
            });

            const combinationsPassed =
                s1_0.isExpanded && s1_1.isCollapsed &&
                s2_0.isCollapsed && s2_1.isExpanded &&
                s3_0.isExpanded && s3_1.isCollapsed &&
                s4_5.isExpanded && s4_0.isCollapsed;

            result.details = {
                independentTests,
                allIndependentPassed: independentTests.every(t => t.passed),
                combinationsPassed,
                layoutCheck,
                scrollbarCheck,
                passed: independentTests.every(t => t.passed) && combinationsPassed && layoutCheck.allFullSize && layoutCheck.canScroll && layoutCheck.headerFixed && scrollbarCheck.passed
            };

        } else if (action === 'copy-interaction-test') {
            // Expand card 0 to reveal copy buttons
            const cards = await page.$$('.flex-1.overflow-y-auto [data-testid="device-card"]');
            if (cards.length === 0) throw new Error('No device cards found');
            const card = cards[0];

            // If not expanded, click to expand
            const isExp = await card.$('[data-testid="chevron-expanded"]');
            if (!isExp) {
                const chevron = await card.$('.hit-area-40');
                if (chevron) await chevron.click();
                else await card.click();
                await page.waitForTimeout(150);
            }

            const copyResults = await page.evaluate(async () => {
                const cardEl = document.querySelector('.flex-1.overflow-y-auto [data-testid="device-card"]');
                const results = { address: null, serial: null };

                // 1. Address copy test
                const addrBtn = cardEl.querySelector('button[title="Copy Address"]');
                if (addrBtn) {
                    addrBtn.click();
                    await new Promise(r => setTimeout(r, 50));
                    const badgeAfterClick = !!cardEl.querySelector('[data-testid="copied-badge"]');
                    const badgeText = cardEl.querySelector('[data-testid="copied-badge"]')?.innerText || '';
                    const serialBtnStillPresent = !!cardEl.querySelector('button[title="Copy Serial"]');

                    // Wait 1300ms for clear
                    await new Promise(r => setTimeout(r, 1300));
                    const btnRestored = !!cardEl.querySelector('button[title="Copy Address"]');
                    const badgeCleared = !cardEl.querySelector('[data-testid="copied-badge"]');

                    results.address = {
                        badgeShown: badgeAfterClick && badgeText.includes('Copied'),
                        serialNotTriggered: serialBtnStillPresent,
                        clearedAfterTimeout: btnRestored && badgeCleared,
                        passed: badgeAfterClick && badgeText.includes('Copied') && serialBtnStillPresent && btnRestored && badgeCleared
                    };
                }

                // 2. Serial copy test
                const serialBtn = cardEl.querySelector('button[title="Copy Serial"]');
                if (serialBtn) {
                    serialBtn.click();
                    await new Promise(r => setTimeout(r, 50));
                    const badgeAfterClick = !!cardEl.querySelector('[data-testid="copied-badge"]');
                    const badgeText = cardEl.querySelector('[data-testid="copied-badge"]')?.innerText || '';

                    await new Promise(r => setTimeout(r, 1300));
                    const btnRestored = !!cardEl.querySelector('button[title="Copy Serial"]');
                    const badgeCleared = !cardEl.querySelector('[data-testid="copied-badge"]');

                    results.serial = {
                        badgeShown: badgeAfterClick && badgeText.includes('Copied'),
                        clearedAfterTimeout: btnRestored && badgeCleared,
                        passed: badgeAfterClick && badgeText.includes('Copied') && btnRestored && badgeCleared
                    };
                }

                return results;
            });

            result.details = copyResults;

        } else if (action === 'pairing-interactions') {
            result.details = await page.evaluate((fixture) => {
                const card = document.querySelector('main');
                const text = card.innerText;
                const res = { fixture, passed: false, details: {} };

                if (fixture === 'pairing-offer') {
                    const hasTitle = text.includes('Wireless pairing available');
                    const hasDevice = text.includes('Galaxy S24');
                    const hasSub = text.includes('Waiting for a pairing code');
                    const pairBtn = Array.from(document.querySelectorAll('button')).find(b => b.innerText.includes('Pair'));
                    const hasPairBtn = !!pairBtn;
                    res.details = { hasTitle, hasDevice, hasSub, hasPairBtn };
                    res.passed = hasTitle && hasDevice && hasSub && hasPairBtn;

                } else if (fixture === 'pairing-code') {
                    const hasInstr = text.includes('Enter the six-digit code shown on the phone.');
                    const slots = document.querySelectorAll('.grid.grid-cols-6 > div');
                    const hasSlots = slots.length === 6;
                    const cancelBtn = Array.from(document.querySelectorAll('button')).find(b => b.innerText.includes('Cancel'));
                    res.details = { hasInstr, slotCount: slots.length, hasCancelBtn: !!cancelBtn };
                    res.passed = hasInstr && hasSlots && !!cancelBtn;

                } else if (fixture === 'pairing-progress') {
                    const hasProgress = text.includes('Pairing with Galaxy S24…');
                    const hasSub = text.includes('Keep the phone nearby while pairing completes.');
                    const spinner = document.querySelector('.animate-spin-quiet');
                    const cancelBtn = Array.from(document.querySelectorAll('button')).find(b => b.innerText.includes('Cancel'));
                    res.details = { hasProgress, hasSub, hasSpinner: !!spinner, hasCancelBtn: !!cancelBtn };
                    res.passed = hasProgress && hasSub && !!spinner && !!cancelBtn;

                } else if (fixture === 'pairing-success') {
                    const hasSuccess = text.includes('Paired with Galaxy S24');
                    const hasSub = text.includes('Waiting for ADB connection…');
                    // Invariant: paired != connected (no adb device details or card)
                    const noDeviceCards = document.querySelectorAll('[data-testid="device-card"]').length === 0;
                    res.details = { hasSuccess, hasSub, noDeviceCards };
                    res.passed = hasSuccess && hasSub && noDeviceCards;

                } else if (fixture === 'pairing-failure') {
                    const hasFailure = text.includes('Pairing failed');
                    const hasSub = text.includes('Check the code and try again.');
                    const retryBtn = Array.from(document.querySelectorAll('button')).find(b => b.innerText.includes('Retry'));
                    const cancelBtn = Array.from(document.querySelectorAll('button')).find(b => b.innerText.includes('Cancel'));
                    res.details = { hasFailure, hasSub, hasRetryBtn: !!retryBtn, hasCancelBtn: !!cancelBtn };
                    res.passed = hasFailure && hasSub && !!retryBtn && !!cancelBtn;
                }

                return res;
            }, fixtureName);

        } else if (action === 'fixture-selector-test') {
            // 1. In test/fixture mode (?fixture=...): Ctrl+Shift+F opens fixture selector with 16 reachable fixtures
            await page.keyboard.press('Control+Shift+KeyF');
            await page.waitForTimeout(150);

            const modalOpen = await page.evaluate(() => {
                const modal = document.querySelector('.fixed.inset-0');
                const title = modal ? modal.querySelector('span.font-semibold')?.innerText : '';
                const buttons = modal ? Array.from(modal.querySelectorAll('button')).map(b => b.innerText.replace(/[●\n]/g, '').trim()) : [];
                return {
                    isOpen: !!modal,
                    title,
                    buttons
                };
            });

            // Close via Escape
            await page.keyboard.press('Escape');
            await page.waitForTimeout(100);

            const modalClosed = await page.evaluate(() => {
                return !document.querySelector('.fixed.inset-0');
            });

            // 2. Automated invariant: Normal production launch (without ?fixture=) MUST NOT expose fixture selector
            const prodGatingPassed = await page.evaluate(async () => {
                const origSearch = window.location.search;
                const origHref = window.location.href;
                try {
                    // Strip fixture param to simulate production launch
                    window.history.replaceState({}, '', window.location.pathname);

                    // Attempt Ctrl+Shift+F in production state
                    const keyEvent = new KeyboardEvent('keydown', {
                        key: 'F',
                        code: 'KeyF',
                        ctrlKey: true,
                        shiftKey: true,
                        bubbles: true,
                        cancelable: true
                    });
                    window.dispatchEvent(keyEvent);
                    await new Promise(r => setTimeout(r, 100));

                    const modalInProd = document.querySelector('.fixed.inset-0');
                    return modalInProd === null;
                } finally {
                    window.history.replaceState({}, '', origHref);
                }
            });

            result.details = {
                modalOpen,
                modalClosed,
                prodGatingPassed,
                passed: modalOpen.isOpen && modalOpen.title === 'Fixture Browser' && modalOpen.buttons.length >= 16 && modalClosed && prodGatingPassed
            };

        } else if (action === 'accessibility-check') {
            result.details = await page.evaluate(() => {
                const buttons = Array.from(document.querySelectorAll('button'));
                const pinBtn = document.querySelector('header button[title*="in flyout"]');
                const allButtonsHaveNames = buttons.every(b => {
                    const name = b.getAttribute('aria-label') || b.getAttribute('title') || b.innerText.trim();
                    return name.length > 0;
                });
                const pinHasAria = pinBtn ? !!pinBtn.getAttribute('aria-label') && !!pinBtn.getAttribute('title') : false;

                return {
                    totalButtons: buttons.length,
                    allButtonsHaveNames,
                    pinHasAria,
                    passed: allButtonsHaveNames && pinHasAria
                };
            });

        } else if (action === 'pin-info') {
            result.details = await page.evaluate(() => {
                const btn = document.querySelector('header button[title*="in flyout"]');
                const hdr = document.querySelector('header');
                return {
                    title: btn ? btn.getAttribute('title') : '',
                    ariaLabel: btn ? btn.getAttribute('aria-label') : '',
                    isPinnedVisual: btn ? btn.getAttribute('title') === 'Unpin flyout' : false,
                    draggable: hdr ? (hdr.style.webkitAppRegion === 'drag' || window.getComputedStyle(hdr).webkitAppRegion === 'drag') : false
                };
            });

        } else if (action === 'pin-click') {
            const expectedPinned = extraArg === 'true';
            const targetTitle = expectedPinned ? 'Unpin flyout' : 'Pin flyout';

            const pinBtn = await page.waitForSelector('header button[title*="in flyout"]');
            await pinBtn.click({ force: true });

            await page.waitForFunction((expected) => {
                const btn = document.querySelector('header button[title*="in flyout"]');
                return btn && btn.getAttribute('title') === expected;
            }, targetTitle, { timeout: 3000 });

            result.details = await page.evaluate(() => {
                const btn = document.querySelector('header button[title*="in flyout"]');
                const hdr = document.querySelector('header');
                return {
                    title: btn ? btn.getAttribute('title') : '',
                    ariaLabel: btn ? btn.getAttribute('aria-label') : '',
                    isPinnedVisual: btn ? btn.getAttribute('title') === 'Unpin flyout' : false,
                    draggable: hdr ? (hdr.style.webkitAppRegion === 'drag' || window.getComputedStyle(hdr).webkitAppRegion === 'drag') : false
                };
            });
        } else if (action === 'drag-info') {
            await page.waitForSelector('header');
            result.details = await page.evaluate(() => {
                const hdr = document.querySelector('header');
                const r = hdr ? hdr.getBoundingClientRect() : null;
                const cs = hdr ? window.getComputedStyle(hdr) : null;
                return {
                    headerRect: r ? { left: r.left, top: r.top, width: r.width, height: r.height } : null,
                    dpr: window.devicePixelRatio,
                    appRegion: cs ? cs.webkitAppRegion : null
                };
            });
        } else if (action === 'playwright-drag') {
            const hdr = await page.waitForSelector('header');
            const box = await hdr.boundingBox();
            const startX = box.x + 100;
            const startY = box.y + 20;
            await page.mouse.move(startX, startY);
            await page.mouse.down();
            for (let i = 1; i <= 10; i++) {
                await page.mouse.move(startX + i * 10, startY + i * 5);
                await page.waitForTimeout(20);
            }
            await page.mouse.up();
            await page.waitForTimeout(100);
            result.details = { dragged: true, box };
        }

        fs.writeFileSync('audit-result.json', JSON.stringify(result));
    } catch(e) {
        fs.writeFileSync('audit-result.json', JSON.stringify({ success: false, error: e.toString() }));
    } finally {
        if (browser) await browser.close();
    }
})();
