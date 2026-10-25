// Second check: index.db lives in OPFS through the custom VFS and survives a page reload.
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright');
const base = process.argv[2] ?? 'http://localhost:8080/';
const browser = await chromium.launch();
const context = await browser.newContext();
const page = await context.newPage();
page.on('console', m => console.log('[page]', m.text()));
page.on('pageerror', e => console.log('[error]', e.message));
await page.goto(base);
await page.waitForFunction(() => window.spike);
const first = await page.evaluate(async () => {
  const out = {};
  await spike.startWorker();
  out.reset = await spike.call('IndexBench.RemoveOpfs');
  out.open = await spike.call('IndexBench.Open', '/opfs/index.db');
  out.add = await spike.call('IndexBench.AddBooks', 1, 20, 100);
  out.search = await spike.call('IndexBench.Search', 'owlbear tavern');
  out.close = await spike.call('IndexBench.Close');
  out.bytes = await spike.call('IndexBench.OpfsBytes');
  return out;
});
console.log('first visit', JSON.stringify(first));
await page.reload();
await page.waitForFunction(() => window.spike);
const second = await page.evaluate(async () => {
  const out = {};
  await spike.startWorker();
  out.open = await spike.call('IndexBench.Open', '/opfs/index.db');
  out.stats = await spike.call('IndexBench.Stats', '/opfs/index.db');
  out.search = await spike.call('IndexBench.Search', 'owlbear tavern');
  out.add = await spike.call('IndexBench.AddBooks', 21, 5, 100);
  out.stats2 = await spike.call('IndexBench.Stats', '/opfs/index.db');
  return out;
});
console.log('after reload', JSON.stringify(second));
await browser.close();
