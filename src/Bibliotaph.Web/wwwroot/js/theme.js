// The Theme button's choice: "light", "dark", or "system" (no attribute, so the CSS follows the device).
// Kept in this browser only; it's a preference, not part of the library.
(function () {
  var key = 'bibliotaph.theme';
  function read() {
    try { return localStorage.getItem(key) || 'system'; } catch (e) { return 'system'; }
  }
  function apply(mode) {
    if (mode === 'light' || mode === 'dark') document.documentElement.setAttribute('data-theme', mode);
    else document.documentElement.removeAttribute('data-theme');
  }
  apply(read());
  window.bibliotaphTheme = {
    get: read,
    set: function (mode) {
      try { localStorage.setItem(key, mode); } catch (e) { /* private window: still applies for this page */ }
      apply(mode);
    }
  };
})();
