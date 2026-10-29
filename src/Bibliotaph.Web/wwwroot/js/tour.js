// The tour's overlay: four dimmed strips round the step's controls (found by data-tour), so the controls themselves
// stay clear and clickable, and the callout beside them: to the right when there's room, else below, else above,
// kept inside the window. Styles are set through the CSSOM, as the page's policy refuses style attributes.
(function () {
  var pad = 6, gap = 14, edge = 12, rowHeight = 270;
  var current = null;

  function shown(el) {
    if (!el.isConnected) return false;
    var style = getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    var box = el.getBoundingClientRect();
    return box.width > 0 && box.height > 0;
  }

  function targets(anchors) {
    var found = [];
    anchors.forEach(function (name) {
      document.querySelectorAll('[data-tour="' + name + '"]').forEach(function (el) {
        if (shown(el)) found.push(el);
      });
    });
    return found;
  }

  function spot(elements, firstRow) {
    var r = null;
    elements.forEach(function (el) {
      var b = el.getBoundingClientRect();
      // A grid of covers is spotlit by its first row only.
      var bottom = firstRow ? b.top + Math.min(b.height, rowHeight) : b.bottom;
      r = r ? { left: Math.min(r.left, b.left), top: Math.min(r.top, b.top), right: Math.max(r.right, b.right), bottom: Math.max(r.bottom, bottom) }
            : { left: b.left, top: b.top, right: b.right, bottom: bottom };
    });
    if (!r) return null;
    return { left: r.left - pad, top: r.top - pad, right: r.right + pad, bottom: r.bottom + pad };
  }

  function px(el, left, top, width, height) {
    el.style.left = left + 'px';
    el.style.top = top + 'px';
    el.style.width = Math.max(0, width) + 'px';
    el.style.height = Math.max(0, height) + 'px';
  }

  function place() {
    if (!current || !current.root.isConnected) return false;
    var elements = targets(current.anchors);
    var s = spot(elements, current.firstRow);
    if (!s) return false;
    var w = window.innerWidth, h = window.innerHeight;
    var dims = current.root.querySelectorAll('.bt-tourdim');
    px(dims[0], 0, 0, w, s.top);
    px(dims[1], 0, s.top, s.left, s.bottom - s.top);
    px(dims[2], s.right, s.top, w - s.right, s.bottom - s.top);
    px(dims[3], 0, s.bottom, w, h - s.bottom);
    var callout = current.root.querySelector('.bt-tourcallout');
    var cw = callout.offsetWidth, ch = callout.offsetHeight;
    var left, top;
    if (s.right + gap + cw + edge <= w) { left = s.right + gap; top = s.top - 8; }
    else if (s.bottom + gap + ch + edge <= h) { left = s.right - cw; top = s.bottom + gap; }
    else { left = s.right - cw; top = s.top - gap - ch; }
    left = Math.min(Math.max(left, edge), Math.max(edge, w - cw - edge));
    top = Math.min(Math.max(top, edge), Math.max(edge, h - ch - edge));
    callout.style.left = left + 'px';
    callout.style.top = top + 'px';
    return true;
  }

  function follow() {
    if (current) place();
  }

  window.bibliotaphTour = {
    // True once the step's controls were found and the overlay drawn round them.
    show: function (root, anchors, firstRow) {
      if (!current) {
        window.addEventListener('resize', follow);
        window.addEventListener('scroll', follow, true);
      }
      current = { root: root, anchors: anchors, firstRow: firstRow };
      var first = targets(anchors)[0];
      if (!first) return false;
      first.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      return place();
    },
    clear: function () {
      if (!current) return;
      current = null;
      window.removeEventListener('resize', follow);
      window.removeEventListener('scroll', follow, true);
    }
  };
})();
