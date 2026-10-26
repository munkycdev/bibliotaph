// Check a download, as the Windows app's: the folder or ZIP picked is read here in the browser and never uploaded.
// Each file is hashed with SHA-256, the library's own identity for a file, and a PDF's pages are read for their text,
// which the page turns into fingerprints; only those go to the server. ZIPs are read in place, a member at a time.

const base = new URL('../lib/pdfjs/', import.meta.url).href;

/** Files at most this big are hashed by the browser's own SHA-256, which needs them whole in memory. */
const WholeHashBytes = 256 * 1024 * 1024;

let pdfjs = null;
let picked = [];
let member = null;

async function load() {
  if (pdfjs) return;
  const lib = await import(base + 'pdf.min.mjs');
  lib.GlobalWorkerOptions.workerPort ??= new Worker(base + 'pdf.worker.min.mjs', { type: 'module' });
  pdfjs = lib;
}

/**
 * Asks for a folder ("folder") or a ZIP ("zip"), as Check a folder… and Check a ZIP… do. Resolves with every file
 * picked, by its place in the folder, or null when the picker was closed.
 */
export function pick(kind) {
  return new Promise(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    if (kind === 'folder') input.webkitdirectory = true;
    else input.accept = '.zip,application/zip';
    input.addEventListener('change', () => {
      picked = [...input.files];
      member = null;
      resolve(picked.map((file, id) => ({ id, path: file.webkitRelativePath || file.name, size: file.size })));
    });
    input.addEventListener('cancel', () => resolve(null));
    input.click();
  });
}

/** Forgets the files picked, and the member last read from a ZIP. */
export function release() {
  picked = [];
  member = null;
}

/**
 * The files in a picked ZIP: name, sizes, whether it is encrypted, and whether its compression is one a browser reads.
 * Throws when it isn't a readable ZIP.
 */
export async function list(id) {
  return (await entries(picked[id])).map(e => ({ name: e.name, size: e.usize, encrypted: (e.flags & 1) !== 0, readable: e.method === 0 || e.method === 8 }));
}

/** A file's SHA-256 as lowercase hex, or a ZIP member's, inflated as it is read. */
export async function hash(id, name) {
  return hex(await sha256(await blobOf(id, name)));
}

/**
 * A PDF's pages as text, a line a line as PDF.js lays them out (null for a page that couldn't be read), and its own
 * title, author, subject and keywords; or why its pages couldn't be read: "password" or "invalid".
 */
export async function pages(id, name) {
  await load();
  const blob = await blobOf(id, name);
  const task = pdfjs.getDocument({
    data: new Uint8Array(await blob.arrayBuffer()),
    isEvalSupported: false,
    enableXfa: false,
    cMapUrl: base + 'cmaps/',
    cMapPacked: true,
    standardFontDataUrl: base + 'standard_fonts/',
    wasmUrl: base + 'wasm/',
    iccUrl: base + 'iccs/',
  });
  let locked = false;
  task.onPassword = () => {
    locked = true;
    task.destroy();
  };
  let doc;
  try {
    doc = await task.promise;
  } catch {
    return { unread: locked ? 'password' : 'invalid' };
  }
  try {
    const texts = [];
    for (let i = 1; i <= doc.numPages; i++) {
      try {
        const page = await doc.getPage(i);
        const content = await page.getTextContent();
        let text = '';
        for (const item of content.items) {
          if (!('str' in item)) continue;
          text += item.str;
          if (item.hasEOL) text += '\n';
        }
        texts.push(text);
        page.cleanup();
      } catch {
        texts.push(null);
      }
    }
    const info = (await doc.getMetadata().catch(() => null))?.info ?? {};
    const field = value => typeof value === 'string' && value.trim() ? value : null;
    return { texts, title: field(info.Title), author: field(info.Author), subject: field(info.Subject), keywords: field(info.Keywords) };
  } finally {
    doc.destroy();
  }
}

/** A picked file, or a member of a picked ZIP, kept while its hash and then its pages are read. */
async function blobOf(id, name) {
  const file = picked[id];
  if (name == null) return file;
  if (member?.id === id && member.name === name) return member.blob;
  const entry = (await entries(file)).find(e => e.name === name);
  if (!entry) throw new Error('The file is no longer in its ZIP.');
  member = { id, name, blob: await inflate(file, entry) };
  return member.blob;
}

const zips = new WeakMap();

/** A ZIP's central directory, ZIP64 included. */
async function entries(file) {
  if (zips.has(file)) return zips.get(file);
  const tailLength = Math.min(file.size, 65557);
  const tail = new DataView(await file.slice(file.size - tailLength).arrayBuffer());
  let end = -1;
  for (let i = tailLength - 22; i >= 0; i--) {
    if (tail.getUint32(i, true) === 0x06054b50) { end = i; break; }
  }
  if (end < 0) throw new Error("It isn't a ZIP.");
  let count = tail.getUint16(end + 10, true);
  let size = tail.getUint32(end + 12, true);
  let offset = tail.getUint32(end + 16, true);
  if (end >= 20 && tail.getUint32(end - 20, true) === 0x07064b50) {
    const at = Number(tail.getBigUint64(end - 12, true));
    const record = new DataView(await file.slice(at, at + 56).arrayBuffer());
    if (record.getUint32(0, true) === 0x06064b50) {
      count = Number(record.getBigUint64(32, true));
      size = Number(record.getBigUint64(40, true));
      offset = Number(record.getBigUint64(48, true));
    }
  }
  const directory = new DataView(await file.slice(offset, offset + size).arrayBuffer());
  const names = new TextDecoder();
  const found = [];
  for (let p = 0, n = 0; n < count && p + 46 <= directory.byteLength; n++) {
    if (directory.getUint32(p, true) !== 0x02014b50) throw new Error("Its directory is damaged.");
    const entry = {
      flags: directory.getUint16(p + 8, true),
      method: directory.getUint16(p + 10, true),
      csize: directory.getUint32(p + 20, true),
      usize: directory.getUint32(p + 24, true),
      local: directory.getUint32(p + 42, true),
    };
    const nameLength = directory.getUint16(p + 28, true);
    const extraLength = directory.getUint16(p + 30, true);
    const commentLength = directory.getUint16(p + 32, true);
    entry.name = names.decode(new Uint8Array(directory.buffer, directory.byteOffset + p + 46, nameLength));
    // ZIP64: the sizes and offset too big for their fields follow in this order, each only when its field is full.
    for (let x = p + 46 + nameLength; x + 4 <= p + 46 + nameLength + extraLength;) {
      const tag = directory.getUint16(x, true), length = directory.getUint16(x + 2, true);
      if (tag === 1) {
        let at = x + 4;
        for (const key of ['usize', 'csize', 'local']) {
          if (entry[key] !== 0xffffffff || at + 8 > x + 4 + length) continue;
          entry[key] = Number(directory.getBigUint64(at, true));
          at += 8;
        }
      }
      x += 4 + length;
    }
    found.push(entry);
    p += 46 + nameLength + extraLength + commentLength;
  }
  zips.set(file, found);
  return found;
}

/** A member's bytes. It can't give more than the ZIP says it holds, so a crafted ZIP can't fill the browser's memory. */
async function inflate(file, entry) {
  const head = new DataView(await file.slice(entry.local, entry.local + 30).arrayBuffer());
  if (head.byteLength < 30 || head.getUint32(0, true) !== 0x04034b50) throw new Error('It is damaged inside the ZIP.');
  const start = entry.local + 30 + head.getUint16(26, true) + head.getUint16(28, true);
  const raw = file.slice(start, start + entry.csize);
  if (entry.method === 0) return raw;
  const reader = raw.stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  const parts = [];
  let length = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.length;
    if (length > entry.usize) {
      await reader.cancel();
      throw new Error('It is damaged inside the ZIP.');
    }
    parts.push(value);
  }
  return new Blob(parts);
}

async function sha256(blob) {
  if (blob.size <= WholeHashBytes) return new Uint8Array(await crypto.subtle.digest('SHA-256', await blob.arrayBuffer()));
  const hasher = new Sha256();
  const reader = blob.stream().getReader();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    hasher.update(value);
  }
  return hasher.digest();
}

function hex(bytes) {
  return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/** SHA-256 a piece at a time, for a file too big to hold whole: the browser's own only hashes a whole buffer. */
export class Sha256 {
  constructor() {
    this.h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
    this.w = new Uint32Array(64);
    this.buffer = new Uint8Array(64);
    this.buffered = 0;
    this.length = 0;
  }

  update(data) {
    this.length += data.length;
    let i = 0;
    if (this.buffered > 0) {
      i = Math.min(64 - this.buffered, data.length);
      this.buffer.set(data.subarray(0, i), this.buffered);
      this.buffered += i;
      if (this.buffered < 64) return;
      this.block(this.buffer, 0);
      this.buffered = 0;
    }
    for (; i + 64 <= data.length; i += 64) this.block(data, i);
    if (i < data.length) {
      this.buffer.set(data.subarray(i));
      this.buffered = data.length - i;
    }
  }

  digest() {
    const bits = this.length * 8;
    const tail = new Uint8Array(this.buffered < 56 ? 64 - this.buffered : 128 - this.buffered);
    tail[0] = 0x80;
    const end = new DataView(tail.buffer);
    end.setUint32(tail.length - 8, Math.floor(bits / 0x100000000));
    end.setUint32(tail.length - 4, bits >>> 0);
    this.update(tail);
    const out = new Uint8Array(32);
    const view = new DataView(out.buffer);
    this.h.forEach((v, i) => view.setUint32(i * 4, v));
    return out;
  }

  block(bytes, o) {
    const w = this.w;
    for (let t = 0; t < 16; t++, o += 4) w[t] = (bytes[o] << 24) | (bytes[o + 1] << 16) | (bytes[o + 2] << 8) | bytes[o + 3];
    for (let t = 16; t < 64; t++) {
      const x = w[t - 15], y = w[t - 2];
      const s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
      const s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
      w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
    }
    const s = this.h;
    let a = s[0], b = s[1], c = s[2], d = s[3], e = s[4], f = s[5], g = s[6], h = s[7];
    for (let t = 0; t < 64; t++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const t1 = (h + S1 + ((e & f) ^ (~e & g)) + K[t] + w[t]) | 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const t2 = (S0 + ((a & b) ^ (a & c) ^ (b & c))) | 0;
      h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
    }
    s[0] += a; s[1] += b; s[2] += c; s[3] += d; s[4] += e; s[5] += f; s[6] += g; s[7] += h;
  }
}
