// A static server with Range support, like Blob Storage or a drive's download link, for the spike's pages and books.
// Usage: node tools/serve.mjs <wwwroot> <port> [booksFolder]
// Books are served at /books/<name>, and /__books lists the folder's PDFs. Slow connections are simulated in the browser (tools/pdfbench.mjs), not here.
import { createServer } from 'node:http';
import { createReadStream, statSync, existsSync, readdirSync } from 'node:fs';
import { extname, join, normalize, resolve, sep } from 'node:path';

const [root, port = '8080', books] = process.argv.slice(2);
const types = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm',
  '.css': 'text/css', '.dll': 'application/octet-stream', '.dat': 'application/octet-stream', '.pdf': 'application/pdf',
  '.webmanifest': 'application/manifest+json', '.map': 'application/json',
};
export const counters = { requests: 0, bytes: 0 };

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (url.pathname === '/__counters') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(counters));
    if (url.searchParams.has('reset')) { counters.requests = 0; counters.bytes = 0; }
    return;
  }
  if (url.pathname === '/__books') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(books ? readdirSync(books).filter(name => extname(name).toLowerCase() === '.pdf') : []));
    return;
  }
  const isBook = url.pathname.startsWith('/books/') && books;
  const base = resolve(isBook ? books : root);
  let path = resolve(base, '.' + sep + normalize(decodeURIComponent(isBook ? url.pathname.slice(7) : url.pathname)));
  // Nothing outside the two folders is served, whatever the request's path says.
  if (!path.startsWith(base + sep) || !existsSync(path) || statSync(path).isDirectory()) path = join(root, 'index.html');
  const size = statSync(path).size;
  const headers = {
    'content-type': types[extname(path)] ?? 'application/octet-stream', 'accept-ranges': 'bytes', 'cache-control': 'no-store',
    'access-control-allow-origin': '*', 'access-control-expose-headers': 'content-range, accept-ranges, content-length',
  };
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range ?? '');
  // Only book traffic is counted, so a test can tell what a read cost without the page's own files.
  if (isBook) counters.requests++;
  if (range) {
    const start = range[1] === '' ? size - Number(range[2]) : Number(range[1]);
    const end = range[1] !== '' && range[2] !== '' ? Math.min(Number(range[2]), size - 1) : size - 1;
    if (isBook) counters.bytes += end - start + 1;
    res.writeHead(206, { ...headers, 'content-range': `bytes ${start}-${end}/${size}`, 'content-length': end - start + 1 });
    createReadStream(path, { start, end }).pipe(res);
  } else {
    if (isBook) counters.bytes += size;
    res.writeHead(200, { ...headers, 'content-length': size });
    createReadStream(path).pipe(res);
  }
}).listen(Number(port), () => console.log(`serving ${root} on ${port}`));
