// What a web Bibliotaph needs from OneDrive, measured from the browser against a real drive: signing in with no
// server, listing a library folder (by walking it, and by delta, which also stands in for a folder watcher), reading
// byte ranges of a book without downloading it, PDF.js over those reads, downloading a whole book to hash it, and
// writing Bibliotaph's own files to its app folder rather than anywhere among the user's files.
// Results hold counts and timings only; names stay in this page's memory.
import * as pdfjs from '/node_modules/pdfjs-dist/build/pdf.min.mjs';

pdfjs.GlobalWorkerOptions.workerSrc = '/pdf.worker.patched.mjs';
const worker = new pdfjs.PDFWorker();

const Graph = 'https://graph.microsoft.com/v1.0';
const ReadScopes = ['User.Read', 'Files.Read'];
// Consented to at sign-in, so its token comes silently later; requested on its own, so the read tests never need it.
const AppFolderScopes = ['Files.ReadWrite.AppFolder'];
const Select = 'id,name,size,lastModifiedDateTime,file,folder,parentReference,deleted';
const ChunkSize = 1 << 20;

const $ = id => document.getElementById(id);
const now = () => performance.now();
const ms = t => Math.round(now() - t);
const mb = bytes => Math.round(bytes / 1e5) / 10;
const sleep = time => new Promise(resolve => setTimeout(resolve, time));
const median = values => values.length ? [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)] : null;

const results = { startedAt: new Date().toISOString(), browser: navigator.userAgentData?.brands?.map(b => `${b.brand} ${b.version}`).join(', ') ?? navigator.userAgent };
const state = { folderId: null, files: null, deltaLink: null, deltaScope: null, parents: new Map() };
const counters = { graphRequests: 0, throttled: 0 };

function log(message) {
  $('log').textContent += `${new Date().toLocaleTimeString()} ${message}\n`;
}

function save(name, value) {
  results[name] = value;
  $('results').textContent = JSON.stringify(results, null, 2);
}

function remember(id) {
  try { localStorage.setItem(`spike.${id}`, $(id).value); } catch { /* private window */ }
}

for (const id of ['clientId', 'folder', 'sample']) {
  try { $(id).value = localStorage.getItem(`spike.${id}`) ?? $(id).value; } catch { /* private window */ }
  $(id).addEventListener('change', () => remember(id));
}

// --- Signing in ---

let client;
let account = null;

async function msalClient() {
  const clientId = $('clientId').value.trim();
  if (!clientId) throw new Error('Paste the application (client) ID first');
  if (client?.clientId !== clientId) {
    const pca = new msal.PublicClientApplication({
      // common: work, school and personal accounts alike, as any user of bibliotaph.app would be.
      auth: { clientId, authority: 'https://login.microsoftonline.com/common', redirectUri: `${location.origin}/auth.html` },
      cache: { cacheLocation: 'localStorage' },
    });
    await pca.initialize();
    client = { pca, clientId };
    account = pca.getAllAccounts()[0] ?? null;
    showAccount();
  }
  return client.pca;
}

function showAccount() {
  $('who').textContent = account ? `Signed in as ${account.username}` : 'Not signed in';
}

async function token(scopes = ReadScopes, { forceRefresh = false } = {}) {
  const pca = await msalClient();
  if (!account) throw new Error('Sign in first');
  try {
    return (await pca.acquireTokenSilent({ scopes, account, forceRefresh })).accessToken;
  } catch (e) {
    if (!(e instanceof msal.InteractionRequiredAuthError)) throw e;
    log(`Silent token refused (${e.errorCode}); asking in a popup`);
    return (await pca.acquireTokenPopup({ scopes, account })).accessToken;
  }
}

$('signIn').onclick = () => run('signIn', async () => {
  const pca = await msalClient();
  const t = now();
  const result = await pca.loginPopup({ scopes: ReadScopes, extraScopesToConsent: AppFolderScopes, prompt: 'select_account' });
  account = result.account;
  showAccount();
  // The consumer tenant: a personal Microsoft account rather than a work or school one.
  return { ms: ms(t), accountKind: account.tenantId === '9188040d-6c67-4c5b-b112-36a304b66dad' ? 'personal' : 'work or school' };
});

$('signOut').onclick = async () => {
  const pca = await msalClient();
  await pca.clearCache();
  account = null;
  showAccount();
};

// --- Graph ---

function errorCode(e) {
  try { return `${e.status} ${JSON.parse(e.body).error.code}`; } catch { return e.status ? `${e.status}` : e.message; }
}

async function graph(path, { method = 'GET', body, headers = {}, scopes, raw = false } = {}) {
  const url = path.startsWith('https://') ? path : Graph + path;
  for (let attempt = 0; ; attempt++) {
    const authorization = `Bearer ${await token(scopes)}`;
    counters.graphRequests++;
    const response = await fetch(url, { method, body, headers: { Authorization: authorization, ...headers } });
    if ((response.status === 429 || response.status === 503) && attempt < 5) {
      counters.throttled++;
      await sleep((Number(response.headers.get('retry-after')) || 2 ** attempt) * 1000);
      continue;
    }
    if (!response.ok) {
      const text = await response.text();
      throw Object.assign(new Error(`${method} ${response.status}: ${text.slice(0, 300)}`), { status: response.status, body: text });
    }
    if (raw) return response;
    return response.status === 204 ? null : response.json();
  }
}

async function folderId() {
  if (state.folderId) return state.folderId;
  const path = $('folder').value.trim().replace(/^\/+|\/+$/g, '');
  const item = await graph(path ? `/me/drive/root:/${path.split('/').map(encodeURIComponent).join('/')}?$select=id,folder` : '/me/drive/root?$select=id,folder');
  state.folderId = item.id;
  return item.id;
}
$('folder').addEventListener('change', () => Object.assign(state, { folderId: null, files: null, deltaLink: null }));

function fileOf(item) {
  return { id: item.id, name: item.name, size: item.size ?? 0, isPdf: /\.pdf$/i.test(item.name), hashes: Object.keys(item.file?.hashes ?? {}) };
}

function summarize(files) {
  const pdfs = files.filter(f => f.isPdf);
  const hashTypes = {};
  for (const file of files) for (const hash of file.hashes) hashTypes[hash] = (hashTypes[hash] ?? 0) + 1;
  return {
    files: files.length, totalMB: mb(files.reduce((sum, f) => sum + f.size, 0)),
    pdfs: pdfs.length, pdfMB: mb(pdfs.reduce((sum, f) => sum + f.size, 0)), largestPdfMB: mb(Math.max(0, ...pdfs.map(f => f.size))),
    hashTypes,
  };
}

// --- Tests ---

const tests = {
  async drive() {
    const t = now();
    const drive = await graph('/me/drive?$select=id,driveType,quota');
    await folderId();
    return { driveType: drive.driveType, usedGB: Math.round((drive.quota?.used ?? 0) / 1e8) / 10, ms: ms(t) };
  },

  // Every folder's children, eight folders at a time: what a first scan costs.
  async walk() {
    const t = now();
    const before = counters.graphRequests;
    const root = await folderId();
    const files = [];
    let folders = 0;
    const queue = [root];
    let active = 0;
    await new Promise((resolve, reject) => {
      const pump = () => {
        if (!queue.length && !active) return resolve();
        while (queue.length && active < 8) {
          const id = queue.shift();
          active++;
          children(id).then(items => {
            for (const item of items) {
              if (item.folder) { folders++; queue.push(item.id); } else if (item.file) files.push(fileOf(item));
            }
            active--;
            pump();
          }, reject);
        }
      };
      pump();
    });
    state.files = files;
    return { ms: ms(t), requests: counters.graphRequests - before, folders, ...summarize(files) };
  },

  // The folder's delta, which personal drives give for any folder and business drives only for the root; the root's
  // is then filtered to the folder by following parent IDs, since a business drive's delta leaves out paths.
  async delta() {
    const t = now();
    const before = counters.graphRequests;
    const root = await folderId();
    let scope = 'folder';
    let read;
    try {
      read = await readDelta(`/me/drive/items/${root}/delta?$select=${Select}`);
    } catch (e) {
      if (!e.status || e.status >= 500) throw e;
      log(`Folder delta refused (${errorCode(e)}); using the drive root's`);
      scope = 'root';
      read = await readDelta(`/me/drive/root/delta?$select=${Select}`);
    }
    const files = read.items.filter(i => i.file && !i.deleted && (scope === 'folder' || isUnder(i, root))).map(fileOf);
    Object.assign(state, { deltaLink: read.deltaLink, deltaScope: scope });
    state.files ??= files;
    return {
      scope, ms: ms(t), requests: counters.graphRequests - before, itemsInDelta: read.items.length, ...summarize(files),
      sameCountAsWalk: results.walk?.files === undefined ? null : results.walk.files === files.length,
    };
  },

  // The delta again from where the last one ended: what a watcher would poll. Change a file in the folder first.
  async changes() {
    if (!state.deltaLink) await tests.delta();
    const t = now();
    const read = await readDelta(state.deltaLink);
    state.deltaLink = read.deltaLink;
    const root = await folderId();
    const mine = read.items.filter(i => state.deltaScope === 'folder' || isUnder(i, root));
    return { ms: ms(t), scope: state.deltaScope, itemsInDrive: read.items.length, itemsInFolder: mine.length, files: mine.filter(i => i.file).length, deleted: mine.filter(i => i.deleted).length };
  },

  // Which ways of reading a book the browser allows, on the smallest sample. A failed fetch here is CORS: the
  // console names the reason (no Access-Control-Allow-Origin, or a refused preflight), which a page can't see.
  async cors() {
    const file = (await sample())[0];
    const url = await downloadUrl(file.id);
    const link = new URL(url);
    const content = `/me/drive/items/${file.id}/content`;
    const range = { Range: 'bytes=0-65535' };
    const result = {
      linkHost: link.hostname.endsWith('.sharepoint.com') ? 'sharepoint' : link.hostname.endsWith('.1drv.com') ? '1drv' : 'other',
      linkPage: link.pathname.split('/').pop(), linkHasTempauth: link.searchParams.has('tempauth'),
    };
    const tries = {
      linkPlain: () => fetch(url),
      linkRange: () => fetch(url, { headers: range }),
      linkRangeSuffix: () => fetch(url, { headers: { Range: 'bytes=0-' } }),
      contentPlain: () => graph(content, { raw: true }),
      contentRange: () => graph(content, { headers: range, raw: true }),
    };
    for (const [name, attempt] of Object.entries(tries)) {
      const t = now();
      try {
        const response = await attempt();
        const reader = response.body.getReader();
        const first = await reader.read();
        await reader.cancel();
        result[name] = { status: response.status, ms: ms(t), firstBytes: first.value?.length ?? 0, contentRangeVisible: response.headers.get('content-range') !== null };
      } catch (e) {
        result[name] = { error: e.status ? errorCode(e) : e.message, ms: ms(t) };
      }
    }
    return result;
  },

  // A book's download link, then ranges of it as PDF.js would ask: the start, a middle chunk and the last megabyte.
  async ranges() {
    const samples = [];
    for (const file of await sample()) {
      const t = now();
      const url = await downloadUrl(file.id);
      const linkMs = ms(t);
      try {
        const head = await rangeFetch(url, 0, 65535);
        const middle = await rangeFetch(url, Math.floor(file.size / 2), Math.floor(file.size / 2) + ChunkSize - 1);
        const tail = await rangeFetch(url, Math.max(0, file.size - ChunkSize), file.size - 1);
        samples.push({ sizeMB: mb(file.size), linkMs, head: head.summary, middle: middle.summary, tail: tail.summary });
      } catch (e) {
        throw new Error(`a range read of a link failed (${e.message}); see the cors test`);
      }
    }
    // Whether the content endpoint can be read directly, without first asking for the link.
    let direct;
    try {
      const file = (await sample())[0];
      const t = now();
      const response = await graph(`/me/drive/items/${file.id}/content`, { headers: { Range: 'bytes=0-65535' }, raw: true });
      direct = { status: response.status, bytes: (await response.arrayBuffer()).byteLength, ms: ms(t) };
    } catch (e) {
      direct = { error: errorCode(e) };
    }
    return {
      samples, contentEndpointWithRange: direct,
      medianLinkMs: median(samples.map(s => s.linkMs)), medianHeadMs: median(samples.map(s => s.head.ms)),
      medianChunkMs: median(samples.map(s => s.middle.ms)), allPartial: samples.every(s => [s.head, s.middle, s.tail].every(r => r.status === 206)),
    };
  },

  // PDF.js reading each sample through range requests on its download link, with the patched worker.
  async pdfjs() {
    const books = [];
    for (const file of await sample()) books.push(await openPdf(file));
    return {
      books, medianOpenMs: median(books.map(b => b.openMs)), medianFirstRenderMs: median(books.map(b => b.firstRenderMs)),
      medianMBForFirstPage: median(books.map(b => b.firstPageMB)),
    };
  },

  // The median sample downloaded whole and hashed: what indexing pays for each book once.
  async hash() {
    const files = await sample();
    const file = files[Math.floor(files.length / 2)];
    const url = await downloadUrl(file.id);
    const t = now();
    const response = await fetch(url);
    const reader = response.body.getReader();
    const parts = [];
    let bytes = 0;
    for (let read; !(read = await reader.read()).done;) { parts.push(read.value); bytes += read.value.length; }
    const downloadMs = ms(t);
    const all = new Uint8Array(bytes);
    let at = 0;
    for (const part of parts) { all.set(part, at); at += part.length; }
    const h = now();
    await crypto.subtle.digest('SHA-256', all);
    return { sizeMB: mb(bytes), downloadMs, mbPerSecond: Math.round(bytes / 1e3 / downloadMs * 10) / 10, sha256Ms: ms(h) };
  },

  // Bibliotaph's own files (per-book text, sync files) belong in its app folder, so it never needs to write
  // anywhere among the user's files. Writes one small file there, reads it back, and deletes it.
  async appFolder() {
    const t = now();
    try {
      await token(AppFolderScopes);
    } catch (e) {
      return { consent: 'refused', error: e.errorCode ?? e.message };
    }
    const result = { consentMs: ms(t) };
    const options = { scopes: AppFolderScopes };
    try {
      await graph('/me/drive/special/approot?$select=id', options);
      result.appFolder = 'ok';
      const w = now();
      const body = JSON.stringify({ spike: true, at: new Date().toISOString() });
      const item = await graph('/me/drive/special/approot:/spike-test.json:/content', { ...options, method: 'PUT', body, headers: { 'Content-Type': 'application/json' } });
      result.writeMs = ms(w);
      const r = now();
      const back = await (await graph(`/me/drive/items/${item.id}/content`, { ...options, raw: true })).text();
      result.readBackMs = ms(r);
      result.readBackMatches = back === body;
      await graph(`/me/drive/items/${item.id}`, { ...options, method: 'DELETE' });
      result.deleted = true;
    } catch (e) {
      result.error = errorCode(e);
    }
    return result;
  },

  // A new access token without the user, as the app needs every hour.
  async refresh() {
    const t = now();
    await token(ReadScopes, { forceRefresh: true });
    return { ms: ms(t) };
  },
};

async function readDelta(url) {
  const items = [];
  for (;;) {
    const page = await graph(url);
    for (const item of page.value) {
      items.push(item);
      if (item.parentReference?.id) state.parents.set(item.id, item.parentReference.id);
    }
    if (page['@odata.deltaLink']) return { items, deltaLink: page['@odata.deltaLink'] };
    url = page['@odata.nextLink'];
  }
}

function isUnder(item, root) {
  for (let id = item.parentReference?.id, hops = 0; id && hops < 64; id = state.parents.get(id), hops++)
    if (id === root) return true;
  return false;
}

async function children(id) {
  const items = [];
  for (let url = `/me/drive/items/${id}/children?$select=${Select}`; url;) {
    const page = await graph(url);
    items.push(...page.value);
    url = page['@odata.nextLink'];
  }
  return items;
}

async function sample() {
  if (!state.files) await tests.walk();
  const pdfs = state.files.filter(f => f.isPdf && f.size > 0).sort((a, b) => a.size - b.size);
  const count = Math.min(pdfs.length, Math.max(1, Number($('sample').value) || 6));
  if (!count) throw new Error('No PDFs in that folder');
  // Spread across the sizes, smallest to largest.
  return Array.from({ length: count }, (_, i) => pdfs[count === 1 ? pdfs.length - 1 : Math.round(i * (pdfs.length - 1) / (count - 1))]);
}

async function downloadUrl(id) {
  return (await graph(`/me/drive/items/${id}?$select=id,@microsoft.graph.downloadUrl`))['@microsoft.graph.downloadUrl'];
}

async function rangeFetch(url, start, end) {
  const t = now();
  const response = await fetch(url, { headers: { Range: `bytes=${start}-${end}` } });
  const data = new Uint8Array(await response.arrayBuffer());
  return { data, summary: { status: response.status, ms: ms(t), bytes: data.length, contentRangeVisible: response.headers.get('content-range') !== null } };
}

async function openPdf(file) {
  let url = await downloadUrl(file.id);
  const traffic = { requests: 0, bytes: 0, linkRefreshes: 0 };
  const transport = new pdfjs.PDFDataRangeTransport(file.size, null);
  // A range that can't be read would leave PDF.js waiting for ever, so it fails the book instead.
  let fail;
  const failure = new Promise((_, reject) => { fail = reject; });
  const guard = promise => Promise.race([promise, failure]);
  transport.requestDataRange = async (begin, end) => {
    try {
      let response = await fetch(url, { headers: { Range: `bytes=${begin}-${end - 1}` } });
      // A download link lasts about an hour; a book open longer asks for a new one.
      if (response.status === 401 || response.status === 403) {
        traffic.linkRefreshes++;
        url = await downloadUrl(file.id);
        response = await fetch(url, { headers: { Range: `bytes=${begin}-${end - 1}` } });
      }
      const data = new Uint8Array(await response.arrayBuffer());
      traffic.requests++;
      traffic.bytes += data.length;
      transport.onDataRange(begin, data);
    } catch (e) {
      log(`Range ${begin}-${end} failed: ${e.message}`);
      fail(e);
    }
  };
  const t = now();
  const task = pdfjs.getDocument({ worker, range: transport, rangeChunkSize: ChunkSize, disableAutoFetch: true });
  try {
    return await guard(readPdf(task, file, traffic, t, guard));
  } finally {
    await task.destroy();
  }
}

async function readPdf(task, file, traffic, t, guard) {
  const doc = await guard(task.promise);
  const openMs = ms(t);
  const page = await guard(doc.getPage(1));
  const viewport = page.getViewport({ scale: 1 });
  const canvas = $('page');
  canvas.width = viewport.width;
  canvas.height = viewport.height;
  await guard(page.render({ canvas, viewport }).promise);
  const firstRenderMs = ms(t);
  const firstPageMB = mb(traffic.bytes);
  const x = now();
  let chars = 0;
  for (let n = 1; n <= doc.numPages; n++) {
    const p = await guard(doc.getPage(n));
    for (const item of (await guard(p.getTextContent())).items) chars += item.str?.length ?? 0;
    p.cleanup();
  }
  return { sizeMB: mb(file.size), pages: doc.numPages, openMs, firstRenderMs, firstPageMB, allTextMs: ms(x), allTextMB: mb(traffic.bytes), chars, ...traffic };
}

async function run(name, test) {
  log(`${name}…`);
  try {
    const value = await test();
    save(name, value);
    log(`${name} done`);
  } catch (e) {
    save(name, { error: errorCode(e) });
    log(`${name} failed: ${e.message}`);
  }
  save('graph', { ...counters });
}

for (const button of document.querySelectorAll('[data-test]')) button.onclick = () => run(button.dataset.test, tests[button.dataset.test]);

$('runAll').onclick = async () => {
  for (const name of ['drive', 'walk', 'delta', 'cors', 'hash', 'appFolder', 'refresh', 'ranges', 'pdfjs']) await run(name, tests[name]);
  log('All done. Copy the results and paste them back.');
};

$('copy').onclick = () => navigator.clipboard.writeText(JSON.stringify(results, null, 2)).then(() => log('Results copied'));

msalClient().catch(() => showAccount());
