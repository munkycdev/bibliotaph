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
