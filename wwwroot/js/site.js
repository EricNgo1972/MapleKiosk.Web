(function () {
  // ===== Theme (light default, dark optional; persisted) =====
  function applyTheme(t) {
    if (t === 'dark') document.documentElement.setAttribute('data-theme', 'dark');
    else document.documentElement.removeAttribute('data-theme');
  }
  try {
    const saved = localStorage.getItem('mk-theme');
    if (saved) applyTheme(saved);
  } catch (e) { /* storage unavailable */ }
  function toggleTheme() {
    const next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    applyTheme(next);
    try { localStorage.setItem('mk-theme', next); } catch (e) { /* ignore */ }
  }

  function openTrialModal() {
    const m = document.getElementById('trialModal');
    if (!m) return;
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

  document.addEventListener('click', (ev) => {
    if (ev.target.closest('[data-theme-toggle]'))  { ev.preventDefault(); ev.stopPropagation(); toggleTheme(); return; }

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

    const a = ev.target.closest('a[href^="#"]');
    if (!a) return;
    const hash = a.getAttribute('href');
    if (!hash || hash === '#') return;
    const el = document.querySelector(hash);
    if (!el) return;
    ev.preventDefault();
    ev.stopPropagation();
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, true);

  document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') { closeTrialModal(); closeUserMenu(); }
  });

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
})();
