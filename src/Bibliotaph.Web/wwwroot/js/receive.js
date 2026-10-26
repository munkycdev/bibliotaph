// Send to Bibliotaph's receiving end (Pages/OnlineReceive.razor): takes the library page js/inbox.js kept, from a
// service Bibliotaph reads or from the extension, then asks whoever sent it for the covers the preview still wants.

const inbox = () => window.bibliotaphInbox;
let sender = null;
let covers = [];

/** Whether an origin is one of the services' sites (www.dndbeyond.com for dndbeyond.com), or this site's own. */
function allowed(origin, sites) {
  if (origin === location.origin) return true;
  try {
    const url = new URL(origin);
    return url.protocol === 'https:' && sites.some(site => url.hostname === site || url.hostname.endsWith('.' + site));
  } catch {
    return false;
  }
}

/** Resolves when the inbox changes, or after ms. */
function changeOrTimeout(ms) {
  return new Promise(resolve => {
    const timer = setTimeout(() => {
      inbox().changed = null;
      resolve();
    }, ms);
    inbox().changed = () => {
      clearTimeout(timer);
      inbox().changed = null;
      resolve();
    };
  });
}

/**
 * The first page sent from one of the sites, or null if none arrives within ms: { url, html }. The sender is kept for
 * the covers.
 */
export async function take(sites, ms) {
  const until = Date.now() + ms;
  while (true) {
    const page = inbox().pages.find(p => allowed(p.origin, sites));
    if (page) {
      inbox().pages.length = 0;
      sender = page;
      return { url: page.url, html: page.html };
    }
    const left = until - Date.now();
    if (left <= 0) return null;
    await changeOrTimeout(left);
  }
}

/**
 * Asks the sender for the covers at these addresses, resolved against the page's, and waits until it says it's done
 * or ms pass. Returns how many arrived; cover(i) and coverBytes(i) give each.
 */
export async function fetchCovers(urls, ms) {
  covers = [];
  if (!sender || urls.length === 0) return 0;
  const { origin, source, url: page } = sender;
  const absolute = [...new Set(urls.map(u => {
    try {
      return new URL(u, page || undefined).href;
    } catch {
      return null;
    }
  }).filter(u => u && /^https?:/.test(u)))];
  inbox().covers.length = 0;
  inbox().done.delete(origin);
  try {
    source.postMessage({ bibliotaph: 'covers', urls: absolute }, origin);
  } catch {
    return 0;
  }
  const until = Date.now() + ms;
  while (!inbox().done.has(origin) && Date.now() < until) await changeOrTimeout(until - Date.now());
  covers = inbox().covers.filter(c => c.origin === origin);
  inbox().covers.length = 0;
  return covers.length;
}

/** A cover that arrived: its address, its file name as the server names covers, and its type. */
export function cover(i) {
  const c = covers[i];
  let name = '';
  try {
    const path = new URL(c.url).pathname;
    name = decodeURIComponent(path.slice(path.lastIndexOf('/') + 1));
  } catch {
    // A name that can't be read leaves the cover unsent.
  }
  return { url: c.url, name, type: c.type };
}

export function coverBytes(i) {
  return new Uint8Array(covers[i].bytes);
}

export function release() {
  sender = null;
  covers = [];
}
