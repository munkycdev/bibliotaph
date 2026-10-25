// Drives bench/index.html in Chromium: SHA-256, PDF.js text and first render, then the OPFS book cache,
// including a reopen with the book server unreachable.
// Usage: node tools/pdfbench.mjs <base url> <book>[,<book>...]
// Environment: MBPS and LATENCY (ms) throttle the connection; PATCHED=1 uses the patched PDF.js worker.
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright');

const [base = 'http://localhost:8081/', list = 'text-40.pdf'] = process.argv.slice(2);
const books = list.split(',');
const browser = await chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();
// A shared link like a home connection: CDP throttling covers every request the page makes.
if (process.env.MBPS || process.env.LATENCY) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false, latency: Number(process.env.LATENCY ?? 30),
    downloadThroughput: process.env.MBPS ? Number(process.env.MBPS) * 125000 : -1, uploadThroughput: process.env.MBPS ? 10 * 125000 : -1,
  });
}
page.on('console', m => { if (m.type() === 'error') console.error('page:', m.text()); });
await page.goto(base + (process.env.PATCHED ? '?patched' : ''));
await page.waitForFunction(() => window.benchReady);
const run = (fn, ...args) => page.evaluate(([fn, args]) => window.bench[fn](...args), [fn, args]);
const counters = async (reset) => (await fetch(`${base}__counters${reset ? '?reset' : ''}`)).json();
const results = { storage: await run('storage'), books: {} };

for (const book of books) {
  const url = `${base}books/${book}`;
  const r = results.books[book] = {};
  r.hashWasm = await run('hashWasm', url);
  r.hashSubtle = await run('hashSubtle', url);
  await counters(true);
  r.direct = await run('direct', url);
  r.direct.network = await counters(true);
  await run('clearAll');
  r.cachedCold = await run('cached', book, url, { background: true });
  r.cachedCold.network = await counters(true);
  await page.reload();
  await page.waitForFunction(() => window.benchReady);
  // Nothing may reach the book server now: the reopen must come entirely from OPFS.
  await page.route('**/books/**', route => route.abort());
  r.cachedOffline = await run('cached', book, url);
  r.cachedOffline.network = await counters(true);
  await page.unroute('**/books/**');
  console.error(book, 'done');
}
await browser.close();
console.log(JSON.stringify(results, null, 2));
