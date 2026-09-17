const fs = require('fs');
const path = require('path');
const http = require('http');
const { chromium } = require('../ui/node_modules/playwright');

const baseDir = path.resolve(__dirname, '..');
const passB1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_windows_11_system_tray_flyout_pass_b_frozen_corrected/code.html').replace(/\\/g, '/');
const passC1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_pass_c.1_strict_consistency_correction/code.html').replace(/\\/g, '/');
const passD1Path = 'file:///' + path.join(baseDir, 'design/reference/extracted/stitch_adb_view_flyout_interface/adb_view_pass_d.1_theme_integrity_windows_forced_colors_matrix/code.html').replace(/\\/g, '/');

const states = [
    { id: 'dark-empty', name: 'empty', theme: 'dark', refFile: passB1Path, type: 'b1', index: 10 },
    { id: 'dark-connected-wifi', name: 'connected-wifi', theme: 'dark', refFile: passB1Path, type: 'b1', index: 0 },
    { id: 'dark-expanded', name: 'expanded', theme: 'dark', refFile: passB1Path, type: 'b1', index: 1 },
    { id: 'dark-unauthorized', name: 'unauthorized', theme: 'dark', refFile: passC1Path, type: 'c1', index: 0 },
    { id: 'dark-multi-device', name: 'multi-device', theme: 'dark', refFile: passC1Path, type: 'c1', index: 16 },
    { id: 'dark-pairing-offer', name: 'pairing-offer', theme: 'dark', refFile: passB1Path, type: 'b1', index: 3 },
    { id: 'dark-pairing-code', name: 'pairing-code', theme: 'dark', refFile: passB1Path, type: 'b1', index: 4 },
    { id: 'dark-pairing-success', name: 'pairing-success', theme: 'dark', refFile: passB1Path, type: 'b1', index: 8 },
    { id: 'dark-pairing-failure', name: 'pairing-failure', theme: 'dark', refFile: passB1Path, type: 'b1', index: 9 },
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

server.listen(5176, async () => {
    let browser;
    try {
        browser = await chromium.launch();
        const refPage = await browser.newPage({ viewport: { width: 1920, height: 1200 } });
        const implPage = await browser.newPage({ viewport: { width: 376, height: 600 } });
        
        let currentRef = '';
        const report = {};

        for (const s of states) {
            if (currentRef !== s.refFile) {
                await refPage.goto(s.refFile);
                await refPage.waitForLoadState('networkidle');
                await refPage.evaluate(() => {
                    const headers = document.querySelectorAll('header.sticky, header[class*="sticky"], header.fixed, body > header');
                    headers.forEach(h => h.remove());
                });
                currentRef = s.refFile;
            }
            let locator;
            if (s.type === 'b1') locator = refPage.locator('.flyout-frame').nth(s.index);
            else if (s.type === 'c1') locator = refPage.locator('.flyout-shell').nth(s.index);
            else if (s.type === 'd1') locator = refPage.locator('[data-purpose="card-wrapper"]').nth(s.index).locator('.w-\\[376px\\]');
            
            let refText = [];
            try {
                const raw = await locator.evaluate(el => el.innerText);
                refText = raw.split('\n').map(t => t.trim()).filter(Boolean);
            } catch (e) {
                console.error(`Error reading ref for ${s.id}:`, e.message);
            }

            await implPage.goto(`http://localhost:5176/?fixture=${s.name}&theme=${s.theme}`);
            await implPage.waitForLoadState('networkidle');
            const implText = (await implPage.locator('#root').evaluate(el => el.innerText)).split('\n').map(t => t.trim()).filter(Boolean);

            report[s.id] = {
                ref: refText,
                impl: implText
            };
        }

        await browser.close();
        const reportPath = path.join(baseDir, 'artifacts', 'phase2-audit', 'textual-diff.json');
        fs.writeFileSync(reportPath, JSON.stringify(report, null, 2));
        console.log(`Saved textual diff report to ${reportPath}`);
        console.log(JSON.stringify(report, null, 2));
    } catch (err) {
        console.error(err);
    } finally {
        server.close();
    }
});
