// A book cache in OPFS: the file is split into fixed chunks so PDF.js range reads can be served from disk,
// and whatever was read once never comes over the network again. Chunk files rather than one sparse file
// because a main-thread OPFS writable with keepExistingData copies the whole file on every close.
export const ChunkSize = 1 << 20;

export class BookCache {
  static async open(key, url) {
    const root = await navigator.storage.getDirectory();
    const books = await root.getDirectoryHandle('books', { create: true });
    const dir = await books.getDirectoryHandle(key, { create: true });
    const cache = new BookCache(dir, url);
    await cache.#load();
    return cache;
  }

  constructor(dir, url) {
    this.dir = dir;
    this.url = url;
    this.size = 0;
    this.have = new Set();
    this.inflight = new Map();
    this.counters = { fromCache: 0, fromNetwork: 0, networkRequests: 0 };
  }

  get complete() { return this.size > 0 && this.have.size === this.chunkCount; }
  get chunkCount() { return Math.ceil(this.size / ChunkSize); }

  async #load() {
    try {
      const manifest = JSON.parse(await (await (await this.dir.getFileHandle('manifest.json')).getFile()).text());
      this.size = manifest.size;
    } catch {
      // Unknown book: ask the server for its length without downloading it.
      const response = await fetch(this.url, { headers: { range: 'bytes=0-0' } });
      this.size = Number(/\/(\d+)$/.exec(response.headers.get('content-range'))[1]);
      await response.body?.cancel();
      await this.#writeFile('manifest.json', JSON.stringify({ size: this.size }));
    }
    for await (const name of this.dir.keys()) if (/^\d+$/.test(name)) this.have.add(Number(name));
  }

  async #writeFile(name, data) {
    const writable = await (await this.dir.getFileHandle(name, { create: true })).createWritable();
    await writable.write(data);
    await writable.close();
  }

  // Bytes [begin, end) from the cache, fetching only the chunks that are missing, in contiguous runs.
  async read(begin, end) {
    const first = Math.floor(begin / ChunkSize), last = Math.floor((end - 1) / ChunkSize);
    const missing = [];
    for (let i = first; i <= last; i++) if (!this.have.has(i) && !this.inflight.has(i)) missing.push(i);
    for (const run of runs(missing)) this.#fetchRun(run[0], run[run.length - 1]);
    for (let i = first; i <= last; i++) this.#promote(i);
    const parts = [];
    for (let i = first; i <= last; i++) parts.push(await this.#chunk(i));
    const joined = concat(parts);
    const offset = begin - first * ChunkSize;
    return joined.subarray(offset, offset + (end - begin));
  }

  async #chunk(i) {
    if (this.inflight.has(i)) return this.inflight.get(i);
    if (!this.have.has(i)) { this.#fetchRun(i, i); return this.inflight.get(i); }
    this.counters.fromCache++;
    const file = await (await this.dir.getFileHandle(String(i))).getFile();
    return new Uint8Array(await file.arrayBuffer());
  }

  // Requests wait in two queues. PDF.js asks for every page object of a flat page tree up front, so the
  // range it needs for the page on screen arrives last: the newest foreground request goes first, and
  // background completion only runs when nothing in the foreground is waiting.
  #fetchRun(from, to, background = false) {
    const job = { from, to, waiters: [] };
    for (let i = from; i <= to; i++) {
      const chunk = new Promise((resolve, reject) => job.waiters.push({ i, resolve, reject }));
      chunk.catch(() => this.inflight.delete(i));
      this.inflight.set(i, chunk);
    }
    (background ? this.background : this.foreground).push(job);
    this.#pump();
  }

  // A chunk the reader needs now may sit in a queued background job; move that job to the front.
  #promote(i) {
    const at = this.background.findIndex(job => job.from <= i && i <= job.to);
    if (at >= 0) this.foreground.push(...this.background.splice(at, 1));
  }

  foreground = [];
  background = [];
  active = 0;
  static MaxActive = 4;

  #pump() {
    while (this.active < BookCache.MaxActive) {
      const job = this.foreground.pop() ?? this.background.shift();
      if (!job) return;
      this.active++;
      this.#run(job).finally(() => { this.active--; this.#pump(); });
    }
  }

  async #run({ from, to, waiters }) {
    const start = from * ChunkSize, end = Math.min((to + 1) * ChunkSize, this.size) - 1;
    this.counters.networkRequests++;
    try {
      const response = await fetch(this.url, { headers: { range: `bytes=${start}-${end}` } });
      if (response.status !== 206) throw new Error(`range read returned ${response.status}`);
      const bytes = new Uint8Array(await response.arrayBuffer());
      for (const { i, resolve } of waiters) {
        const offset = (i - from) * ChunkSize;
        const part = bytes.slice(offset, offset + ChunkSize);
        await this.#writeFile(String(i), part);
        this.have.add(i);
        this.counters.fromNetwork++;
        this.inflight.delete(i);
        resolve(part);
      }
    } catch (e) {
      for (const { reject } of waiters) reject(e);
    }
  }

  // Fetches the rest of the book a few chunks at a time, so it can be read offline later.
  async completeInBackground({ runLength = 8 } = {}) {
    const missing = [];
    for (let i = 0; i < this.chunkCount; i++) if (!this.have.has(i) && !this.inflight.has(i)) missing.push(i);
    for (const run of runs(missing))
      for (let i = 0; i < run.length; i += runLength) {
        const batch = run.slice(i, i + runLength);
        this.#fetchRun(batch[0], batch[batch.length - 1], true);
      }
    await Promise.allSettled([...this.inflight.values()]);
  }

  async bytesOnDisk() {
    let total = 0;
    for await (const handle of this.dir.values()) if (handle.kind === 'file') total += (await handle.getFile()).size;
    return total;
  }
}

function runs(sorted) {
  const out = [];
  for (const i of sorted) {
    const last = out[out.length - 1];
    if (last && last[last.length - 1] === i - 1) last.push(i); else out.push([i]);
  }
  return out;
}

function concat(parts) {
  if (parts.length === 1) return parts[0];
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

export async function clearAll() {
  const root = await navigator.storage.getDirectory();
  try { await root.removeEntry('books', { recursive: true }); } catch { }
}
