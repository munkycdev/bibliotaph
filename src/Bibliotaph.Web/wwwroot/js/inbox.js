// Library pages sent by the Send to Bibliotaph bookmark or the browser extension, and the covers they fetch, kept from
// the moment the page loads: they can arrive before Blazor has started, and js/receive.js takes them from here. The
// bookmark posts from the service's page that opened this tab; the extension posts from its script in this tab, so as
// this site. Which senders count is decided when the page is taken, by the services Bibliotaph reads.
(() => {
  const inbox = (window.bibliotaphInbox = { pages: [], covers: [], done: new Set(), changed: null });
  window.addEventListener('message', e => {
    const d = e.data;
    if (!d || typeof d !== 'object' || typeof d.bibliotaph !== 'string') return;
    if (d.bibliotaph === 'page' && typeof d.html === 'string')
      inbox.pages.push({ origin: e.origin, source: e.source, url: String(d.url || ''), html: d.html });
    else if (d.bibliotaph === 'cover' && typeof d.url === 'string' && d.bytes instanceof ArrayBuffer)
      inbox.covers.push({ origin: e.origin, url: d.url, type: String(d.type || ''), bytes: d.bytes });
    else if (d.bibliotaph === 'covers-done') inbox.done.add(e.origin);
    else return;
    inbox.changed?.();
  });
  // The bookmark waits for this before it sends the page; it says nothing about the user, so any opener may hear it.
  if (location.pathname === '/online/receive' && window.opener) {
    try {
      window.opener.postMessage({ bibliotaph: 'ready' }, '*');
    } catch {
      // The service's page is gone; the page below says nothing arrived.
    }
  }
})();
