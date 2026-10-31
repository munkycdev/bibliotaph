// Send to Bibliotaph's button: on a service's library page it takes the page as it shows,
// opens Bibliotaph's /online/receive beside it and hands the page to bibliotaph.js there. Then it
// fetches the covers that tab asks for: here first, where the services' sites and image hosts need no permission
// from them, else on the service's page itself. Nothing is sent anywhere but Bibliotaph, and only when clicked.

const BIBLIOTAPH = 'https://bibliotaph.online';
const SITES = ['dndbeyond.com', 'demiplane.com', 'humblebundle.com', 'drivethrurpg.com', 'dmsguild.com'];
const MOST_COVER_BYTES = 8 * 1024 * 1024;

// The page waiting for each receiving tab, and the service's tab it came from. Kept only while the worker runs: a send
// takes seconds, and one lost to the worker stopping is sent again with the button.
const waiting = new Map();

function isService(url) {
  try {
    const { protocol, hostname } = new URL(url);
    return protocol === 'https:' && SITES.some(site => hostname === site || hostname.endsWith('.' + site));
  } catch {
    return false;
  }
}

// Runs in the service's page. What is chosen in a list or typed in a search box lives in the page, not its markup, so
// it is written in first; never what is typed in a form that asks for a password.
function snapshot() {
  for (const list of document.querySelectorAll('select'))
    for (const option of list.options) option.toggleAttribute('selected', option.selected);
  for (const input of document.querySelectorAll('input')) {
    const type = (input.getAttribute('type') || 'text').toLowerCase();
    if (type === 'checkbox' || type === 'radio') input.toggleAttribute('checked', input.checked);
    else if ((type === 'text' || type === 'search') && !input.form?.querySelector('input[type=password]')) input.setAttribute('value', input.value);
  }
  return { url: location.href, html: document.documentElement.outerHTML };
}

function tell(tabId, text) {
  return chrome.scripting.executeScript({ target: { tabId }, func: message => alert(message), args: [text] }).catch(() => {});
}

async function send(tab) {
  if (!tab.id || !isService(tab.url || '')) {
    if (tab.id) await tell(tab.id, 'Open your library on D&D Beyond, Demiplane, Humble Bundle or DriveThruRPG, signed in, then click Send to Bibliotaph there.');
    return;
  }
  let page;
  try {
    [{ result: page }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: snapshot });
  } catch {
    await tell(tab.id, "Bibliotaph couldn't read this page. Reload it and try again.");
    return;
  }
  const receiving = await chrome.tabs.create({ url: BIBLIOTAPH + '/online/receive', index: tab.index + 1, openerTabId: tab.id });
  waiting.set(receiving.id, { ...page, serviceTab: tab.id });
}

chrome.action.onClicked.addListener(send);

chrome.tabs.onRemoved.addListener(tabId => waiting.delete(tabId));

async function bytesOf(response) {
  if (!response.ok) return null;
  const buffer = await response.arrayBuffer();
  return buffer.byteLength > 0 && buffer.byteLength <= MOST_COVER_BYTES ? { type: response.headers.get('content-type') || '', buffer } : null;
}

// Runs in the service's page, for a cover only it may fetch: its own, or one sent with the service's cookies.
async function fetchOnPage(url, most) {
  try {
    const response = await fetch(url);
    if (!response.ok) return null;
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.length === 0 || bytes.length > most) return null;
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    return { type: response.headers.get('content-type') || '', data: btoa(binary) };
  } catch {
    return null;
  }
}

function base64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

async function cover(url, serviceTab) {
  if (!/^https:/.test(url)) return null;
  try {
    const got = await bytesOf(await fetch(url, { credentials: 'omit' }));
    if (got) return { type: got.type, data: base64(got.buffer) };
  } catch {
    // Not a host this extension may read: the service's page may.
  }
  if (serviceTab === undefined) return null;
  try {
    const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: serviceTab }, func: fetchOnPage, args: [url, MOST_COVER_BYTES] });
    return result;
  } catch {
    return null;
  }
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  const tabId = sender.tab?.id;
  if (!message || tabId === undefined || !sender.url?.startsWith(BIBLIOTAPH + '/')) return false;
  if (message.bibliotaph === 'take') {
    const page = waiting.get(tabId);
    reply(page ? { url: page.url, html: page.html } : null);
    return false;
  }
  if (message.bibliotaph === 'cover' && typeof message.url === 'string') {
    cover(message.url, waiting.get(tabId)?.serviceTab).then(reply);
    return true;
  }
  if (message.bibliotaph === 'done') waiting.delete(tabId);
  return false;
});
