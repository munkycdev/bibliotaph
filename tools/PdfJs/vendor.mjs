// Copies PDF.js into the web client, which serves it itself: the site's content security policy allows scripts from
// its own origin only. Run it again to move to another version.
// Usage: node tools/PdfJs/vendor.mjs [version]
//
// The worker is patched as the web feasibility spike measured: its last-page check on open looks the last page up
// through a flat /Kids array, and PDF.js then fetches every page object of such a tree at once, so in a book whose page
// objects sit beside their images the whole file downloads before page 1 shows. The check only guards against a wrong
// /Count, so it still runs when PDF.js is recovering a damaged file.
import { execFileSync } from 'node:child_process';
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const version = process.argv[2] ?? '6.4.299';
const target = new URL('../../src/Bibliotaph.Web/wwwroot/lib/pdfjs/', import.meta.url).pathname;
const work = mkdtempSync(join(tmpdir(), 'pdfjs-'));
try {
  execFileSync('npm', ['pack', `pdfjs-dist@${version}`, '--silent'], { cwd: work, stdio: 'inherit', shell: process.platform === 'win32' });
  execFileSync('tar', ['xzf', `pdfjs-dist-${version}.tgz`], { cwd: work });
  const from = join(work, 'package');
  rmSync(target, { recursive: true, force: true });
  mkdirSync(target, { recursive: true });
  // The minified builds, as the spike measured them: the unminified ones take noticeably longer to start.
  cpSync(join(from, 'build', 'pdf.min.mjs'), join(target, 'pdf.min.mjs'));
  cpSync(join(from, 'web', 'pdf_viewer.mjs'), join(target, 'pdf_viewer.mjs'));
  cpSync(join(from, 'web', 'pdf_viewer.css'), join(target, 'pdf_viewer.css'));
  cpSync(join(from, 'web', 'images'), join(target, 'images'), { recursive: true });
  // Fonts a PDF names without embedding, character maps for CJK text, and the image and colour decoders.
  for (const folder of ['standard_fonts', 'cmaps', 'wasm', 'iccs']) cpSync(join(from, folder), join(target, folder), { recursive: true });
  cpSync(join(from, 'LICENSE'), join(target, 'LICENSE'));

  const check = /await this\.ensureDoc\("checkLastPage",\[(\w+)\]\)/g;
  const worker = readFileSync(join(from, 'build', 'pdf.worker.min.mjs'), 'utf8');
  if ((worker.match(check) ?? []).length !== 1) throw new Error('PDF.js changed: the last-page check was not found exactly once');
  writeFileSync(join(target, 'pdf.worker.min.mjs'), worker.replace(check, (call, recoveryMode) => `${recoveryMode}&&${call}`));
  writeFileSync(join(target, 'VERSION'), `pdfjs-dist ${version}, worker patched by tools/PdfJs/vendor.mjs\n`);
  console.log(`PDF.js ${version} copied to ${target}`);
} finally {
  rmSync(work, { recursive: true, force: true });
}
