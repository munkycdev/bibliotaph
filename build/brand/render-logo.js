const { chromium } = require('playwright');
const fs = require('fs');
// Usage: node render-logo.js <brand folder>. Draws each SVG there as a PNG for places that take no SVG.
const [,, brand] = process.argv;
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  async function shot(file, w, h, inner, bg = 'transparent') {
    await page.setViewportSize({ width: w, height: h });
    await page.setContent(`<html><body style="margin:0;background:${bg}">${inner}</body></html>`);
    await page.waitForTimeout(200);
    await page.screenshot({ path: file, omitBackground: bg === 'transparent', clip: { x: 0, y: 0, width: w, height: h } });
  }
  const svg = n => 'data:image/svg+xml;base64,' + fs.readFileSync(`${brand}/${n}.svg`).toString('base64');
  for (const t of ['', '-dark']) {
    await shot(`${brand}/bibliotaph-logo${t}.png`, 2432, 680, `<img src="${svg('bibliotaph-logo' + t)}" width="2432" height="680">`);
    await shot(`${brand}/bibliotaph-monogram${t}.png`, 1024, 1024,
      `<div style="width:1024px;height:1024px;display:grid;place-items:center"><img src="${svg('bibliotaph-monogram' + t)}" width="790" height="896"></div>`);
  }
  await browser.close();
})();
