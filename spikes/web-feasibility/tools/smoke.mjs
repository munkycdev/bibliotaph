// First check: the worker starts, SQLite answers, and the real index schema and search run.
import { createRequire } from 'node:module';
// Playwright is installed globally in the spike's container, so it is found through require rather than import.
const { chromium } = createRequire(import.meta.url)('playwright');
const base = process.argv[2] ?? 'http://localhost:8080/';
const browser = await chromium.launch();
const page = await browser.newPage();
page.on('console', m => console.log('[page]', m.text()));
await page.goto(base);
await page.waitForFunction(() => window.spike);
const r = await page.evaluate(async () => {
  const out = {};
  out.start = await spike.startWorker();
  out.sqlite = await spike.call('IndexBench.Sqlite');
  out.open = await spike.call('IndexBench.Open', '/index.db');
  out.add = await spike.call('IndexBench.AddBooks', 1, 20, 100);
  out.stats = await spike.call('IndexBench.Stats', '/index.db');
  out.search = await spike.call('IndexBench.Search', 'owlbear tavern');
  return out;
});
console.log(JSON.stringify(r, null, 1));
await browser.close();
