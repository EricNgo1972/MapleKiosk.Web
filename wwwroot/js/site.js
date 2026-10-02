(function () {
  function openTrialModal() {
    const m = document.getElementById('trialModal');
    if (!m) {
      // Pages without the form (legal pages, ...) send you to the homepage's.
      const seg = location.pathname.split('/')[1];
      const root = ['fr', 'vi', 'ru'].includes(seg) ? '/' + seg : '/';
      location.href = root + '#demo';
      return;
    }
    m.classList.add('open');
    document.body.classList.add('modal-open');
    const first = m.querySelector('input, select, textarea');
    if (first) setTimeout(() => first.focus(), 50);
  }
  function closeTrialModal() {
    const m = document.getElementById('trialModal');
    if (!m) return;
    m.classList.remove('open');
    document.body.classList.remove('modal-open');
  }
  window.openTrialModal  = openTrialModal;
  window.closeTrialModal = closeTrialModal;

  function closeUserMenu() {
    const pop = document.querySelector('[data-user-pop].open');
    if (pop) pop.classList.remove('open');
    const btn = document.querySelector('[data-user-menu][aria-expanded="true"]');
    if (btn) btn.setAttribute('aria-expanded', 'false');
  }
  window.closeUserMenu = closeUserMenu;

  // ===== Video lightbox: any [data-video] opens it — a YouTube id, or a site-served
  // file path ("/media/....mp4") played in a native <video> =====
  function isFile(id) { return /^\/|^https?:|\.mp4$/i.test(id); }
  let lastFocus = null;
  function openVideo(id, start) {
    closeVideo();
    lastFocus = document.activeElement;
    const lang = document.documentElement.lang || 'en';
    const box = document.createElement('div');
    box.className = 'vbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.innerHTML =
      '<button type="button" class="vbox__close" aria-label="Close" data-close-video>&times;</button>' +
      '<div class="vbox__frame">' + (isFile(id)
        ? '<video src="' + encodeURI(id) + (start ? '#t=' + (parseInt(start, 10) || 0) : '') +
          '" controls autoplay playsinline preload="auto" title="MapleKiosk video"></video>'
        : '<iframe src="https://www.youtube-nocookie.com/embed/' + encodeURIComponent(id) +
          '?autoplay=1&rel=0&modestbranding=1&hl=' + lang +
          (start ? '&start=' + (parseInt(start, 10) || 0) : '') + '" title="MapleKiosk video"' +
          ' allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen></iframe>') + '</div>';
    box.addEventListener('click', (e) => { if (e.target === box) closeVideo(); });
    document.body.appendChild(box);
    document.body.classList.add('modal-open');
    box.querySelector('.vbox__close').focus();
  }
  function closeVideo() {
    const box = document.querySelector('.vbox');
    if (!box) return;
    box.remove();
    document.body.classList.remove('modal-open');
    if (lastFocus) { lastFocus.focus(); lastFocus = null; }
  }

  document.addEventListener('click', (ev) => {
    const vid = ev.target.closest('[data-video]');
    if (vid) { ev.preventDefault(); ev.stopPropagation(); openVideo(vid.getAttribute('data-video'), vid.getAttribute('data-start')); return; }
    if (ev.target.closest('[data-close-video]')) { ev.preventDefault(); closeVideo(); return; }

    const menuBtn = ev.target.closest('[data-user-menu]');
    if (menuBtn) {
      ev.preventDefault(); ev.stopPropagation();
      const pop = menuBtn.parentElement.querySelector('[data-user-pop]');
      const willOpen = pop && !pop.classList.contains('open');
      closeUserMenu();
      if (willOpen) { pop.classList.add('open'); menuBtn.setAttribute('aria-expanded', 'true'); }
      return;
    }
    // A click anywhere outside the open popup closes it (clicks inside it pass through).
    if (!ev.target.closest('[data-user-pop]')) closeUserMenu();

    if (ev.target.closest('[data-open-trial]'))  { ev.preventDefault(); ev.stopPropagation(); openTrialModal();  return; }
    if (ev.target.closest('[data-close-trial]')) { ev.preventDefault(); ev.stopPropagation(); closeTrialModal(); return; }

    const a = ev.target.closest('a[href*="#"]');
    if (!a) return;
    const href = a.getAttribute('href');
    const cut = href.indexOf('#');
    const path = href.slice(0, cut);
    const hash = href.slice(cut);
    // Smooth-scroll only same-page anchors; others go to Blazor navigation.
    const norm = (p) => p.replace(/\/+$/, '') || '/';
    if (path && norm(path) !== norm(location.pathname)) return;
    if (!hash || hash === '#') return;
    const el = document.querySelector(hash);
    if (!el) return;
    ev.preventDefault();
    ev.stopPropagation();
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, true);

  document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') { closeVideo(); closeTrialModal(); closeUserMenu(); }
  });

  // Arriving from another page's "Book a demo" (see openTrialModal).
  if (location.hash === '#demo') {
    history.replaceState(null, '', location.pathname + location.search);
    setTimeout(openTrialModal, 300);
  }

  const nav = document.querySelector('.nav');
  let lastY = 0;
  function onScroll() {
    const y = window.scrollY;
    if (!nav) return;
    nav.classList.toggle('scrolled', y > 8);
    if (y > lastY && y > 200) nav.classList.add('hidden');
    else nav.classList.remove('hidden');
    lastY = y;
  }
  window.addEventListener('scroll', onScroll, { passive: true });

  if ('IntersectionObserver' in window) {
    document.documentElement.classList.add('fade-ready');
    const io = new IntersectionObserver((entries) => {
      entries.forEach(e => {
        if (e.isIntersecting) { e.target.classList.add('visible'); io.unobserve(e.target); }
      });
    }, { threshold: 0.08, rootMargin: '0px 0px -5% 0px' });
    document.querySelectorAll('.fade-in').forEach(el => io.observe(el));
  }

  document.querySelectorAll('img[data-fallback]').forEach(img => {
    img.addEventListener('error', () => {
      const label = img.getAttribute('data-fallback') || 'Image';
      const div = document.createElement('div');
      div.className = 'img-fallback';
      div.style.cssText = 'width:100%;height:100%;min-height:' + (img.height || 260) + 'px';
      div.textContent = label;
      img.replaceWith(div);
    });
  });

  // ===== Email assembly (defeats Cloudflare "[email protected]" obfuscation) =====
  // The server HTML never contains a literal user@domain string, so Cloudflare has
  // nothing to rewrite. We build the mailto link + visible text client-side instead.
  function wireMail() {
    document.querySelectorAll('a[data-user][data-domain]').forEach(a => {
      if (a.dataset.mailWired) return;
      a.dataset.mailWired = '1';
      const addr = a.getAttribute('data-user') + '@' + a.getAttribute('data-domain');
      a.setAttribute('href', 'mailto:' + addr);
      a.textContent = addr;
    });
  }
  wireMail();
  // Re-run after Blazor enhanced navigation swaps the DOM.
  document.addEventListener('enhancedload', wireMail);

  // ===== Scroll to top on page change =====
  const origPushState = history.pushState.bind(history);
  history.pushState = function (state, title, url) {
    const from = location.pathname;
    origPushState(state, title, url);
    if (location.pathname !== from && !location.hash) {
      try { window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); }
      catch (e) { window.scrollTo(0, 0); }
    }
  };
})();
