// SHA-256 of each test book in .NET inside the worker, to compare with hash-wasm and SubtleCrypto in bench/.
// Usage: node tools/hash.mjs <app base> <books base> <book>[,<book>...]
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright');
const [base = 'http://localhost:8080/', booksBase = 'http://localhost:8081/books/', list = 'text-40.pdf'] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage();
page.on('pageerror', e => console.log('[error]', e.message));
await page.goto(base);
await page.waitForFunction(() => window.spike);
const result = await page.evaluate(async ({ booksBase, books }) => {
  await spike.startWorker();
  const out = {};
  for (const book of books) out[book] = await spike.call('HashBench.Sha256', booksBase + book);
  return out;
}, { booksBase, books: list.split(',') });
console.log(JSON.stringify(result, null, 1));
await browser.close();
