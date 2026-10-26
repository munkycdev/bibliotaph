// The reader's pages, as the Windows reader shows them: a PDF drawn by PDF.js from byte ranges of the file in the user's
// storage, every page in one scrolling column, with its text selectable, its links followed, the words of a search or
// a find marked, and a strip of page thumbnails. The page around it is Blazor's; this reports back what the reader does.
import { BookCache, ChunkSize, LinkError, trim } from './bookcache.js';

const base = new URL('../lib/pdfjs/', import.meta.url).href;
const ZoomSteps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
const ThumbnailHeight = 96;

// Lucide's notebook-pen, as the Windows strip's note badge.
const NoteIcon = '<svg viewBox="0 0 24 24" width="11" height="11" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><path d="M 13.4 2 H 6 a 2 2 0 0 0 -2 2 v 16 a 2 2 0 0 0 2 2 h 12 a 2 2 0 0 0 2 -2 v -7.4 M 2 6 h 4 M 2 10 h 4 M 2 14 h 4 M 2 18 h 4 M 21.378 5.626 a 1 1 0 1 0 -3.004 -3.004 l -5.01 5.012 a 2 2 0 0 0 -0.506 0.854 l -0.837 2.87 a 0.5 0.5 0 0 0 0.62 0.62 l 2.87 -0.837 a 2 2 0 0 0 0.854 -0.506 Z"/></svg>';

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

// The screen stays on while a book is open, as an e-book app's does, so a tablet at the table doesn't dim mid-session.
// The browser lets the lock go whenever the page is hidden, so it's asked for again when the page shows.
let awake = 0;
let wakeLock = null;
let locking = false;
async function lockScreen() {
  if (awake === 0 || wakeLock || locking || document.visibilityState !== 'visible' || !('wakeLock' in navigator)) return;
  locking = true;
  try {
    const lock = await navigator.wakeLock.request('screen');
    if (awake === 0) {
      lock.release();
    } else {
      wakeLock = lock;
      lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
    }
  } catch {
    // Battery saver, or a browser that refuses: the screen dims as it usually does.
  } finally {
    locking = false;
  }
}
document.addEventListener('visibilitychange', lockScreen);

/** Keeps the screen on until the returned function is called. */
function stayAwake() {
  awake++;
  lockScreen();
  let released = false;
  return () => {
    if (released) return;
    released = true;
    if (--awake === 0 && wakeLock) {
      wakeLock.release();
      wakeLock = null;
    }
  };
}

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
    this.awake = stayAwake();
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
      if (!div) return;
      this.marks.pageShown(e.pageNumber - 1, div);
      this.#printedLinks(e.pageNumber - 1, e.source).catch(() => { });
    });
    eventBus.on('pagerendered', () => this.thumbnails?.wake(), { once: true });

    viewer.setDocument(doc);
    linkService.setDocument(doc);

    // A Set of the flags the file allows, or null when it isn't encrypted and so restricts nothing.
    const permissions = await doc.getPermissions();
    this.canCopy = !permissions || permissions.has(pdfjs.PermissionFlag.COPY);
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

  /**
   * An index or contents page whose entries aren't links in the PDF gets links made from its printed page numbers, as
   * in the Windows reader: the page asks which numbers they are, by the same rules, and each is covered by a link to
   * its page. A page with links of its own keeps them, and gets none guessed beside them.
   */
  async #printedLinks(index, view) {
    view.div.querySelector('.bt-printedlinks')?.remove();
    const layer = view.textLayer?.div;
    // A page of prose seldom ends three lines with numbers, so most pages are never asked about.
    const { text, nodes } = layerText(layer);
    if ((text.match(/\p{L}.*\d[,;]?\s*$/gmu) ?? []).length < 3) return;
    const annotations = await view.pdfPage.getAnnotations({ intent: 'display' });
    if (annotations.some(a => a.subtype === 'Link')) return;
    const links = await this.page.invokeMethodAsync('PrintedLinks', index, text);
    if (this.closed || !links?.length || !layer.isConnected) return;
    view.div.querySelector('.bt-printedlinks')?.remove();
    const box = view.div.getBoundingClientRect();
    const overlay = document.createElement('div');
    overlay.className = 'bt-printedlinks';
    for (const link of links) {
      const range = textRange(nodes, link.start, link.start + link.length);
      const rects = range ? [...range.getClientRects()].filter(r => r.width > 0 && r.height > 0) : [];
      if (rects.length === 0) continue;
      const left = Math.min(...rects.map(r => r.left)), top = Math.min(...rects.map(r => r.top));
      const right = Math.max(...rects.map(r => r.right)), bottom = Math.max(...rects.map(r => r.bottom));
      const a = document.createElement('a');
      a.href = '#';
      a.title = link.tip;
      a.setAttribute('aria-label', link.tip);
      // A point of slack around the digits, so a narrow "4" is not fiddly to hit.
      Object.assign(a.style, { left: `${left - box.left - 1}px`, top: `${top - box.top - 1}px`, width: `${right - left + 2}px`, height: `${bottom - top + 2}px` });
      a.addEventListener('click', e => {
        e.preventDefault();
        const from = this.viewer.currentPageNumber - 1;
        this.goTo(link.pageIndex);
        this.page.invokeMethodAsync('LinkFollowed', from);
      });
      overlay.appendChild(a);
    }
    view.div.appendChild(overlay);
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

  /** The open book's page notes, as [first, last] pages (from 0), which the thumbnails badge. */
  setNotes(ranges) {
    this.notes = ranges;
    this.thumbnails?.notes(ranges);
  }

  /** A binder item's pages (from 0), which the thumbnails mark; null for none. */
  itemPages(first, last) {
    this.item = first == null || last == null ? null : [first, last];
    this.thumbnails?.itemPages(this.item);
  }

  /** The page the selected text starts on, or -1 when nothing in the pages is selected. */
  selectionPage() {
    const selection = document.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) return -1;
    let node = selection.getRangeAt(0).startContainer;
    if (node.nodeType !== Node.ELEMENT_NODE) node = node.parentElement;
    const page = node?.closest('.page[data-page-number]');
    return page && this.stage.contains(page) ? Number(page.dataset.pageNumber) - 1 : -1;
  }

  /** Shows or hides the thumbnails, which are drawn only while they show. */
  showStrip(show) {
    if (!this.doc) return;
    if (show && !this.thumbnails) {
      this.thumbnails = new Thumbnails(this, this.strip);
      if (this.notes) this.thumbnails.notes(this.notes);
      this.thumbnails.itemPages(this.item);
      this.thumbnails.current(this.viewer.currentPageNumber - 1);
      if (this.stage.querySelector('canvas')) this.thumbnails.wake();
    }
    this.thumbnails?.show(show);
  }

  dispose() {
    this.closed = true;
    this.awake();
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

// A text layer's text, its lines broken where PDF.js breaks them, and where each of its many runs starts in it.
function layerText(div) {
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
  return { text, nodes };
}

// The characters from start to end of a text layer's text, as a range over its runs.
function textRange(nodes, start, end) {
  const from = locate(nodes, start, false), to = locate(nodes, end, true);
  if (!from || !to) return null;
  const range = document.createRange();
  range.setStart(from.node, from.offset);
  range.setEnd(to.node, to.offset);
  return range;
}

// Every match of a pattern in a text layer, as ranges over its text, which PDF.js splits into many runs.
function findRanges(div, pattern) {
  const { text, nodes } = layerText(div);
  const ranges = [];
  pattern.lastIndex = 0;
  for (let match = pattern.exec(text); match; match = pattern.exec(text)) {
    if (match[0].length === 0) { pattern.lastIndex++; continue; }
    const range = textRange(nodes, match.index, match.index + match[0].length);
    if (range) ranges.push(range);
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
      item.innerHTML = '<span class="bt-thumbframe"><span class="bt-small"></span></span><span class="bt-thumbnumber"></span>'
        + `<span class="bt-thumbnote" hidden>${NoteIcon}</span>`;
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
    const before = this.currentIndex;
    this.currentIndex = index;
    this.items[before]?.classList.remove('current');
    this.#describe(before);
    const now = this.items[index];
    if (!now) return;
    now.classList.add('current');
    this.#describe(index);
    if (!this.strip.hidden) now.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  /** Badges the pages with notes on them, as the Windows strip does. */
  notes(ranges) {
    const counts = new Array(this.items.length).fill(0);
    for (const [first, last] of ranges) {
      for (let i = Math.max(0, Math.min(first, last)); i <= Math.min(counts.length - 1, Math.max(first, last)); i++) counts[i]++;
    }
    this.counts = counts;
    this.items.forEach((item, i) => {
      item.querySelector('.bt-thumbnote').hidden = counts[i] === 0;
      this.#describe(i);
    });
  }

  /** Marks the binder item's pages with a bar, as the Windows strip does, held to the book. */
  itemPages(range) {
    const count = this.items.length;
    let marked = null;
    if (range && count > 0) {
      const [from, to] = range[0] <= range[1] ? range : [range[1], range[0]];
      if (to >= 0 && from < count) marked = [Math.max(0, from), Math.min(count - 1, to)];
    }
    this.items.forEach((item, i) => item.classList.toggle('initem', marked != null && i >= marked[0] && i <= marked[1]));
  }

  // As the Windows strip names a thumbnail: "Page 12, 2 notes, the page in view".
  #describe(i) {
    const item = this.items[i];
    if (!item) return;
    const parts = [item.title];
    const notes = this.counts?.[i] ?? 0;
    if (notes > 0) parts.push(notes === 1 ? '1 note' : `${notes.toLocaleString()} notes`);
    if (i === this.currentIndex) parts.push('the page in view');
    item.setAttribute('aria-label', parts.join(', '));
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
    this.awake = stayAwake();
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
    this.awake();
    for (const stop of this.listeners) stop();
    this.cache?.close();
    this.host.replaceChildren();
    if (this.url) URL.revokeObjectURL(this.url);
  }
}

// An e-book, as the Windows reader shows one: the page that reader shows, built by the same .NET code in the browser,
// in a frame whose every request ebook-sw.js brings back here to answer from the book. The frame is same-origin so
// the reader can call the page's script directly (bt.goTo, bt.find); what keeps the book's content from running is
// the page's own policy, a nonce for the reader's style and script and nothing else, as in the Windows reader.
const EbookScope = '/ebook-res/';
const ThemeSlot = '<!--bt-theme-->';
const ThemeTokens = ['--bt-bg', '--bt-side', '--bt-text', '--bt-muted', '--bt-line', '--bt-accent', '--bt-accent-text'];
const ebooks = new Map();
let ebookWorker = null;

/**
 * @param {HTMLElement} stage .bt-pages, which holds the frame
 * @param {{key: string, size: number, start: number, scale: string, title: string, entryId: number, documentId: number}} book
 * @param {any} page the Blazor page: OpenEbook, EbookFile, PageChanged, ScaleChanged, Opened, Failed, Link, LinkFollowed,
 *   FindKey, Offline
 */
export async function openEbook(stage, book, page) {
  const reader = new EbookReader(stage, book, page);
  await reader.start();
  return reader;
}

async function workerReady() {
  if (!('serviceWorker' in navigator)) throw new Error("This browser doesn't let Bibliotaph show e-books.");
  ebookWorker ??= (async () => {
    navigator.serviceWorker.addEventListener('message', event => {
      if (event.data?.t !== 'bibliotaph-ebook') return;
      const port = event.ports[0];
      const reader = ebooks.get(event.data.session);
      if (!port) return;
      if (!reader) {
        port.postMessage(null);
        return;
      }
      reader.answer(String(event.data.path)).then(found => port.postMessage(found), () => port.postMessage(null));
    });
    navigator.serviceWorker.startMessages();
    const registration = await navigator.serviceWorker.register(new URL('../ebook-sw.js', import.meta.url), { scope: EbookScope });
    if (registration.active) return;
    const installing = registration.installing ?? registration.waiting;
    await new Promise((resolve, reject) => installing.addEventListener('statechange', () => {
      if (installing.state === 'activated') resolve();
      else if (installing.state === 'redundant') reject(new Error("The browser didn't start the e-book reader."));
    }));
  })();
  try {
    await ebookWorker;
  } catch (e) {
    ebookWorker = null;
    throw e;
  }
}

class EbookReader {
  constructor(stage, book, page) {
    this.stage = stage;
    this.host = stage.querySelector('.bt-ebook');
    this.book = book;
    this.page = page;
    this.session = crypto.randomUUID();
    this.base = `${EbookScope}${this.session}/`;
    this.current = book.start;
    this.scale = book.scale || 'page-width';
    this.version = 0;
    this.closed = false;
    this.awake = stayAwake();
    this.listeners = [];
    this.found = null;
  }

  async start() {
    const { title, entryId, documentId } = this.book;
    this.cache = await BookCache.open(this.book.key, this.book.size, () => this.page.invokeMethodAsync('Link'), { title, entryId, documentId });
    trim(this.book.key).catch(() => { });
    let bytes;
    try {
      bytes = await this.cache.read(0, this.book.size);
      await workerReady();
    } catch (e) {
      if (!this.closed) this.page.invokeMethodAsync('Failed', e instanceof LinkError ? 'link' : 'other', String(e?.message ?? ''));
      return;
    }
    if (this.closed) return;
    // Null when the book can't be shown, which the page has said why.
    const built = await this.page.invokeMethodAsync('OpenEbook', bytes, this.base + 'res/');
    if (!built || this.closed) return;
    this.html = built.page;
    this.nonce = built.nonce;
    this.fixed = built.fixedLayout;
    ebooks.set(this.session, this);
    this.#listen(window, 'message', e => this.#message(e));
    this.#listen(document, 'keydown', e => this.#key(e));
    // The page follows the theme, as the Windows reader's does.
    const dark = matchMedia('(prefers-color-scheme: dark)');
    this.#listen(dark, 'change', () => this.#reload());
    const theme = new MutationObserver(() => this.#reload());
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    this.listeners.push(() => theme.disconnect());
    this.frame = document.createElement('iframe');
    this.frame.className = 'bt-ebookframe';
    this.frame.title = title;
    this.host.replaceChildren(this.frame);
    this.#show();
    this.page.invokeMethodAsync('Opened', true);
    this.#offline();
  }

  #listen(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    this.listeners.push(() => target.removeEventListener(type, handler, options));
  }

  #show() {
    this.ready = false;
    this.frame.src = `${this.base}book.html?v=${++this.version}`;
  }

  #reload() {
    if (this.frame) this.#show();
  }

  get #bt() {
    try {
      return this.ready ? this.frame.contentWindow.bt : null;
    } catch {
      return null;
    }
  }

  /** What the frame asks for: its page, or a file of the book, with the policy the Windows reader gives each. */
  async answer(path) {
    const origin = location.origin;
    const files = `${origin}${this.base}res/`;
    if (path === 'book.html') {
      return {
        bytes: new TextEncoder().encode(this.html.replace(ThemeSlot, `<style nonce="${this.nonce}">${themeCss()}</style>`)),
        type: 'text/html; charset=utf-8',
        csp: `default-src 'none'; script-src 'nonce-${this.nonce}'; style-src 'nonce-${this.nonce}'; img-src ${files}; font-src ${files}; `
          + `frame-src ${files}; media-src 'none'; base-uri 'none'; form-action 'none'`,
      };
    }
    if (!path.startsWith('res/')) return null;
    let name;
    try {
      name = path.slice(4).split('/').map(decodeURIComponent).join('/');
    } catch {
      return null;
    }
    const found = await this.page.invokeMethodAsync('EbookFile', name);
    if (!found || this.closed) return null;
    const html = found.mediaType === 'text/html';
    return {
      bytes: found.bytes,
      type: html ? 'text/html; charset=utf-8' : found.mediaType,
      csp: html
        ? `default-src 'none'; style-src ${files} 'unsafe-inline'; img-src ${files}; font-src ${files}; base-uri 'none'; form-action 'none'`
        : `default-src 'none'; style-src 'unsafe-inline'; img-src ${files}`,
    };
  }

  // The reader's keys while the toolbar has the keyboard; the book's page sends the same ones itself.
  #key(e) {
    if (document.querySelector('.bt-dialog')) return;
    const ctrl = (e.ctrlKey || e.metaKey) && !e.altKey;
    if (e.key === 'F3') this.page.invokeMethodAsync('FindKey', e.shiftKey);
    else if (ctrl && (e.key === 'f' || e.key === 'F')) {
      const box = document.querySelector('.bt-findbox');
      if (!box) return;
      box.focus();
      box.select();
    } else if (ctrl && (e.key === '+' || e.key === '=')) this.zoom(true);
    else if (ctrl && (e.key === '-' || e.key === '_')) this.zoom(false);
    else if (ctrl && e.key === '0') this.setScale('page-width');
    else return;
    e.preventDefault();
  }

  // The page script's messages, as the Windows reader takes them: only from this frame's page.
  #message(e) {
    if (e.origin !== location.origin || !this.frame || e.source !== this.frame.contentWindow) return;
    const message = e.data;
    if (!message || typeof message !== 'object') return;
    switch (message.t) {
      case 'ready':
        this.ready = true;
        this.#zoomFrame();
        this.#bt?.setFit(this.scale === 'page-fit' ? 'page' : 'width');
        this.#bt?.start(this.current);
        if (this.terms) this.markTerms(...this.terms);
        break;
      case 'page':
        if (Number.isInteger(message.page) && message.page >= 0) {
          this.current = message.page;
          this.page.invokeMethodAsync('PageChanged', message.page);
        }
        break;
      case 'linked':
        if (Number.isInteger(message.from) && message.from >= 0) this.page.invokeMethodAsync('LinkFollowed', message.from);
        break;
      case 'selection':
        this.selected = Number.isInteger(message.page) ? message.page : -1;
        break;
      case 'web':
        if (typeof message.url === 'string' && /^(https?|mailto):/i.test(message.url)) window.open(message.url, '_blank', 'noopener,noreferrer');
        break;
      case 'find': {
        const box = document.querySelector('.bt-findbox');
        box?.focus();
        box?.select();
        break;
      }
      case 'next':
      case 'previous':
        this.page.invokeMethodAsync('FindKey', message.t === 'previous');
        break;
      case 'zoom':
        if (message.step === 0) this.setScale('page-width');
        else this.zoom(message.step > 0);
        break;
    }
  }

  /** The page the selected text starts on, or -1 when nothing is selected. */
  selectionPage() { return this.selected ?? -1; }

  /** Goes to a page (from 0). */
  goTo(index) {
    this.current = index;
    this.#bt?.goTo(index);
  }

  // A zoom step is the page's own zoom, as the Windows reader's is the browser's; a fitted size is 100%.
  #zoomFrame() {
    try {
      const root = this.frame.contentDocument?.documentElement;
      if (root) root.style.zoom = this.scale.startsWith('page-') ? '' : String(Number(this.scale) || 1);
    } catch {
      // The frame is between pages.
    }
  }

  /** "page-width" (fit width, or a flowing book's usual size), "page-fit", or a scale where 1 is 100%. */
  setScale(value) {
    this.scale = value;
    this.#zoomFrame();
    if (value.startsWith('page-')) this.#bt?.setFit(value === 'page-fit' ? 'page' : 'width');
    this.page.invokeMethodAsync('ScaleChanged', value);
  }

  zoom(up) {
    const current = this.scale.startsWith('page-') ? 1 : Number(this.scale) || 1;
    const next = up ? ZoomSteps.find(z => z > current + 0.01) ?? ZoomSteps[ZoomSteps.length - 1]
      : [...ZoomSteps].reverse().find(z => z < current - 0.01) ?? ZoomSteps[0];
    this.setScale(String(next));
  }

  /** The words of the search the book was opened from, marked on the page it opened at; the first brought into view. */
  markTerms(terms, pageIndex) {
    this.terms = [terms, pageIndex];
    const bt = this.#bt;
    if (!bt || this.fixed) return;
    if (bt.terms(terms, pageIndex).length > 0) bt.show('terms', 0);
  }

  /** What Find found on that page: every match marked, the nth on the page standing out and in view. */
  showMatch(phrase, pageIndex, nth) {
    const bt = this.#bt;
    if (!bt) return;
    if (this.fixed) {
      this.goTo(pageIndex);
      return;
    }
    if (this.found?.phrase !== phrase) this.found = { phrase, pages: bt.find(phrase, 2000) };
    let at = -1;
    for (let i = 0, seen = 0; i < this.found.pages.length; i++) {
      if (this.found.pages[i] !== pageIndex) continue;
      if (seen++ === nth) {
        at = i;
        break;
      }
    }
    if (at >= 0) bt.show('find', at);
    else this.goTo(pageIndex);
  }

  /** Find cleared: the search's own words again. */
  clearFind() {
    this.found = null;
    const bt = this.#bt;
    if (!bt) return;
    if (this.terms && !this.fixed) bt.show('terms', -1);
    else bt.show(null, -1);
  }

  #offline() {
    if (!this.closed) this.page.invokeMethodAsync('Offline', this.cache.stored, this.cache.kept, this.cache.progress);
  }

  keepOffline(on) {
    this.cache.keepOffline(on).then(() => this.#offline());
  }

  dispose() {
    this.closed = true;
    this.awake();
    ebooks.delete(this.session);
    for (const stop of this.listeners) stop();
    this.cache?.close();
    this.host.replaceChildren();
  }
}

/** The theme's colours for the book's page, as the Windows reader gives its own: the app's tokens and its scheme. */
function themeCss() {
  const root = document.documentElement;
  const style = getComputedStyle(root);
  const dark = root.dataset.theme ? root.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
  const tokens = ThemeTokens.map(name => `${name}:${style.getPropertyValue(name).trim()}`).join(';');
  return `:root{color-scheme:${dark ? 'dark' : 'light'};${tokens};--bt-hit:color-mix(in srgb,var(--bt-accent) 40%,transparent)}`;
}

export function popOut(url, name) {
  const opened = window.open(url, name || '_blank', 'popup,width=960,height=900');
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
