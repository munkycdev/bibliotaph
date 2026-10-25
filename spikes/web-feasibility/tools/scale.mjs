// Third check: a library about the size of Dave's (500 books, 100 pages each) in OPFS, then the searches people type.
// Usage: node tools/scale.mjs [base] [books] [pages] [opfs|memory]
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright');
const [base = 'http://localhost:8080/', books = '500', pages = '100', where = 'opfs'] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage();
page.on('pageerror', e => console.log('[error]', e.message));
await page.goto(base);
await page.waitForFunction(() => window.spike);
const result = await page.evaluate(async ({ books, pages, where }) => {
  const path = where === 'opfs' ? '/opfs/index.db' : '/index.db';
  const out = { where, books, pages };
  await spike.startWorker();
  await spike.call('IndexBench.RemoveOpfs');
  out.open = await spike.call('IndexBench.Open', path);
  const started = performance.now();
  for (let first = 1; first <= books; first += 50) await spike.call('IndexBench.AddBooks', first, Math.min(50, books - first + 1), pages);
  out.addMs = Math.round(performance.now() - started);
  out.stats = await spike.call('IndexBench.Stats', path);
  out.searches = [];
  for (const q of await spike.call('IndexBench.Queries'))
    for (const run of ['cold', 'warm']) out.searches.push({ run, ...await spike.call('IndexBench.Search', q) });
  return out;
}, { books: Number(books), pages: Number(pages), where });
console.log(JSON.stringify(result, null, 1));
await browser.close();
