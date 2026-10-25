// The Library page's helpers: what this browser remembers about how the library is shown, and a watch on the end of
// the cards so more are drawn as it comes into view, as the Windows app only creates the covers in view.
(function () {
  var prefix = 'bibliotaph.';
  window.bibliotaphPrefs = {
    get: function (key) {
      try { return localStorage.getItem(prefix + key); } catch (e) { return null; }
    },
    set: function (key, value) {
      try { localStorage.setItem(prefix + key, value); } catch (e) { /* private window: kept for this page only */ }
    }
  };
  window.bibliotaphMore = {
    watch: function (element, page) {
      if (!element || !('IntersectionObserver' in window)) return null;
      var observer = new IntersectionObserver(function (entries) {
        if (entries.some(function (e) { return e.isIntersecting; })) page.invokeMethodAsync('ShowMore');
      }, { rootMargin: '600px 0px' });
      observer.observe(element);
      return { stop: function () { observer.disconnect(); } };
    }
  };
})();
// A pin dragged to a new place in the sidebar, or a binder's item by its grip: Firefox starts a drag only once it
// carries some data.
document.addEventListener('dragstart', function (e) {
  if (e.target && e.target.closest && e.target.closest('.bt-pinrow, .bt-bindergrip') && e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', '');
  }
});
// Select mode's keys, as in the Windows app: Ctrl+A ticks everything shown and Esc leaves, unless a box or a dialog
// has them.
window.bibliotaphSelect = (function () {
  var page = null;
  document.addEventListener('keydown', function (e) {
    if (!page) return;
    var target = e.target;
    var typing = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable);
    if (typing || document.querySelector('.bt-dialog, .bt-drawer')) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'a' || e.key === 'A')) {
      e.preventDefault();
      page.invokeMethodAsync('SelectAll');
    } else if (e.key === 'Escape') {
      page.invokeMethodAsync('StopSelecting');
    }
  });
  return {
    start: function (p) { page = p; },
    stop: function () { page = null; }
  };
})();
// A file made in the browser, such as Export these' CSV, saved where the browser saves downloads. A CSV starts with a
// byte order mark so Excel reads its accents and dashes right.
window.bibliotaphSave = function (name, type, text) {
  var url = URL.createObjectURL(new Blob(['\ufeff' + text], { type: type }));
  var link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
};
