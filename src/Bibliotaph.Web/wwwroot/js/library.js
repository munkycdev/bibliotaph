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
// Copy details: a bug's report text on the clipboard. False when the browser refuses, as it may without a click.
window.bibliotaphCopy = function (text) {
  if (!navigator.clipboard) return Promise.resolve(false);
  return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
};
// The keys that work everywhere, as in the Windows app: Ctrl+K (or /, as on the web) puts the keyboard in the search
// box, and Ctrl+/ shows every shortcut. In a reader popped out of the main window, Ctrl+K searches in that window.
// Shift+Enter or a middle-click on a book's card pops it out into a window of its own.
window.bibliotaphShell = (function () {
  var shell = null;
  function typing(target) {
    return target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable);
  }
  function searchBox(win) {
    try { return win.document.querySelector('.bt-search input'); } catch (e) { return null; }
  }
  function focusSearch() {
    var box = searchBox(window);
    var main = window;
    if (!box && window.opener && !window.opener.closed) {
      main = window.opener;
      box = searchBox(main);
    }
    if (!box) return false;
    main.focus();
    box.focus();
    box.select();
    return true;
  }
  function popOut(card) {
    var url = card && card.getAttribute('data-popout');
    if (!url) return false;
    window.open(url, '_blank', 'popup,width=960,height=900');
    return true;
  }
  document.addEventListener('keydown', function (e) {
    var ctrl = (e.ctrlKey || e.metaKey) && !e.altKey;
    if (ctrl && !e.shiftKey && (e.key === 'k' || e.key === 'K')) {
      if (focusSearch()) e.preventDefault();
    } else if (ctrl && e.key === '/') {
      if (!shell || document.querySelector('.bt-dialog')) return;
      e.preventDefault();
      shell.invokeMethodAsync('ShowShortcuts');
    } else if (e.key === '/' && !ctrl && !e.altKey && !typing(e.target) && !document.querySelector('.bt-dialog')) {
      if (focusSearch()) e.preventDefault();
    } else if (e.key === 'Enter' && e.shiftKey && !ctrl && e.target && e.target.closest) {
      if (popOut(e.target.closest('[data-popout]'))) e.preventDefault();
    }
  });
  document.addEventListener('auxclick', function (e) {
    if (e.button === 1 && e.target && e.target.closest && popOut(e.target.closest('[data-popout]'))) e.preventDefault();
  });
  return {
    start: function (s) { shell = s; },
    stop: function () { shell = null; }
  };
})();
