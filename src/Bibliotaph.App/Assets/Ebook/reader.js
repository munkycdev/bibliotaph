// The e-book reader's page script: the only script on the page (the book's own are taken out before it gets here).
// It says which page is in view, goes to a page, finds text and marks it, and hands links and keys to the app.
(() => {
  'use strict';

  const post = message => {
    try {
      window.chrome.webview.postMessage(message);
    } catch {
      // No app to tell: a page opened outside the reader.
    }
  };

  const book = document.getElementById('bt-book');
  const fixed = document.body.classList.contains('bt-fixed');
  const markers = Array.from(book.getElementsByTagName('bt-page'));
  let current = -1;

  // ---------- Where the reader is ----------

  // The line a page has to start above to be the page in view.
  const readingLine = () => Math.min(window.innerHeight * 0.3, 160);

  // The page in view: the last whose start is above a line near the top of the window, or, scrolled to the end, the
  // last whose start shows at all.
  function pageInView() {
    if (markers.length === 0) return 0;
    const atEnd = window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2;
    const line = atEnd ? window.innerHeight - 1 : readingLine();
    let low = 0, high = markers.length - 1, found = 0;
    while (low <= high) {
      const middle = (low + high) >> 1;
      if (markers[middle].getBoundingClientRect().top <= line) {
        found = middle;
        low = middle + 1;
      } else high = middle - 1;
    }
    return Number(markers[found].dataset.page);
  }

  let tracking = false;
  function track() {
    if (tracking) return;
    tracking = true;
    requestAnimationFrame(() => {
      tracking = false;
      const page = pageInView();
      if (page === current) return;
      current = page;
      post({ t: 'page', page });
    });
  }

  // Scrolls an element's top, or a range's, to just below the top of the window, then again once the sections it
  // passed have been laid out, which can move it.
  function scrollToTop(target, margin) {
    let tries = 0;
    const step = () => {
      const top = target.getBoundingClientRect().top;
      if (Math.abs(top - margin) > 2) window.scrollBy(0, top - margin);
      if (++tries < 4) requestAnimationFrame(step);
      else track();
    };
    step();
  }

  function goToPage(page) {
    const marker = document.getElementById('bt-p' + page);
    if (!marker) return;
    // Going to the page a match is on shows the match, as Find and the search hit go to its page too.
    if (active && active.current >= 0 && active.list[active.current].page === page) {
      reveal(rangeOf(active.list[active.current]));
      return;
    }
    if (marker === markers[0]) window.scrollTo(0, 0);
    else scrollToTop(marker, 16);
    current = page;
    post({ t: 'page', page });
  }

  // ---------- Finding text ----------

  // The book's text as one string, white space collapsed and lower-cased letter by letter (so offsets map back to
  // the page), with where each text node starts in it and where each page starts. Made the first time it's needed.
  const blockTags = new Set(['ADDRESS', 'ARTICLE', 'ASIDE', 'BLOCKQUOTE', 'BR', 'CAPTION', 'DD', 'DIV', 'DL', 'DT', 'FIGCAPTION', 'FIGURE',
    'FOOTER', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'HEADER', 'HR', 'LI', 'MAIN', 'NAV', 'OL', 'P', 'PRE', 'SECTION', 'TABLE', 'TD', 'TH',
    'TR', 'UL']);
  let index = null;

  function lower(c) {
    const l = c.toLowerCase();
    return l.length === 1 ? l : c;
  }

  function buildIndex() {
    const parts = [];
    const nodes = [];
    const pages = [];
    let length = 0;
    let space = true;
    const pad = () => {
      if (space) return;
      parts.push(' ');
      length++;
      space = true;
    };
    const stack = [[book, false]];
    while (stack.length > 0) {
      const [node, leaving] = stack.pop();
      if (node.nodeType === Node.TEXT_NODE) {
        nodes.push({ node, start: length, space });
        const data = node.data;
        let out = '';
        for (let i = 0; i < data.length; i++) {
          const c = data[i];
          if (/\s/.test(c)) {
            if (!space) {
              out += ' ';
              space = true;
            }
          } else {
            out += lower(c);
            space = false;
          }
        }
        parts.push(out);
        length += out.length;
        continue;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) continue;
      if (leaving) {
        pad();
        continue;
      }
      if (node.localName === 'bt-page') {
        pages.push({ at: length, page: Number(node.dataset.page) });
        continue;
      }
      const block = blockTags.has(node.tagName);
      if (block) {
        pad();
        stack.push([node, true]);
      }
      for (let i = node.childNodes.length - 1; i >= 0; i--) stack.push([node.childNodes[i], false]);
    }
    return { text: parts.join(''), nodes, pages };
  }

  function normalize(query) {
    let out = '';
    let space = true;
    for (const c of String(query)) {
      if (/\s/.test(c)) {
        if (!space) out += ' ';
        space = true;
      } else {
        out += c.length === 1 ? lower(c) : c;
        space = false;
      }
    }
    return out.trim();
  }

  function pageAt(offset) {
    const pages = index.pages;
    let low = 0, high = pages.length - 1, found = pages.length > 0 ? pages[0].page : 0;
    while (low <= high) {
      const middle = (low + high) >> 1;
      if (pages[middle].at <= offset) {
        found = pages[middle].page;
        low = middle + 1;
      } else high = middle - 1;
    }
    return found;
  }

  // The text node and the offset in it of a place in the index's text. A place in collapsed white space falls at the
  // white space's start.
  function locate(offset) {
    const nodes = index.nodes;
    let low = 0, high = nodes.length - 1, found = 0;
    while (low <= high) {
      const middle = (low + high) >> 1;
      if (nodes[middle].start <= offset) {
        found = middle;
        low = middle + 1;
      } else high = middle - 1;
    }
    // Several nodes can start at the same place (empty ones); the last of them holds it.
    const entry = nodes[found];
    let at = entry.start;
    let space = entry.space;
    const data = entry.node.data;
    for (let i = 0; i < data.length; i++) {
      if (at === offset && !(/\s/.test(data[i]) && space)) return { node: entry.node, offset: i };
      if (/\s/.test(data[i])) {
        if (!space) {
          at++;
          space = true;
        }
      } else {
        at++;
        space = false;
      }
    }
    return { node: entry.node, offset: data.length };
  }

  function rangeOf(hit) {
    if (hit.range) return hit.range;
    const start = locate(hit.at);
    const end = locate(hit.at + hit.length - 1);
    const range = document.createRange();
    range.setStart(start.node, start.offset);
    range.setEnd(end.node, Math.min(end.offset + 1, end.node.data.length));
    hit.range = range;
    return range;
  }

  function search(query, from, to, most) {
    const needle = normalize(query);
    const found = [];
    if (needle.length === 0) return found;
    index ??= buildIndex();
    let at = index.text.indexOf(needle, from);
    while (at >= 0 && at < to && found.length < most) {
      found.push({ at, length: needle.length, page: pageAt(at) });
      at = index.text.indexOf(needle, at + needle.length);
    }
    return found;
  }

  const lists = { find: null, terms: null };
  let active = null;

  // Every match of the text in the book, in order; the page of each.
  function find(query, most) {
    lists.find = { list: search(query, 0, Infinity, most), current: -1 };
    return lists.find.list.map(h => h.page);
  }

  // The words of a search on the page it found, as when the reader opens from a hit; the page of each.
  function terms(words, page) {
    index ??= buildIndex();
    const pages = index.pages;
    const at = pages.findIndex(p => p.page === page);
    const from = at >= 0 ? pages[at].at : 0;
    const to = at >= 0 && at + 1 < pages.length ? pages[at + 1].at : Infinity;
    const list = words.flatMap(word => search(word, from, to, 200)).sort((a, b) => a.at - b.at);
    lists.terms = { list, current: -1 };
    return list.map(h => h.page);
  }

  // Marks a list's matches, the current one standing out and brought into view; none for no list.
  function show(name, currentHit) {
    active = name ? lists[name] : null;
    if (!('highlights' in CSS)) return;
    CSS.highlights.clear();
    if (!active || active.list.length === 0) return;
    CSS.highlights.set('bt-hit', new Highlight(...active.list.map(rangeOf)));
    active.current = currentHit;
    if (currentHit < 0 || currentHit >= active.list.length) return;
    const range = rangeOf(active.list[currentHit]);
    CSS.highlights.set('bt-current', new Highlight(range));
    reveal(range);
  }

  function reveal(range) {
    const rect = range.getBoundingClientRect();
    if (rect.top >= 48 && rect.bottom <= window.innerHeight - 48 && rect.height > 0) {
      track();
      return;
    }
    // Just above the reading line, so the page in view is the match's page.
    scrollToTop(range, Math.max(16, Math.round(readingLine()) - 8));
  }

  // ---------- The page the selection is on ----------

  function pageOfNode(node) {
    let low = 0, high = markers.length - 1, found = 0;
    while (low <= high) {
      const middle = (low + high) >> 1;
      const position = markers[middle].compareDocumentPosition(node);
      if (position & Node.DOCUMENT_POSITION_FOLLOWING) {
        found = middle;
        low = middle + 1;
      } else high = middle - 1;
    }
    return markers.length ? Number(markers[found].dataset.page) : 0;
  }

  let selectionTimer = 0;
  document.addEventListener('selectionchange', () => {
    clearTimeout(selectionTimer);
    selectionTimer = setTimeout(() => {
      const selection = document.getSelection();
      const page = !selection || selection.rangeCount === 0 || selection.isCollapsed ? -1 : pageOfNode(selection.getRangeAt(0).startContainer);
      post({ t: 'selection', page });
    }, 150);
  });

  // ---------- Links, keys and the wheel ----------

  document.addEventListener('click', event => {
    const link = event.target instanceof Element ? event.target.closest('a[href]') : null;
    if (!link) return;
    event.preventDefault();
    const href = link.getAttribute('href');
    if (href.startsWith('#')) {
      const target = document.getElementById(decodeURIComponent(href.slice(1)));
      if (!target) return;
      const from = current;
      scrollToTop(target, 16);
      post({ t: 'linked', from });
    } else post({ t: 'web', url: link.href });
  });
  document.addEventListener('auxclick', event => {
    if (event.target instanceof Element && event.target.closest('a[href]')) event.preventDefault();
  });

  // The app's own keys, for when the page has the keyboard.
  window.addEventListener('keydown', event => {
    const control = event.ctrlKey && !event.altKey && !event.metaKey;
    let message = null;
    if (control && (event.key === 'f' || event.key === 'F')) message = { t: 'find' };
    else if (event.key === 'F3') message = { t: event.shiftKey ? 'previous' : 'next' };
    else if (control && (event.key === '+' || event.key === '=')) message = { t: 'zoom', step: 1 };
    else if (control && (event.key === '-' || event.key === '_')) message = { t: 'zoom', step: -1 };
    else if (control && event.key === '0') message = { t: 'zoom', step: 0 };
    if (!message) return;
    event.preventDefault();
    post(message);
  });
  window.addEventListener('wheel', event => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    post({ t: 'zoom', step: event.deltaY < 0 ? 1 : -1 });
  }, { passive: false });

  // ---------- A fixed-layout book's pages, fitted ----------

  let fitMode = 'width';
  function fit() {
    if (!fixed) return;
    const width = book.clientWidth;
    const height = window.innerHeight - 32;
    for (const section of book.querySelectorAll('section.bt-chapter')) {
      const w = Number(section.dataset.width) || 600;
      const h = Number(section.dataset.height) || 900;
      let scale = width / w;
      if (fitMode === 'page') scale = Math.min(scale, height / h);
      const frame = section.querySelector('iframe');
      section.style.width = `${Math.floor(w * scale)}px`;
      section.style.height = `${Math.floor(h * scale)}px`;
      if (!frame) continue;
      frame.style.width = `${w}px`;
      frame.style.height = `${h}px`;
      frame.style.transform = `scale(${scale})`;
    }
  }

  function setFit(mode) {
    fitMode = mode === 'page' ? 'page' : 'width';
    const page = current;
    fit();
    if (page >= 0) goToPage(page);
  }

  window.addEventListener('scroll', track, { passive: true });
  window.addEventListener('resize', () => {
    fit();
    track();
  });
  fit();

  // Shows the book at a page, once: until then the page is hidden, so the reader never sees it start at the top.
  function start(page) {
    goToPage(page);
    document.body.classList.add('bt-shown');
  }

  window.bt = { start, goTo: goToPage, find, terms, show, setFit };
  post({ t: 'ready', pages: markers.length });
})();
