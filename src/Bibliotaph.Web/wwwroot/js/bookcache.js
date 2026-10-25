// A book's bytes as the reader asks for them: byte ranges read straight from the user's storage through a short-lived
// link, and kept in the browser's private storage (OPFS) in 1 MB chunks, so a page read once never comes over the
// network again. Chunk files rather than one sparse file because a main-thread OPFS writable with keepExistingData
// copies the whole file on every close. Without OPFS (a private window, say), chunks are kept for this page only.
export const ChunkSize = 1 << 20;

export class BookCache {
  /**
   * @param {string} key the file's SHA-256, so a cached copy is never another version's
   * @param {number} size the file's length, which the storage's ranged answers don't let the page read
   * @param {() => Promise<string|null>} link the file's link now: asked for at first and again whenever reading fails
   */
  static async open(key, size, link) {
    const cache = new BookCache(key, size, link);
    await cache.#load();
    return cache;
  }

  constructor(key, size, link) {
    this.key = key;
    this.size = size;
    this.link = link;
    this.url = null;
    this.dir = null;
    this.have = new Set();
    this.memory = new Map();
    this.inflight = new Map();
  }

  get chunkCount() { return Math.ceil(this.size / ChunkSize); }

  async #load() {
    try {
      const root = await navigator.storage.getDirectory();
      const books = await root.getDirectoryHandle('books', { create: true });
      this.dir = await books.getDirectoryHandle(this.key, { create: true });
      for await (const name of this.dir.keys()) if (/^\d+$/.test(name)) this.have.add(Number(name));
    } catch {
      this.dir = null;
    }
  }

  // Bytes [begin, end), fetching only the chunks that are missing, in contiguous runs.
  async read(begin, end) {
    const first = Math.floor(begin / ChunkSize), last = Math.floor((end - 1) / ChunkSize);
    const missing = [];
    for (let i = first; i <= last; i++) if (!this.#held(i) && !this.inflight.has(i)) missing.push(i);
    for (const run of runs(missing)) this.#fetchRun(run[0], run[run.length - 1]);
    const parts = [];
    for (let i = first; i <= last; i++) parts.push(await this.#chunk(i));
    const joined = concat(parts);
    const offset = begin - first * ChunkSize;
    return joined.subarray(offset, offset + (end - begin));
  }

  #held(i) { return this.have.has(i) || this.memory.has(i); }

  async #chunk(i) {
    if (this.inflight.has(i)) return this.inflight.get(i);
    if (this.memory.has(i)) return this.memory.get(i);
    if (!this.have.has(i)) { this.#fetchRun(i, i); return this.inflight.get(i); }
    try {
      const file = await (await this.dir.getFileHandle(String(i))).getFile();
      return new Uint8Array(await file.arrayBuffer());
    } catch {
      // Cleared meanwhile, as by the browser freeing space: read it again.
      this.have.delete(i);
      this.#fetchRun(i, i);
      return this.inflight.get(i);
    }
  }

  // PDF.js asks for every page object of a flat page tree up front, so the range it needs for the page on screen
  // arrives last: the newest request goes first.
  #fetchRun(from, to) {
    const job = { from, to, waiters: [] };
    for (let i = from; i <= to; i++) {
      const chunk = new Promise((resolve, reject) => job.waiters.push({ i, resolve, reject }));
      chunk.catch(() => this.inflight.delete(i));
      this.inflight.set(i, chunk);
    }
    this.queue.push(job);
    this.#pump();
  }

  queue = [];
  active = 0;
  static MaxActive = 4;

  #pump() {
    while (this.active < BookCache.MaxActive) {
      const job = this.queue.pop();
      if (!job) return;
      this.active++;
      this.#run(job).finally(() => { this.active--; this.#pump(); });
    }
  }

  async #run({ from, to, waiters }) {
    const start = from * ChunkSize, end = Math.min((to + 1) * ChunkSize, this.size) - 1;
    try {
      const bytes = await this.#fetch(start, end);
      for (const { i, resolve } of waiters) {
        const offset = (i - from) * ChunkSize;
        const part = bytes.slice(offset, offset + ChunkSize);
        await this.#keep(i, part);
        this.inflight.delete(i);
        resolve(part);
      }
    } catch (e) {
      for (const { reject } of waiters) reject(e);
    }
  }

  // The link lasts about an hour, so a read that fails asks for a new one and tries once more.
  async #fetch(start, end) {
    for (let attempt = 0; ; attempt++) {
      if (!this.url || attempt > 0) {
        this.url = await this.link();
        if (!this.url) throw new LinkError();
      }
      try {
        const response = await fetch(this.url, { headers: { range: `bytes=${start}-${end}` }, credentials: 'omit' });
        if (response.status === 206) return new Uint8Array(await response.arrayBuffer());
        await response.body?.cancel();
        if (attempt > 0) throw new Error(`range read returned ${response.status}`);
      } catch (e) {
        if (attempt > 0 || e instanceof LinkError) throw e;
      }
    }
  }

  async #keep(i, part) {
    if (this.dir) {
      try {
        const writable = await (await this.dir.getFileHandle(String(i), { create: true })).createWritable();
        await writable.write(part);
        await writable.close();
        this.have.add(i);
        return;
      } catch {
        // Out of space: this sitting keeps it.
      }
    }
    this.memory.set(i, part);
  }
}

/** The file's link couldn't be had: it has moved, or its drive needs connecting again. */
export class LinkError extends Error {
  constructor() { super('no link to the file'); }
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
