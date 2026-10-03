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
    // Desktop only: on a phone, focusing pops the keyboard over the modal as it opens.
    const first = m.querySelector('input, select, textarea');
    if (first && !matchMedia('(pointer: coarse)').matches) setTimeout(() => first.focus(), 50);
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
  // A film can come in several languages: data-video is the cut in data-video-lang (English when
  // absent) and data-video-<lang> adds the others. The visitor's pick is remembered; failing that
  // the page's language, then the first cut.
  const FILM_LANGS = { en: 'English', fr: 'Français', vi: 'Tiếng Việt', ru: 'Русский' };
  function isFile(id) { return /^\/|^https?:|\.mp4$/i.test(id); }
  let lastFocus = null;
  function filmLang(langs) {
    try { const v = localStorage.getItem('mk.filmLang'); if (langs.indexOf(v) >= 0) return v; } catch (e) { }
    const seg = location.pathname.split('/')[1];
    const page = FILM_LANGS[seg] ? seg : 'en';
    return langs.indexOf(page) >= 0 ? page : langs[0];
  }
  function setFilmLang(lang) {
    try { localStorage.setItem('mk.filmLang', lang); } catch (e) { }
    showFilmLang();
  }
  // Pressed state of each language group on the page, and film lengths (data-len-<lang>) in the
  // language each film will play in.
  function showFilmLang() {
    document.querySelectorAll('.film-lang').forEach((g) => {
      const btns = Array.from(g.querySelectorAll('[data-film-lang]'));
      const lang = filmLang(btns.map((b) => b.getAttribute('data-film-lang')));
      btns.forEach((b) => b.setAttribute('aria-pressed', String(b.getAttribute('data-film-lang') === lang)));
    });
    const sel = Object.keys(FILM_LANGS).map((l) => '[data-len-' + l + ']').join(',');
    document.querySelectorAll(sel).forEach((el) => {
      const langs = Object.keys(FILM_LANGS).filter((l) => el.hasAttribute('data-len-' + l));
      el.textContent = el.getAttribute('data-len-' + filmLang(langs));
    });
  }
  showFilmLang();
  onEnhancedLoad(showFilmLang);
  function openFilm(trigger) {
    const cuts = {};
    cuts[trigger.getAttribute('data-video-lang') || 'en'] = trigger.getAttribute('data-video');
    Object.keys(FILM_LANGS).forEach((l) => { const id = trigger.getAttribute('data-video-' + l); if (id) cuts[l] = id; });
    const langs = Object.keys(cuts);
    const lang = filmLang(langs);
    openVideo(cuts[lang], trigger.getAttribute('data-start'), langs.length > 1 ? { cuts: cuts, lang: lang } : null,
      trigger.hasAttribute('data-video-tall'));
  }
  // tall = a vertical (9:16) film, e.g. a YouTube Short.
  function openVideo(id, start, alt, tall) {
    closeVideo();
    lastFocus = document.activeElement;
    const lang = alt ? alt.lang : (document.documentElement.lang || 'en');
    const box = document.createElement('div');
    box.className = 'vbox';
    box.setAttribute('role', 'dialog');
    box.setAttribute('aria-modal', 'true');
    box.innerHTML =
      '<button type="button" class="vbox__close" aria-label="Close" data-close-video>&times;</button>' +
      (alt ? '<div class="vbox__lang film-lang" role="group" aria-label="Language">' +
        Object.keys(alt.cuts).map((l) => '<button type="button" lang="' + l + '" data-vbox-lang="' + l +
          '" aria-pressed="' + (alt.lang === l) + '">' + FILM_LANGS[l] + '</button>').join('') + '</div>' : '') +
      '<div class="vbox__frame' + (tall ? ' vbox__frame--tall' : '') + '">' + (isFile(id)
        ? '<video src="' + encodeURI(id) + (start ? '#t=' + (parseInt(start, 10) || 0) : '') +
          '" controls autoplay playsinline preload="auto" title="MapleKiosk video"></video>'
        : '<iframe src="https://www.youtube-nocookie.com/embed/' + encodeURIComponent(id) +
          '?autoplay=1&rel=0&modestbranding=1&hl=' + lang +
          (start ? '&start=' + (parseInt(start, 10) || 0) : '') + '" title="MapleKiosk video"' +
          ' allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen></iframe>') + '</div>';
    box.addEventListener('click', (e) => {
      if (e.target === box) { closeVideo(); return; }
      const pick = e.target.closest('[data-vbox-lang]');
      if (pick && alt && pick.getAttribute('data-vbox-lang') !== alt.lang) {
        const keep = lastFocus;
        setFilmLang(pick.getAttribute('data-vbox-lang'));
        openVideo(alt.cuts[pick.getAttribute('data-vbox-lang')], null, { cuts: alt.cuts, lang: pick.getAttribute('data-vbox-lang') }, tall);
        lastFocus = keep;
      }
    });
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
    if (vid) { ev.preventDefault(); ev.stopPropagation(); openFilm(vid); return; }
    const langBtn = ev.target.closest('[data-film-lang]');
    if (langBtn) { ev.preventDefault(); setFilmLang(langBtn.getAttribute('data-film-lang')); return; }
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
  onEnhancedLoad(wireMail);

  // Blazor raises 'enhancedload' through Blazor.addEventListener (not as a DOM event).
  function onEnhancedLoad(fn) {
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') window.Blazor.addEventListener('enhancedload', fn);
    else document.addEventListener('enhancedload', fn);
  }

  // ===== Page changes feel instant =====
  // Every page is a round trip to the origin (Cloudflare doesn't cache HTML), and enhanced navigation
  // keeps the old page on screen until the new one arrives — so a click looked ignored. Show a
  // loading bar the moment a link is clicked, and prefetch pages on hover/touch so the click is
  // usually served from the browser cache (the server lets signed-out visitors cache pages briefly).
  function sameSitePage(a) {
    if (!a || a.target || a.hasAttribute('download') || a.hasAttribute('data-video')) return null;
    const href = a.getAttribute('href');
    if (!href || href.startsWith('#') || href.startsWith('javascript:') || href.startsWith('mailto:')) return null;
    const url = new URL(a.href, location.href);
    if (url.origin !== location.origin || /^\/(api|media|login|logout|auth|signin|onboarding)\b/.test(url.pathname)) return null;
    if (url.pathname === location.pathname && url.search === location.search) return null;
    return url;
  }

  const prefetched = new Set();
  function prefetch(ev) {
    const url = sameSitePage(ev.target.closest && ev.target.closest('a[href]'));
    if (!url || prefetched.has(url.href)) return;
    prefetched.add(url.href);
    fetch(url.href, { credentials: 'same-origin', headers: { 'Accept': 'text/html' } }).catch(() => prefetched.delete(url.href));
  }
  document.addEventListener('pointerover', prefetch, { passive: true });
  document.addEventListener('touchstart', prefetch, { passive: true });
  document.addEventListener('focusin', prefetch);

  let busyTimer = null;
  function navDone() {
    document.documentElement.classList.remove('nav-busy');
    clearTimeout(busyTimer);
  }
  document.addEventListener('click', (ev) => {
    // (Not checking defaultPrevented: Blazor's enhanced navigation prevents the default first.)
    if (ev.button !== 0 || ev.metaKey || ev.ctrlKey || ev.shiftKey || ev.altKey) return;
    const a = ev.target.closest && ev.target.closest('a[href]');
    if (!sameSitePage(a)) return;
    // Language pills: light up the chosen one right away.
    const sw = a.closest('.lang-switch');
    if (sw) sw.querySelectorAll('a').forEach(x => x.classList.toggle('active', x === a));
    document.documentElement.classList.add('nav-busy');
    clearTimeout(busyTimer);
    busyTimer = setTimeout(navDone, 15000); // never leave the bar stuck
  }, true); // capture: Blazor's enhanced-navigation handler stops the click before the bubble phase
  onEnhancedLoad(navDone);

  // Forms (the demo request) post as enhanced forms: show the bar and lock the button meanwhile.
  document.addEventListener('submit', (ev) => {
    const btn = ev.target.querySelector('button[type="submit"][data-busy-label]');
    if (btn) { btn.disabled = true; btn.textContent = btn.getAttribute('data-busy-label'); }
    document.documentElement.classList.add('nav-busy');
    clearTimeout(busyTimer);
    busyTimer = setTimeout(navDone, 15000);
  });
  // A demo request that failed validation comes back with the modal still open.
  onEnhancedLoad(() => document.body.classList.toggle('modal-open', !!document.querySelector('#trialModal.open')));
  window.addEventListener('pageshow', navDone);

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
