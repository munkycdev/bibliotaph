// The reader's pages, as the Windows reader shows them: a PDF drawn by PDF.js from byte ranges of the file in the user's
// storage, every page in one scrolling column, with its text selectable, its links followed, the words of a search or
// a find marked, and a strip of page thumbnails. The page around it is Blazor's; this reports back what the reader does.
import { BookCache, ChunkSize, LinkError, trim } from './bookcache.js';

const base = new URL('../lib/pdfjs/', import.meta.url).href;
const ZoomSteps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
const ThumbnailHeight = 96;

let pdfjs = null, viewers = null;

// pdf_viewer.mjs reads PDF.js from globalThis as it loads. One worker serves every book, so a second book opens at once.
async function load() {
  if (pdfjs) return;
  const style = document.createElement('link');
  style.rel = 'stylesheet';
  style.href = base + 'pdf_viewer.css';
  document.head.prepend(style);
  const lib = await import(base + 'pdf.min.mjs');
  globalThis.pdfjsLib = lib;
  viewers = await import(base + 'pdf_viewer.mjs');
  lib.GlobalWorkerOptions.workerPort = new Worker(base + 'pdf.worker.min.mjs', { type: 'module' });
  pdfjs = lib;
}

// PDF.js's own words (its tooltips and labels) in English, without fetching its translations.
const l10n = {
  getLanguage: () => 'en-US',
  getDirection: () => 'ltr',
  get: async (ids, args, fallback) => fallback ?? '',
  translate: async () => { },
  translateOnce: async () => { },
  pause: () => { },
  resume: () => { },
  destroy: async () => { },
};

/**
 * Opens a book in the reader's stage.
 * @param {HTMLElement} stage .bt-pages, which scrolls the pages
 * @param {HTMLElement} strip .bt-strip, for the thumbnails
 * @param {{key: string, size: number, start: number, labels: (string|null)[], marks: string[], title: string, entryId: number, documentId: number}} book
 * @param {any} page the Blazor page: PageChanged, ScaleChanged, Opened, Failed, AskPassword, Link, LinkFollowed, Notice, FindKey,
 *   Offline
 */
export async function open(stage, strip, book, page) {
  await load();
  const reader = new Reader(stage, strip, book, page);
  await reader.start();
  return reader;
}

class Reader {
  constructor(stage, strip, book, page) {
    this.stage = stage;
    this.strip = strip;
    this.book = book;
    this.page = page;
    this.closed = false;
    this.marks = new Marks(this);
    this.thumbnails = null;
    this.canCopy = true;
    this.lastWheel = 0;
    this.listeners = [];
  }

  async start() {
    const { EventBus, PDFLinkService, PDFViewer, LinkTarget } = viewers;
    const { title, entryId, documentId } = this.book;
    const cache = this.cache = await BookCache.open(this.book.key, this.book.size, () => this.page.invokeMethodAsync('Link'),
      { title, entryId, documentId });
    // Room for this book is made from the others, never from it.
    trim(this.book.key).catch(() => { });
    const transport = new pdfjs.PDFDataRangeTransport(this.book.size, null);
    transport.requestDataRange = (begin, end) =>
      cache.read(begin, end).then(bytes => transport.onDataRange(begin, bytes), e => this.#failed(e));
    this.task = pdfjs.getDocument({
      range: transport,
      rangeChunkSize: ChunkSize,
      disableAutoFetch: true,
      disableStream: true,
      isEvalSupported: false,
      enableXfa: false,
      cMapUrl: base + 'cmaps/',
      cMapPacked: true,
      standardFontDataUrl: base + 'standard_fonts/',
      wasmUrl: base + 'wasm/',
      iccUrl: base + 'iccs/',
    });
    // As the Windows reader asks: once, then again while the password given doesn't open it, until Cancel.
    this.task.onPassword = (update, reason) => {
      this.page.invokeMethodAsync('AskPassword', reason === pdfjs.PasswordResponses.INCORRECT_PASSWORD).then(password => {
        if (password == null) {
          this.cancelled = true;
          this.task.destroy();
        } else update(password);
      });
    };
    let doc;
    try {
      doc = await this.task.promise;
    } catch (e) {
      if (!this.closed) this.#failed(e);
      return;
    }
    if (this.closed) { doc.destroy(); return; }
    this.doc = doc;

    const eventBus = new EventBus();
    const linkService = new PDFLinkService({ eventBus, externalLinkTarget: LinkTarget.BLANK, externalLinkRel: 'noopener noreferrer nofollow' });
    const viewer = new PDFViewer({
      container: this.stage,
      viewer: this.stage.firstElementChild,
      eventBus,
      linkService,
      l10n,
      imageResourcesPath: base + 'images/',
      enablePermissions: false,
      removePageBorders: false,
    });
    linkService.setViewer(viewer);
    this.viewer = viewer;
    this.eventBus = eventBus;

    // A link in the book (from its contents or index, say) leaves a way back to the page it was followed from.
    for (const name of ['goToDestination', 'goToPage']) {
      const follow = linkService[name].bind(linkService);
      linkService[name] = (...args) => {
        const from = viewer.currentPageNumber - 1;
        const done = follow(...args);
        this.page.invokeMethodAsync('LinkFollowed', from);
        return done;
      };
    }

    eventBus.on('pagesinit', () => {
      viewer.currentScaleValue = 'page-width';
      if (this.book.start > 0) viewer.currentPageNumber = this.book.start + 1;
    });
    eventBus.on('pagechanging', e => {
      this.thumbnails?.current(e.pageNumber - 1);
      this.page.invokeMethodAsync('PageChanged', e.pageNumber - 1);
    });
    eventBus.on('scalechanging', e => this.page.invokeMethodAsync('ScaleChanged', e.presetValue ?? String(e.scale)));
    eventBus.on('textlayerrendered', e => {
      const div = e.source?.textLayer?.div;
      if (div) this.marks.pageShown(e.pageNumber - 1, div);
    });
    eventBus.on('pagerendered', () => this.thumbnails?.wake(), { once: true });

    viewer.setDocument(doc);
    linkService.setDocument(doc);

    const permissions = await doc.getPermissions();
    this.canCopy = !permissions || permissions.includes(pdfjs.PermissionFlag.COPY);
    this.#listen(this.stage, 'copy', e => {
      if (this.canCopy) return;
      e.preventDefault();
      this.page.invokeMethodAsync('Notice', "This PDF doesn't allow copying its text.");
    });
    // Ctrl+wheel zooms the pages, as in the Windows reader, rather than the whole page.
    this.#listen(this.stage, 'wheel', e => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      const now = performance.now();
      if (now - this.lastWheel < 90) return;
      this.lastWheel = now;
      this.zoom(e.deltaY < 0);
    }, { passive: false });
    this.#listen(document, 'keydown', e => this.#key(e));
    // A fitted page fits again as the window or the contents panel changes the room the pages have.
    const refit = new ResizeObserver(() => {
      const value = viewer.currentScaleValue;
      if (value === 'page-width' || value === 'page-fit') viewer.currentScaleValue = value;
    });
    refit.observe(this.stage);
    this.listeners.push(() => refit.disconnect());
    this.page.invokeMethodAsync('Opened', this.canCopy);
    this.#offline();
    // A book kept offline that the browser has partly cleared reads the rest again.
    if (cache.kept && !cache.complete) this.keepOffline(true);
  }

  #offline() {
    if (!this.closed) this.page.invokeMethodAsync('Offline', this.cache.stored, this.cache.kept, this.cache.progress);
  }

  /** Keeps the whole book in this browser, or lets it go with the rest; the page hears how far it has got. */
  keepOffline(on) {
    let told = 0;
    this.cache.keepOffline(on, () => {
      // A long book reads in hundreds of chunks: the page hears of it a few times a second at most.
      if (Date.now() - told < 250) return;
      told = Date.now();
      this.#offline();
    }).then(done => {
      this.#offline();
      if (!done && on && this.cache.kept && !this.closed)
        this.page.invokeMethodAsync('Notice', "The whole book couldn't be kept: the connection dropped or the browser is out of room. It carries on when you next open it.");
    });
  }

  #listen(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    this.listeners.push(() => target.removeEventListener(type, handler, options));
  }

  #key(e) {
    if (document.querySelector('.bt-dialog')) return;
    const ctrl = (e.ctrlKey || e.metaKey) && !e.altKey;
    if (e.key === 'F3') {
      e.preventDefault();
      this.page.invokeMethodAsync('FindKey', e.shiftKey);
    } else if (ctrl && (e.key === 'f' || e.key === 'F')) {
      const box = document.querySelector('.bt-findbox');
      if (!box) return;
      e.preventDefault();
      box.focus();
      box.select();
    } else if (ctrl && (e.key === '+' || e.key === '=')) {
      e.preventDefault();
      this.zoom(true);
    } else if (ctrl && (e.key === '-' || e.key === '_')) {
      e.preventDefault();
      this.zoom(false);
    } else if (ctrl && e.key === '0') {
      e.preventDefault();
      this.setScale('page-width');
    }
  }

  #failed(e) {
    if (this.closed || this.reported) return;
    this.reported = true;
    const kind = this.cancelled ? 'password'
      : e instanceof LinkError ? 'link'
      : e?.name === 'InvalidPDFException' ? 'invalid'
      : this.doc ? 'read' : 'other';
    this.page.invokeMethodAsync('Failed', kind, String(e?.message ?? ''));
  }

  /** Goes to a page (from 0). */
  goTo(index) {
    if (!this.viewer) return;
    this.viewer.currentPageNumber = Math.min(Math.max(index, 0), this.viewer.pagesCount - 1) + 1;
  }

  /** "page-width", "page-fit", or a scale where 1 is 100%. */
  setScale(value) {
    if (this.viewer) this.viewer.currentScaleValue = value;
  }

  /** The next zoom step from the size on screen, so a fitted page steps from its fitted size. */
  zoom(up) {
    if (!this.viewer) return;
    const current = this.viewer.currentScale;
    const next = up ? ZoomSteps.find(z => z > current + 0.01) ?? ZoomSteps[ZoomSteps.length - 1]
      : [...ZoomSteps].reverse().find(z => z < current - 0.01) ?? ZoomSteps[0];
    this.viewer.currentScale = next;
  }

  /** The words of the search the book was opened from, on the page it opened at; the first brought into view. */
  markTerms(terms, pageIndex) { this.marks.terms(terms, pageIndex); }

  /** What Find found: every match of the phrase marked, the one at that place on that page standing out and in view. */
  showMatch(phrase, pageIndex, nth) {
    // The page first, so a match on a page already drawn is brought into view from where the page now is.
    if (this.viewer && this.viewer.currentPageNumber !== pageIndex + 1) this.goTo(pageIndex);
    this.marks.find(phrase, pageIndex, nth);
  }

  /** Find cleared: the search's own words again. */
  clearFind() { this.marks.find(null); }

  /** Shows or hides the thumbnails, which are drawn only while they show. */
  showStrip(show) {
    if (!this.doc) return;
    if (show && !this.thumbnails) {
      this.thumbnails = new Thumbnails(this, this.strip);
      this.thumbnails.current(this.viewer.currentPageNumber - 1);
      if (this.stage.querySelector('canvas')) this.thumbnails.wake();
    }
    this.thumbnails?.show(show);
  }

  dispose() {
    this.closed = true;
    for (const stop of this.listeners) stop();
    this.marks.dispose();
    this.thumbnails?.dispose();
    this.cache?.close();
    this.viewer?.setDocument(null);
    this.task?.destroy();
  }
}

// Marks on the pages, drawn with the CSS Custom Highlight API over the text layer, so the page's own text is left
// as PDF.js made it.
class Marks {
  constructor(reader) {
    this.reader = reader;
    this.layers = new Map();
    this.termList = [];
    this.termPage = -1;
    this.phrase = null;
    this.matchPage = -1;
    this.nth = -1;
    this.scrollPending = false;
  }

  get supported() { return typeof CSS !== 'undefined' && 'highlights' in CSS && typeof Highlight !== 'undefined'; }

  terms(terms, pageIndex) {
    this.termList = terms.filter(t => t.trim().length > 0);
    this.termPage = pageIndex;
    this.scrollPending = this.termList.length > 0 && this.phrase == null;
    this.#draw();
  }

  find(phrase, pageIndex = -1, nth = -1) {
    this.phrase = phrase && phrase.trim().length > 0 ? phrase : null;
    this.matchPage = pageIndex;
    this.nth = nth;
    this.scrollPending = this.phrase != null;
    this.#draw();
  }

  pageShown(pageIndex, div) {
    this.layers.set(pageIndex, div);
    this.#draw();
  }

  #draw() {
    if (!this.supported) return;
    const all = [], current = [];
    for (const [pageIndex, div] of this.layers) {
      if (!div.isConnected) { this.layers.delete(pageIndex); continue; }
      let ranges = [];
      if (this.phrase != null) ranges = findRanges(div, phrasePattern(this.phrase));
      else if (pageIndex === this.termPage) ranges = this.termList.flatMap(t => findRanges(div, phrasePattern(t)));
      ranges.sort((a, b) => a.compareBoundaryPoints(Range.START_TO_START, b));
      const standout = this.phrase != null ? (pageIndex === this.matchPage ? ranges[this.nth] : null)
        : (pageIndex === this.termPage && this.scrollPending ? ranges[0] : null);
      for (const range of ranges) (range === standout && this.phrase != null ? current : all).push(range);
      if (standout && this.scrollPending) {
        this.scrollPending = false;
        this.#bringIntoView(standout);
      }
    }
    CSS.highlights.set('bt-mark', new Highlight(...all));
    CSS.highlights.set('bt-mark-current', new Highlight(...current));
  }

  // The mark a third of the way down the pages, so the lines above it give it context.
  #bringIntoView(range) {
    const stage = this.reader.stage;
    const box = range.getBoundingClientRect(), view = stage.getBoundingClientRect();
    if (box.top >= view.top + 40 && box.bottom <= view.bottom - 40) return;
    stage.scrollTop += box.top - view.top - view.height / 3;
  }

  dispose() {
    if (!this.supported) return;
    CSS.highlights.delete('bt-mark');
    CSS.highlights.delete('bt-mark-current');
  }
}

// The words of a phrase in order, ignoring case and how the text breaks between them.
function phrasePattern(phrase) {
  const words = phrase.trim().split(/\s+/).filter(w => w.length > 0).map(w => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  return new RegExp(words.join('\\s+'), 'giu');
}

// Every match of a pattern in a text layer, as ranges over its text, which PDF.js splits into many runs.
function findRanges(div, pattern) {
  const nodes = [];
  let text = '';
  const walker = document.createTreeWalker(div, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (node.nodeType === Node.TEXT_NODE) {
      nodes.push({ node, start: text.length });
      text += node.data;
    } else if (node.tagName === 'BR') {
      text += '\n';
    }
  }
  const ranges = [];
  pattern.lastIndex = 0;
  for (let match = pattern.exec(text); match; match = pattern.exec(text)) {
    if (match[0].length === 0) { pattern.lastIndex++; continue; }
    const from = locate(nodes, match.index, false), to = locate(nodes, match.index + match[0].length, true);
    if (!from || !to) continue;
    const range = document.createRange();
    range.setStart(from.node, from.offset);
    range.setEnd(to.node, to.offset);
    ranges.push(range);
  }
  return ranges;
}

function locate(nodes, at, end) {
  let low = 0, high = nodes.length - 1, found = -1;
  while (low <= high) {
    const mid = (low + high) >> 1;
    if (nodes[mid].start < at || (end && nodes[mid].start === at)) { found = mid; low = mid + 1; } else high = mid - 1;
  }
  if (found < 0) return nodes.length ? { node: nodes[0].node, offset: 0 } : null;
  const { node, start } = nodes[found];
  return { node, offset: Math.min(at - start, node.data.length) };
}

// The strip of page thumbnails: a picture of every page, scrolled sideways, drawn only near the strip's view and
// after the pages on screen, the page in view outlined.
class Thumbnails {
  constructor(reader, strip) {
    this.reader = reader;
    this.strip = strip;
    this.awake = false;
    this.drawn = new Set();
    this.queue = [];
    this.drawing = false;
    const count = reader.doc.numPages;
    const fragment = document.createDocumentFragment();
    this.items = [];
    for (let i = 0; i < count; i++) {
      const label = display(i, reader.book.labels);
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'bt-thumbnail';
      item.title = `Page ${label}`;
      item.setAttribute('aria-label', `Page ${label}`);
      item.innerHTML = '<span class="bt-thumbframe"><span class="bt-small"></span></span><span class="bt-thumbnumber"></span>';
      item.querySelector('.bt-small').textContent = label;
      item.querySelector('.bt-thumbnumber').textContent = label;
      item.addEventListener('click', () => reader.goTo(i));
      fragment.appendChild(item);
      this.items.push(item);
    }
    strip.replaceChildren(fragment);
    this.observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        const i = this.items.indexOf(entry.target);
        if (entry.isIntersecting && !this.drawn.has(i)) this.queue.push(i);
      }
      this.#pump();
    }, { root: strip, rootMargin: '0px 400px' });
    for (const item of this.items) this.observer.observe(item);
    // The wheel scrolls the strip sideways, as the Windows strip does.
    reader.listeners.push(listen(strip, 'wheel', e => {
      if (e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
      e.preventDefault();
      strip.scrollLeft += e.deltaY;
    }, { passive: false }));
  }

  wake() {
    this.awake = true;
    this.#pump();
  }

  show(show) {
    this.strip.hidden = !show;
    if (show) this.items[this.currentIndex]?.scrollIntoView({ block: 'nearest', inline: 'center' });
  }

  current(index) {
    const before = this.items[this.currentIndex];
    if (before) { before.classList.remove('current'); before.setAttribute('aria-label', before.title); }
    this.currentIndex = index;
    const now = this.items[index];
    if (!now) return;
    now.classList.add('current');
    now.setAttribute('aria-label', `${now.title}, the page in view`);
    if (!this.strip.hidden) now.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  async #pump() {
    if (!this.awake || this.drawing) return;
    this.drawing = true;
    try {
      while (this.queue.length > 0 && !this.reader.closed) {
        const i = this.queue.shift();
        if (this.drawn.has(i)) continue;
        this.drawn.add(i);
        await this.#draw(i);
      }
    } finally {
      this.drawing = false;
    }
  }

  async #draw(i) {
    try {
      const page = await this.reader.doc.getPage(i + 1);
      const unit = page.getViewport({ scale: 1 });
      const ratio = window.devicePixelRatio || 1;
      const viewport = page.getViewport({ scale: ThumbnailHeight * ratio / unit.height });
      const canvas = document.createElement('canvas');
      canvas.width = Math.round(viewport.width);
      canvas.height = Math.round(viewport.height);
      await page.render({ canvas, viewport }).promise;
      const frame = this.items[i].querySelector('.bt-thumbframe');
      frame.replaceChildren(canvas);
    } catch {
      this.drawn.delete(i);
    }
  }

  dispose() {
    this.observer.disconnect();
    this.strip.replaceChildren();
  }
}

function listen(target, type, handler, options) {
  target.addEventListener(type, handler, options);
  return () => target.removeEventListener(type, handler, options);
}

function display(index, labels) {
  const label = labels[index];
  return label && label.trim().length > 0 ? label.trim() : String(index + 1);
}

/** Pops the reader out into a window of its own, as the Windows reader's Pop out does. */
// A picture, as the Windows reader shows one: fitted to the window or at a zoom step, the pack's next and previous on
// the arrow keys. The whole file is read through the same cache as a PDF's pages.
const ImageTypes = { jpg: 'image/jpeg', png: 'image/png', webp: 'image/webp' };

/**
 * @param {HTMLElement} stage .bt-pages, which scrolls the picture
 * @param {{key: string, size: number, format: string, scale: string, title: string, entryId: number, documentId: number}} book
 * @param {any} page the Blazor page: ScaleChanged, Opened, Failed, Link, Offline, Step
 */
export async function openImage(stage, book, page) {
  const reader = new ImageReader(stage, book, page);
  await reader.start();
  return reader;
}

class ImageReader {
  constructor(stage, book, page) {
    this.stage = stage;
    this.host = stage.querySelector('.bt-picture');
    this.book = book;
    this.page = page;
    this.scale = book.scale || 'page-fit';
    this.closed = false;
    this.listeners = [];
  }

  async start() {
    const { title, entryId, documentId } = this.book;
    this.cache = await BookCache.open(this.book.key, this.book.size, () => this.page.invokeMethodAsync('Link'), { title, entryId, documentId });
    trim(this.book.key).catch(() => { });
    let bytes;
    try {
      bytes = await this.cache.read(0, this.book.size);
    } catch (e) {
      if (!this.closed) this.page.invokeMethodAsync('Failed', e instanceof LinkError ? 'link' : 'other', String(e?.message ?? ''));
      return;
    }
    if (this.closed) return;
    this.url = URL.createObjectURL(new Blob([bytes], { type: ImageTypes[this.book.format] ?? '' }));
    const img = new Image();
    img.alt = title;
    img.src = this.url;
    try {
      await img.decode();
    } catch {
      if (!this.closed) this.page.invokeMethodAsync('Failed', 'image', '');
      return;
    }
    if (this.closed) return;
    this.img = img;
    this.host.replaceChildren(img);
    this.#apply();
    this.#listen(this.stage, 'wheel', e => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      this.zoom(e.deltaY < 0);
    }, { passive: false });
    this.#listen(document, 'keydown', e => this.#key(e));
    const refit = new ResizeObserver(() => { if (this.scale === 'page-fit') this.#apply(); });
    refit.observe(this.stage);
    this.listeners.push(() => refit.disconnect());
    this.page.invokeMethodAsync('Opened', true);
    this.#offline();
  }

  #listen(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    this.listeners.push(() => target.removeEventListener(type, handler, options));
  }

  #key(e) {
    if (document.querySelector('.bt-dialog') || e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
    const ctrl = (e.ctrlKey || e.metaKey) && !e.altKey;
    if (ctrl && (e.key === '+' || e.key === '=')) this.zoom(true);
    else if (ctrl && (e.key === '-' || e.key === '_')) this.zoom(false);
    else if (ctrl && e.key === '0') this.setScale('page-fit');
    else if (!ctrl && !e.altKey && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) this.page.invokeMethodAsync('Step', e.key === 'ArrowRight' ? 1 : -1);
    else return;
    e.preventDefault();
  }

  // The size on screen: the window's (less a margin) for Fit to window, else the picture's own times the zoom.
  get #shown() {
    if (!this.img) return 1;
    if (this.scale !== 'page-fit') return Number(this.scale) || 1;
    const room = 32;
    return Math.min((this.stage.clientWidth - room) / this.img.naturalWidth, (this.stage.clientHeight - room) / this.img.naturalHeight);
  }

  #apply() {
    if (!this.img) return;
    const scale = Math.max(this.#shown, 0.01);
    this.img.style.width = `${Math.round(this.img.naturalWidth * scale)}px`;
    this.img.style.height = `${Math.round(this.img.naturalHeight * scale)}px`;
  }

  /** "page-fit", or a scale where 1 is 100%. */
  setScale(value) {
    this.scale = value;
    this.#apply();
    this.page.invokeMethodAsync('ScaleChanged', value);
  }

  zoom(up) {
    const current = this.#shown;
    const next = up ? ZoomSteps.find(z => z > current + 0.01) ?? ZoomSteps[ZoomSteps.length - 1]
      : [...ZoomSteps].reverse().find(z => z < current - 0.01) ?? ZoomSteps[0];
    this.setScale(String(next));
  }

  #offline() {
    if (!this.closed) this.page.invokeMethodAsync('Offline', this.cache.stored, this.cache.kept, this.cache.progress);
  }

  keepOffline(on) {
    this.cache.keepOffline(on).then(() => this.#offline());
  }

  dispose() {
    this.closed = true;
    for (const stop of this.listeners) stop();
    this.cache?.close();
    this.host.replaceChildren();
    if (this.url) URL.revokeObjectURL(this.url);
  }
}

export function popOut(url) {
  const opened = window.open(url, '_blank', 'popup,width=960,height=900');
  return opened != null;
}

/** Back in the main window: the window this one was popped out of, when it's still open. */
export function returnToMain(url) {
  if (window.opener && !window.opener.closed) {
    window.opener.location.href = url;
    window.opener.focus();
    window.close();
    return true;
  }
  return false;
}

/** Once the book has popped out, this window goes back to where the book was opened from, else the library. */
export function leave() {
  if (history.length > 1) history.back(); else location.href = 'library';
}

/** Whether the thumbnails showed when last left, for this sitting, as in the Windows reader. */
export function stripShown() {
  try { return sessionStorage.getItem('bibliotaph.reader.strip') === '1'; } catch { return false; }
}

export function keepStrip(shown) {
  try { sessionStorage.setItem('bibliotaph.reader.strip', shown ? '1' : '0'); } catch { /* kept for this page only */ }
}

/** The page box selects what it holds as it takes focus, so typing replaces it. */
export function selectAll(input) {
  input?.select();
}
