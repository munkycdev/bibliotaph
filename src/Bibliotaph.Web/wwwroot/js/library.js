// The Library page's helpers: what this browser remembers about how the library is shown, and a watch on the end of
// the cards so more are drawn as it comes into view.
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
// Select mode's keys: Ctrl+A ticks everything shown and Esc leaves, unless a box or a dialog
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
    } else if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'z' || e.key === 'Z')) {
      // Organize mode takes back its own changes one at a time; a box keeps its own undo.
      e.preventDefault();
      page.invokeMethodAsync('UndoLast');
    } else if (e.key === 'Escape') {
      page.invokeMethodAsync('StopSelecting');
    }
  });
  return {
    start: function (p) { page = p; },
    stop: function () { page = null; }
  };
})();
// Organize mode's drags. A card is picked up by moving the mouse a little with the button down, or by holding a finger
// on it, so a swipe still scrolls; the ticked cards go with a ticked one. What is under the pointer is named to the
// page, which says what a drop there does; the words follow the pointer, and the window scrolls near its top and bottom
// edges. Esc puts the cards down where they were.
window.bibliotaphOrganize = (function () {
  var page = null, drag = null, swallowClick = false;
  var MOVE = 6, HOLD = 350, EDGE = 64;

  function cardAt(el) { return el && el.closest ? el.closest('[data-organize-card]') : null; }

  function targetAt(x, y) {
    if (drag && drag.ghost) drag.ghost.style.display = 'none';
    var el = document.elementFromPoint(x, y);
    if (drag && drag.ghost) drag.ghost.style.display = '';
    return el && el.closest ? el.closest('[data-drop]') : null;
  }

  function cancel() {
    if (!drag) return;
    clearTimeout(drag.timer);
    if (drag.ghost) drag.ghost.remove();
    cancelAnimationFrame(drag.frame);
    var started = drag.started;
    drag = null;
    if (started && page) page.invokeMethodAsync('Drop', '', '', 0, 0);
  }

  function begin() {
    if (!drag || !page) return;
    drag.started = true;
    page.invokeMethodAsync('DragStart', drag.id).then(function (count) {
      if (!drag || !count) { cancel(); return; }
      var ghost = document.createElement('div');
      ghost.className = 'bt-dragghost';
      ghost.innerHTML = '<strong></strong><span></span>';
      ghost.firstChild.textContent = count;
      document.body.appendChild(ghost);
      drag.ghost = ghost;
      place(drag.x, drag.y);
      scroll();
    });
  }

  function place(x, y) {
    if (!drag || !drag.ghost) return;
    drag.ghost.style.left = (x + 14) + 'px';
    drag.ghost.style.top = (y + 14) + 'px';
  }

  // Keeps scrolling while the pointer rests near an edge.
  function scroll() {
    if (!drag || !drag.started) return;
    var step = drag.y < EDGE ? -12 : drag.y > window.innerHeight - EDGE ? 12 : 0;
    if (step) { window.scrollBy(0, step); over(drag.x, drag.y); }
    drag.frame = requestAnimationFrame(scroll);
  }

  function over(x, y) {
    var target = targetAt(x, y);
    var key = target ? target.getAttribute('data-drop') : '';
    if (key === drag.key) return;
    drag.key = key;
    var name = target ? target.getAttribute('data-drop-name') || '' : '';
    page.invokeMethodAsync('DragOver', key, name).then(function (hover) {
      if (!drag || !drag.ghost || drag.key !== key) return;
      drag.ghost.lastChild.textContent = hover.label || '';
      drag.ghost.classList.toggle('no', !hover.ok && !!key);
    });
  }

  document.addEventListener('pointerdown', function (e) {
    if (!page || e.button !== 0 || drag) return;
    var card = cardAt(e.target);
    if (!card) return;
    drag = { id: card.getAttribute('data-organize-card'), x: e.clientX, y: e.clientY, fromX: e.clientX, fromY: e.clientY,
      touch: e.pointerType !== 'mouse', started: false, key: null };
    if (drag.touch) drag.timer = setTimeout(begin, HOLD);
  }, true);

  document.addEventListener('pointermove', function (e) {
    if (!drag) return;
    drag.x = e.clientX;
    drag.y = e.clientY;
    var moved = Math.abs(e.clientX - drag.fromX) + Math.abs(e.clientY - drag.fromY);
    if (!drag.started) {
      // A finger that moves before the hold is scrolling, not dragging.
      if (drag.touch) { if (moved > MOVE * 2) { clearTimeout(drag.timer); drag = null; } return; }
      if (moved > MOVE) begin();
      return;
    }
    e.preventDefault();
    place(e.clientX, e.clientY);
    over(e.clientX, e.clientY);
  }, true);

  // Once a finger has picked a card up, the page stays still under it.
  document.addEventListener('touchmove', function (e) { if (drag && drag.started) e.preventDefault(); }, { passive: false });

  document.addEventListener('pointerup', function (e) {
    if (!drag) return;
    clearTimeout(drag.timer);
    if (!drag.started) { drag = null; return; }
    var target = targetAt(e.clientX, e.clientY);
    var key = target ? target.getAttribute('data-drop') : '';
    var name = target ? target.getAttribute('data-drop-name') || '' : '';
    if (drag.ghost) drag.ghost.remove();
    cancelAnimationFrame(drag.frame);
    drag = null;
    swallowClick = true;
    setTimeout(function () { swallowClick = false; }, 0);
    page.invokeMethodAsync('Drop', key, name, e.clientX, e.clientY);
  }, true);

  document.addEventListener('pointercancel', cancel, true);

  // The click that ends a drag doesn't tick the card it started on.
  document.addEventListener('click', function (e) { if (swallowClick) { e.stopPropagation(); e.preventDefault(); swallowClick = false; } }, true);

  // A cover's image would otherwise be dragged as an image, which ends the pointer's events.
  document.addEventListener('dragstart', function (e) { if (page && cardAt(e.target)) e.preventDefault(); }, true);

  // A held finger doesn't open the browser's own menu, nor a long press the link's.
  document.addEventListener('contextmenu', function (e) { if (drag && drag.touch) e.preventDefault(); }, true);

  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && drag && drag.started) { e.stopPropagation(); cancel(); } }, true);

  return {
    start: function (p) { page = p; },
    stop: function () { cancel(); page = null; },
    // The menu for books dropped on a book opens where the pointer let go, kept inside the window.
    placeMenu: function (menu, x, y) {
      if (!menu) return;
      var r = menu.getBoundingClientRect(), gap = 8;
      menu.style.left = Math.max(gap, Math.min(x, window.innerWidth - r.width - gap)) + 'px';
      menu.style.top = Math.max(gap, Math.min(y, window.innerHeight - r.height - gap)) + 'px';
      menu.style.visibility = 'visible';
      var first = menu.querySelector('[role=menuitem]');
      if (first) first.focus();
    }
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
// The keys that work everywhere: Ctrl+K (or /, as on the web) puts the keyboard in the search
// box, Ctrl+B folds the sidebar, and Ctrl+/ shows every shortcut. In a reader popped out of the main window, Ctrl+K searches in that window.
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
    } else if (ctrl && !e.shiftKey && (e.key === 'b' || e.key === 'B') && !typing(e.target)) {
      // Ctrl+B folds the sidebar; in a text box it stays the box's (bold, in some).
      if (!shell || document.querySelector('.bt-dialog') || !document.querySelector('.bt-sidebar')) return;
      e.preventDefault();
      shell.invokeMethodAsync('ToggleSidebarFromKeyAsync');
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
// The search box's field guide: the shell draws it, and this tells it where the cursor is, since
// a Blazor event doesn't say. The box keeps the keyboard, so while the guide is open (data-guide="open") the arrow keys
// move its highlight, Enter or Tab picks the highlighted row and Esc closes it, before the box's own keys see them.
window.bibliotaphGuide = {
  attach: function (box, shell) {
    if (!box || box.dataset.guideAttached) return;
    box.dataset.guideAttached = '1';
    function at() { return box.selectionStart == null ? box.value.length : box.selectionStart; }
    function tell(method) { shell.invokeMethodAsync(method, box.value, at()); }
    box.addEventListener('focus', function () { tell('GuideFocused'); });
    box.addEventListener('input', function () { tell('GuideTextChanged'); });
    box.addEventListener('click', function () { tell('GuideCaretMoved'); });
    box.addEventListener('keyup', function (e) {
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End') tell('GuideCaretMoved');
    });
    box.addEventListener('blur', function () { shell.invokeMethodAsync('GuideClose'); });
    box.addEventListener('keydown', function (e) {
      var open = box.dataset.guide === 'open';
      var plain = !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey;
      var handled = true;
      if (e.key === ' ' && e.ctrlKey && !e.altKey && !e.shiftKey) tell('GuideRequest');
      else if (plain && e.key === 'ArrowDown') {
        if (open) shell.invokeMethodAsync('GuideMove', 1); else tell('GuideRequest');
      } else if (plain && open && e.key === 'ArrowUp') shell.invokeMethodAsync('GuideMove', -1);
      else if (plain && open && (e.key === 'Enter' || e.key === 'Tab') && box.getAttribute('aria-activedescendant')) shell.invokeMethodAsync('GuidePickAsync');
      else if (plain && open && e.key === 'Escape') shell.invokeMethodAsync('GuideClose');
      else handled = false;
      if (handled) {
        e.preventDefault();
        e.stopPropagation();
      }
    });
  },
  place: function (box, caret) {
    if (!box) return;
    box.focus();
    box.setSelectionRange(caret, caret);
  }
};
