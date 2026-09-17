const http = require('http');
const fs = require('fs');
const path = require('path');
const { chromium } = require('../ui/node_modules/playwright');

const baseDir = path.resolve(__dirname, '..');
const outDir = path.join(baseDir, 'artifacts', 'phase2-audit', 'visual-comparison');
const refDir = path.join(outDir, 'reference');
const implDir = path.join(outDir, 'implementation');
const diffDir = path.join(outDir, 'diff');

[refDir, implDir, diffDir].forEach(d => {
    if (!fs.existsSync(d)) fs.mkdirSync(d, { recursive: true });
});

const passB1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_windows_11_system_tray_flyout_pass_b_frozen_corrected/code.html').replace(/\\/g, '/');
const passC1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_pass_c.1_strict_consistency_correction/code.html').replace(/\\/g, '/');
const passD1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_pass_d.1_theme_integrity_windows_forced_colors_matrix/code.html').replace(/\\/g, '/');

const states = [
    // DARK (9 states)
    { id: 'dark-empty', name: 'empty', theme: 'dark', refFile: passB1Path, type: 'b1', index: 10 },
    { id: 'dark-connected-wifi', name: 'connected-wifi', theme: 'dark', refFile: passB1Path, type: 'b1', index: 0 },
    { id: 'dark-expanded', name: 'expanded', theme: 'dark', refFile: passB1Path, type: 'b1', index: 1 },
    { id: 'dark-unauthorized', name: 'unauthorized', theme: 'dark', refFile: passC1Path, type: 'c1', index: 0 },
    { id: 'dark-multi-device', name: 'multi-device', theme: 'dark', refFile: passC1Path, type: 'c1', index: 16 },
    { id: 'dark-pairing-offer', name: 'pairing-offer', theme: 'dark', refFile: passB1Path, type: 'b1', index: 3 },
    { id: 'dark-pairing-code', name: 'pairing-code', theme: 'dark', refFile: passB1Path, type: 'b1', index: 4 },
    { id: 'dark-pairing-success', name: 'pairing-success', theme: 'dark', refFile: passB1Path, type: 'b1', index: 8 },
    { id: 'dark-pairing-failure', name: 'pairing-failure', theme: 'dark', refFile: passB1Path, type: 'b1', index: 9 },

    // LIGHT (4 states)
    { id: 'light-empty', name: 'empty', theme: 'light', refFile: passD1Path, type: 'd1', index: 0 },
    { id: 'light-connected-wifi', name: 'connected-wifi', theme: 'light', refFile: passD1Path, type: 'd1', index: 1 },
    { id: 'light-expanded', name: 'expanded', theme: 'light', refFile: passD1Path, type: 'd1', index: 2 },
    { id: 'light-pairing-success', name: 'pairing-success', theme: 'light', refFile: passD1Path, type: 'd1', index: 8 }
];

// Simple static server for ui/dist
const distDir = path.join(baseDir, 'ui', 'dist');
const server = http.createServer((req, res) => {
    let filePath = path.join(distDir, req.url.split('?')[0]);
    if (req.url === '/' || !path.extname(filePath)) filePath = path.join(distDir, 'index.html');
    fs.readFile(filePath, (err, data) => {
        if (err) { res.writeHead(404); res.end(); return; }
        const ext = path.extname(filePath);
        const mime = ext === '.html' ? 'text/html' : ext === '.js' ? 'application/javascript' : ext === '.css' ? 'text/css' : 'application/octet-stream';
        res.writeHead(200, { 'Content-Type': mime });
        res.end(data);
    });
});

server.listen(5174, async () => {
    console.log('Static server started on http://localhost:5174');
    let browser;
    try {
        browser = await chromium.launch();
        const results = [];

        // Reference Page with 1920px wide viewport to prevent grid column overflow
        const refPage = await browser.newPage({
            viewport: { width: 1920, height: 1200 },
            deviceScaleFactor: 1
        });

        // Implementation Page
        const implPage = await browser.newPage({
            viewport: { width: 376, height: 600 },
            deviceScaleFactor: 1
        });

        // Diff Page
        const diffPage = await browser.newPage({
            viewport: { width: 800, height: 800 }
        });

        let currentRefFile = '';

        for (const s of states) {
            console.log(`\nComparing state: ${s.id} (${s.name}, ${s.theme})`);

            // 1. Navigate reference page if needed
            if (currentRefFile !== s.refFile) {
                await refPage.goto(s.refFile);
                await refPage.waitForLoadState('networkidle');
                await refPage.evaluate(() => {
                    document.fonts.ready;
                    // Remove sticky/fixed master headers that would obscure screenshotting
                    const headers = document.querySelectorAll('header.sticky, header[class*="sticky"], header.fixed, body > header');
                    headers.forEach(h => h.remove());
                });
                currentRefFile = s.refFile;
            }

            // Find reference locator
            let locator;
            if (s.type === 'b1') {
                locator = refPage.locator('.flyout-frame').nth(s.index);
            } else if (s.type === 'c1') {
                locator = refPage.locator('.flyout-shell').nth(s.index);
            } else if (s.type === 'd1') {
                locator = refPage.locator('[data-purpose="card-wrapper"]').nth(s.index).locator('.w-\\[376px\\]');
            }

            const count = await locator.count();
            if (count === 0) {
                console.error(`Could not find reference element for ${s.id}`);
                continue;
            }

            const refBox = await locator.boundingBox();
            const targetHeight = Math.round(refBox.height);
            console.log(`  Reference bounding box: ${Math.round(refBox.width)}x${targetHeight}`);

            const refPngPath = path.join(refDir, `${s.id}.png`);
            await locator.screenshot({ path: refPngPath });

            // 2. Render Implementation
            await implPage.emulateMedia({ colorScheme: s.theme });
            await implPage.goto(`http://localhost:5174/?fixture=${s.name}`);
            await implPage.waitForLoadState('networkidle');
            await implPage.evaluate(() => document.fonts.ready);

            // Set deterministic background for browser comparison
            await implPage.evaluate(({ theme }) => {
                const style = document.createElement('style');
                style.textContent = `* { animation: none !important; transition: none !important; }`;
                document.head.appendChild(style);
                document.documentElement.style.backgroundColor = theme === 'dark' ? '#202329' : '#f3f4f6';
                document.body.style.backgroundColor = theme === 'dark' ? '#202329' : '#f3f4f6';
            }, { theme: s.theme });

            const implPngPath = path.join(implDir, `${s.id}.png`);
            await implPage.screenshot({
                path: implPngPath,
                clip: { x: 0, y: 0, width: 376, height: targetHeight }
            });

            // 3. Compute Diff
            const refBuffer = fs.readFileSync(refPngPath);
            const implBuffer = fs.readFileSync(implPngPath);

            const diffMetrics = await diffPage.evaluate(async ({ refB64, implB64, targetH }) => {
                function loadImg(src) {
                    return new Promise((resolve) => {
                        const img = new Image();
                        img.onload = () => resolve(img);
                        img.src = src;
                    });
                }
                const [imgA, imgB] = await Promise.all([
                    loadImg('data:image/png;base64,' + refB64),
                    loadImg('data:image/png;base64,' + implB64)
                ]);

                const w = 376;
                const h = targetH;

                const cA = document.createElement('canvas');
                cA.width = w; cA.height = h;
                const ctxA = cA.getContext('2d');
                ctxA.drawImage(imgA, 0, 0, w, h);
                const dataA = ctxA.getImageData(0, 0, w, h).data;

                const cB = document.createElement('canvas');
                cB.width = w; cB.height = h;
                const ctxB = cB.getContext('2d');
                ctxB.drawImage(imgB, 0, 0, w, h);
                const dataB = ctxB.getImageData(0, 0, w, h).data;

                const cDiff = document.createElement('canvas');
                cDiff.width = w; cDiff.height = h;
                const ctxDiff = cDiff.getContext('2d');
                const imgDiff = ctxDiff.createImageData(w, h);
                const dataDiff = imgDiff.data;

                let diffCount = 0;
                const total = w * h;

                for (let i = 0; i < dataA.length; i += 4) {
                    const dr = Math.abs(dataA[i] - dataB[i]);
                    const dg = Math.abs(dataA[i+1] - dataB[i+1]);
                    const db = Math.abs(dataA[i+2] - dataB[i+2]);
                    const da = Math.abs(dataA[i+3] - dataB[i+3]);

                    const delta = (dr + dg + db + da) / 4;
                    if (delta > 20) {
                        diffCount++;
                        dataDiff[i] = 255;
                        dataDiff[i+1] = 0;
                        dataDiff[i+2] = 255;
                        dataDiff[i+3] = 255;
                    } else {
                        dataDiff[i] = Math.round(dataB[i] * 0.35);
                        dataDiff[i+1] = Math.round(dataB[i+1] * 0.35);
                        dataDiff[i+2] = Math.round(dataB[i+2] * 0.35);
                        dataDiff[i+3] = 255;
                    }
                }
                ctxDiff.putImageData(imgDiff, 0, 0);

                return {
                    diffCount,
                    total,
                    mismatchPct: ((diffCount / total) * 100).toFixed(2),
                    diffB64: cDiff.toDataURL('image/png')
                };
            }, {
                refB64: refBuffer.toString('base64'),
                implB64: implBuffer.toString('base64'),
                targetH: targetHeight
            });

            const diffPngPath = path.join(diffDir, `${s.id}.png`);
            const b64Pure = diffMetrics.diffB64.replace(/^data:image\/png;base64,/, '');
            fs.writeFileSync(diffPngPath, Buffer.from(b64Pure, 'base64'));

            console.log(`  Diff: ${diffMetrics.diffCount} mismatched pixels (${diffMetrics.mismatchPct}%)`);
            results.push({
                id: s.id,
                name: s.name,
                theme: s.theme,
                dimensions: `376x${targetHeight}`,
                diffCount: diffMetrics.diffCount,
                mismatchPct: parseFloat(diffMetrics.mismatchPct),
                refPath: `artifacts/phase2-audit/visual-comparison/reference/${s.id}.png`,
                implPath: `artifacts/phase2-audit/visual-comparison/implementation/${s.id}.png`,
                diffPath: `artifacts/phase2-audit/visual-comparison/diff/${s.id}.png`
            });
        }

        fs.writeFileSync(path.join(outDir, 'summary.json'), JSON.stringify(results, null, 2));
        console.log('\nVisual comparison complete. Results written to summary.json');
    } finally {
        if (browser) await browser.close();
        server.close();
    }
});
