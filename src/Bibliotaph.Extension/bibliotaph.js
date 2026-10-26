// In Bibliotaph's own tabs: says the extension is here, so Settings can say so; and on /online/receive hands over the
// page the button took, then the covers that page asks for, one at a time, over the same messages the bookmark uses.
document.documentElement.dataset.bibliotaphExtension = chrome.runtime.getManifest().version;

if (location.pathname === '/online/receive') {
  const post = message => window.postMessage(message, location.origin);

  const deliver = async () => {
    const page = await chrome.runtime.sendMessage({ bibliotaph: 'take' });
    if (page) post({ bibliotaph: 'page', url: page.url, html: page.html });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', deliver, { once: true });
  else deliver();

  window.addEventListener('message', async e => {
    if (e.source !== window || e.origin !== location.origin) return;
    const d = e.data;
    if (!d || d.bibliotaph !== 'covers' || !Array.isArray(d.urls)) return;
    for (const url of d.urls) {
      if (typeof url !== 'string') continue;
      const got = await chrome.runtime.sendMessage({ bibliotaph: 'cover', url });
      if (!got) continue;
      const binary = atob(got.data);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      window.postMessage({ bibliotaph: 'cover', url, type: got.type, bytes: bytes.buffer }, location.origin, [bytes.buffer]);
    }
    post({ bibliotaph: 'covers-done' });
    chrome.runtime.sendMessage({ bibliotaph: 'done' });
  });
}
