(function () {
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

  document.addEventListener('click', (ev) => {
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
    if (ev.key === 'Escape') closeTrialModal();
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
})();
