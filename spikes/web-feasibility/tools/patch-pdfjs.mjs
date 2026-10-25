// Writes bench/pdf.worker.patched.mjs: PDF.js's worker without the last-page check on open.
// That check looks up the last page through a flat /Kids array, and PDF.js fetches every page object of such a
// tree at once, so in a book whose page objects sit beside their images the whole file downloads before page 1
// shows. The check only guards against a wrong /Count, so it still runs when PDF.js is recovering a damaged file.
// Usage: node tools/patch-pdfjs.mjs
import { readFileSync, writeFileSync } from 'node:fs';
const source = new URL('../bench/node_modules/pdfjs-dist/build/pdf.worker.mjs', import.meta.url);
const target = new URL('../bench/pdf.worker.patched.mjs', import.meta.url);
const before = 'await this.ensureDoc("checkLastPage", [recoveryMode]);';
const text = readFileSync(source, 'utf8');
if (text.split(before).length !== 2) throw new Error('PDF.js changed: the last-page check was not found exactly once');
writeFileSync(target, text.replace(before, `if (recoveryMode) ${before}`));
console.log('wrote', target.pathname);
