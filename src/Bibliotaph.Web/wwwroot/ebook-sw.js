// The e-book reader's frame, answered from the book. Everything under /ebook-res/{session}/ is asked of the reader
// that opened that session (in this tab or a pop-out), which answers from the book it holds, with the same security
// headers the Windows reader gives its page; nothing here ever reaches the server. Registered for /ebook-res/ only, so
// the rest of Bibliotaph never goes through it.
const Prefix = '/ebook-res/';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin || !url.pathname.startsWith(Prefix)) return;
  const rest = url.pathname.slice(Prefix.length);
  const slash = rest.indexOf('/');
  if (slash <= 0 || event.request.method !== 'GET') {
    event.respondWith(refuse());
    return;
  }
  event.respondWith(answer(rest.slice(0, slash), rest.slice(slash + 1)));
});

async function answer(session, path) {
  // Only a reader's page holds a book; the frames themselves don't answer.
  const pages = (await self.clients.matchAll({ type: 'window', includeUncontrolled: true }))
    .filter(client => !new URL(client.url).pathname.startsWith(Prefix));
  let found = null;
  try {
    found = await Promise.any(pages.map(page => ask(page, session, path)));
  } catch {
    return refuse();
  }
  // Nothing is kept in the browser's cache: two books can have a file at the same path.
  return new Response(found.bytes, {
    status: 200,
    headers: {
      'content-type': found.type,
      'content-security-policy': found.csp,
      'cache-control': 'no-store',
      'x-content-type-options': 'nosniff',
      'referrer-policy': 'no-referrer',
    },
  });
}

function ask(page, session, path) {
  return new Promise((resolve, reject) => {
    const channel = new MessageChannel();
    const timer = setTimeout(reject, 30000);
    channel.port1.onmessage = event => {
      clearTimeout(timer);
      if (event.data) resolve(event.data);
      else reject();
    };
    page.postMessage({ t: 'bibliotaph-ebook', session, path }, [channel.port2]);
  });
}

function refuse() {
  return new Response(null, { status: 404, headers: { 'cache-control': 'no-store' } });
}
