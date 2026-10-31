// The details pane's left edge: dragged, it sets the pane's width through the CSSOM, as the page's policy refuses style
// attributes, and tells the page the width once the drag ends so it's kept. The pane scrolls, so the edge is found by
// where the pointer is rather than drawn. A window too narrow for a wider pane keeps the stylesheet's width.
(function () {
  var narrowest = 440, widest = 960, room = 160, grip = 6;

  function fit(width) {
    return Math.round(Math.max(narrowest, Math.min(width, widest, window.innerWidth - room)));
  }

  function roomy() {
    return window.innerWidth - room > narrowest;
  }

  window.bibliotaphDetails = {
    resizable: function (pane, width, owner) {
      if (!pane) return null;
      if (width && roomy()) pane.style.width = fit(width) + 'px';
      var startX = 0, startWidth = 0, dragging = false;

      function atEdge(e) {
        return roomy() && e.clientX - pane.getBoundingClientRect().left < grip;
      }

      function down(e) {
        if (e.button !== 0 || !atEdge(e)) return;
        dragging = true;
        startX = e.clientX;
        startWidth = pane.getBoundingClientRect().width;
        pane.setPointerCapture(e.pointerId);
        pane.classList.add('resizing');
        e.preventDefault();
      }

      function move(e) {
        if (dragging) pane.style.width = fit(startWidth + startX - e.clientX) + 'px';
        else pane.classList.toggle('at-edge', atEdge(e));
      }

      function up(e) {
        if (!dragging) return;
        dragging = false;
        pane.releasePointerCapture(e.pointerId);
        pane.classList.remove('resizing');
        owner.invokeMethodAsync('Resized', Math.round(pane.getBoundingClientRect().width));
      }

      function leave() {
        if (!dragging) pane.classList.remove('at-edge');
      }

      pane.addEventListener('pointerdown', down);
      pane.addEventListener('pointermove', move);
      pane.addEventListener('pointerup', up);
      pane.addEventListener('pointercancel', up);
      pane.addEventListener('pointerleave', leave);
      return {
        stop: function () {
          pane.removeEventListener('pointerdown', down);
          pane.removeEventListener('pointermove', move);
          pane.removeEventListener('pointerup', up);
          pane.removeEventListener('pointercancel', up);
          pane.removeEventListener('pointerleave', leave);
        }
      };
    }
  };
})();
