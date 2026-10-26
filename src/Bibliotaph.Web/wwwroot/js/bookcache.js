// A book's bytes as the reader asks for them: byte ranges read straight from the user's storage through a short-lived
// link, and kept in the browser's private storage (OPFS) in 1 MB chunks, so a page read once never comes over the
// network again. Chunk files rather than one sparse file because a main-thread OPFS writable with keepExistingData
// copies the whole file on every close. Without OPFS (a private window, say), chunks are kept for this page only.
//
// Each book's folder has a manifest: its size, title and card, when it was last read, and whether it is kept offline.
// Books not kept offline are let go, least recently read first, once all of them take more than the limit Settings sets
// for this browser.
export const ChunkSize = 1 << 20;

const LimitKey = 'bibliotaph.reader.cacheLimit';
export const DefaultLimit = 2 * 1024 ** 3;

export class BookCache {
  /**
   * @param {string} key the file's SHA-256, so a cached copy is never another version's
   * @param {number} size the file's length, which the storage's ranged answers don't let the page read
   * @param {() => Promise<string|null>} link the file's link now: asked for at first and again whenever reading fails
   * @param {{title: string, entryId: number, documentId: number}} about what Settings lists it as
   */
  static async open(key, size, link, about) {
    const cache = new BookCache(key, size, link);
    await cache.#load(about);
    return cache;
  }

  constructor(key, size, link) {
    this.key = key;
    this.size = size;
    this.link = link;
    this.url = null;
    this.dir = null;
    this.manifest = null;
    this.have = new Set();
    this.memory = new Map();
    this.inflight = new Map();
  }

  get chunkCount() { return Math.ceil(this.size / ChunkSize); }
  get complete() { return this.size > 0 && this.have.size === this.chunkCount; }
  get stored() { return this.dir != null; }
  get kept() { return this.manifest?.keep === true; }

  async #load(about) {
    try {
      const books = await booksFolder(true);
      this.dir = await books.getDirectoryHandle(this.key, { create: true });
      for await (const name of this.dir.keys()) if (/^\d+$/.test(name)) this.have.add(Number(name));
      const before = await readManifest(this.dir);
      this.manifest = { ...before, size: this.size, ...about, read: Date.now(), keep: before?.keep === true };
      await writeJson(this.dir, 'manifest.json', this.manifest);
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
    for (let i = first; i <= last; i++) this.#promote(i);
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

  // Requests wait in two queues. PDF.js asks for every page object of a flat page tree up front, so the range it needs
  // for the page on screen arrives last: the newest request goes first, and keeping the book offline only reads on
  // while nothing on screen is waiting.
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

  // A chunk the reader needs now may sit in a queued background job; it moves to the front.
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

  /** How much of the book is here, from 0 to 1. */
  get progress() { return this.chunkCount === 0 ? 1 : this.have.size / this.chunkCount; }

  /**
   * Keeps the whole book in this browser, so it opens and turns pages without waiting on the network, and leaves it out
   * of what the limit lets go; or lets it go with the rest again. Reads the rest a few chunks at a time, telling
   * <paramref name="progress"/> as it goes.
   */
  async keepOffline(on, progress) {
    if (!this.dir) return false;
    this.manifest = { ...this.manifest, keep: on };
    await writeJson(this.dir, 'manifest.json', this.manifest);
    if (!on) {
      this.close();
      return true;
    }
    // Asked once: a browser that grants it won't clear the books it keeps when space runs low.
    try { await navigator.storage.persist?.(); } catch { }
    const missing = [];
    for (let i = 0; i < this.chunkCount; i++) if (!this.have.has(i) && !this.inflight.has(i)) missing.push(i);
    for (const run of runs(missing))
      for (let i = 0; i < run.length; i += 8) {
        const batch = run.slice(i, i + 8);
        this.#fetchRun(batch[0], batch[batch.length - 1], true);
      }
    const waiting = [...this.inflight.values()];
    for (const chunk of waiting) chunk.then(() => progress?.(this.progress), () => { });
    const results = await Promise.allSettled(waiting);
    return results.every(r => r.status === 'fulfilled') && this.complete;
  }

  /** What was only reading on for Keep offline stops: the book closed, or isn't to be kept after all. */
  close() {
    const closed = new DOMException('the book closed', 'AbortError');
    for (const { waiters } of this.background.splice(0)) for (const { reject } of waiters) reject(closed);
  }
}

/** The file's link couldn't be had: it has moved, or its drive needs connecting again. */
export class LinkError extends Error {
  constructor() { super('no link to the file'); }
}

async function booksFolder(create) {
  const root = await navigator.storage.getDirectory();
  return root.getDirectoryHandle('books', { create });
}

async function readManifest(dir) {
  try {
    return JSON.parse(await (await (await dir.getFileHandle('manifest.json')).getFile()).text());
  } catch {
    return null;
  }
}

async function writeJson(dir, name, value) {
  const writable = await (await dir.getFileHandle(name, { create: true })).createWritable();
  await writable.write(JSON.stringify(value));
  await writable.close();
}

/** The most this browser keeps of books not kept offline, as Settings sets it. */
export function limit() {
  try {
    const value = Number(localStorage.getItem(LimitKey));
    return value > 0 ? value : DefaultLimit;
  } catch {
    return DefaultLimit;
  }
}

export function setLimit(bytes) {
  try { localStorage.setItem(LimitKey, String(bytes)); } catch { /* the default stands */ }
}

/** Every book kept in this browser: its card, how much of it is here, when it was read and whether it's kept offline. */
export async function books() {
  let folder;
  try {
    folder = await booksFolder(false);
  } catch {
    return [];
  }
  const list = [];
  for await (const [key, dir] of folder.entries()) {
    if (dir.kind !== 'directory') continue;
    const manifest = await readManifest(dir) ?? {};
    let chunks = 0, last = false;
    for await (const name of dir.keys()) if (/^\d+$/.test(name)) { chunks++; if (manifest.size && Number(name) === Math.ceil(manifest.size / ChunkSize) - 1) last = true; }
    const size = manifest.size ?? 0;
    const bytes = last && size ? (chunks - 1) * ChunkSize + (size - (Math.ceil(size / ChunkSize) - 1) * ChunkSize) : chunks * ChunkSize;
    list.push({
      key, title: manifest.title ?? '', entryId: manifest.entryId ?? 0, documentId: manifest.documentId ?? 0, size, bytes,
      complete: size > 0 && chunks === Math.ceil(size / ChunkSize), keep: manifest.keep === true, read: manifest.read ?? 0,
    });
  }
  return list;
}

/**
 * Lets go of the books read longest ago, never one kept offline or the one open, until the rest take no more than the
 * limit.
 */
export async function trim(openKey, most = limit()) {
  const all = await books();
  let total = all.filter(b => !b.keep).reduce((n, b) => n + b.bytes, 0);
  for (const book of all.filter(b => !b.keep && b.key !== openKey).sort((a, b) => a.read - b.read)) {
    if (total <= most) break;
    if (await remove(book.key)) total -= book.bytes;
  }
}

export async function remove(key) {
  try {
    await (await booksFolder(false)).removeEntry(key, { recursive: true });
    return true;
  } catch {
    // Open in another tab, which still writes to it: it goes next time.
    return false;
  }
}

/** Every book not kept offline. */
export async function clear() {
  for (const book of await books()) if (!book.keep) await remove(book.key);
}

/** Every book this browser keeps, offline or not, and the limit: for an account being closed. */
export async function forgetAll() {
  try { await (await navigator.storage.getDirectory()).removeEntry('books', { recursive: true }); } catch { /* none kept */ }
  try { localStorage.removeItem(LimitKey); } catch { /* nothing set */ }
}

/** How much the browser lets Bibliotaph keep, and whether it has promised not to clear it. */
export async function space() {
  try {
    const estimate = await navigator.storage.estimate();
    return { usage: estimate.usage ?? 0, quota: estimate.quota ?? 0, persisted: await navigator.storage.persisted?.() === true };
  } catch {
    return { usage: 0, quota: 0, persisted: false };
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
